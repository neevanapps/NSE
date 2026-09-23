namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "Experiment 6 -- Standalone Mean-Reversion Strategy +
/// Gates" task -- see docs/VOLUME_BAR_FINDINGS.md's dated section). Builds on Experiment 5's
/// already-validated base signal/trade simulation (<see cref="Experiment5UnderlyingAnalyzer"/>,
/// <see cref="Experiment5OptionTradeSimulator"/>) WITHOUT changing any of it -- this class only
/// adds (1) attaching a <see cref="GateFeatureExtractor.GateFeatures"/> snapshot to each trade, (2)
/// winner-vs-loser distribution statistics per gate-candidate feature, (3) applying a single gate
/// predicate and reporting the required retention/quality metrics (item 9/10), and (4) a
/// trade-number-indexed cumulative equity curve (item 4). No composite score, no weighted
/// combination of features anywhere in this file -- gates are always evaluated ONE AT A TIME
/// against the base signal, per the task's own item 8 instruction.
/// </summary>
public static class Experiment6GateAnalyzer
{
    /// <summary>One base-strategy trade plus the gate-feature snapshot read at its own entry bar.</summary>
    public sealed record GatedTrade(Experiment5OptionTradeSimulator.OptionTrade Trade, GateFeatureExtractor.GateFeatures Features);

    public static List<GatedTrade> Attach(
        IReadOnlyList<Experiment5OptionTradeSimulator.OptionTrade> trades,
        IReadOnlyDictionary<DateOnly, List<VolumeBarRow>> byDayBars,
        TickActivityAnalyzer.ActivityEfficiencyThreshold threshold)
    {
        var result = new List<GatedTrade>(trades.Count);
        foreach (var t in trades)
        {
            var dayBars = byDayBars[t.Date];
            if (t.BarIndex >= dayBars.Count)
            {
                continue;
            }

            result.Add(new GatedTrade(t, GateFeatureExtractor.Build(dayBars, t.BarIndex, threshold)));
        }

        return result;
    }

    /// <summary>
    /// Item 6: per-feature winner-vs-loser distribution (median/mean/quartiles) plus an explicit
    /// overlap note -- "Heavy overlap" when the winner and loser inter-quartile ranges intersect,
    /// "Separated" when they don't. This is a plain point-estimate/IQR comparison, not a
    /// significance test (consistent with this document's existing practice throughout).
    /// </summary>
    public sealed record DistributionStat(
        string FeatureName, int NWinners, int NLosers,
        double? WinnerMedian, double? WinnerMean, double? WinnerP25, double? WinnerP75,
        double? LoserMedian, double? LoserMean, double? LoserP25, double? LoserP75,
        string OverlapNote);

