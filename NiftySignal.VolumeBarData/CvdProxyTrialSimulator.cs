using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-22, "Options CVD Approximate, transparent research" track -- structurally identical
/// simulator to <see cref="DepthImbalanceTrialSimulator"/> (same sign-only rolling-window rule, same
/// price-band strike selection), pointed at <see cref="CvdProxySumBarRow"/> instead of
/// <see cref="DepthImbalanceSumBarRow"/>. Sign is AS-BUILT/untested for both sides, same discipline.
/// </summary>
public static class CvdProxyTrialSimulator
{
    public sealed record Trial(
        VolumeBarTrade Trade, int WindowBars, bool UseNotional, int DaysToExpiry,
        decimal MaePoints, decimal MfePoints, decimal MaePercent, decimal MfePercent, double RollingValueAtEntry);

    public static async Task<List<Trial>> SimulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        int bandWidth, OptionType side, int windowBars, bool useNotional, CancellationToken cancellationToken,
        Dictionary<(DateOnly, string), OptionPriceSeries>? sharedPriceCache = null,
        decimal? minEntryPrice = null, decimal? maxEntryPrice = null)
    {
        var bars = await volumeBarDb.CvdProxySumBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == side)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (bars.Count == 0)
        {
            return [];
        }

        var priceCache = sharedPriceCache ?? new Dictionary<(DateOnly, string), OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            if (priceCache.TryGetValue((asOfDate, token), out var cached))
            {
                return cached;
            }

            var series = await OptionPriceSeries.LoadAsync(source, token, bars[0].EndTimestamp.AddHours(-1), bars[^1].EndTimestamp, cancellationToken);
            priceCache[(asOfDate, token)] = series;
            return series;
        }

        List<Domain.Entities.Instrument>? chain = null;
        async Task<(string Token, decimal Strike, decimal Price)?> PickStrikeInBandAsync(decimal futurePrice, DateTimeOffset atTime)
        {
            if (minEntryPrice is null && maxEntryPrice is null)
            {
                return null;
            }

            if (chain is null)
            {
                // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
                // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
                var nearestExpiry = await source.Instruments
                    .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate != null && i.Underlying == "NIFTY")
                    .Select(i => i.ExpiryDate!.Value)
                    .MinAsync(cancellationToken);
                chain = await source.Instruments
                    .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate == nearestExpiry && i.Underlying == "NIFTY")
                    .ToListAsync(cancellationToken);
            }

            foreach (var candidate in chain.OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice)))
            {
                var series = await GetSeriesAsync(candidate.Token);
                var price = series.PriceAtOrBefore(atTime);
                if (price is { } p && p > 0
                    && (minEntryPrice is null || p >= minEntryPrice)
                    && (maxEntryPrice is null || p <= maxEntryPrice))
                {
                    return (candidate.Token, candidate.StrikePrice!.Value, p);
                }
            }

            return null;
        }

        var values = bars.Select(b => useNotional ? b.NotionalCvdSum ?? 0.0 : b.RawCvdSum ?? 0.0).ToArray();

        var trials = new List<Trial>();
        var inPosition = false;
        DateTimeOffset entryTime = default;
        decimal entryPrice = 0;
        string entryToken = "";
        decimal entryStrike = 0;
        int entryDte = 0;
        double entryRolling = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            if (i < windowBars - 1)
            {
                continue;
            }

            double rolling = 0;
            for (var j = i - windowBars + 1; j <= i; j++)
            {
                rolling += values[j];
            }

            var bar = bars[i];
            var isLastBar = i == bars.Count - 1;

            if (!inPosition && rolling > 0 && !isLastBar)
            {
                var picked = await PickStrikeInBandAsync(bar.FutureClosePrice, bar.EndTimestamp);
                string? token = picked?.Token;
                decimal? price = picked?.Price;
                decimal? strike = picked?.Strike;

                if (picked is null && minEntryPrice is null && maxEntryPrice is null)
                {
                    var series = await GetSeriesAsync(bar.AtmToken);
                    price = series.PriceAtOrBefore(bar.EndTimestamp);
                    token = bar.AtmToken;
                    strike = bar.AtmStrike;
                }

                if (price is { } p && p > 0 && token is not null && strike is not null)
                {
                    inPosition = true;
                    entryTime = bar.EndTimestamp;
                    entryPrice = p;
                    entryToken = token;
                    entryStrike = strike.Value;
                    entryDte = bar.DaysToExpiry;
                    entryRolling = rolling;
                }
            }
            else if (inPosition && (rolling <= 0 || isLastBar))
            {
                var series = await GetSeriesAsync(entryToken);
                var exitPrice = series.PriceAtOrBefore(bar.EndTimestamp);
                if (exitPrice is { } ep && ep > 0)
                {
                    var pricesDuringHold = series.AllPrices
                        .Where(t => t.Timestamp >= entryTime && t.Timestamp <= bar.EndTimestamp)
                        .Select(t => t.Price)
                        .ToList();
                    var maeMfe = MaeMfeCalculator.Compute(entryPrice, pricesDuringHold);

                    var trade = new VolumeBarTrade(
                        entryTime, entryPrice, side, entryStrike,
                        bar.EndTimestamp, ep, isLastBar ? "EndOfDay" : "SignFlip", entryRolling);

                    trials.Add(new Trial(trade, windowBars, useNotional, entryDte,
                        maeMfe.MaePoints, maeMfe.MfePoints, maeMfe.MaePercent, maeMfe.MfePercent, entryRolling));
                }

                inPosition = false;
            }
        }

        return trials;
    }
}
