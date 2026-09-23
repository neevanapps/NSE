namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "Experiment 5 -- Tradeability of Activity/Efficiency Mean
/// Reversion" task -- see docs/VOLUME_BAR_FINDINGS.md's dated section). Reuses Experiment 3's LOCKED
/// HighActivity+HighEfficiency state definition (<see cref="TickActivityAnalyzer.ComputeThreshold"/>,
/// same TickVelocity/PriceEfficiency median-split logic, byte-for-byte -- not redefined or
/// re-derived here) to find qualifying bars, then computes the underlying (NIFTY future)'s own
/// forward-outcome numbers at multiple horizons (item 2 of the task spec) -- BEFORE any option
/// simulation. Pure functions over already-loaded <see cref="VolumeBarRow"/>/<see cref="OptionAtmBarRow"/>
/// data, no DB access, no look-ahead (every forward number strictly uses bars after the signal bar).
/// </summary>
public static class Experiment5UnderlyingAnalyzer
{
    /// <summary>The 5 fixed horizons item 2/6 of the task spec require -- 1/2/5/10/20 volume bars.</summary>
    public static readonly int[] Horizons = [1, 2, 5, 10, 20];

    /// <summary>
    /// One horizon's underlying-price outcome from a qualifying bar's own close. MFE/MAE are
    /// DIRECTION-AGNOSTIC (best-case up excursion / worst-case down excursion), same convention
    /// <see cref="TickActivityAnalyzer.CollectDay"/> already uses -- a caller interested in the
    /// REVERSAL-direction excursion picks the sign that matches the qualifying bar's own hypothesis
    /// (see <see cref="QualifyingBar.ReversalFavorableExcursion"/>/<see cref="QualifyingBar.ReversalAdverseExcursion"/>).
    /// BarsToMfe/BarsToMae are 1-based offsets from the signal bar to the bar where that extreme
    /// occurred (null if the horizon window doesn't exist, i.e. too close to day-end).
    /// </summary>
    public sealed record UnderlyingHorizonOutcome(int Horizon, double? FwdReturn, double? Mfe, double? Mae, int? BarsToMfe, int? BarsToMae);

    /// <summary>
    /// One HighActivity+HighEfficiency qualifying bar plus its underlying-outcome set across all
    /// <see cref="Horizons"/>. <see cref="Direction"/> is the qualifying bar's own NetMove sign
    /// (Close-Open, from <see cref="NiftySignal.Features.TickActivityFeatures.Result.Direction"/>) --
    /// per the task's own locked trade-direction rule (item 3, not reversed): Direction&gt;0 (bar
    /// closed up) hypothesizes mean-reversion DOWN -> BUY PUT; Direction&lt;0 hypothesizes reversion
    /// UP -> BUY CALL. <see cref="AtmStrike"/> is read directly from the already-populated
    /// <see cref="OptionAtmBarRow"/> at this bar's own index (synthetic-forward-based ATM, the
    /// established <see cref="OptionAtmPopulator"/> methodology, not recomputed here) -- null if that
    /// table has no row for this bar (should not happen for an already-populated day/threshold).
    /// </summary>
    public sealed record QualifyingBar(
        DateOnly Date, int BarIndex, long BarVolumeThreshold, DateTimeOffset SignalTimestamp,
        decimal NiftyPriceAtSignal, int Direction, string Session, int Dte, bool IsZeroDte,
        decimal? AtmStrike, IReadOnlyList<UnderlyingHorizonOutcome> Underlying)
    {
        /// <summary>Sign of the hypothesized reversal move: Direction&gt;0 (bar closed up) expects DOWN (-1); Direction&lt;0 expects UP (+1).</summary>
        public int ExpectedReversalSign => Direction > 0 ? -1 : 1;

        UnderlyingHorizonOutcome? At(int horizon) => Underlying.FirstOrDefault(u => u.Horizon == horizon);

        /// <summary>Max excursion IN the hypothesized reversal direction over the horizon (the "would this have overcome option costs" number item 2 asks for) -- positive means the reversal partially or fully happened.</summary>
        public double? ReversalFavorableExcursion(int horizon) => At(horizon) is { } u
            ? (ExpectedReversalSign > 0 ? u.Mfe : u.Mae is { } mae ? -mae : null)
            : null;

        /// <summary>Max excursion AGAINST the hypothesized reversal (continuation) over the horizon.</summary>
        public double? ReversalAdverseExcursion(int horizon) => At(horizon) is { } u
            ? (ExpectedReversalSign > 0 ? (u.Mae is { } mae ? -mae : null) : u.Mfe)
            : null;

        /// <summary>True if the forward return at this horizon actually landed on the hypothesized reversal side (0 excluded from either side).</summary>
        public bool? Reversed(int horizon) => At(horizon)?.FwdReturn is { } fwd
            ? (ExpectedReversalSign > 0 ? fwd > 0 : fwd < 0)
            : null;

        public double? FwdReturn(int horizon) => At(horizon)?.FwdReturn;
    }

