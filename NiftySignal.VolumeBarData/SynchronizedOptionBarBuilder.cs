using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (sections 6-12). Builds every available 0-DTE
/// CE/PE contract's bars using the EXACT same interval boundaries a <see cref="FutureEventBar"/>
/// series already produced -- the option market never sets its own boundaries (spec section 6).
/// In-memory only, rebuilt per run -- no new database table (same convention as
/// <see cref="FutureEventBarBuilder"/>).
/// </summary>
public static class SynchronizedOptionBarBuilder
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    /// <summary>
    /// Builds bars for every 0-DTE (ExpiryDate == asOfDate) CE and PE instrument, one
    /// <see cref="SynchronizedOptionEventBar"/> per <paramref name="futureBars"/> entry per
    /// contract.
    /// </summary>
    public static async Task<List<SynchronizedOptionEventBar>> BuildDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate, IReadOnlyList<FutureEventBar> futureBars, CancellationToken cancellationToken)
    {
        if (futureBars.Count == 0)
        {
            return [];
        }

        var chain = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option
                && i.ExpiryDate == asOfDate && i.Underlying == "NIFTY")
            .OrderBy(i => i.StrikePrice)
            .ThenBy(i => i.OptionType)
            .ToListAsync(cancellationToken);

        return await BuildDayForChainAsync(source, asOfDate, chain, futureBars, cancellationToken);
    }

    /// <summary>
    /// 2026-09-24, DTE-expansion experiment. Identical bar-construction logic to
    /// <see cref="BuildDayAsync"/> (same interval-bucketing, same missing/stale rules, same VWAP
    /// arithmetic) -- the only difference is the caller supplies <paramref name="chain"/>
    /// directly instead of this method querying it via the fixed `ExpiryDate == asOfDate`
    /// (0-DTE-only) filter. This lets a caller build synchronized bars for ANY expiry's chain
    /// (e.g. next-week options on a 0-DTE day) against the exact same futures event-bar clock,
    /// without duplicating or altering the bucketing logic itself.
    /// <see cref="BuildDayAsync"/> is unchanged and still the sole implementation used by every
    /// existing 0-DTE command.
    /// </summary>
    public static async Task<List<SynchronizedOptionEventBar>> BuildDayForChainAsync(
        NiftySignalDbContext source, DateOnly asOfDate, IReadOnlyList<Instrument> chain, IReadOnlyList<FutureEventBar> futureBars, CancellationToken cancellationToken)
    {
        if (futureBars.Count == 0)
        {
            return [];
        }

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();

        var bars = new List<SynchronizedOptionEventBar>(chain.Count * futureBars.Count);

        foreach (var instrument in chain)
        {
            var series = await OptionTickSeries.LoadAsync(source, instrument.Token, dayStart, dayEnd, cancellationToken);
            var entries = series.AllEntries;
            var dte = instrument.ExpiryDate!.Value.DayNumber - asOfDate.DayNumber;

            var cursor = 0;
            decimal? lastKnownPrice = null;
            DateTimeOffset? lastKnownTimestamp = null;

            foreach (var bar in futureBars)
            {
                decimal? open = null, high = null, low = null, close = null, averageLtp = null;
                double? vwap = null;
                DateTimeOffset? firstTick = null, lastTick = null;
                var tickCount = 0;
                long tradedVolume = 0;
                decimal priceSum = 0;
                double vwapPriceVolume = 0;
                double vwapVolume = 0;

                while (cursor < entries.Count && entries[cursor].Timestamp <= bar.EndTimestamp)
                {
                    if (entries[cursor].Timestamp < bar.StartTimestamp)
                    {
                        // Belongs to an EARLIER interval this contract had no real tick in --
                        // shouldn't happen given non-overlapping, strictly-increasing future bar
                        // boundaries, but guard rather than silently misattribute.
                        cursor++;
                        continue;
                    }

                    var entry = entries[cursor];
                    open ??= entry.LastPrice;
                    high = high is { } h ? Math.Max(h, entry.LastPrice) : entry.LastPrice;
                    low = low is { } l ? Math.Min(l, entry.LastPrice) : entry.LastPrice;
                    close = entry.LastPrice;
                    firstTick ??= entry.Timestamp;
                    lastTick = entry.Timestamp;
                    priceSum += entry.LastPrice;
                    tickCount++;

                    if (entry.VolumeDelta > 0)
                    {
                        vwapPriceVolume += (double)entry.LastPrice * entry.VolumeDelta;
                        vwapVolume += entry.VolumeDelta;
                        tradedVolume += entry.VolumeDelta;
                    }

                    lastKnownPrice = entry.LastPrice;
                    lastKnownTimestamp = entry.Timestamp;
                    cursor++;
                }

                var missingData = tickCount == 0;
                if (!missingData)
                {
                    averageLtp = priceSum / tickCount;
                    vwap = vwapVolume > 0 ? vwapPriceVolume / vwapVolume : (double?)null;
                }

                var isStale = missingData && lastKnownTimestamp is not null;
                var ageMs = isStale ? (bar.EndTimestamp - lastKnownTimestamp!.Value).TotalMilliseconds : (double?)null;

                bars.Add(new SynchronizedOptionEventBar(
                    bar.EventId, asOfDate, bar.StartTimestamp, bar.EndTimestamp,
                    instrument.StrikePrice!.Value, instrument.OptionType, dte, instrument.Token,
                    open, high, low, close, averageLtp, vwap, tickCount, tradedVolume,
                    firstTick, lastTick, missingData, isStale,
                    isStale ? lastKnownPrice : null, ageMs));
            }
        }

        return bars;
    }
}
