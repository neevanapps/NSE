using NiftySignal.Features;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "tick activity + option premium SMA/EMA research" task --
/// see docs/VOLUME_BAR_FINDINGS.md's dated section, subsections 2-4). Builds <see cref="TickActivityFeatures"/>
/// readings per bar plus forward-looking Nifty-return research labels (same "forward return is a
/// RESEARCH LABEL computed strictly after the bar, never a tradeable signal" discipline
/// <see cref="MomentumRelationshipAnalyzer"/> already established), then the bucketing/state-split/
/// transition/incremental-information analyses the task's Parts 3-8 specify. Reads only
/// <see cref="VolumeBarRow"/> -- no option data needed, this track is purely about the FUTURE bar's
/// own tick/price behavior. Never opens a trade, never touches TradeSimulator's dispatch chain.
/// </summary>
public static class TickActivityAnalyzer
{
    public sealed record Sample(
        DateOnly Date, DateTimeOffset EndTimestamp, int BarIndex,
        TickActivityFeatures.Result Features, double? DepthImbalance,
        double? NiftyFwd5, double? NiftyFwd10, double? NiftyFwd20,
        double? Mfe5, double? Mae5, double? Mfe10, double? Mae10, double? Mfe20, double? Mae20,
        // Part 7: bar-over-bar deltas of the four "important" features.
        double? TickDensityDelta, double? TickVelocityDelta, double? PriceEfficiencyDelta, double? ChurnDelta,
        // Part 3: rolling (trailing 100-bar) and session percentile ranks, filled in by RankSamples.
        double? TickDensitySessionPct = null, double? TickDensityRollingPct = null,
        double? TickVelocitySessionPct = null, double? TickVelocityRollingPct = null,
        double? PriceEfficiencySessionPct = null, double? PriceEfficiencyRollingPct = null,
        double? ChurnSessionPct = null, double? ChurnRollingPct = null)
    {
        public string Session => TimeOnly.FromDateTime(EndTimestamp.ToOffset(TimeSpan.FromHours(5.5)).DateTime) switch
        {
            var t when t < new TimeOnly(10, 0) => "Open(<10:00)",
            var t when t < new TimeOnly(13, 30) => "Mid(10:00-13:30)",
            _ => "Close(>13:30)",
        };
    }

    /// <summary>
    /// One day's samples, in bar order. Forward labels use the SAME forward-return convention
    /// <see cref="MomentumRelationshipAnalyzer"/> uses: (Close[i+h]-Close[i])/Close[i], strictly
    /// forward, null past the day's last bar (never wraps to the next day). MFE/MAE over the
    /// horizon are computed from the underlying future's own High/Low path (best/worst excursion of
    /// price relative to bar i's close), a NIFTY-FUTURE-price proxy for MFE/MAE -- NOT an option
    /// P&amp;L reconstruction (<see cref="MaeMfeCalculator"/> is the option-P&amp;L tool; this is
    /// deliberately a simpler, option-agnostic proxy for "how far did the underlying move in Claude's favor/against, at best/worst").
    /// </summary>
    public static List<Sample> CollectDay(IReadOnlyList<VolumeBarRow> bars)
    {
        if (bars.Count == 0)
        {
            return [];
        }

        var featuresByBar = bars.Select(b => TickActivityFeatures.Compute(b.OpenPrice, b.HighPrice, b.LowPrice, b.ClosePrice, b.Volume, b.TickCount, b.DurationSeconds)).ToList();

        double? NiftyFwd(int i, int horizon)
        {
            var j = i + horizon;
            if (j >= bars.Count)
            {
                return null;
            }

            var basePrice = bars[i].ClosePrice;
            return basePrice > 0 ? (double)((bars[j].ClosePrice - basePrice) / basePrice) : null;
        }

        (double? mfe, double? mae) MfeMae(int i, int horizon)
        {
            var j = i + horizon;
            if (j >= bars.Count)
            {
                return (null, null);
            }

            var basePrice = bars[i].ClosePrice;
            if (basePrice <= 0)
            {
                return (null, null);
            }

            var highestHigh = bars.Skip(i + 1).Take(horizon).Max(b => b.HighPrice);
            var lowestLow = bars.Skip(i + 1).Take(horizon).Min(b => b.LowPrice);
            // MFE/MAE are DIRECTION-AGNOSTIC excursions here (best-case up move, worst-case down
            // move) -- a caller who wants "in favor of a specific side" picks the sign that matches
            // their own direction call; this class doesn't assume long or short.
            var mfe = (double)((highestHigh - basePrice) / basePrice);
            var mae = (double)((lowestLow - basePrice) / basePrice);
            return (mfe, mae);
        }

        var samples = new List<Sample>();
        for (var i = 0; i < bars.Count; i++)
        {
            var f = featuresByBar[i];
            double? Delta(Func<TickActivityFeatures.Result, double?> selector)
            {
                if (i == 0)
                {
                    return null;
                }

                var cur = selector(f);
                var prev = selector(featuresByBar[i - 1]);
                return cur is { } c && prev is { } p ? c - p : null;
            }

            var (mfe5, mae5) = MfeMae(i, 5);
            var (mfe10, mae10) = MfeMae(i, 10);
            var (mfe20, mae20) = MfeMae(i, 20);

            samples.Add(new Sample(
                bars[i].AsOfDate, bars[i].EndTimestamp, bars[i].BarIndex,
                f, bars[i].FutureDepthImbalance,
                NiftyFwd(i, 5), NiftyFwd(i, 10), NiftyFwd(i, 20),
                mfe5, mae5, mfe10, mae10, mfe20, mae20,
                Delta(r => r.TickDensity), Delta(r => r.TickVelocity), Delta(r => r.PriceEfficiency), Delta(r => r.Churn)));
        }

        return samples;
    }

