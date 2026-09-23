using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 SMA-vs-EMA option-premium task, Parts 9-15 -- see
/// docs/VOLUME_BAR_FINDINGS.md's dated section). Deliberate near-mirror of
/// <see cref="MomentumRelationshipAnalyzer.CollectDayAsync"/> (same chain-loading/ATM-selection/
/// forward-return-labeling logic, same audit finding F62 NIFTY-filter discipline) with ONE
/// substitution: <see cref="MaSpreadEngine"/> (supports SMA/EMA per leg) in place of
/// <see cref="PriceCrossoverEngine"/> (SMA/SMA only). Duplicated rather than parameterized into the
/// existing class so <see cref="MomentumRelationshipAnalyzer"/>'s own already-reviewed behavior
/// (and the 2026-09-22 momentum-relationship findings already recorded against it) stays untouched.
/// Every forward-return field is a forward-looking RESEARCH LABEL, never a tradeable signal. Never
/// opens a trade.
/// </summary>
public static class MaSpreadRelationshipAnalyzer
{
    public sealed record Sample(
        DateOnly Date, DateTimeOffset Timestamp, int BarIndex, OptionType Side, double Spread,
        double? NiftyFwd5, double? NiftyFwd10, double? NiftyFwd20,
        double? OptionFwd5, double? OptionFwd10, double? OptionFwd20);

    public static async Task<List<Sample>> CollectDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        OptionType side, int fastBars, int slowBars, MaType fastType, MaType slowType, int? bandWidth,
        CancellationToken cancellationToken, Dictionary<(DateOnly, string), OptionPriceSeries>? sharedPriceCache = null)
    {
        var bars = await volumeBarDb.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (bars.Count == 0)
        {
            return [];
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .OrderBy(i => i.StrikePrice).ThenBy(i => i.OptionType)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return [];
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var strikesSorted = chain.Where(o => o.OptionType == side).Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();

        var dayStart = bars[0].StartTimestamp;
        var dayEnd = bars[^1].EndTimestamp;

        var priceCache = sharedPriceCache ?? new Dictionary<(DateOnly, string), OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            var key = (asOfDate, token);
            if (priceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var series = await OptionPriceSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            priceCache[key] = series;
            return series;
        }

        Domain.Entities.Instrument? PickAtm(decimal futurePrice) => AtmStrikeSelector.PickAtm(chain, side, futurePrice);

        async Task<double?> GetPriceAsync(decimal futurePrice, DateTimeOffset atTime)
        {
            var atm = PickAtm(futurePrice); // Rolling strike mode only -- SignalFixed is out of scope for this SMA/EMA track (momentum-relationship already covers that axis).
            if (atm is null)
            {
                return null;
            }

            if (bandWidth is not { } bw)
            {
                var atmSeries = await GetSeriesAsync(atm.Token);
                var atmPrice = atmSeries.PriceAtOrBefore(atTime);
                return atmPrice is { } ap && ap > 0 ? (double)ap : null;
            }

            var atmIndex = strikesSorted.IndexOf(atm.StrikePrice!.Value);
            if (atmIndex < 0)
            {
                return null;
            }

            var half = (bw - 1) / 2;
            var loIndex = Math.Max(0, atmIndex - half);
            var hiIndex = Math.Min(strikesSorted.Count - 1, atmIndex + half);
            var prices = new List<double>();
            for (var idx = loIndex; idx <= hiIndex; idx++)
            {
                var strike = strikesSorted[idx];
                var instrument = chain.First(o => o.OptionType == side && o.StrikePrice == strike);
                var series = await GetSeriesAsync(instrument.Token);
                var price = series.PriceAtOrBefore(atTime);
                if (price is { } pr && pr > 0)
                {
                    prices.Add((double)pr);
                }
            }

            return prices.Count > 0 ? prices.Average() : null;
        }

        var optionPricesByBar = new double?[bars.Count];
        var engine = new MaSpreadEngine(fastBars, slowBars, fastType, slowType);
        var spreadsByBar = new double?[bars.Count];

        for (var i = 0; i < bars.Count; i++)
        {
            var price = await GetPriceAsync(bars[i].ClosePrice, bars[i].EndTimestamp);
            optionPricesByBar[i] = price;
            var step = engine.Observe(price);
            spreadsByBar[i] = step.DiffFraction;
        }

        var samples = new List<Sample>();
        for (var i = 0; i < bars.Count; i++)
        {
            var spread = spreadsByBar[i];
            if (spread is not { } s)
            {
                continue;
            }

            double? NiftyFwd(int horizon)
            {
                var j = i + horizon;
                if (j >= bars.Count)
                {
                    return null;
                }

                var basePrice = bars[i].ClosePrice;
                return basePrice > 0 ? (double)((bars[j].ClosePrice - basePrice) / basePrice) : null;
            }

            double? OptionFwd(int horizon)
            {
                var j = i + horizon;
                if (j >= bars.Count)
                {
                    return null;
                }

                var basePrice = optionPricesByBar[i];
                var futPrice = optionPricesByBar[j];
                if (basePrice is not { } bp || bp <= 0 || futPrice is not { } fp)
                {
                    return null;
                }

                return (fp - bp) / bp;
            }

            samples.Add(new Sample(
                asOfDate, bars[i].EndTimestamp, bars[i].BarIndex, side, s,
                NiftyFwd(5), NiftyFwd(10), NiftyFwd(20),
                OptionFwd(5), OptionFwd(10), OptionFwd(20)));
        }

        return samples;
    }
}
