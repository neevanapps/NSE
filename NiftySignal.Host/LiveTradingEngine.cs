using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;

namespace NiftySignal.Host;

/// <summary>
/// Runs the entry/exit rule engine and paper-trade simulator against each cadence's
/// composite score, on top of the already-tested EntryRuleEvaluator/ExitRuleEvaluator/
/// StrikeSelector/PaperTradeSimulator building blocks. Open-position and daily-count state
/// is read fresh from the database each cadence rather than cached in memory -- trade
/// volume is low (single digits per day), so the DB round trip is cheap, and it means this
/// engine can't drift out of sync with what's actually persisted.
/// </summary>
public sealed class LiveTradingEngine(
    IServiceScopeFactory scopeFactory,
    ITelegramNotifier telegram,
    DashboardPushClient dashboardPush,
    IValidatedOptions<RulesetConfig> rulesetOptions,
    ILogger<LiveTradingEngine> logger)
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    readonly IStrikeSelector _strikeSelector = new StrikeSelector();
    readonly ScoreSustainTracker _sustainTracker = new();

    // A property, not a field -- reads the current hot-reloaded, already-validated value fresh
    // on every access (2026-09-09, external review), so a config change takes effect on the
    // very next cadence rather than requiring a redeploy/restart. See ValidatedOptionsMonitor's
    // own doc comment for why a bad reload can never make it into rulesetOptions.Current.
    RulesetConfig _config => rulesetOptions.Current;

    public async Task EvaluateCadenceAsync(ScoreSnapshot snapshot, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        if (snapshot.CompositeScore is not { } score)
        {
            return; // no composite yet -- nothing for the rule engine to act on
        }

        var now = snapshot.ComputedAt;
        var sustained = _sustainTracker.Observe(now, score, _config.Entry.MinAbsScore);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        // Exits first: a position closing this tick frees a MaxConcurrentPositions slot
        // for a fresh entry evaluated in the same tick.
        var openPositions = await db.PaperTrades.Where(p => p.ExitTime == null).ToListAsync(ct);
        foreach (var position in openPositions)
        {
            await EvaluateExitAsync(db, position, score, now, featureEngine, ct);
        }

        // Positions closed by the loop above have ExitTime set on the same tracked entity
        // (EvaluateExitAsync mutates position in place) -- re-filtering in memory reflects
        // this cadence's exits without a second DB round trip.
        var stillOpenPositions = openPositions.Where(p => p.ExitTime is null).ToList();

        await EvaluateEntryAsync(db, score, sustained, stillOpenPositions, now, featureEngine, snapshot, ct);
    }

    async Task EvaluateEntryAsync(
        NiftySignalDbContext db, double score, SustainStatus sustained, IReadOnlyList<PaperTrade> openPositions,
        DateTimeOffset now, LiveFeatureEngine featureEngine, ScoreSnapshot snapshot, CancellationToken ct)
    {
        // UTC, not IST -- Npgsql only accepts Offset=0 DateTimeOffset values for
        // timestamptz query parameters (same rule that bit ReceivedAt/UpdatedAt, and
        // MarketDataIngestionWorker's own history-seed query, earlier this session).
        var todayIstMidnight = new DateTimeOffset(now.ToOffset(IstOffset).Date, IstOffset).ToUniversalTime();
        var prospectiveDirection = Math.Sign(score) switch
        {
            > 0 => EntryDirection.Bullish,
            < 0 => EntryDirection.Bearish,
            _ => EntryDirection.None,
        };

        var killSwitch = await db.KillSwitchStates.FindAsync([KillSwitchState.SingletonId], ct);
        var hasOpenGap = await db.DataGaps.AnyAsync(g => g.EndedAt == null, ct);
        var tradesToday = await db.PaperTrades.CountAsync(p => p.EntryTime >= todayIstMidnight, ct);
        var lastEntrySameDirection = prospectiveDirection == EntryDirection.None
            ? null
            : await db.PaperTrades
                .Where(p => p.EntryTime >= todayIstMidnight && p.Direction == prospectiveDirection)
                .OrderByDescending(p => p.EntryTime)
                .Select(p => (DateTimeOffset?)p.EntryTime)
                .FirstOrDefaultAsync(ct);

        // Newest first: both closedTodayNetPnl (sum) and the F3 consecutive-losses streak
        // (2026-09-08) come from this one fetch -- no separate round trip for either.
        var closedTodayPnls = await db.PaperTrades
            .Where(p => p.ExitTime != null && p.ExitTime >= todayIstMidnight && p.NetPnl != null)
            .OrderByDescending(p => p.ExitTime)
            .Select(p => p.NetPnl!.Value)
            .ToListAsync(ct);
        var closedTodayNetPnl = closedTodayPnls.Sum();
        var dailyLossLimit = _config.Capital.Total * (decimal)_config.RiskLimits.MaxDailyLossPct / 100m;
        var dailyLossLimitBreached = closedTodayNetPnl <= -dailyLossLimit;

        // The other half of F3: count losses backward from the most recent close, stopping at
        // the first non-loss (or the day's start) -- "how many in a row, right now."
        var consecutiveLossesToday = 0;
        foreach (var pnl in closedTodayPnls)
        {
            if (pnl >= 0)
            {
                break;
            }

            consecutiveLossesToday++;
        }

        // Same already-fetched figure, opposite side -- no extra round trip. Realised only:
        // an open position sitting in profit does not trip this, since it isn't banked yet.
        var dailyProfitTarget = _config.Capital.Total * (decimal)_config.RiskLimits.MaxDailyProfitPct / 100m;
        var dailyProfitTargetReached = closedTodayNetPnl >= dailyProfitTarget;

        var isExpiryDay = featureEngine.NearestExpiry == DateOnly.FromDateTime(todayIstMidnight.Date);

        var context = new EntryContext(
            Now: now,
            Score: score,
            ScoreSustainedDuration: sustained.Duration,
            ScoreSustainedCadenceCount: sustained.CadenceCount,
            KillSwitchEntriesEnabled: killSwitch?.EntriesEnabled ?? true,
            DailyLossLimitBreached: dailyLossLimitBreached,
            AllFeaturesWarmedUp: snapshot.IsWarmedUp,
            HasOpenDataGap: hasOpenGap,
            TradesSoFarToday: tradesToday,
            OpenConcurrentPositions: openPositions.Count,
            LastEntryTimeSameDirection: lastEntrySameDirection,
            IsExpiryDay: isExpiryDay,
            DailyProfitTargetReached: dailyProfitTargetReached,
            CurrentIvRank: snapshot.IvRankRaw,
            ConsecutiveLossesToday: consecutiveLossesToday,
            IvRankSessionCount: snapshot.IvRankSessionCount);

        var decision = EntryRuleEvaluator.Evaluate(context, _config);
        if (!decision.ShouldEnter)
        {
            // Only when the score itself would otherwise qualify -- every other rejection
            // (score too weak, not sustained yet) is the normal, expected majority of
            // cadences and would drown out the interesting case: score is strong but
            // something ELSE is silently blocking (live-caught 2026-09-07: this exact gap
            // was invisible for days because EntryRuleEvaluator's failures were never logged).
            if (Math.Abs(score) >= _config.Entry.MinAbsScore)
            {
                logger.LogInformation(
                    "Entry NOT taken despite qualifying score {Score:F1}: {Reasons}",
                    score, string.Join("; ", decision.FailedConditions));
            }

            return;
        }

        var side = decision.Direction == EntryDirection.Bullish ? OptionType.Call : OptionType.Put;
        var candidates = featureEngine.BuildStrikeCandidates(side, now);
        var selection = _strikeSelector.SelectBestCandidate(candidates, decision.Direction, _config);

        if (selection.Selected is not { } chosen || chosen.AskPrice is not { } ask)
        {
            logger.LogInformation(
                "Entry signal ({Direction}, score {Score:F1}) but no strike candidate passed selection: {Diagnostics}",
                decision.Direction, score, string.Join("; ", selection.Diagnostics));
            return;
        }

        var instrument = featureEngine.FindInstrument(chosen.Token);
        var tickSize = instrument?.TickSize ?? 0.05m;
        var quantity = _config.Capital.LotSize * _config.Capital.LotsPerTrade;
        var fill = PaperTradeSimulator.FillEntry(ask, tickSize, quantity, _config.Costs);

        // Audit finding F48 (2026-09-10): MaxConcurrentPositions (checked inside
        // EntryRuleEvaluator, above) is a position COUNT, not a capital check -- it assumed a
        // roughly fixed premium band made count-based gating a safe proxy for capital, which
        // breaks at the band's own upper edge (3 positions at the strike-selection band's max
        // premium can commit more than Capital.Total even though the count gate never fires).
        // Checked here, after the real strike and its real ask-plus-slippage fill price are
        // known, rather than as an early estimate against the band's max -- an estimate would
        // either reject trades that actually fit or admit ones that don't, at the band's edges.
        // Premium x quantity only (matching EntryPrice x Quantity below for the already-open
        // side) -- brokerage is a sunk transaction cost already spent regardless of position
        // size, not capital still tied up in a position, so it's deliberately excluded here.
        var newPositionValue = fill.FillPrice * quantity;
        var committedCapital = openPositions.Sum(p => p.EntryPrice * p.Quantity) + newPositionValue;
        if (committedCapital > _config.Capital.Total)
        {
            logger.LogInformation(
                "Entry signal ({Direction}, score {Score:F1}) but committing {NewPosition:F2} to {Symbol} would bring total open capital to {Committed:F2}, over Capital.Total {Total:F2} -- skipped",
                decision.Direction, score, newPositionValue, chosen.TradingSymbol, committedCapital, _config.Capital.Total);
            return;
        }

        var trade = new PaperTrade
        {
            InstrumentToken = chosen.Token,
            TradingSymbol = chosen.TradingSymbol,
            Direction = decision.Direction,
            EntryTime = now,
            EntryPrice = fill.FillPrice,
            Quantity = quantity,
            EntryScore = score,
            RulesetVersion = _config.RulesetVersion,
            ScoreWeightsVersion = snapshot.WeightSetVersion,
        };
        db.PaperTrades.Add(trade);
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation("Paper trade ENTRY: {Symbol} {Direction} @ {Price} (score {Score:F1})", trade.TradingSymbol, trade.Direction, trade.EntryPrice, score);
        await telegram.SendAsync(
            NotificationCategory.TradeEntry,
            $"NiftySignal ENTRY: {trade.TradingSymbol} {trade.Direction} @ {trade.EntryPrice} (score {score:+0.0;-0.0})",
            ct);
    }

    /// <summary>Returns true if the position is now fully closed (false for a partial book, which leaves it open).</summary>
    async Task<bool> EvaluateExitAsync(
        NiftySignalDbContext db, PaperTrade position, double score, DateTimeOffset now, LiveFeatureEngine featureEngine, CancellationToken ct)
    {
        if (!featureEngine.TryGetLatestQuote(position.InstrumentToken, out var ltp, out var bid))
        {
            return false; // no live quote yet this cadence -- re-evaluate next tick
        }

        var markPrice = bid ?? ltp;

        // Before the exit evaluation, not after: the tick that trips a stop or a partial book
        // is by definition an extreme, so it belongs in the excursion record. Cadences with no
        // live quote returned above, so those gaps are missing from MFE/MAE the same way they're
        // missing from the exit rules themselves.
        var excursionMoved = UpdateExcursions(position, markPrice);

        var state = new OpenPositionState(
            EntryTime: position.EntryTime,
            EntryPremium: position.EntryPrice,
            CurrentPremium: markPrice,
            HasPartiallyBooked: position.HasPartiallyBooked,
            Direction: position.Direction);

        var decision = ExitRuleEvaluator.Evaluate(state, score, now, _config);
        if (!decision.ShouldExit)
        {
            // Only write when a watermark actually moved -- otherwise this turns every cadence
            // into a DB write per open position for no new information.
            if (excursionMoved)
            {
                await db.SaveChangesAsync(ct);
            }

            return false;
        }

        var instrument = featureEngine.FindInstrument(position.InstrumentToken);
        var tickSize = instrument?.TickSize ?? 0.05m;
        // The quantity actually traded at entry, not a fresh recompute from current config --
        // a config change (LotsPerTrade, LotSize) must never retroactively change what an
        // already-open position's exit is worth.
        var totalQty = position.Quantity;

        if (decision.IsPartialExit)
        {
            var partialQty = (int)(totalQty * _config.Exit.PartialBookFraction);
            var fill = PaperTradeSimulator.FillExit(markPrice, tickSize, partialQty, _config.Costs);

            position.HasPartiallyBooked = true;
            position.PartialExitTime = now;
            position.PartialExitPrice = fill.FillPrice;
            position.PartialExitQuantity = partialQty;
            await db.SaveChangesAsync(ct);
            await dashboardPush.PushTradesChangedAsync(ct);

            logger.LogInformation("Paper trade PARTIAL BOOK: {Symbol} @ {Price}", position.TradingSymbol, fill.FillPrice);
            await telegram.SendAsync(NotificationCategory.PartialBook, $"NiftySignal PARTIAL BOOK: {position.TradingSymbol} @ {fill.FillPrice}", ct);
            return false;
        }

        // Final exit -- blend the partial leg (if one happened) with the remaining
        // quantity's leg. Entry/partial-exit net values are derived directly from the
        // prices already on the row (they were already filled -- re-running FillEntry on
        // them would apply slippage a second time), not re-simulated. partialQtyFinal reads
        // back the quantity actually filled at partial-book time (position.PartialExitQuantity)
        // rather than recomputing totalQty * PartialBookFraction against -- possibly by
        // now -- a different config, same "never let a config change retroactively alter an
        // already-open position's economics" rule totalQty itself already follows.
        var partialQtyFinal = position.HasPartiallyBooked ? position.PartialExitQuantity ?? 0 : 0;
        var remainingQty = totalQty - partialQtyFinal;

        var finalFill = PaperTradeSimulator.FillExit(markPrice, tickSize, remainingQty, _config.Costs);

        var entryNetValue = (position.EntryPrice * totalQty) + _config.Costs.BrokeragePerOrder;
        var partialGross = position.HasPartiallyBooked ? (position.PartialExitPrice ?? 0m) * partialQtyFinal : 0m;
        var partialNetValue = position.HasPartiallyBooked ? partialGross - _config.Costs.BrokeragePerOrder : 0m;

        var grossPnl = (partialGross + (finalFill.FillPrice * remainingQty)) - (position.EntryPrice * totalQty);
        var netPnl = (partialNetValue + finalFill.NetValue) - entryNetValue;

        position.ExitTime = now;
        position.ExitPrice = finalFill.FillPrice;
        position.ExitReason = decision.Reason;
        position.GrossPnl = grossPnl;
        position.NetPnl = netPnl;
        await db.SaveChangesAsync(ct);
        await dashboardPush.PushTradesChangedAsync(ct);

        logger.LogInformation(
            "Paper trade EXIT: {Symbol} {Reason} @ {Price} netPnl={NetPnl:F2}",
            position.TradingSymbol, decision.Reason, finalFill.FillPrice, netPnl);
        await telegram.SendAsync(
            NotificationCategory.Exit,
            $"NiftySignal EXIT: {position.TradingSymbol} {decision.Reason} @ {finalFill.FillPrice} P&L {netPnl:+0.00;-0.00}",
            ct);
        return true;
    }

    /// <summary>
    /// Rolls the trade's best/worst unrealised excursion forward with the latest mark, returning
    /// whether either watermark actually moved (so the caller can skip a pointless write).
    ///
    /// Percent of entry premium, matching ExitRuleEvaluator's own profit calculation, so MFE/MAE
    /// read on the same scale as StopLossPct and PartialBookAtProfitPct -- "ran to +40% before
    /// exiting at +30%" is directly answerable. Both are seeded on the first quoted cadence
    /// rather than at entry, since entry has no mark price of its own yet.
    /// </summary>
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