    /// <summary>
    /// Part 3: fills in session percentile (rank within that sample's own trading day) and rolling
    /// percentile (rank among the trailing <paramref name="rollingWindow"/> samples, POOLED across
    /// days in the order given -- caller must pass samples already date-sorted for this to be
    /// meaningful). Same-time-of-day percentile is NOT computed here -- with 11 trading days there
    /// are only ~11 observations per (bar-of-day) cell, too sparse to rank meaningfully; this is a
    /// deliberate scope cut, not an oversight (see docs/VOLUME_BAR_FINDINGS.md).
    /// </summary>
    public static List<Sample> RankSamples(List<Sample> samples, int rollingWindow)
    {
        static double Percentile(IReadOnlyList<double> populationSorted, double value)
        {
            var below = populationSorted.Count(v => v < value);
            return populationSorted.Count > 1 ? 100.0 * below / (populationSorted.Count - 1) : 50.0;
        }

        var byDay = samples.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<Sample>(samples.Count);

        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var daySlice = byDay[s.Date];

            double? SessionPct(Func<Sample, double?> selector)
            {
                var v = selector(s);
                if (v is not { } val)
                {
                    return null;
                }

                var pop = daySlice.Select(selector).Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
                return pop.Count > 0 ? Percentile(pop, val) : null;
            }

            double? RollingPct(Func<Sample, double?> selector)
            {
                var v = selector(s);
                if (v is not { } val)
                {
                    return null;
                }

                var lo = Math.Max(0, i - rollingWindow);
                var pop = samples.Skip(lo).Take(i - lo).Select(selector).Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
                return pop.Count >= 10 ? Percentile(pop, val) : null; // require a minimum warm-up before trusting a rolling rank
            }

            result.Add(s with
            {
                TickDensitySessionPct = SessionPct(x => x.Features.TickDensity),
                TickDensityRollingPct = RollingPct(x => x.Features.TickDensity),
                TickVelocitySessionPct = SessionPct(x => x.Features.TickVelocity),
                TickVelocityRollingPct = RollingPct(x => x.Features.TickVelocity),
                PriceEfficiencySessionPct = SessionPct(x => x.Features.PriceEfficiency),
                PriceEfficiencyRollingPct = RollingPct(x => x.Features.PriceEfficiency),
                ChurnSessionPct = SessionPct(x => x.Features.Churn),
                ChurnRollingPct = RollingPct(x => x.Features.Churn),
            });
        }