    public static string SessionOf(DateTimeOffset endTimestamp)
    {
        var t = TimeOnly.FromDateTime(endTimestamp.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        return t switch
        {
            var x when x < new TimeOnly(10, 0) => "Open(<10:00)",
            var x when x < new TimeOnly(13, 30) => "Mid(10:00-13:30)",
            _ => "Close(>13:30)",
        };
    }

    static List<UnderlyingHorizonOutcome> ComputeUnderlyingOutcomes(IReadOnlyList<VolumeBarRow> bars, int i)
    {
        var result = new List<UnderlyingHorizonOutcome>(Horizons.Length);
        var basePrice = bars[i].ClosePrice;
        foreach (var h in Horizons)
        {
            var j = i + h;
            if (j >= bars.Count || basePrice <= 0)
            {
                result.Add(new UnderlyingHorizonOutcome(h, null, null, null, null, null));
                continue;
            }

            var fwd = (double)((bars[j].ClosePrice - basePrice) / basePrice);

            var bestHigh = decimal.MinValue;
            var bestHighOffset = -1;
            var worstLow = decimal.MaxValue;
            var worstLowOffset = -1;
            for (var k = i + 1; k <= j; k++)
            {
                if (bars[k].HighPrice > bestHigh)
                {
                    bestHigh = bars[k].HighPrice;
                    bestHighOffset = k - i;
                }

                if (bars[k].LowPrice < worstLow)
                {
                    worstLow = bars[k].LowPrice;
                    worstLowOffset = k - i;
                }
            }

            var mfe = (double)((bestHigh - basePrice) / basePrice);
            var mae = (double)((worstLow - basePrice) / basePrice);
            result.Add(new UnderlyingHorizonOutcome(h, fwd, mfe, mae, bestHighOffset, worstLowOffset));
        }

        return result;
    }

    /// <summary>
    /// Scans one day's bars for HighActivity+HighEfficiency qualifying bars against an
    /// ALREADY-COMPUTED, pooled <paramref name="threshold"/> (per Experiment 3's own locked
    /// discipline: a slice never defines its own median). A bar with Direction==0 (Close==Open,
    /// exactly flat) is excluded -- neither cell of the locked split applies to it.
    /// </summary>
    public static List<QualifyingBar> Collect(
        IReadOnlyList<VolumeBarRow> bars,
        TickActivityAnalyzer.ActivityEfficiencyThreshold threshold,
        IReadOnlyDictionary<int, OptionAtmBarRow> atmByBarIndex,
        int dte, bool isZeroDte) =>
        CollectByState(bars, threshold, atmByBarIndex, dte, isZeroDte, requireHighActivity: true, requireHighEfficiency: true);

    /// <summary>
    /// Item 16 (interaction check): same scan as <see cref="Collect"/> but generalized to any of the
    /// 4 Activity x Efficiency cells against the SAME locked pooled threshold -- <see cref="Collect"/>
    /// is just this with both flags true (state D, the real signal). No new threshold is computed
    /// for any cell; all 4 reuse the identical pooled median split.
    /// </summary>
    public static List<QualifyingBar> CollectByState(
        IReadOnlyList<VolumeBarRow> bars,
        TickActivityAnalyzer.ActivityEfficiencyThreshold threshold,
        IReadOnlyDictionary<int, OptionAtmBarRow> atmByBarIndex,
        int dte, bool isZeroDte, bool requireHighActivity, bool requireHighEfficiency)
    {
        var result = new List<QualifyingBar>();
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var f = Features.TickActivityFeatures.Compute(b.OpenPrice, b.HighPrice, b.LowPrice, b.ClosePrice, b.Volume, b.TickCount, b.DurationSeconds);
            if (f.TickVelocity is not { } tv || f.PriceEfficiency is not { } pe)
            {
                continue;
            }

            var isHighActivity = tv >= threshold.ActivityMedian;
            var isHighEfficiency = pe >= threshold.EfficiencyMedian;
            if (isHighActivity != requireHighActivity || isHighEfficiency != requireHighEfficiency || f.Direction == 0)
            {
                continue;
            }

            atmByBarIndex.TryGetValue(b.BarIndex, out var atm);
            result.Add(new QualifyingBar(
                b.AsOfDate, b.BarIndex, b.BarVolumeThreshold, b.EndTimestamp, b.ClosePrice, f.Direction,
                SessionOf(b.EndTimestamp), dte, isZeroDte, atm?.AtmStrike, ComputeUnderlyingOutcomes(bars, i)));
        }

        return result;
    }

