namespace NiftySignal.AdaptiveObserver;

public sealed record OptionBandRollingMetrics(
    int WindowBars,
    double? RollingBandPriceChange,
    double RollingEfficiency,
    long ContractStrictDelta,
    double ContractStrictDeltaRatioTotal,
    double ContractStrictCoverage,
    long ContractEnrichedDelta,
    double ContractEnrichedDeltaRatio,
    double NotionalStrictDelta,
    double NotionalStrictDeltaRatioTotal,
    double NotionalStrictCoverage,
    double NotionalEnrichedDelta,
    double NotionalEnrichedDeltaRatio,
    long TradeUpdates,
    double ContractActivityPerSecond,
    double NotionalActivityPerSecond,
    long? OiChange,
    double? OiChangePct);

public static class OptionBandRollingCalculator
{
    public static OptionBandRollingMetrics? BuildLatest(
        IReadOnlyList<OptionBandSideMetrics?> bars,
        IReadOnlyList<double> durationsSeconds,
        int windowBars = 10)
    {
        if (bars.Count != durationsSeconds.Count)
        {
            throw new ArgumentException("Bars and durations must align.");
        }

        if (windowBars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBars));
        }

        if (bars.Count < windowBars)
        {
            return null;
        }

        var start = bars.Count - windowBars;
        var window = bars.Skip(start).Take(windowBars).ToArray();
        if (window.Any(x => x is null))
        {
            return null;
        }

        var xs = window.Select(x => x!).ToArray();
        var ds = durationsSeconds.Skip(start).Take(windowBars).ToArray();
        var elapsed = ds.Sum();

        var contractTotal = xs.Sum(x => x.ContractTotalQuantity);
        var contractClassified = xs.Sum(x => x.ContractStrictBuy + x.ContractStrictSell);
        var cStrict = xs.Sum(x => x.ContractStrictDelta);
        var cEnriched = xs.Sum(x => x.ContractEnrichedDelta);
        var cEnrichedTotal = xs.Sum(x => x.ContractEnrichedBuy + x.ContractEnrichedSell + x.ContractEnrichedUnknown);

        var notionalTotal = xs.Sum(x => x.NotionalTotal);
        var notionalClassified = xs.Sum(x => x.NotionalStrictBuy + x.NotionalStrictSell);
        var nStrict = xs.Sum(x => x.NotionalStrictDelta);
        var nEnriched = xs.Sum(x => x.NotionalEnrichedDelta);
        var nEnrichedTotal = xs.Sum(x => x.NotionalEnrichedBuy + x.NotionalEnrichedSell + x.NotionalEnrichedUnknown);

        var priceChanges = xs.Select(x => x.BarPriceChange).ToArray();
        double? rollingPrice = priceChanges.All(x => x.HasValue) ? priceChanges.Sum(x => x!.Value) : null;
        var path = priceChanges.Where(x => x.HasValue).Sum(x => Math.Abs(x!.Value));
        var efficiency = rollingPrice.HasValue && path > 0 ? Math.Min(1d, Math.Abs(rollingPrice.Value) / path) : 0d;

        long? oiChange = xs.All(x => x.OiChange.HasValue) ? xs.Sum(x => x.OiChange!.Value) : null;
        // Composition can roll every futures bar. Summing per-bar percentage changes avoids
        // comparing a new basket's endpoint against a different basket ten bars earlier.
        double? oiPct = xs.All(x => x.OiChangePct.HasValue) ? xs.Sum(x => x.OiChangePct!.Value) : null;

        return new OptionBandRollingMetrics(
            windowBars,
            rollingPrice,
            efficiency,
            cStrict,
            contractTotal > 0 ? (double)cStrict / contractTotal : 0d,
            contractTotal > 0 ? (double)contractClassified / contractTotal : 0d,
            cEnriched,
            cEnrichedTotal > 0 ? (double)cEnriched / cEnrichedTotal : 0d,
            nStrict,
            notionalTotal > 0 ? nStrict / notionalTotal : 0d,
            notionalTotal > 0 ? notionalClassified / notionalTotal : 0d,
            nEnriched,
            nEnrichedTotal > 0 ? nEnriched / nEnrichedTotal : 0d,
            xs.Sum(x => x.TradeUpdates),
            elapsed > 0 ? contractTotal / elapsed : 0d,
            elapsed > 0 ? notionalTotal / elapsed : 0d,
            oiChange,
            oiPct);
    }
}