        return result;
    }

    public sealed record BucketStat(string Label, int Count, double? MeanFwd, double? MedianFwd, double? ProbUp, double? MeanAbsFwd, double? MeanMfe, double? MeanMae);

    /// <summary>Part 5: quantile buckets of <paramref name="featureSelector"/>, per-bucket forward-outcome stats at the given horizon.</summary>
    public static List<BucketStat> BucketByQuantile(
        IReadOnlyList<Sample> samples, int bucketCount,
        Func<Sample, double?> featureSelector,
        Func<Sample, double?> fwdSelector, Func<Sample, double?> mfeSelector, Func<Sample, double?> maeSelector)
    {
        var usable = samples.Where(s => featureSelector(s) is not null && fwdSelector(s) is not null).OrderBy(featureSelector).ToList();
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
            var fwdVals = slice.Select(fwdSelector).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
            var mfeVals = slice.Select(mfeSelector).Where(v => v is not null).Select(v => v!.Value).ToList();
            var maeVals = slice.Select(maeSelector).Where(v => v is not null).Select(v => v!.Value).ToList();

            var lo_v = featureSelector(slice[0])!.Value;
            var hi_v = featureSelector(slice[^1])!.Value;
            var label = $"{lo_v:F4} to {hi_v:F4}";

            result.Add(new BucketStat(
                label, slice.Count,
                fwdVals.Count > 0 ? fwdVals.Average() * 100.0 : null,
                fwdVals.Count > 0 ? Median(fwdVals) * 100.0 : null,
                fwdVals.Count > 0 ? 100.0 * fwdVals.Count(v => v > 0) / fwdVals.Count : null,
                fwdVals.Count > 0 ? fwdVals.Select(Math.Abs).Average() * 100.0 : null,
                mfeVals.Count > 0 ? mfeVals.Average() * 100.0 : null,
                maeVals.Count > 0 ? maeVals.Average() * 100.0 : null));
        }

        return result;
    }

    public sealed record StateStat(string State, int Count, double? ProbUp, double? MeanFwd, double? MeanAbsFwd, double? MeanMfe, double? MeanMae);

    /// <summary>
    /// Part 6: 2x2 Activity x PriceEfficiency state split, median-split each axis WITHIN the pooled
    /// sample passed in (caller decides scope -- whole dataset, one threshold, etc.). A/B/C/D use
    /// Activity=TickVelocity, Efficiency=PriceEfficiency (the two "how much activity, how much did
    /// it accomplish" axes the task's own framing centers on).
    /// </summary>
    public static List<StateStat> TwoByTwoStates(IReadOnlyList<Sample> samples, Func<Sample, double?> fwdSelector, Func<Sample, double?> mfeSelector, Func<Sample, double?> maeSelector)
    {
        var withActivity = samples.Where(s => s.Features.TickVelocity is not null && s.Features.PriceEfficiency is not null && fwdSelector(s) is not null).ToList();
        if (withActivity.Count < 4)
        {
            return [];
        }

        var activityMedian = Median(withActivity.Select(s => s.Features.TickVelocity!.Value).OrderBy(v => v).ToList());
        var efficiencyMedian = Median(withActivity.Select(s => s.Features.PriceEfficiency!.Value).OrderBy(v => v).ToList());

        List<StateStat> Build()
        {
            var groups = new Dictionary<string, List<Sample>>
            {
                ["A: LowActivity+LowEfficiency"] = [],
                ["B: LowActivity+HighEfficiency"] = [],
                ["C: HighActivity+LowEfficiency"] = [],
                ["D: HighActivity+HighEfficiency"] = [],
            };

            foreach (var s in withActivity)
            {
                var highActivity = s.Features.TickVelocity!.Value >= activityMedian;
                var highEfficiency = s.Features.PriceEfficiency!.Value >= efficiencyMedian;
                var key = (highActivity, highEfficiency) switch
                {
                    (false, false) => "A: LowActivity+LowEfficiency",
                    (false, true) => "B: LowActivity+HighEfficiency",
                    (true, false) => "C: HighActivity+LowEfficiency",
                    (true, true) => "D: HighActivity+HighEfficiency",
                };
                groups[key].Add(s);
            }

            var result = new List<StateStat>();
            foreach (var (label, slice) in groups)
            {
                var fwdVals = slice.Select(fwdSelector).Where(v => v is not null).Select(v => v!.Value).ToList();
                var mfeVals = slice.Select(mfeSelector).Where(v => v is not null).Select(v => v!.Value).ToList();
                var maeVals = slice.Select(maeSelector).Where(v => v is not null).Select(v => v!.Value).ToList();
                result.Add(new StateStat(
                    label, slice.Count,
                    fwdVals.Count > 0 ? 100.0 * fwdVals.Count(v => v > 0) / fwdVals.Count : null,
                    fwdVals.Count > 0 ? fwdVals.Average() * 100.0 : null,
                    fwdVals.Count > 0 ? fwdVals.Select(Math.Abs).Average() * 100.0 : null,
                    mfeVals.Count > 0 ? mfeVals.Average() * 100.0 : null,
                    maeVals.Count > 0 ? maeVals.Average() * 100.0 : null));
            }

            return result;
        }

        return Build();
    }

    static double Median(List<double> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    public readonly record struct ActivityEfficiencyThreshold(double ActivityMedian, double EfficiencyMedian);

    /// <summary>
    /// Experiment 3 (2026-09-22, docs/VOLUME_BAR_FINDINGS.md "Experiment 3" section): computes the
    /// TickVelocity/PriceEfficiency median-split thresholds from a POOLED reference population (e.g.
    /// all 11 days at one bar size) -- the same computation <see cref="TwoByTwoStates"/> already does
    /// inline, factored out so callers can compute it ONCE and reuse it across many slices (one day,
    /// one session, one DTE regime, a leave-one-out pool). Reusing a single pooled threshold across
    /// slices is deliberate: letting each slice define its own median would force an artificial
    /// ~50/50 split inside every slice by construction, making a day-by-day or session breakdown
    /// meaningless.
    /// </summary>
    public static ActivityEfficiencyThreshold ComputeThreshold(IReadOnlyList<Sample> referencePopulation)
    {
        var withFeatures = referencePopulation.Where(s => s.Features.TickVelocity is not null && s.Features.PriceEfficiency is not null).ToList();
        if (withFeatures.Count == 0)
        {
            return new ActivityEfficiencyThreshold(0, 0);
        }

        var activityMedian = Median(withFeatures.Select(s => s.Features.TickVelocity!.Value).OrderBy(v => v).ToList());
        var efficiencyMedian = Median(withFeatures.Select(s => s.Features.PriceEfficiency!.Value).OrderBy(v => v).ToList());
        return new ActivityEfficiencyThreshold(activityMedian, efficiencyMedian);
    }

    public sealed record MeanReversionStat(string Label, int NPos, double? PosProbUp, double? PosMeanFwd, int NNeg, double? NegProbUp, double? NegMeanFwd);

    /// <summary>
    /// Experiment 3: applies an ALREADY-COMPUTED threshold (see <see cref="ComputeThreshold"/>) to
    /// an arbitrary <paramref name="scope"/> (one day, one session, one DTE regime, a leave-one-out
    /// pool -- caller decides), selects the HighActivity+HighEfficiency bars within that scope, then
    /// splits by the bar's own NetMove direction and reports P(Up)/mean forward return for both
    /// direction cells at the given horizon (<paramref name="fwdSelector"/>). This is exactly the
    /// state-split-then-NetMove-split logic the original E4 finding used (see the
    /// <c>tick-activity-research</c> CLI command in Program.cs), factored out so it can run against
    /// slices smaller than the full pooled dataset and at horizons other than 10.
    /// </summary>
    public static MeanReversionStat MeanReversionSplit(IReadOnlyList<Sample> scope, string label, ActivityEfficiencyThreshold threshold, Func<Sample, double?> fwdSelector)
    {
        var haheOnly = scope.Where(s =>
            s.Features.TickVelocity is not null && s.Features.PriceEfficiency is not null &&
            s.Features.TickVelocity.Value >= threshold.ActivityMedian &&
            s.Features.PriceEfficiency.Value >= threshold.EfficiencyMedian).ToList();

        var pos = haheOnly.Where(s => s.Features.Direction > 0).ToList();
        var neg = haheOnly.Where(s => s.Features.Direction < 0).ToList();

        (int n, double? probUp, double? meanFwd) Stats(List<Sample> group)
        {
            var fwd = group.Select(fwdSelector).Where(v => v is not null).Select(v => v!.Value).ToList();
            return (group.Count,
                fwd.Count > 0 ? 100.0 * fwd.Count(v => v > 0) / fwd.Count : null,
                fwd.Count > 0 ? fwd.Average() * 100.0 : null);
        }

        var (nPos, posProbUp, posMeanFwd) = Stats(pos);
        var (nNeg, negProbUp, negMeanFwd) = Stats(neg);
        return new MeanReversionStat(label, nPos, posProbUp, posMeanFwd, nNeg, negProbUp, negMeanFwd);
    }
}
