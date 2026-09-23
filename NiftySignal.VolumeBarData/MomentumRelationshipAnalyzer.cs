using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "option-premium momentum: relationship research matrix"
/// task -- see docs/VOLUME_BAR_FINDINGS.md's dated section). Entirely separate from
/// <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/>'s trading-rule dispatch (same
/// "wholly separate method" discipline the 2026-09-22 price-crossover task itself established) --
/// this class never opens or closes a trade. It exists to measure whether option-premium momentum
/// (Spread = (FastMA-SlowMA)/SlowMA, Velocity = Spread_t - Spread_(t-1)) has any real predictive
/// relationship with NIFTY's own forward return, BEFORE any trading rule is built around it.
///
/// Every forward-return field this class produces is a FORWARD-LOOKING RESEARCH LABEL, NOT A
/// TRADEABLE SIGNAL. It is computed by looking at bars that occur strictly after the bar the
/// Spread/Velocity reading itself was computed from -- deliberate look-ahead, valid only for
/// labeling historical data for this research question, never something a live/backtest trading
/// decision could see at the time. This is the same distinction this project's no-look-ahead
/// discipline (CLAUDE.md) draws between "genuinely available at the timestamp" (Spread/Velocity
/// themselves, computed the same rolling-window way <see cref="PriceCrossoverEngine"/> already
/// does) and "only knowable afterward" (every NiftyFwd*/OptionFwd* field below).
/// </summary>
public static class MomentumRelationshipAnalyzer
{
    public enum StrikeMode
    {
        /// <summary>ATM strike re-selected fresh at every bar from that bar's own future price -- what <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/> already does (verified by reading its GetPriceAsync/PickAtm call sites).</summary>
        Rolling,
        /// <summary>ATM strike selected ONCE from the first bar of the day's future price and held fixed for the whole day's Spread/Velocity computation, even if spot later drifts to a different "true" ATM strike.</summary>
        SignalFixed,
    }

    public sealed record Sample(
        DateOnly Date, DateTimeOffset Timestamp, int BarIndex, OptionType Side, double Spread, double? Velocity,
        double? NiftyFwd5, double? NiftyFwd10, double? NiftyFwd20,
        double? OptionFwd5, double? OptionFwd10, double? OptionFwd20);

    /// <summary>
    /// Collects one day's Spread/Velocity readings plus their forward-looking research labels for
    /// one option side. Mirrors <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/>'s own
    /// chain-loading/band-averaging logic (same NIFTY/nearest-expiry filter, same audit finding F62
    /// discipline) but never opens a trade -- pure measurement.
    /// </summary>
    public static async Task<List<Sample>> CollectDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        OptionType side, int fastBars, int slowBars, StrikeMode strikeMode, int? bandWidth,
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

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, deterministic ordering.
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

        // SignalFixed: pick the ATM strike ONCE from the day's first bar and hold it -- step 4's
        // control. Rolling: re-picked every bar (below), same as SimulatePriceCrossoverDayAsync.
        var fixedAtm = strikeMode == StrikeMode.SignalFixed ? PickAtm(bars[0].ClosePrice) : null;

        async Task<double?> GetPriceAsync(decimal futurePrice, DateTimeOffset atTime)
        {
            var atm = strikeMode == StrikeMode.SignalFixed ? fixedAtm : PickAtm(futurePrice);
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

        // Same-side option's own forward price needs a price lookup at future bars too -- use the
        // SAME token/band choice as the momentum computation itself (GetPriceAsync above) so
        // "does Spread predict the option's own forward price" uses exactly the instrument Spread
        // was computed from, not a different one re-selected at the forward timestamp.
        var optionPricesByBar = new double?[bars.Count];
        var engine = new PriceCrossoverEngine(fastBars, slowBars);
        var spreadsByBar = new double?[bars.Count];

        for (var i = 0; i < bars.Count; i++)
        {
            var price = await GetPriceAsync(bars[i].ClosePrice, bars[i].EndTimestamp);
            optionPricesByBar[i] = price;
            // thresholdFraction=0 here -- this class never gates on a threshold or reports
            // crossings, only the continuous DiffFraction (Spread) reading itself.
            var step = engine.Observe(price, thresholdFraction: 0.0);
            spreadsByBar[i] = step.DiffFraction;
        }

        var samples = new List<Sample>();
        double? previousSpread = null;
        for (var i = 0; i < bars.Count; i++)
        {
            var spread = spreadsByBar[i];
            if (spread is not { } s)
            {
                previousSpread = null;
                continue;
            }

            double? velocity = previousSpread is { } prev ? s - prev : null;
            previousSpread = s;

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
                asOfDate, bars[i].EndTimestamp, bars[i].BarIndex, side, s, velocity,
                NiftyFwd(5), NiftyFwd(10), NiftyFwd(20),
                OptionFwd(5), OptionFwd(10), OptionFwd(20)));
        }

        return samples;
    }

    public sealed record BucketStat(
        string BucketLabel, int Count,
        double? MeanNiftyFwd, double? MedianNiftyFwd, double? ProbNiftyUp,
        double? MeanOptionFwd, double? MedianOptionFwd);

    /// <summary>
    /// Quantile-based bucketing (per this task's own instruction to prefer quantile buckets when
    /// more informative than fixed illustrative edges) over the pooled sample's Spread values, then
    /// per-bucket forward-return stats for one horizon (5/10/20 bars), selected via <paramref name="niftyFwdSelector"/>/<paramref name="optionFwdSelector"/>.
    /// </summary>
    public static List<BucketStat> BucketByQuantile(
        IReadOnlyList<Sample> samples, int bucketCount,
        Func<Sample, double?> niftyFwdSelector, Func<Sample, double?> optionFwdSelector)
    {
        var usable = samples.Where(s => niftyFwdSelector(s) is not null).OrderBy(s => s.Spread).ToList();
        if (usable.Count == 0)
        {
            return [];
        }

        var n = usable.Count;
        var result = new List<BucketStat>();
        for (var b = 0; b < bucketCount; b++)
        {
            var lo = b * n / bucketCount;
            var hi = (b + 1) * n / bucketCount;
            if (hi <= lo)
            {
                continue;
            }

            var slice = usable.GetRange(lo, hi - lo);
            var niftyVals = slice.Select(niftyFwdSelector).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
            var optVals = slice.Select(optionFwdSelector).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();

            var spreadLo = slice[0].Spread * 100.0;
            var spreadHi = slice[^1].Spread * 100.0;
            var label = $"{spreadLo:F2}% to {spreadHi:F2}%";

            result.Add(new BucketStat(
                label, slice.Count,
                niftyVals.Count > 0 ? niftyVals.Average() * 100.0 : null,
                niftyVals.Count > 0 ? Median(niftyVals) * 100.0 : null,
                niftyVals.Count > 0 ? 100.0 * niftyVals.Count(v => v > 0) / niftyVals.Count : null,
                optVals.Count > 0 ? optVals.Average() * 100.0 : null,
                optVals.Count > 0 ? Median(optVals) * 100.0 : null));
        }

        return result;
    }

    static double Median(List<double> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
