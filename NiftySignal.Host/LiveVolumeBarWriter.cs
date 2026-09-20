using Microsoft.EntityFrameworkCore;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Host;

/// <summary>
/// Phase A of docs/LIVE_PARITY_PLAN.md: the first live writer into <see cref="VolumeBarDbContext"/>
/// -- until this class existed, only the offline populators (<see cref="NiftySignal.VolumeBarData"/>)
/// ever wrote to that database. Deliberately does NOT hook into <see cref="MarketDataIngestionWorker"/>'s
/// own tick loop or its <c>_engineSync</c> lock -- see <see cref="LiveVolumeBarPopulator"/>'s own doc
/// comment for why a periodic poll-and-replay-from-<see cref="NiftySignalDbContext.Ticks"/> was
/// chosen instead of a live in-process subscriber: it needs no coupling to that already-complex
/// worker, and it gets idempotency/restart-safety for free (every poll recomputes from the same
/// durable source of truth and only inserts bars not already persisted).
///
/// Explicitly OUT of scope here, per the Phase A task boundary: no score computation, no paper
/// trading, no reads of these tables by anything else yet (<see cref="NiftySignal.Scoring"/> and
/// <c>TradeSimulator.cs</c>'s dispatch logic are untouched) -- this class only WRITES the four bar
/// tables. Wiring live score computation on top of them is Phase C/D.
///
/// Phase G addition (docs/LIVE_PARITY_PLAN.md): owns the single <see cref="LiveOptionSeriesCache"/>
/// instance passed into the ATM/Max-Pain populators, letting them stop re-scanning each touched
/// option token's whole day-so-far tick history from scratch on every poll -- see that class's own
/// doc comment and the plan's Phase G section for the concrete measurement that motivated it.
/// </summary>
public sealed class LiveVolumeBarWriter(
    IServiceScopeFactory scopeFactory,
    LiveOptionSeriesCache seriesCache,
    ILogger<LiveVolumeBarWriter> logger) : BackgroundService
{
    /// <summary>The locked target metric's own bar threshold (docs/LIVE_PARITY_PLAN.md's "Locked target score" section) -- not a CLI-configurable default like the offline populator's, since this worker has exactly one live threshold to maintain, not a calibration sweep.</summary>
    public const long BarVolumeThreshold = 2600;

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketPreOpen = new(9, 15);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OutsideMarketPollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
            var nowTime = TimeOnly.FromDateTime(nowIst.DateTime);
            var isWeekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (!isWeekday || nowTime < MarketPreOpen || nowTime >= MarketHardClose)
            {
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
                var finalizeDay = nowTime >= new TimeOnly(15, 30);
                await WritePendingBarsAsync(asOfDate, DateTimeOffset.UtcNow, finalizeDay, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same "one bad cadence must never take down the rest of the day" resilience as
                // every loop in MarketDataIngestionWorker (audit findings F34/F49) -- a transient
                // DB hiccup here must not stop this worker from trying again on the next poll.
                logger.LogError(ex, "Live volume-bar write failed for {AsOfDate} -- continuing with the next poll", asOfDate);
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

    /// <summary>
    /// One poll's worth of work: advance the future bars first (they're the clock every option-side
    /// table joins against by BarIndex), then the three option-side tables against whatever future
    /// bars now exist. Each of the four calls is independently idempotent (see
    /// <see cref="LiveVolumeBarPopulator"/>'s own doc comment) -- a failure partway through this
    /// method (e.g. the future write succeeds but ATM throws) leaves already-written rows intact and
    /// simply gets retried whole on the next poll.
    /// </summary>
    public async Task WritePendingBarsAsync(DateOnly asOfDate, DateTimeOffset nowUtc, bool finalizeDay, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var destination = scope.ServiceProvider.GetRequiredService<VolumeBarDbContext>();

        var futureResult = await LiveVolumeBarPopulator.WriteNewBarsAsync(source, destination, asOfDate, BarVolumeThreshold, nowUtc, finalizeDay, ct);
        if (futureResult.Outcome == LiveVolumeBarWriteOutcome.Written)
        {
            logger.LogInformation("Live volume bars: wrote {Count} new future bar(s) for {AsOfDate}", futureResult.RowCount, asOfDate);
        }

        if (futureResult.Outcome == LiveVolumeBarWriteOutcome.NoTradableData)
        {
            // No future instrument resolved yet for today (e.g. before the 08:45-equivalent
            // universe-resolution job has run) or no ticks yet -- nothing downstream can compute
            // either; try again next poll rather than querying Instruments three more times for
            // nothing.
            return;
        }

        var atmResult = await LiveOptionAtmPopulator.WriteNewBarsAsync(source, destination, asOfDate, BarVolumeThreshold, ct, seriesCache);
        if (atmResult.Outcome == LiveOptionAtmWriteOutcome.Written)
        {
            logger.LogInformation("Live volume bars: wrote {Count} new OptionAtmBar(s) for {AsOfDate}", atmResult.RowCount, asOfDate);
        }

        var depthResult = await LiveOptionDepthPopulator.WriteNewBarsAsync(source, destination, asOfDate, BarVolumeThreshold, ct);
        if (depthResult.Outcome == LiveOptionDepthWriteOutcome.Written)
        {
            logger.LogInformation("Live volume bars: wrote {Count} new OptionDepthBar(s) for {AsOfDate}", depthResult.RowCount, asOfDate);
        }

        // Phase C addition (docs/LIVE_PARITY_PLAN.md): the locked OptionsScoreThreeWaySwitchMaxPainConfirmed
        // metric's Open leg reads OptionDepthBars at BandWidth=5 (ATM+/-2, its own adopted band -- see
        // TradeSimulator.cs's own optionDepthWideByBarIndex load), NOT LiveOptionDepthPopulator.DefaultBandWidth
        // (3, ATM+/-1) written above. Phase A only ever populated/verified the default band -- this second
        // call, at the wide band, is what makes TradingDaySession's own input actually complete; without it
        // every Open-leg bar in a live trading day would silently score null (no depth reading at all), a
        // Phase A gap this task's own parity replay caught before it ever reached the live entry-signal logic.
        var depthWideResult = await LiveOptionDepthPopulator.WriteNewBarsAsync(source, destination, asOfDate, BarVolumeThreshold, ct, bandWidth: TradingDaySession.DepthBandWidth);
        if (depthWideResult.Outcome == LiveOptionDepthWriteOutcome.Written)
        {
            logger.LogInformation("Live volume bars: wrote {Count} new OptionDepthBar(s) (BandWidth={BandWidth}) for {AsOfDate}", depthWideResult.RowCount, TradingDaySession.DepthBandWidth, asOfDate);
        }

        var maxPainResult = await LiveOptionMaxPainPopulator.WriteNewBarsAsync(source, destination, asOfDate, BarVolumeThreshold, ct, seriesCache);
        if (maxPainResult.Outcome == LiveOptionMaxPainWriteOutcome.Written)
        {
            logger.LogInformation("Live volume bars: wrote {Count} new OptionMaxPainBar(s) for {AsOfDate}", maxPainResult.RowCount, asOfDate);
        }
    }
}
