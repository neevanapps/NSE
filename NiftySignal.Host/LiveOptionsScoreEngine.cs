using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Host;

/// <summary>
/// Phase C of docs/LIVE_PARITY_PLAN.md: computes the LIVE <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c>
/// score for every bar <see cref="LiveVolumeBarWriter"/> writes, applies the SAME entry/exit rule set
/// the offline backtest uses (<c>TradeSimulator.SimulateDayAsync</c>'s own percentile gate + Max Pain
/// confirmation + trading-hours window), and persists both the per-bar score
/// (<see cref="LiveOptionsScoreRow"/>) and any resulting entry SIGNAL (<see cref="LiveEntrySignalRow"/>)
/// to <see cref="VolumeBarDbContext"/>. Deliberately does NOT place a paper trade (no strike
/// selection, no fill price, no exit-price P&amp;L) -- that is Phase D, out of scope here; this class
/// stops at "here is the live score, and here is whether an entry would fire right now."
///
/// Runs as its OWN polling loop (not literally chained after <see cref="LiveVolumeBarWriter"/>'s own
/// <c>ExecuteAsync</c>) so a slow/failing score computation can never hold up bar writing, and vice
/// versa -- same "independent, resilient loops" shape <see cref="LiveVolumeBarWriter"/> itself
/// already established relative to <c>MarketDataIngestionWorker</c>. It cannot race ahead of bars not
/// yet written by construction, not by timing: every poll only advances past a future
/// <see cref="VolumeBarRow"/> once its matching <see cref="OptionAtmBarRow"/>/
/// <see cref="OptionDepthBarRow"/> (BandWidth=<see cref="TradingDaySession.DepthBandWidth"/>)/
/// <see cref="OptionMaxPainBarRow"/> rows ALL already exist for that BarIndex -- if any is still
/// missing (e.g. <see cref="LiveVolumeBarWriter"/> wrote the future bar this poll but hasn't reached
/// the option tables yet), this engine simply stops advancing at that BarIndex and retries the whole
/// gap on its next poll, exactly the same "a partial poll just gets retried" resilience
/// <see cref="LiveVolumeBarWriter"/>'s own four-writer sequence already relies on.
/// </summary>
public sealed class LiveOptionsScoreEngine(
    IServiceScopeFactory scopeFactory,
    ILogger<LiveOptionsScoreEngine> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketPreOpen = new(9, 15);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OutsideMarketPollInterval = TimeSpan.FromMinutes(1);

    // One TradingDaySession lives for the lifetime of one trading day, owned entirely by this
    // worker's own single-threaded poll loop (BackgroundService.ExecuteAsync never runs two
    // iterations concurrently) -- no lock needed, same "no shared mutable state across polls other
    // than what this loop itself owns" shape LiveVolumeBarWriter's stateless-per-poll design uses,
    // just with one piece of real in-process state (the trackers) that HAS to live across polls by
    // construction (see TradingDaySession's own doc comment on why RebuildAsync exists instead).
    TradingDaySession? _session;

    /// <summary>Phase G logging (docs/LIVE_PARITY_PLAN.md): edge-triggered guard for the "stalled waiting on option-side bars" warning below -- null when nothing is currently stalled, else the BarIndex already logged for, so a persisting stall doesn't spam a new line every 10s poll.</summary>
    int? _lastStallLoggedBarIndex;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
            var nowTime = TimeOnly.FromDateTime(nowIst.DateTime);
            var isWeekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (!isWeekday || nowTime < MarketPreOpen || nowTime >= MarketHardClose)
            {
                // Day boundary: if a session is still open from earlier today (e.g. hard close hit
                // before a force-close bar happened to land, or the day just ended), flush it here so
                // no signal is ever left dangling open across a day boundary -- see
                // TradingDaySession.FlushEndOfDay's own doc comment. Discard the in-memory session
                // either way; tomorrow's first poll starts a fresh one via RebuildAsync (which will
                // correctly see nothing scored yet for the new date and return a brand-new session).
                if (_session is not null)
                {
                    await FlushAndDiscardSessionAsync(stoppingToken);
                }

                try
                {
                    await Task.Delay(OutsideMarketPollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var asOfDate = DateOnly.FromDateTime(nowIst.Date);
            try
            {
                await ProcessPendingBarsAsync(asOfDate, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same "one bad poll must never take down the rest of the day" resilience as
                // LiveVolumeBarWriter's own loop (and MarketDataIngestionWorker's F34/F49 precedent).
                logger.LogError(ex, "Live options-score computation failed for {AsOfDate} -- continuing with the next poll", asOfDate);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task FlushAndDiscardSessionAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<VolumeBarDbContext>();
            var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            await FlushOpenSignalIfAnyAsync(db, source, _session!, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush end-of-day open signal for {AsOfDate} -- will retry on next day-boundary transition if the session is still held", _session!.AsOfDate);
            return;
        }

        _session = null;
    }

    /// <summary>
    /// One poll's worth of work, also the unit <see cref="ReplayLiveScoreCommand"/> drives directly
    /// for the Phase C parity replay (same call, same guarantees, whether driven by the real clock or
    /// a replay harness feeding historical bars one poll-checkpoint at a time).
    /// </summary>
    public async Task ProcessPendingBarsAsync(DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VolumeBarDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        if (_session is null || _session.AsOfDate != asOfDate)
        {
            if (_session is not null)
            {
                // Defensive: a day rolled over without the day-boundary branch above catching it
                // (e.g. this method called directly by a replay harness across two dates back to
                // back) -- flush the old day's open signal before abandoning its session, same as
                // the real day-boundary path does.
                await FlushOpenSignalIfAnyAsync(db, source, _session, _session.LastProcessedBarIndex >= 0 ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow, ct);
            }

            _session = await TradingDaySession.RebuildAsync(db, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, ct);
            logger.LogInformation("LiveOptionsScoreEngine: session for {AsOfDate} ready, resuming at BarIndex {NextIndex}", asOfDate, _session.LastProcessedBarIndex + 1);
        }

        var session = _session;
        var nextIndex = session.LastProcessedBarIndex + 1;

        var candidateBars = await db.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && b.BarIndex >= nextIndex)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(ct);
        if (candidateBars.Count == 0)
        {
            return;
        }

        var maxCandidateIndex = candidateBars[^1].BarIndex;
        var atmByIndex = await db.OptionAtmBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && b.BarIndex >= nextIndex && b.BarIndex <= maxCandidateIndex)
            .ToDictionaryAsync(b => b.BarIndex, ct);
        var depthByIndex = await db.OptionDepthBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && b.BandWidth == TradingDaySession.DepthBandWidth && b.BarIndex >= nextIndex && b.BarIndex <= maxCandidateIndex)
            .ToDictionaryAsync(b => b.BarIndex, ct);
        var maxPainByIndex = await db.OptionMaxPainBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && b.BarIndex >= nextIndex && b.BarIndex <= maxCandidateIndex)
            .ToDictionaryAsync(b => b.BarIndex, ct);

        var scoreRows = new List<LiveOptionsScoreRow>();
        var scoredCount = 0;
        foreach (var bar in candidateBars)
        {
            // Only score a bar once ALL THREE option-side tables have a row for it -- see this
            // class's own doc comment on why this is what makes racing ahead of LiveVolumeBarWriter
            // structurally impossible rather than merely unlikely. Stop at the first gap (don't skip
            // over it to a later bar that happens to be ready -- BarIndex order must stay strict for
            // TradingDaySession.ProcessBar); the gap gets retried whole on the next poll.
            if (!atmByIndex.TryGetValue(bar.BarIndex, out var atmBar)
                || !depthByIndex.TryGetValue(bar.BarIndex, out var depthBar)
                || !maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPainBar))
            {
                // Phase G logging (docs/LIVE_PARITY_PLAN.md): this is the exact "why did scoring stall"
                // gap a verify-parity FAIL diagnosis needs to see without re-running replay tooling --
                // structurally impossible to race ahead of LiveVolumeBarWriter (see this class's own doc
                // comment), but if that writer is itself stuck/slow, THIS is where it becomes visible.
                // Edge-triggered (only logs once per BarIndex actually reached, not every 10s poll it
                // keeps stalling at) -- same throttling discipline MarketDataIngestionWorker's own
                // _warmUpBlockedLogged flag already established for an analogous "don't spam every
                // cadence while blocked" case.
                if (bar.BarIndex != _lastStallLoggedBarIndex)
                {
                    _lastStallLoggedBarIndex = bar.BarIndex;
                    logger.LogWarning(
                        "Live options score: stalled at BarIndex {BarIndex} for {AsOfDate} -- waiting on OptionAtmBar={HasAtm}, OptionDepthBar(BandWidth={BandWidth})={HasDepth}, OptionMaxPainBar={HasMaxPain} (LiveVolumeBarWriter hasn't written all three for this bar yet; will retry next poll).",
                        bar.BarIndex, asOfDate, atmByIndex.ContainsKey(bar.BarIndex), TradingDaySession.DepthBandWidth, depthByIndex.ContainsKey(bar.BarIndex), maxPainByIndex.ContainsKey(bar.BarIndex));
                }

                break;
            }

            var result = session.ProcessBar(bar, atmBar, depthBar, maxPainBar);
            scoreRows.Add(result.ScoreRow);
            scoredCount++;

            // Phase G logging (docs/LIVE_PARITY_PLAN.md): one line per SCORED bar (not just the last
            // one in a poll's batch) with every field a verify-parity FAIL diagnosis needs -- which bar,
            // which leg, what value, both the raw and gate-relevant readings. Debug, not Information --
            // ~600-900 of these/day (per Phase C's own measured bar counts) is reasonable at Debug but
            // would be noisy at the project's default Information floor; ENTRY/EXIT/trade lines below
            // stay at Information since those are comparatively rare and always decision-relevant.
            logger.LogDebug(
                "Bar scored: {AsOfDate} BarIndex={BarIndex} Leg={SessionLeg} EndTimestamp={EndTimestamp:O} RawScore={RawScore} ScaledScore={ScaledScore} Percentile={Percentile} MaxPainConfirmScore={MaxPainConfirmScore} MaxPainConfirmPasses={MaxPainConfirmPasses}",
                asOfDate, result.ScoreRow.BarIndex, result.ScoreRow.SessionLeg, result.ScoreRow.EndTimestamp,
                result.ScoreRow.RawScore, result.ScoreRow.ScaledScore, result.ScoreRow.Percentile,
                result.ScoreRow.MaxPainConfirmScore, result.ScoreRow.MaxPainConfirmPasses);

            if (result.Opened is { } opened)
            {
                db.LiveEntrySignals.Add(new LiveEntrySignalRow
                {
                    AsOfDate = asOfDate,
                    BarVolumeThreshold = LiveVolumeBarWriter.BarVolumeThreshold,
                    Side = opened.Side,
                    EntryBarIndex = opened.EntryBarIndex,
                    EntryTimestamp = opened.EntryTimestamp,
                    EntryScore = opened.EntryScore,
                    EntryPercentile = opened.EntryPercentile,
                });

                // Phase G logging: the decision itself, independent of whether LivePaperTradeExecutor
                // goes on to actually find a tradeable strike/price for it -- so "a signal fired" is
                // always visible even when the paper trade below is declined (kill switch, no chain,
                // no priced tick).
                logger.LogInformation(
                    "ENTRY SIGNAL: {AsOfDate} BarIndex={BarIndex} Side={Side} EntryScore={EntryScore} EntryPercentile={EntryPercentile} MaxPainConfirmScore={MaxPainConfirmScore}",
                    asOfDate, opened.EntryBarIndex, opened.Side, opened.EntryScore, opened.EntryPercentile, result.ScoreRow.MaxPainConfirmScore);

                // Phase D of docs/LIVE_PARITY_PLAN.md: strike selection + live fill price at decision
                // time, right where the signal fired -- decisionTimestamp is real wall-clock "now"
                // (or the replay harness's own simulated poll-checkpoint "now"), deliberately NOT
                // bar.EndTimestamp -- see LivePaperTradeRow's own doc comment on the fill-price policy.
                await LivePaperTradeExecutor.OpenAsync(
                    source, db, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, opened, bar.ClosePrice,
                    DateTimeOffset.UtcNow, msg => logger.LogInformation("{Message}", msg), ct);
            }

            if (result.Closed is { } closed)
            {
                // Phase G logging: same reasoning as the ENTRY SIGNAL line above -- the exit DECISION
                // (score/percentile at the moment it fired, and why: ScoreInvalidated vs TimeCutoff)
                // logged here, independent of LivePaperTradeExecutor.CloseAsync's own fill-price line
                // below.
                logger.LogInformation(
                    "EXIT SIGNAL: {AsOfDate} EntryBarIndex={EntryBarIndex} ExitBarIndex={ExitBarIndex} ExitReason={ExitReason} ScaledScoreAtExit={ScaledScore} PercentileAtExit={Percentile}",
                    asOfDate, closed.EntryBarIndex, closed.ExitBarIndex, closed.ExitReason, result.ScoreRow.ScaledScore, result.ScoreRow.Percentile);

                await CloseSignalRowAsync(db, asOfDate, closed, ct);
                await LivePaperTradeExecutor.CloseAsync(
                    db, source, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, closed,
                    DateTimeOffset.UtcNow, msg => logger.LogInformation("{Message}", msg), ct);
            }
        }

        if (scoredCount == 0)
        {
            return;
        }

        // A bar scored cleanly this poll -- clear the stall guard so a LATER, genuinely new stall (a
        // different BarIndex) logs again rather than staying silenced by an old one.
        _lastStallLoggedBarIndex = null;

        db.LiveOptionsScoreBars.AddRange(scoreRows);
        await db.SaveChangesAsync(ct);

        var lastScored = scoreRows[^1];
        logger.LogInformation(
            "Live options score: scored {Count} bar(s) for {AsOfDate}, up to BarIndex {LastIndex} (ScaledScore={ScaledScore}, Percentile={Percentile})",
            scoredCount, asOfDate, lastScored.BarIndex, lastScored.ScaledScore, lastScored.Percentile);
    }

    /// <summary>
    /// Marks the matching open <see cref="LiveEntrySignalRow"/> closed. Looks first among the rows
    /// just added to <paramref name="db"/>'s own change tracker THIS poll (an open-then-close inside
    /// the same batch of pending bars is entirely possible on a slow poll or a replay run), then
    /// falls back to a DB query for a signal opened on an earlier poll.
    /// </summary>
    static async Task CloseSignalRowAsync(VolumeBarDbContext db, DateOnly asOfDate, LiveSignalClosed closed, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<LiveEntrySignalRow>()
            .Select(e => e.Entity)
            .FirstOrDefault(r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && r.EntryBarIndex == closed.EntryBarIndex);

        var row = tracked ?? await db.LiveEntrySignals.FirstOrDefaultAsync(
            r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && r.EntryBarIndex == closed.EntryBarIndex, ct);

        if (row is null)
        {
            // Should not happen -- TradingDaySession never closes a signal it didn't itself open,
            // and RebuildAsync replays the same ProcessBar calls that would have inserted it. Not
            // thrown (a missing row here shouldn't take down the whole poll), but logged loudly
            // would be appropriate in a real deployment; left as a defensive no-op for this phase.
            return;
        }

        row.ExitBarIndex = closed.ExitBarIndex;
        row.ExitTimestamp = closed.ExitTimestamp;
        row.ExitReason = closed.ExitReason;
    }

    async Task FlushOpenSignalIfAnyAsync(VolumeBarDbContext db, NiftySignalDbContext source, TradingDaySession session, DateTimeOffset asOfTimestamp, CancellationToken ct)
    {
        if (session.FlushEndOfDay(asOfTimestamp) is not { } closed)
        {
            return;
        }

        await CloseSignalRowAsync(db, session.AsOfDate, closed, ct);
        await LivePaperTradeExecutor.CloseAsync(
            db, source, session.AsOfDate, session.BarVolumeThreshold, closed,
            asOfTimestamp, msg => logger.LogInformation("{Message}", msg), ct);
        await db.SaveChangesAsync(ct);
    }
}
