using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Host;

/// <summary>
/// Observation-only 09:30 adaptive-volume clock selector.
///
/// This service does not write volume bars, scores, signals, or trades. Once per trading day,
/// after 09:30 IST, it reads the already-persisted NIFTY front-future ticks from 09:15..09:30,
/// computes opening traded volume with the same monotonic cumulative-volume semantics used by the
/// research experiment, applies <see cref="OpeningVolumeProjectionV1"/>, and logs the frozen
/// base/rolling volume selected for that day.
///
/// The selected threshold is deliberately frozen for the session. This worker is a correctness
/// observer only; existing live/paper-trading pipelines remain untouched.
/// </summary>
public sealed class LiveAdaptiveVolumeObserver(
    IServiceScopeFactory scopeFactory,
    ILogger<LiveAdaptiveVolumeObserver> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly SelectionTime = new(9, 30);
    static readonly TimeOnly MarketClose = new(15, 30);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OutsidePollInterval = TimeSpan.FromMinutes(1);

    DateOnly? _selectedDate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowIst = DateTimeOffset.UtcNow.ToOffset(IstOffset);
            var day = DateOnly.FromDateTime(nowIst.Date);
            var time = TimeOnly.FromDateTime(nowIst.DateTime);
            var weekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (!weekday || time < SelectionTime || time >= MarketClose)
            {
                await DelaySafely(OutsidePollInterval, stoppingToken);
                continue;
            }

            if (_selectedDate != day)
            {
                var selected = await TrySelectAsync(day, stoppingToken);
                if (selected)
                {
                    _selectedDate = day;
                }
            }

            await DelaySafely(PollInterval, stoppingToken);
        }
    }

    public async Task<bool> TrySelectAsync(DateOnly day, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        var future = await db.Instruments
            .Where(i => i.AsOfDate == day
                && i.InstrumentType == InstrumentType.Future
                && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(ct);

        if (future is null)
        {
            logger.LogInformation("Adaptive volume observer: NIFTY future not resolved yet for {TradeDate}", day);
            return false;
        }

        var startUtc = new DateTimeOffset(day.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var cutoffUtc = new DateTimeOffset(day.ToDateTime(SelectionTime), IstOffset).ToUniversalTime();

        var volumes = await db.Ticks
            .Where(t => t.Token == future.Token
                && t.ExchangeTimestamp >= startUtc
                && t.ExchangeTimestamp < cutoffUtc)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .Select(t => t.Volume)
            .ToListAsync(ct);

        if (volumes.Count < 2)
        {
            logger.LogInformation(
                "Adaptive volume observer: insufficient NIFTY future ticks by 09:30 for {TradeDate}; rows={Rows}",
                day,
                volumes.Count);
            return false;
        }

        var openingVolume = ComputePositiveCumulativeVolume(volumes);
        var predictedDayVolume = OpeningVolumeProjectionV1.PredictFullDayVolume(openingVolume);
        var baseBarVolume = OpeningVolumeProjectionV1.SelectBaseBarVolume(openingVolume, future.LotSize);
        var rollingVolume = checked(baseBarVolume * 10L);

        logger.LogInformation(
            "Adaptive volume V1 selected for {TradeDate}: openingVolume0915To0930={OpeningVolume}; " +
            "predictedDayVolume={PredictedDayVolume:F0}; lotSize={LotSize}; baseBarVolume={BaseBarVolume}; " +
            "rolling10BarVolume={RollingVolume}; targetBarsPerDay={TargetBars}; modelIntercept={Intercept:F3}; modelSlope={Slope:F9}. " +
            "Threshold is frozen for the session; observation-only.",
            day,
            openingVolume,
            predictedDayVolume,
            future.LotSize,
            baseBarVolume,
            rollingVolume,
            OpeningVolumeProjectionV1.TargetBarsPerDay,
            OpeningVolumeProjectionV1.Intercept,
            OpeningVolumeProjectionV1.Slope);

        return true;
    }

    /// <summary>
    /// Mirrors the research cumulative-volume semantics: the first observed cumulative value is the
    /// baseline, and only new highs add traded volume. Feed resets/decreases do not create negative
    /// volume and do not get counted again until cumulative volume exceeds the prior maximum.
    /// </summary>
    public static long ComputePositiveCumulativeVolume(IReadOnlyList<long> cumulativeVolumes)
    {
        if (cumulativeVolumes.Count == 0)
        {
            return 0;
        }

        var maxSeen = cumulativeVolumes[0];
        long traded = 0;
        for (var i = 1; i < cumulativeVolumes.Count; i++)
        {
            var current = cumulativeVolumes[i];
            if (current > maxSeen)
            {
                traded = checked(traded + current - maxSeen);
                maxSeen = current;
            }
        }

        return traded;
    }

    static async Task DelaySafely(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal hosted-service shutdown.
        }
    }
}
