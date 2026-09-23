using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-22, "Depth Imbalance, transparent research" track -- final, scope-limited experiment
/// per user's own instruction (chat): trade the CALL-minus-PUT depth imbalance SPREAD as a single
/// switching metric, instead of either side alone. Spread &gt; 0 -&gt; long the ATM Call; spread &lt; 0
/// -&gt; long the ATM Put; the position switches sides on a sign flip (never both legs at once, never
/// flat except transiently between the exit and the next entry). Two smoothing modes, per the
/// user's own "Test 1 / Test 2" spec:
///   - RollingSum: sliding sum of the last N bars' (CallRawDiff - PutRawDiff), same mechanism
///     <see cref="DepthImbalanceTrialSimulator"/> already uses for each side alone.
///   - Ema: exponential moving average of the per-bar spread, alpha = 2/(N+1), a genuinely
///     different (shorter, bar-to-bar decaying) smoother than the rolling sum.
/// Explicitly scoped as a ONE-BATCH final test, not a new sweep -- see chat: no N=5-30 re-sweep,
/// no new day-by-day narrative, one summary table.
/// </summary>
public static class DepthSpreadTrialSimulator
{
    public enum SmoothingMode { RollingSum, Ema }

    public sealed record Trial(
        VolumeBarTrade Trade, int DaysToExpiry, decimal MaePoints, decimal MfePoints,
        decimal MaePercent, decimal MfePercent, double SpreadValueAtEntry);

    public static async Task<List<Trial>> SimulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        int bandWidth, SmoothingMode mode, int n, bool useNotional, CancellationToken cancellationToken,
        Dictionary<(DateOnly, string), OptionPriceSeries>? sharedPriceCache = null,
        decimal? minEntryPrice = null, decimal? maxEntryPrice = null)
    {
        var callBars = await volumeBarDb.DepthImbalanceSumBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == OptionType.Call)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);
        var putBars = await volumeBarDb.DepthImbalanceSumBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == OptionType.Put)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (callBars.Count == 0 || putBars.Count == 0 || callBars.Count != putBars.Count)
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

            var series = await OptionPriceSeries.LoadAsync(source, token, callBars[0].EndTimestamp.AddHours(-1), callBars[^1].EndTimestamp, cancellationToken);
            priceCache[(asOfDate, token)] = series;
            return series;
        }

        var chainBySide = new Dictionary<OptionType, List<Domain.Entities.Instrument>>();
        async Task<List<Domain.Entities.Instrument>> GetChainAsync(OptionType side)
        {
            if (chainBySide.TryGetValue(side, out var cached))
            {
                return cached;
            }

            // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
            // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
            var nearestExpiry = await source.Instruments
                .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate != null && i.Underlying == "NIFTY")
                .Select(i => i.ExpiryDate!.Value)
                .MinAsync(cancellationToken);
            var chain = await source.Instruments
                .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate == nearestExpiry && i.Underlying == "NIFTY")
                .ToListAsync(cancellationToken);
            chainBySide[side] = chain;
            return chain;
        }

        async Task<(string Token, decimal Strike, decimal Price)?> PickStrikeInBandAsync(OptionType side, decimal futurePrice, DateTimeOffset atTime)
        {
            var chain = await GetChainAsync(side);
            if (minEntryPrice is null && maxEntryPrice is null)
            {
                var atm = chain.OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice)).FirstOrDefault();
                if (atm is null)
                {
                    return null;
                }
                var atmSeries = await GetSeriesAsync(atm.Token);
                var atmPrice = atmSeries.PriceAtOrBefore(atTime);
                return atmPrice is { } ap && ap > 0 ? (atm.Token, atm.StrikePrice!.Value, ap) : null;
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

        var callValues = callBars.Select(b => useNotional ? b.NotionalDiffSum ?? 0.0 : b.RawQtyDiffSum ?? 0.0).ToArray();
        var putValues = putBars.Select(b => useNotional ? b.NotionalDiffSum ?? 0.0 : b.RawQtyDiffSum ?? 0.0).ToArray();
        var spread = callValues.Zip(putValues, (c, p) => c - p).ToArray();

        var smoothed = new double?[spread.Length];
        if (mode == SmoothingMode.RollingSum)
        {
            for (var i = 0; i < spread.Length; i++)
            {
                if (i < n - 1)
                {
                    continue;
                }
                double sum = 0;
                for (var j = i - n + 1; j <= i; j++)
                {
                    sum += spread[j];
                }
                smoothed[i] = sum;
            }
        }
        else
        {
            var alpha = 2.0 / (n + 1);
            double? ema = null;
            for (var i = 0; i < spread.Length; i++)
            {
                ema = ema is null ? spread[i] : alpha * spread[i] + (1 - alpha) * ema.Value;
                smoothed[i] = ema;
            }
        }

        var trials = new List<Trial>();
        OptionType? heldSide = null;
        DateTimeOffset entryTime = default;
        decimal entryPrice = 0;
        string entryToken = "";
        decimal entryStrike = 0;
        int entryDte = 0;
        double entrySpread = 0;

        for (var i = 0; i < callBars.Count; i++)
        {
            if (smoothed[i] is not { } value)
            {
                continue;
            }

            var bar = callBars[i];
            var isLastBar = i == callBars.Count - 1;
            var desiredSide = value > 0 ? OptionType.Call : value < 0 ? OptionType.Put : (OptionType?)null;

            if (heldSide is { } held && (isLastBar || desiredSide != held))
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
                        entryTime, entryPrice, held, entryStrike,
                        bar.EndTimestamp, ep, isLastBar ? "EndOfDay" : "SideFlip", entrySpread);

                    trials.Add(new Trial(trade, entryDte, maeMfe.MaePoints, maeMfe.MfePoints, maeMfe.MaePercent, maeMfe.MfePercent, entrySpread));
                }

                heldSide = null;
            }

            if (heldSide is null && desiredSide is { } wantSide && !isLastBar)
            {
                var picked = await PickStrikeInBandAsync(wantSide, bar.FutureClosePrice, bar.EndTimestamp);
                if (picked is { } p)
                {
                    heldSide = wantSide;
                    entryTime = bar.EndTimestamp;
                    entryPrice = p.Price;
                    entryToken = p.Token;
                    entryStrike = p.Strike;
                    entryDte = bar.DaysToExpiry;
                    entrySpread = value;
                }
            }
        }

        return trials;
    }
}