    public sealed record UnderlyingHorizonStat(
        int Horizon, int N, double? MeanFwdPct, double? MeanAbsFwdPct, double? ProbUpPct,
        double? MeanMfePct, double? MeanMaePct, double? MeanBarsToMfe, double? MeanBarsToMae,
        double? ReversalProbPct, double? MeanReversalFavorableExcursionPct, double? MeanReversalAdverseExcursionPct);

    /// <summary>
    /// Item 2's mandatory per-horizon underlying stats for one direction cell (pos or neg) within an
    /// arbitrary scope (all qualifying bars already filtered to Direction&gt;0 or Direction&lt;0 by
    /// the caller). Returns one row per horizon in <see cref="Horizons"/> order.
    /// </summary>
    public static List<UnderlyingHorizonStat> SummarizeUnderlying(IReadOnlyList<QualifyingBar> cell)
    {
        var result = new List<UnderlyingHorizonStat>(Horizons.Length);
        foreach (var h in Horizons)
        {
            var fwdVals = cell.Select(q => q.FwdReturn(h)).Where(v => v is not null).Select(v => v!.Value).ToList();
            var mfe = cell.Select(q => q.Underlying.First(u => u.Horizon == h).Mfe).Where(v => v is not null).Select(v => v!.Value).ToList();
            var mae = cell.Select(q => q.Underlying.First(u => u.Horizon == h).Mae).Where(v => v is not null).Select(v => v!.Value).ToList();
            var bmfe = cell.Select(q => q.Underlying.First(u => u.Horizon == h).BarsToMfe).Where(v => v is not null).Select(v => (double)v!.Value).ToList();
            var bmae = cell.Select(q => q.Underlying.First(u => u.Horizon == h).BarsToMae).Where(v => v is not null).Select(v => (double)v!.Value).ToList();
            var reversed = cell.Select(q => q.Reversed(h)).Where(v => v is not null).Select(v => v!.Value).ToList();
            var favEx = cell.Select(q => q.ReversalFavorableExcursion(h)).Where(v => v is not null).Select(v => v!.Value).ToList();
            var advEx = cell.Select(q => q.ReversalAdverseExcursion(h)).Where(v => v is not null).Select(v => v!.Value).ToList();

            result.Add(new UnderlyingHorizonStat(
                h, fwdVals.Count,
                fwdVals.Count > 0 ? fwdVals.Average() * 100.0 : null,
                fwdVals.Count > 0 ? fwdVals.Select(Math.Abs).Average() * 100.0 : null,
                fwdVals.Count > 0 ? 100.0 * fwdVals.Count(v => v > 0) / fwdVals.Count : null,
                mfe.Count > 0 ? mfe.Average() * 100.0 : null,
                mae.Count > 0 ? mae.Average() * 100.0 : null,
                bmfe.Count > 0 ? bmfe.Average() : null,
                bmae.Count > 0 ? bmae.Average() : null,
                reversed.Count > 0 ? 100.0 * reversed.Count(v => v) / reversed.Count : null,
                favEx.Count > 0 ? favEx.Average() * 100.0 : null,
                advEx.Count > 0 ? advEx.Average() * 100.0 : null));
        }

        return result;
    }
}
