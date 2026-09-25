using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, per-strike time-cadence trading simulation (docs/Price_Based_Findings.md), step 2 --
/// the trading logic built on top of <see cref="PutCadenceExporter"/>'s raw cadence export. NO
/// future/underlying price anywhere in this path (same discipline the raw export already
/// established) -- there is no ATM concept here at all, since nothing anchors "nearest to the
/// future" without a future. Instead, every strike in the chain (for the requested side) gets its
/// own independent 15-second (default) cadence and its own independent
/// <see cref="PriceCrossoverEngine"/> tracking the fast/slow SMA of that strike's OWN AvgPrice
/// series -- reused, not reimplemented, same engine the volume-bar crossover work already used.
///
/// Only ONE position open at a time (same invariant every other simulator in this project follows),
/// scanning strikes in ascending order each bucket for the first one whose CURRENT premium falls in
/// [minEntryPrice, maxEntryPrice] AND whose own signal just crossed up. Entry/exit FILLS use that
/// bucket's real last-traded price (not the smoothed AvgPrice) -- AvgPrice drives the signal only.
/// </summary>
public static class PerStrikeCadenceSimulator
{
    // DEFERRED (audit finding F65) — historical LTP fills predate decisions and missing cadences stretch
    // MA windows; diagnostic only. Strict timing research is isolated in ReversalResearch. See docs/REVIEW_FINDINGS.md.
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    public sealed record CadenceTrade(
        DateOnly AsOfDate, OptionType Side, decimal StrikePrice, string Token,
        DateTimeOffset EntryTime, decimal EntryPrice, DateTimeOffset ExitTime, decimal ExitPrice, string ExitReason)
    {
        public decimal NetPnlPoints => ExitPrice - EntryPrice;
        public decimal NetPnlPercent => EntryPrice != 0 ? NetPnlPoints / EntryPrice * 100m : 0m;
    }

    public static async Task<List<CadenceTrade>> SimulateDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate, OptionType side, CancellationToken cancellationToken,
        int intervalSeconds = 15, int fastBars = 2, int slowBars = 10, double thresholdFraction = 0.0,
        decimal minEntryPrice = 100m, decimal maxEntryPrice = 150m)
    {
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .OrderBy(i => i.StrikePrice)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return [];
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
        var bucketCount = (int)Math.Ceiling((dayEnd - dayStart).TotalSeconds / intervalSeconds);

        // Per-strike state: its own price series, its own PriceCrossoverEngine (reused, not
        // reimplemented), and a rolling tick-index cursor for computing this strike's own AvgPrice
        // per bucket (same bucketing approach PutCadenceExporter already uses).
        var strikeStates = new List<(Domain.Entities.Instrument Instrument, OptionPriceSeries Series, PriceCrossoverEngine Engine, int TickCursor)>();
        foreach (var instrument in chain)
        {
            var series = await OptionPriceSeries.LoadAsync(source, instrument.Token, dayStart, dayEnd, cancellationToken);
            strikeStates.Add((instrument, series, new PriceCrossoverEngine(fastBars, slowBars), 0));
        }

        var trades = new List<CadenceTrade>();
        (Domain.Entities.Instrument Instrument, DateTimeOffset EntryTime, decimal EntryPrice)? open = null;

        for (var b = 0; b < bucketCount; b++)
        {
            var bucketStart = dayStart + TimeSpan.FromSeconds(b * (double)intervalSeconds);
            var bucketEnd = dayStart + TimeSpan.FromSeconds((b + 1) * (double)intervalSeconds);
            var isLastBucket = b == bucketCount - 1;

            for (var idx = 0; idx < strikeStates.Count; idx++)
            {
                var (instrument, series, engine, tickCursor) = strikeStates[idx];
                var allPrices = series.AllPrices;

                var sum = 0m;
                var count = 0;
                while (tickCursor < allPrices.Count && allPrices[tickCursor].Timestamp <= bucketEnd)
                {
                    if (allPrices[tickCursor].Timestamp > bucketStart)
                    {
                        sum += allPrices[tickCursor].Price;
                        count++;
                    }
                    tickCursor++;
                }
                strikeStates[idx] = (instrument, series, engine, tickCursor);

                var avgPrice = count > 0 ? (double)(sum / count) : (double?)null;
                var step = engine.Observe(avgPrice, thresholdFraction);
                var lastPrice = series.PriceAtOrBefore(bucketEnd);

                if (open is { } position && position.Instrument.Token == instrument.Token)
                {
                    // The currently-open position's OWN strike: check for its own reversal, or
                    // end of day. lastPrice ?? position.EntryPrice mirrors the volume-bar
                    // simulator's own "no fresh print yet, carry the entry price" fallback.
                    if (step.CrossedDown || isLastBucket)
                    {
                        var exitPrice = lastPrice ?? position.EntryPrice;
                        var exitReason = isLastBucket ? "EndOfData" : "CrossoverReversed";
                        trades.Add(new CadenceTrade(asOfDate, side, position.Instrument.StrikePrice!.Value, position.Instrument.Token,
                            position.EntryTime, position.EntryPrice, bucketEnd, exitPrice, exitReason));
                        open = null;
                    }
                }
                else if (open is null && !isLastBucket && step.CrossedUp
                    && lastPrice is { } ep && ep >= minEntryPrice && ep <= maxEntryPrice)
                {
                    open = (instrument, bucketEnd, ep);
                    // Audit finding F63: every token must observe EVERY cadence, including
                    // those after the purchased token in strike order. `open` already prevents
                    // a second entry; breaking here silently skipped other tokens' readings.
                }
            }
        }

        return trades;
    }
}
