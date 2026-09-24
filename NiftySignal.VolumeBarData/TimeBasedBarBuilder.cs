using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, time-based crossover experiment (docs/Price_Based_Findings.md). Builds one trading
/// day's future bars on a FIXED WALL-CLOCK interval (default 1 minute), not a volume threshold --
/// the complement to every other bar builder in this project, all of which cut a new bar on
/// accumulated volume. Bars are built entirely IN-MEMORY (never persisted -- no new dataset, same
/// convention <see cref="DynamicTickVelocityBarBuilder"/> already established) and returned as
/// plain <see cref="VolumeBarRow"/> objects so they can be fed straight into the existing,
/// unmodified <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/> via its
/// <c>prebuiltBars</c> parameter -- "fast=2/slow=10" on 1-minute bars means exactly "2-minute fast
/// MA vs 10-minute slow MA," no new crossover/trading logic needed.
///
/// A bar with no ticks in its interval is skipped entirely (never fabricated) -- same "no
/// fabricated warm-up value" discipline every other bar/engine in this project follows.
/// </summary>
public static class TimeBasedBarBuilder
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    public static async Task<List<VolumeBarRow>> BuildDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate, CancellationToken cancellationToken, int intervalMinutes = 1)
    {
        // Same future-token resolution as every other bar builder in this project (VolumeBarPopulator,
        // DynamicTickVelocityBarBuilder) -- deliberately identical so results are comparable, not an
        // independently-diverging tick source.
        var future = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (future is null)
        {
            return [];
        }

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var rows = new List<VolumeBarRow>();
        var barIndex = 0;

        DateTimeOffset? bucketStart = null;
        DateTimeOffset? bucketEnd = null;
        decimal? open = null, high = null, low = null, close = null;
        long volumeStart = 0, volumeEnd = 0;
        var tickCount = 0;

        void FlushBucket()
        {
            if (bucketStart is null || open is null)
            {
                return; // no real ticks landed in this bucket -- never fabricate a bar for it.
            }

            rows.Add(new VolumeBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = barIndex++,
                // 2026-09-23: BarVolumeThreshold has no meaning for a time-based bar (there is no
                // qty threshold) -- reusing the field to carry the interval length in MINUTES, so a
                // caller inspecting rows can still see what produced them, without adding a new
                // field to a record every other bar builder in this project also uses.
                BarVolumeThreshold = intervalMinutes,
                StartTimestamp = bucketStart.Value,
                EndTimestamp = bucketEnd!.Value,
                DurationSeconds = (bucketEnd.Value - bucketStart.Value).TotalSeconds,
                OpenPrice = open.Value,
                HighPrice = high!.Value,
                LowPrice = low!.Value,
                ClosePrice = close!.Value,
                Volume = Math.Max(0, volumeEnd - volumeStart),
                TickCount = tickCount,
            });
        }

        await foreach (var tick in ticks.WithCancellation(cancellationToken))
        {
            // Which fixed interval bucket (relative to market open) does this tick belong to?
            var elapsed = tick.ExchangeTimestamp - dayStart;
            var bucketIndex = (long)(elapsed.TotalMinutes / intervalMinutes);
            var thisBucketStart = dayStart + TimeSpan.FromMinutes(bucketIndex * intervalMinutes);
            var thisBucketEnd = thisBucketStart + interval;

            if (bucketStart is not null && thisBucketStart != bucketStart)
            {
                FlushBucket();
                open = high = low = close = null;
                tickCount = 0;
            }

            bucketStart = thisBucketStart;
            bucketEnd = thisBucketEnd;
            open ??= tick.LastPrice;
            high = high is { } h ? Math.Max(h, tick.LastPrice) : tick.LastPrice;
            low = low is { } l ? Math.Min(l, tick.LastPrice) : tick.LastPrice;
            close = tick.LastPrice;
            if (tickCount == 0)
            {
                volumeStart = tick.Volume;
            }
            volumeEnd = tick.Volume;
            tickCount++;
        }

        FlushBucket();
        return rows;
    }
}