    static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi)
        {
            return sorted[lo];
        }

        var frac = idx - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    public static DistributionStat SummarizeFeature(
        IReadOnlyList<GatedTrade> baseline, string name, Func<GateFeatures2, double?> selector)
    {
        var winners = baseline.Where(g => g.Trade.NetPnlLot > 0).Select(g => selector(new GateFeatures2(g.Features))).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
        var losers = baseline.Where(g => g.Trade.NetPnlLot <= 0).Select(g => selector(new GateFeatures2(g.Features))).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();

        double? WMedian() => winners.Count > 0 ? Percentile(winners, 0.5) : null;
        double? LMedian() => losers.Count > 0 ? Percentile(losers, 0.5) : null;

        double? wP25 = winners.Count > 0 ? Percentile(winners, 0.25) : null;
        double? wP75 = winners.Count > 0 ? Percentile(winners, 0.75) : null;
        double? lP25 = losers.Count > 0 ? Percentile(losers, 0.25) : null;
        double? lP75 = losers.Count > 0 ? Percentile(losers, 0.75) : null;

        var overlap = "Insufficient data";
        if (wP25 is not null && wP75 is not null && lP25 is not null && lP75 is not null)
        {
            var intersects = wP25 <= lP75 && lP25 <= wP75;
            overlap = intersects ? "Heavy overlap (IQRs intersect)" : "Separated (IQRs do not intersect)";
        }

        return new DistributionStat(
            name, winners.Count, losers.Count,
            WMedian(), winners.Count > 0 ? winners.Average() : null, wP25, wP75,
            LMedian(), losers.Count > 0 ? losers.Average() : null, lP25, lP75,
            overlap);
    }

    /// <summary>Thin wrapper so <see cref="SummarizeFeature"/>'s selector signature reads naturally at call sites (avoids a raw tuple/record-field lambda every caller has to repeat).</summary>
    public readonly record struct GateFeatures2(GateFeatureExtractor.GateFeatures F)
    {
        public double? DepthImbalance => F.DepthImbalance;
        public double? Ofi => F.Ofi;
        public double? CvdNet => F.CvdNet;
        public double? VwapRelPct => F.VwapRelPct;
        public double? OiAtClose => F.OiAtClose;
        public double? OiChangeFromPrev => F.OiChangeFromPrev;
        public double DurationSeconds => F.DurationSeconds;
        public double TickCount => F.TickCount;
        public double? TickDensity => F.TickDensity;
        public double? TickVelocityExcess => F.TickVelocityExcess;
        public double? PriceEfficiencyExcess => F.PriceEfficiencyExcess;
        public double? Churn => F.Churn;
        public double AbsNetMovePct => F.AbsNetMovePct;
    }

    /// <summary>
    /// Item 9/10: applies one gate predicate to the baseline trade set and reports the full
    /// mandatory metric set -- trade count/removed, win rate, expectancy, profit factor, net P&amp;L,
    /// max drawdown, avg winner/loser, MFE/MAE, PLUS the two retention numbers the task calls out as
    /// critical (item 9/10): % of PROFITABLE base trades retained, % of LOSING base trades removed.
    /// </summary>
    public sealed record GateResult(
        string Label, int BaselineCount, int Kept, int Removed,
        double PctProfitableBaseRetained, double PctLosingBaseRemoved,
        OptionTradeQualityStats.Stats Gross, OptionTradeQualityStats.Stats Net);

    public static GateResult Evaluate(string label, IReadOnlyList<GatedTrade> baseline, Func<GatedTrade, bool> predicate)
    {
        var ordered = baseline.OrderBy(g => g.Trade.Date).ThenBy(g => g.Trade.BarIndex).ToList();
        var kept = ordered.Where(predicate).ToList();

        var baseProfitable = ordered.Where(g => g.Trade.NetPnlLot > 0).ToList();
        var baseLosing = ordered.Where(g => g.Trade.NetPnlLot <= 0).ToList();
        var keptProfitable = kept.Count(g => g.Trade.NetPnlLot > 0);
        var keptLosing = kept.Count(g => g.Trade.NetPnlLot <= 0);

        var pctProfitableRetained = baseProfitable.Count > 0 ? 100.0 * keptProfitable / baseProfitable.Count : 0.0;
        var pctLosingRemoved = baseLosing.Count > 0 ? 100.0 * (baseLosing.Count - keptLosing) / baseLosing.Count : 0.0;

        var grossPnls = kept.Select(g => g.Trade.GrossPnlLot).ToList();
        var netPnls = kept.Select(g => g.Trade.NetPnlLot).ToList();
        var mfePct = kept.Select(g => (double?)g.Trade.MfePercent).ToList();
        var maePct = kept.Select(g => (double?)g.Trade.MaePercent).ToList();
        var hold = kept.Select(g => g.Trade.HoldingMinutes).ToList();

        return new GateResult(
            label, ordered.Count, kept.Count, ordered.Count - kept.Count,
            pctProfitableRetained, pctLosingRemoved,
            OptionTradeQualityStats.Compute(grossPnls, mfePct, maePct, hold),
            OptionTradeQualityStats.Compute(netPnls, mfePct, maePct, hold));
    }

    /// <summary>
    /// Item 16's parameter-plateau check: computes the pooled (data-derived, never invented)
    /// percentile value of a feature across the WHOLE baseline set at each of 5 fixed percentiles
    /// (25/40/50/60/75), for a caller to sweep a gate threshold across and see whether the result is
    /// a broad plateau or a narrow, fragile optimum.
    /// </summary>
    public static readonly double[] PlateauPercentiles = [0.25, 0.40, 0.50, 0.60, 0.75];

    public static IReadOnlyDictionary<double, double> PooledPercentiles(IReadOnlyList<GatedTrade> baseline, Func<GateFeatures2, double?> selector)
    {
        var vals = baseline.Select(g => selector(new GateFeatures2(g.Features))).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
        var result = new Dictionary<double, double>();
        foreach (var p in PlateauPercentiles)
        {
            result[p] = vals.Count > 0 ? Percentile(vals, p) : double.NaN;
        }

        return result;
    }

    /// <summary>Item 4: trade-number-indexed cumulative equity curve (gross, costs, net). Trades must already be in chronological/entry-sequence order.</summary>
    public sealed record EquityPoint(int TradeNumber, DateOnly Date, decimal CumulativeGross, decimal CumulativeCosts, decimal CumulativeNet);

    public static List<EquityPoint> BuildEquityCurve(IReadOnlyList<Experiment5OptionTradeSimulator.OptionTrade> orderedTrades)
    {
        var result = new List<EquityPoint>(orderedTrades.Count);
        decimal cumGross = 0, cumCosts = 0, cumNet = 0;
        for (var i = 0; i < orderedTrades.Count; i++)
        {
            var t = orderedTrades[i];
            cumGross += t.GrossPnlLot;
            cumCosts += t.GrossPnlLot - t.NetPnlLot;
            cumNet += t.NetPnlLot;
            result.Add(new EquityPoint(i + 1, t.Date, cumGross, cumCosts, cumNet));
        }

        return result;
    }
}
