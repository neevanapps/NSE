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
/// Live-trading engine for the Hysteresis Core-score strategy (2026-09-13 live-wiring plan Batch
/// 5, A7/A8) -- a thin adaptation of <see cref="LiveTradingEngine"/>'s own <c>EvaluateCadenceAsync</c>
/// shape, reading <see cref="CoreScoreSnapshot"/> instead of <see cref="ScoreSnapshot"/> and
/// <see cref="CoreScoreHysteresisRules"/> instead of <c>EntryRuleEvaluator</c>/<c>ExitRuleEvaluator</c>
/// (see that class's own doc comment for why those two don't match what was actually backtested).
///
/// Every open-position/daily-count/risk query is scoped to <see cref="StrategyId.CoreScoreHysteresis"/>
/// -- this engine never sees, and can never be blocked by, the legacy engine's or the Crossover
/// engine's own activity (the direct implementation of the "per-strategy independent risk limits"
/// decision). The one deliberate exception is the committed-capital check, which sums ALL open
/// positions across every strategy (Capital.Total is one shared pool, not a per-strategy
/// allowance -- Assumption 1, docs/replication_plan.md).
///
/// <see cref="CoreScoreHysteresisRules"/> reads the INSTANT <c>CoreScore</c> (this cadence's own
/// <c>100*tanh(...)</c> value), never <c>CoreScoreFast</c>/<c>CoreScoreSlow</c> -- those exist only
/// for the Crossover strategy; see <see cref="CoreScoreCrossoverTradingEngine"/>.
///
/// MaxConcurrentPositions is hardcoded to 1 here (<see cref="MaxConcurrentPositions"/>), not read
/// from <see cref="RulesetConfig.Capital"/> -- the user's explicit "max concurrent trades should
/// be 1 per strategy" instruction, a fixed architectural fact of this plan, not a tunable.
/// </summary>
public sealed class CoreScoreHysteresisTradingEngine(
    IServiceScopeFactory scopeFactory,
    ITelegramNotifier telegram,
    DashboardPushClient dashboardPush,
    IValidatedOptions<RulesetConfig> rulesetOptions,
    IValidatedOptions<CoreScoreHysteresisConfig> strategyOptions,
    ILogger<CoreScoreHysteresisTradingEngine> logger)
{
    const StrategyId Strategy = StrategyId.CoreScoreHysteresis;
    const int MaxConcurrentPositions = 1;

    readonly IStrikeSelector _strikeSelector = new StrikeSelector();

    // Properties, not fields -- read the current hot-reloaded, already-validated value fresh on
    // every access, same reasoning as LiveTradingEngine's own _config property.
    RulesetConfig _shared => rulesetOptions.Current;
    CoreScoreHysteresisConfig _strategy => strategyOptions.Current;

    public async Task EvaluateCadenceAsync(CoreScoreSnapshot snapshot, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        if (snapshot.CoreScore is not { } score)
        {
            return; // no Core score yet this cadence -- nothing for the rule engine to act on
        }

        var now = snapshot.ComputedAt;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        // Exits first: a position closing this tick frees the (single) MaxConcurrentPositions
        // slot for a fresh entry evaluated in the same tick.
        var openPositions = await db.PaperTrades.Where(p => p.ExitTime == null && p.StrategyId == Strategy).ToListAsync(ct);
        foreach (var position in openPositions)
        {
            await EvaluateExitAsync(db, position, score, now, featureEngine, ct);
        }

        var stillOpenPositions = openPositions.Where(p => p.ExitTime is null).ToList();

        await EvaluateEntryAsync(db, score, stillOpenPositions, now, featureEngine, snapshot, ct);
    }

    async Task EvaluateEntryAsync(
        NiftySignalDbContext db, double score, IReadOnlyList<PaperTrade> openPositions,
        DateTimeOffset now, LiveFeatureEngine featureEngine, CoreScoreSnapshot snapshot, CancellationToken ct)
    {
        var config = _shared;
        var strategyConfig = _strategy;

        var nowTime = TimeOnly.FromDateTime(now.ToIst().DateTime);
        var noEntryBefore = EntryRuleEvaluator.MarketOpen.AddMinutes(config.Session.NoEntryBeforeMinutes);
        if (nowTime < noEntryBefore)
        {
            return; // before the entry window opens -- not logged, same as the normal majority-case gates below
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

        if (openPositions.Count >= MaxConcurrentPositions)
        {
            return;
        }

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

        // StrategyId-scoped (this strategy's own risk box, independent of the legacy engine's
        // and the Crossover engine's own).
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

        var decision = CoreScoreHysteresisRules.EvaluateEntry(score, strategyConfig.EntryScoreThreshold);
        if (!decision.ShouldEnter)
        {
            return; // |score| below threshold -- the normal, expected majority of cadences
        }

        var side = decision.Direction == EntryDirection.Bullish ? OptionType.Call : OptionType.Put;
        var candidates = featureEngine.BuildStrikeCandidates(side, now);
        var selection = _strikeSelector.SelectBestCandidate(candidates, decision.Direction, config);

        if (selection.Selected is not { } chosen || chosen.AskPrice is not { } ask)
        {
            logger.LogInformation(
                "[CoreScoreHysteresis] Entry signal ({Direction}, score {Score:F1}) but no strike candidate passed selection: {Diagnostics}",
                decision.Direction, score, string.Join("; ", selection.Diagnostics));
            return;
        }

        var instrument = featureEngine.FindInstrument(chosen.Token);
        var tickSize = instrument?.TickSize ?? 0.05m;
        var quantity = config.Capital.LotSize * config.Capital.LotsPerTrade;
        var fill = PaperTradeSimulator.FillEntry(ask, tickSize, quantity, config.Costs);

        // Unscoped across ALL strategies (Capital.Total is one shared pool) -- see class doc comment.
        var newPositionValue = fill.FillPrice * quantity;
        var allOpenPositionsValue = await db.PaperTrades.Where(p => p.ExitTime == null).SumAsync(p => p.EntryPrice * p.Quantity, ct);
        var committedCapital = allOpenPositionsValue + newPositionValue;
        if (committedCapital > config.Capital.Total)
        {
            logger.LogInformation(
                "[CoreScoreHysteresis] Entry signal ({Direction}, score {Score:F1}) but committing {NewPosition:F2} to {Symbol} would bring total open capital to {Committed:F2}, over Capital.Total {Total:F2} -- skipped",
                decision.Direction, score, newPositionValue, chosen.TradingSymbol, committedCapital, config.Capital.Total);
            return;
        }

        var trade = new PaperTrade
        {
            InstrumentToken = chosen.Token,
            TradingSymbol = chosen.TradingSymbol,
            StrategyId = Strategy,
            Direction = decision.Direction,
            EntryTime = now,
            EntryPrice = fill.FillPrice,
            Quantity = quantity,
            EntryScore = score,
            RulesetVersion = config.RulesetVersion,
            ScoreWeightsVersion = snapshot.WeightSetVersion ?? "unknown",
        };
        db.PaperTrades.Add(trade);
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation("[CoreScoreHysteresis] Paper trade ENTRY: {Symbol} {Direction} @ {Price} (score {Score:F1})", trade.TradingSymbol, trade.Direction, trade.EntryPrice, score);
        await telegram.SendAsync(
            NotificationCategory.TradeEntry,
            $"NiftySignal [CoreScoreHysteresis] ENTRY: {trade.TradingSymbol} {trade.Direction} @ {trade.EntryPrice} (score {score:+0.0;-0.0})",
            ct);
    }

    async Task EvaluateExitAsync(
        NiftySignalDbContext db, PaperTrade position, double score, DateTimeOffset now, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        if (!featureEngine.TryGetLatestQuote(position.InstrumentToken, out var ltp, out var bid))
        {
            return; // no live quote yet this cadence -- re-evaluate next tick
        }

        var markPrice = bid ?? ltp;
        var excursionMoved = UpdateExcursions(position, markPrice);

        var decision = CoreScoreHysteresisRules.EvaluateExit(position.Direction, score, _strategy.EntryScoreThreshold, now, _shared.Session.SquareOffTime);
        if (!decision.ShouldExit)
        {
            if (excursionMoved)
            {
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        var instrument = featureEngine.FindInstrument(position.InstrumentToken);
        var tickSize = instrument?.TickSize ?? 0.05m;
        var fill = PaperTradeSimulator.FillExit(markPrice, tickSize, position.Quantity, _shared.Costs);

        var entryNetValue = (position.EntryPrice * position.Quantity) + _shared.Costs.BrokeragePerOrder;
        var grossPnl = (fill.FillPrice * position.Quantity) - (position.EntryPrice * position.Quantity);
        var netPnl = fill.NetValue - entryNetValue;

        position.ExitTime = now;
        position.ExitPrice = fill.FillPrice;
        position.ExitReason = decision.Reason;
        position.GrossPnl = grossPnl;
        position.NetPnl = netPnl;
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation(
            "[CoreScoreHysteresis] Paper trade EXIT: {Symbol} {Reason} @ {Price} netPnl={NetPnl:F2}",
            position.TradingSymbol, decision.Reason, fill.FillPrice, netPnl);
        await telegram.SendAsync(
            NotificationCategory.Exit,
            $"NiftySignal [CoreScoreHysteresis] EXIT: {position.TradingSymbol} {decision.Reason} @ {fill.FillPrice} P&L {netPnl:+0.00;-0.00}",
            ct);
    }

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>Same construction as LiveTradingEngine's own UpdateExcursions -- see that method's doc comment.</summary>
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
