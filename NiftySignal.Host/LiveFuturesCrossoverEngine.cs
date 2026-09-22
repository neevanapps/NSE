using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Host;

/// <summary>
/// Futures-crossover live-wiring task (2026-09-21, docs/LIVE_PARITY_PLAN.md's "Futures crossover:
/// wired live" section): computes the LIVE locked 8-fast/40-slow/5-point-threshold SMA-crossover
/// strategy on <c>SessionGatedDepthDurationConfirmed</c>'s own FuturesScore for every bar
/// <see cref="LiveVolumeBarWriter"/> writes, applies the same entry/exit rule set the offline
/// backtest uses (<c>TradeSimulator.SimulateCrossoverDayAsync</c>'s own futures branch), and persists
/// the per-bar diagnostics (<see cref="LiveFuturesCrossoverScoreRow"/>), any resulting entry SIGNAL
/// (<see cref="LiveEntrySignalRow"/>, <see cref="LiveVolumeBarStrategyId.FuturesCrossover"/>), and the
/// resulting paper trade (<see cref="LivePaperTradeRow"/>, via the SAME <see cref="LivePaperTradeExecutor"/>
/// the options engine calls) -- mirroring <see cref="LiveOptionsScoreEngine"/>'s own polling/day-
/// boundary shape exactly, but simpler: this strategy's own score
/// (<see cref="NiftySignal.Scoring.FuturesSessionGatedScoreCalculator"/>) reads only
/// <see cref="VolumeBarRow"/>'s own futures-side columns, never the option-side tables
/// (<c>OptionAtmBarRow</c>/<c>OptionDepthBarRow</c>/<c>OptionMaxPainBarRow</c>) the options engine has
/// to wait on -- so this engine never stalls behind Phase A's own option-writer sequence, only behind
/// <see cref="LiveVolumeBarWriter"/> itself (which already writes <see cref="VolumeBarRow"/> before
/// any option-side table for the same bar).
///
/// Runs as its OWN polling loop, entirely independent of <see cref="LiveOptionsScoreEngine"/> -- same
/// "independent, resilient loops" shape that engine itself documents relative to
/// <see cref="LiveVolumeBarWriter"/>. The two engines' own <see cref="LiveFuturesCrossoverSession"/>/
/// <c>TradingDaySession</c> instances are entirely separate objects with no shared mutable state, and
/// their entry/paper-trade rows are kept apart on the shared <see cref="LiveEntrySignalRow"/>/
/// <see cref="LivePaperTradeRow"/> tables purely via the <see cref="LiveVolumeBarStrategyId"/>
/// discriminator (see <see cref="LivePaperTradeRow.Strategy"/>'s own doc comment) -- each strategy's
/// own "one position at a time" rule never sees or blocks the other's.
/// </summary>
public sealed class LiveFuturesCrossoverEngine(
    IServiceScopeFactory scopeFactory,
    ITelegramNotifier telegram,
    ILogger<LiveFuturesCrossoverEngine> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketPreOpen = new(9, 15);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OutsideMarketPollInterval = TimeSpan.FromMinutes(1);

    // One LiveFuturesCrossoverSession lives for the lifetime of one trading day, owned entirely by
    // this worker's own single-threaded poll loop -- same "no lock needed" reasoning
    // LiveOptionsScoreEngine's own _session field documents.
    LiveFuturesCrossoverSession? _session;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
            var nowTime = TimeOnly.FromDateTime(nowIst.DateTime);
            var isWeekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (!isWeekday || nowTime < MarketPreOpen || nowTime >= MarketHardClose)
            {
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
            await PollOnceAsync(asOfDate, stoppingToken);

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

    /// <summary>Same "log and continue to next poll, alert on Telegram" resilience as <see cref="LiveOptionsScoreEngine.PollOnceAsync"/> -- extracted so it can be exercised directly by a test/replay harness without real wall-clock market-hours gating.</summary>
    public async Task PollOnceAsync(DateOnly asOfDate, CancellationToken ct)
    {
        try
        {
            await ProcessPendingBarsAsync(asOfDate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Live futures-crossover computation failed for {AsOfDate} -- continuing with the next poll", asOfDate);

            await telegram.SendAsync(
                NotificationCategory.LivePipelineError,
                $"NiftySignal LiveFuturesCrossoverEngine: poll failed for {asOfDate:yyyy-MM-dd} -- {ex.GetType().Name}: {ex.Message}",
                ct);
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
            logger.LogError(ex, "Failed to flush end-of-day open futures-crossover signal for {AsOfDate} -- will retry on next day-boundary transition if the session is still held", _session!.AsOfDate);

            await telegram.SendAsync(
                NotificationCategory.LivePipelineError,
                $"NiftySignal LiveFuturesCrossoverEngine: end-of-day flush failed for {_session!.AsOfDate:yyyy-MM-dd} -- {ex.GetType().Name}: {ex.Message}",
                ct);
            return;
        }

        _session = null;
    }

    /// <summary>One poll's worth of work, also the unit a replay harness drives directly for the parity proof -- same role <see cref="LiveOptionsScoreEngine.ProcessPendingBarsAsync"/> plays for the options side.</summary>
    public async Task ProcessPendingBarsAsync(DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VolumeBarDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        if (_session is null || _session.AsOfDate != asOfDate)
        {
            if (_session is not null)
            {
                await FlushOpenSignalIfAnyAsync(db, source, _session, DateTimeOffset.UtcNow, ct);
            }

            _session = await LiveFuturesCrossoverSession.RebuildAsync(db, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, ct);
            logger.LogInformation("LiveFuturesCrossoverEngine: session for {AsOfDate} ready, resuming at BarIndex {NextIndex}", asOfDate, _session.LastProcessedBarIndex + 1);
        }

        var session = _session;
        var nextIndex = session.LastProcessedBarIndex + 1;

        // Unlike LiveOptionsScoreEngine, this strategy's own score needs only VolumeBarRow itself --
        // no option-side table to wait on -- so every bar LiveVolumeBarWriter has written at/after
        // nextIndex is immediately scoreable.
        var candidateBars = await db.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && b.BarIndex >= nextIndex)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(ct);
        if (candidateBars.Count == 0)
        {
            return;
        }

        var scoreRows = new List<LiveFuturesCrossoverScoreRow>();
        foreach (var bar in candidateBars)
        {
            var result = session.ProcessBar(bar);
            scoreRows.Add(result.ScoreRow);

            logger.LogDebug(
                "Futures-crossover bar scored: {AsOfDate} BarIndex={BarIndex} EndTimestamp={EndTimestamp:O} ScaledScore={ScaledScore} FastMa={FastMa} SlowMa={SlowMa} Diff={Diff} CrossedUp={CrossedUp} CrossedDown={CrossedDown}",
                asOfDate, result.ScoreRow.BarIndex, result.ScoreRow.EndTimestamp, result.ScoreRow.ScaledScore,
                result.ScoreRow.FastMa, result.ScoreRow.SlowMa, result.ScoreRow.Diff, result.ScoreRow.CrossedUp, result.ScoreRow.CrossedDown);

            if (result.Opened is { } opened)
            {
                db.LiveEntrySignals.Add(new LiveEntrySignalRow
                {
                    AsOfDate = asOfDate,
                    BarVolumeThreshold = LiveVolumeBarWriter.BarVolumeThreshold,
                    Strategy = LiveVolumeBarStrategyId.FuturesCrossover,
                    Side = opened.Side,
                    EntryBarIndex = opened.EntryBarIndex,
                    EntryTimestamp = opened.EntryTimestamp,
                    EntryScore = opened.EntryScore,
                    EntryPercentile = null,
                });

                logger.LogInformation(
                    "FUTURES CROSSOVER ENTRY SIGNAL: {AsOfDate} BarIndex={BarIndex} Side={Side} Diff(Gap)={EntryScore}",
                    asOfDate, opened.EntryBarIndex, opened.Side, opened.EntryScore);

                await LivePaperTradeExecutor.OpenAsync(
                    source, db, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, opened, bar.ClosePrice,
                    DateTimeOffset.UtcNow, msg => logger.LogInformation("{Message}", msg), ct,
                    LiveVolumeBarStrategyId.FuturesCrossover);
            }

            if (result.Closed is { } closed)
            {
                logger.LogInformation(
                    "FUTURES CROSSOVER EXIT SIGNAL: {AsOfDate} EntryBarIndex={EntryBarIndex} ExitBarIndex={ExitBarIndex} ExitReason={ExitReason}",
                    asOfDate, closed.EntryBarIndex, closed.ExitBarIndex, closed.ExitReason);

                await CloseSignalRowAsync(db, asOfDate, closed, ct);
                await LivePaperTradeExecutor.CloseAsync(
                    db, source, asOfDate, LiveVolumeBarWriter.BarVolumeThreshold, closed,
                    DateTimeOffset.UtcNow, msg => logger.LogInformation("{Message}", msg), ct,
                    LiveVolumeBarStrategyId.FuturesCrossover);
            }
        }

        db.LiveFuturesCrossoverScoreBars.AddRange(scoreRows);
        await db.SaveChangesAsync(ct);

        var lastScored = scoreRows[^1];
        logger.LogInformation(
            "Live futures-crossover: scored {Count} bar(s) for {AsOfDate}, up to BarIndex {LastIndex} (ScaledScore={ScaledScore}, Diff={Diff})",
            scoreRows.Count, asOfDate, lastScored.BarIndex, lastScored.ScaledScore, lastScored.Diff);
    }

    static async Task CloseSignalRowAsync(VolumeBarDbContext db, DateOnly asOfDate, LiveSignalClosed closed, CancellationToken ct)
    {
        var tracked = db.ChangeTracker.Entries<LiveEntrySignalRow>()
            .Select(e => e.Entity)
            .FirstOrDefault(r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == closed.EntryBarIndex);

        var row = tracked ?? await db.LiveEntrySignals.FirstOrDefaultAsync(
            r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == LiveVolumeBarWriter.BarVolumeThreshold && r.Strategy == LiveVolumeBarStrategyId.FuturesCrossover && r.EntryBarIndex == closed.EntryBarIndex, ct);

        if (row is null)
        {
            return;
        }

        row.ExitBarIndex = closed.ExitBarIndex;
        row.ExitTimestamp = closed.ExitTimestamp;
        row.ExitReason = closed.ExitReason;
    }

    async Task FlushOpenSignalIfAnyAsync(VolumeBarDbContext db, NiftySignalDbContext source, LiveFuturesCrossoverSession session, DateTimeOffset asOfTimestamp, CancellationToken ct)
    {
        if (session.FlushEndOfDay(asOfTimestamp) is not { } closed)
        {
            return;
        }

        await CloseSignalRowAsync(db, session.AsOfDate, closed, ct);
        await LivePaperTradeExecutor.CloseAsync(
            db, source, session.AsOfDate, session.BarVolumeThreshold, closed,
            asOfTimestamp, msg => logger.LogInformation("{Message}", msg), ct,
            LiveVolumeBarStrategyId.FuturesCrossover);
        await db.SaveChangesAsync(ct);
    }
}
