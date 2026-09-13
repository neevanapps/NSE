using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;

namespace NiftySignal.Host;

/// <summary>
/// Live-trading engine for the always-positioned Crossover Core-score strategy (2026-09-13
/// live-wiring plan Batch 5, A7/A8) -- see <see cref="CoreScoreHysteresisTradingEngine"/>'s own
/// doc comment for the shared "why not EntryRuleEvaluator/ExitRuleEvaluator" and "StrategyId
/// scoping / shared capital pool" reasoning, both identical here.
///
/// Structurally different from the Hysteresis engine because entry and exit are the SAME event:
/// once the first crossover fires, this strategy is always positioned (long call or long put),
/// flipping (close current, open opposite) on every subsequent opposite crossover, with SquareOff
/// as the only other exit -- see <see cref="CoreScoreCrossoverRules"/>'s own doc comment. Each
/// cadence checks, in the SAME priority order as <c>CoreScoreOptionSimulator.SimulateDayCrossover</c>:
/// SquareOff first (unconditional, closes any open position and skips the crossover check this
/// cadence entirely) &gt; fast/slow warm-up gate &gt; the crossover observation itself.
///
/// <see cref="_crossoverRules"/> is stateful (one instance for this singleton engine's whole
/// process lifetime) and MUST be seeded from that day's persisted <c>CoreScoreFast</c>/
/// <c>CoreScoreSlow</c> history on every restart via <see cref="SeedFromHistory"/> -- see that
/// method's own doc comment and <see cref="CoreScoreCrossoverRules"/>'s for why this is required,
/// not an accepted gap like <see cref="LiveFeatureEngine"/>'s own SeedHistory-adjacent fast FIFOs.
/// </summary>
public sealed class CoreScoreCrossoverTradingEngine(
    IServiceScopeFactory scopeFactory,
    ITelegramNotifier telegram,
    DashboardPushClient dashboardPush,
    IValidatedOptions<RulesetConfig> rulesetOptions,
    IValidatedOptions<CoreScoreCrossoverConfig> strategyOptions,
    ILogger<CoreScoreCrossoverTradingEngine> logger)
{
    const StrategyId Strategy = StrategyId.CoreScoreCrossover;
    const int MaxConcurrentPositions = 1;

    readonly IStrikeSelector _strikeSelector = new StrikeSelector();
    readonly CoreScoreCrossoverRules _crossoverRules = new();

    RulesetConfig _shared => rulesetOptions.Current;
    CoreScoreCrossoverConfig _strategy => strategyOptions.Current;

    /// <summary>
    /// Restart-safety (plan A7's own correction): replays today's persisted <see cref="CoreScoreSnapshot"/>
    /// history, in chronological order, through a SCRATCH <see cref="CoreScoreCrossoverRules"/>
    /// instance (exactly reproducing the sequence of <c>Observe</c> calls this engine would have
    /// made had the process run continuously), then seeds the real instance with the scratch
    /// instance's resulting <see cref="CoreScoreCrossoverRules.PreviousDiffSign"/>. Must be called
    /// once at startup, before this engine's first live <see cref="EvaluateCadenceAsync"/> call --
    /// see <see cref="MarketDataIngestionWorker"/>'s own seeding sequence. Passing an empty/no-history
    /// enumerable leaves <see cref="CoreScoreCrossoverRules.PreviousDiffSign"/> null, the same
    /// "no prior observation yet" state a fresh instance already starts in.
    /// </summary>
    public void SeedFromHistory(IEnumerable<CoreScoreSnapshot> history)
    {
        var scratch = new CoreScoreCrossoverRules();
        foreach (var snapshot in history.OrderBy(s => s.ComputedAt))
        {
            if (snapshot.CoreScoreFast is { } fast && snapshot.CoreScoreSlow is { } slow)
            {
                scratch.Observe(fast, slow);
            }
        }

        _crossoverRules.Seed(scratch.PreviousDiffSign);
    }

    public async Task EvaluateCadenceAsync(CoreScoreSnapshot snapshot, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        var now = snapshot.ComputedAt;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        var openPositions = await db.PaperTrades.Where(p => p.ExitTime == null && p.StrategyId == Strategy).ToListAsync(ct);
        var openPosition = openPositions.Count > 0 ? openPositions[0] : null;

        // SquareOff first, unconditional, BEFORE the crossover check -- matches
        // CoreScoreOptionSimulator.SimulateDayCrossover's own MustForceClose priority exactly
        // (checked, then `continue`s past the crossover comparison entirely that cadence).
        if (CoreScoreCrossoverRules.IsPastSquareOff(now, _shared.Session.SquareOffTime))
        {
            if (openPosition is not null)
            {
                await CloseAsync(db, openPosition, now, featureEngine, ExitReason.SquareOff, ct);
            }

            return;
        }

        if (snapshot.CoreScoreFast is not { } fast || snapshot.CoreScoreSlow is not { } slow)
        {
            return; // not warmed up yet -- matches the backtest's own fastWindow/slowWindow warm-up guard
        }

        var observation = _crossoverRules.Observe(fast, slow);
        if (!observation.Crossed)
        {
            // No crossover this cadence -- still roll MFE/MAE on any open position, matching
            // LiveTradingEngine's own "update excursions every cadence regardless of exit" behavior.
            if (openPosition is not null && featureEngine.TryGetLatestQuote(openPosition.InstrumentToken, out var ltp, out var bid)
                && UpdateExcursions(openPosition, bid ?? ltp))
            {
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        // A genuine crossover fired -- close whatever's open (if anything), then try to open the
        // new leg. If the new leg's entry gates fail (kill switch, daily loss limit, etc.), the
        // strategy simply goes flat until the next crossover -- _previousDiffSign has already
        // moved on, so a subsequent opposite crossover is still detected correctly next time.
        if (openPosition is not null)
        {
            await CloseAsync(db, openPosition, now, featureEngine, ExitReason.CrossoverFlip, ct);
        }

        await EvaluateEntryAsync(db, observation.Direction, snapshot, now, featureEngine, ct);
    }

    async Task EvaluateEntryAsync(
        NiftySignalDbContext db, EntryDirection direction, CoreScoreSnapshot snapshot, DateTimeOffset now, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        var config = _shared;
        var strategyConfig = _strategy;

        var nowTime = TimeOnly.FromDateTime(now.ToIst().DateTime);
        var noEntryBefore = EntryRuleEvaluator.MarketOpen.AddMinutes(config.Session.NoEntryBeforeMinutes);
        if (nowTime < noEntryBefore)
        {
            return;
        }

        var todayIstMidnight = new DateTimeOffset(now.ToIst().Date, IstOffset).ToUniversalTime();
        var isExpiryDay = featureEngine.NearestExpiry == DateOnly.FromDateTime(todayIstMidnight.Date);
        var noEntryAfter = config.Session.ExpiryDayEnabled && isExpiryDay
            ? config.Session.ExpiryDayNoEntryAfterTime
            : config.Session.NoEntryAfterTime;
        if (nowTime >= noEntryAfter)
        {
            return;
        }

        if (!snapshot.IsWarmedUp)
        {
            return;
        }

        // MaxConcurrentPositions=1 is implicitly satisfied here: the caller only reaches this
        // point after closing whatever was open (if anything), so a fresh open-position count
        // check would always read 0 -- re-querying it would be redundant, not a correctness gap.

        var killSwitch = await db.KillSwitchStates.FindAsync([KillSwitchState.SingletonId], ct);
        if (killSwitch?.EntriesEnabled == false)
        {
            return;
        }

        var hasOpenGap = await db.DataGaps.AnyAsync(g => g.EndedAt == null, ct);
        if (hasOpenGap)
        {
            return;
        }

        var tradesToday = await db.PaperTrades.CountAsync(p => p.EntryTime >= todayIstMidnight && p.StrategyId == Strategy, ct);
        if (tradesToday >= strategyConfig.RiskLimits.MaxTradesPerDay)
        {
            return;
        }

        var closedTodayPnls = await db.PaperTrades
            .Where(p => p.ExitTime != null && p.ExitTime >= todayIstMidnight && p.NetPnl != null && p.StrategyId == Strategy)
            .OrderByDescending(p => p.ExitTime)
            .Select(p => p.NetPnl!.Value)
            .ToListAsync(ct);
        var closedTodayNetPnl = closedTodayPnls.Sum();
        var dailyLossLimit = config.Capital.Total * (decimal)strategyConfig.RiskLimits.MaxDailyLossPct / 100m;
        if (closedTodayNetPnl <= -dailyLossLimit)
        {
            return;
        }

        var consecutiveLossesToday = 0;
        foreach (var pnl in closedTodayPnls)
        {
            if (pnl >= 0)
            {
                break;
            }

            consecutiveLossesToday++;
        }

        if (consecutiveLossesToday >= strategyConfig.RiskLimits.MaxConsecutiveLosses)
        {
            return;
        }

        var side = direction == EntryDirection.Bullish ? OptionType.Call : OptionType.Put;
        var candidates = featureEngine.BuildStrikeCandidates(side, now);
        var selection = _strikeSelector.SelectBestCandidate(candidates, direction, config);

        if (selection.Selected is not { } chosen || chosen.AskPrice is not { } ask)
        {
            logger.LogInformation(
                "[CoreScoreCrossover] Crossover signal ({Direction}) but no strike candidate passed selection: {Diagnostics}",
                direction, string.Join("; ", selection.Diagnostics));
            return;
        }

        var instrument = featureEngine.FindInstrument(chosen.Token);
        var tickSize = instrument?.TickSize ?? 0.05m;
        var quantity = config.Capital.LotSize * config.Capital.LotsPerTrade;
        var fill = PaperTradeSimulator.FillEntry(ask, tickSize, quantity, config.Costs);

        var newPositionValue = fill.FillPrice * quantity;
        var allOpenPositionsValue = await db.PaperTrades.Where(p => p.ExitTime == null).SumAsync(p => p.EntryPrice * p.Quantity, ct);
        var committedCapital = allOpenPositionsValue + newPositionValue;
        if (committedCapital > config.Capital.Total)
        {
            logger.LogInformation(
                "[CoreScoreCrossover] Crossover signal ({Direction}) but committing {NewPosition:F2} to {Symbol} would bring total open capital to {Committed:F2}, over Capital.Total {Total:F2} -- skipped",
                direction, newPositionValue, chosen.TradingSymbol, committedCapital, config.Capital.Total);
            return;
        }

        var trade = new PaperTrade
        {
            InstrumentToken = chosen.Token,
            TradingSymbol = chosen.TradingSymbol,
            StrategyId = Strategy,
            Direction = direction,
            EntryTime = now,
            EntryPrice = fill.FillPrice,
            Quantity = quantity,
            EntryScore = snapshot.CoreScore ?? 0,
            RulesetVersion = config.RulesetVersion,
            ScoreWeightsVersion = snapshot.WeightSetVersion ?? "unknown",
        };
        db.PaperTrades.Add(trade);
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation("[CoreScoreCrossover] Paper trade ENTRY: {Symbol} {Direction} @ {Price}", trade.TradingSymbol, trade.Direction, trade.EntryPrice);
        await telegram.SendAsync(
            NotificationCategory.TradeEntry,
            $"NiftySignal [CoreScoreCrossover] ENTRY: {trade.TradingSymbol} {trade.Direction} @ {trade.EntryPrice}",
            ct);
    }

    async Task CloseAsync(NiftySignalDbContext db, PaperTrade position, DateTimeOffset now, LiveFeatureEngine featureEngine, ExitReason reason, CancellationToken ct)
    {
        if (!featureEngine.TryGetLatestQuote(position.InstrumentToken, out var ltp, out var bid))
        {
            // Should not happen in practice -- an already-open position's own instrument has, by
            // definition, ticked at least once before (at entry), and _latest never forgets a
            // token once seen. Logged loudly rather than silently retried: if this ever fires, the
            // crossover state machine has already moved on internally (Observe already flipped
            // _previousDiffSign) even though the position itself is still open, a real desync that
            // needs a human, not a silent skip.
            logger.LogError(
                "[CoreScoreCrossover] Cannot close {Symbol} for {Reason} -- no live quote available for an already-open position's own instrument",
                position.TradingSymbol, reason);
            return;
        }

        var markPrice = bid ?? ltp;
        UpdateExcursions(position, markPrice);

        var instrument = featureEngine.FindInstrument(position.InstrumentToken);
        var tickSize = instrument?.TickSize ?? 0.05m;
        var fill = PaperTradeSimulator.FillExit(markPrice, tickSize, position.Quantity, _shared.Costs);

        var entryNetValue = (position.EntryPrice * position.Quantity) + _shared.Costs.BrokeragePerOrder;
        var grossPnl = (fill.FillPrice * position.Quantity) - (position.EntryPrice * position.Quantity);
        var netPnl = fill.NetValue - entryNetValue;

        position.ExitTime = now;
        position.ExitPrice = fill.FillPrice;
        position.ExitReason = reason;
        position.GrossPnl = grossPnl;
        position.NetPnl = netPnl;
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation(
            "[CoreScoreCrossover] Paper trade EXIT: {Symbol} {Reason} @ {Price} netPnl={NetPnl:F2}",
            position.TradingSymbol, reason, fill.FillPrice, netPnl);
        await telegram.SendAsync(
            NotificationCategory.Exit,
            $"NiftySignal [CoreScoreCrossover] EXIT: {position.TradingSymbol} {reason} @ {fill.FillPrice} P&L {netPnl:+0.00;-0.00}",
            ct);
    }

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    static bool UpdateExcursions(PaperTrade position, decimal markPrice)
    {
        if (position.EntryPrice <= 0)
        {
            return false;
        }

        var excursionPct = (double)((markPrice - position.EntryPrice) / position.EntryPrice * 100m);
        var moved = false;

        if (position.MaxFavourableExcursionPct is not { } mfe || excursionPct > mfe)
        {
            position.MaxFavourableExcursionPct = excursionPct;
            moved = true;
        }

        if (position.MaxAdverseExcursionPct is not { } mae || excursionPct < mae)
        {
            position.MaxAdverseExcursionPct = excursionPct;
            moved = true;
        }

        return moved;
    }
}
