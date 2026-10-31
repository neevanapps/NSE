namespace NiftySignal.MarketSummary;

public sealed record FuturesWindowSummary(
    SummaryWindow Window, int? FromBar, int ToBar, Coverage Coverage,
    Metric Price, long Volume, Metric Strict, Metric Enriched, Metric Oi, Metric MicroDev, Metric Ofi, double? Activity);

/// <summary>Five-window summary of the primary futures series over completed bars through <c>endBarSeq</c>. Same window rules as the option grids; a missing stored bar makes the window PARTIAL (values use the bars present).</summary>
public static class FuturesSummaryCalculator
{
    public static IReadOnlyList<FuturesWindowSummary> Compute(IReadOnlyDictionary<int, FuturesBar> bars, int endBarSeq)
    {
        var rows = new List<FuturesWindowSummary>(5);
        foreach (var w in SummaryWindow.All)
        {
            var from = w.Bars is { } n ? endBarSeq - n + 1 : 1;
            if (endBarSeq < 1 || from < 1)
            {
                rows.Add(Empty(w, null, endBarSeq, Coverage.Insufficient(1, endBarSeq < 1 ? "no completed bar" : $"only {endBarSeq} completed bars, {w.Name} needs {w.Bars}")));
                continue;
            }

            var present = new List<FuturesBar>();
            for (var b = from; b <= endBarSeq; b++) if (bars.TryGetValue(b, out var x)) present.Add(x);
            if (present.Count == 0) { rows.Add(Empty(w, from, endBarSeq, Coverage.Insufficient(1, "no stored bar in the window"))); continue; }
            var total = endBarSeq - from + 1;
            var cov = new Coverage(present.Count, total, present.Count == total ? CoverageStatus.Full : CoverageStatus.Partial, present.Count == total ? "" : "bars missing in the window", []);
            var firstB = present[0];
            var lastB = present[^1];
            var vol = present.Sum(x => x.Volume);
            var price = new Metric(lastB.Close - firstB.Open, lastB.Close - firstB.Open, firstB.Open > 0d ? firstB.Open : null);
            var strict = new Metric(present.Sum(x => x.StrictDelta), present.Sum(x => x.StrictDelta), vol);
            var enriched = new Metric(present.Sum(x => x.EnrichedDelta), present.Sum(x => x.EnrichedDelta), present.Sum(x => x.EnrichedBuy + x.EnrichedSell + x.EnrichedUnknown));
            var oi = firstB.OiOpen is > 0 && lastB.OiClose is { } oc ? new Metric(oc - firstB.OiOpen.Value, oc - firstB.OiOpen.Value, firstB.OiOpen.Value) : Metric.None;
            var mv = present.Where(x => x.MicroDevTimeWeighted is not null && x.TobTimeWeighted is not null && x.MicroDevUsableSeconds is > 0d && x.TobUsableSeconds is > 0d).ToList();
            var micro = Metric.None;
            if (mv.Count > 0)
            {
                var mw = mv.Sum(x => x.MicroDevUsableSeconds!.Value);
                var tw = mv.Sum(x => x.TobUsableSeconds!.Value);
                micro = new Metric(mv.Sum(x => x.MicroDevTimeWeighted!.Value * x.MicroDevUsableSeconds!.Value) / mw, mv.Sum(x => x.TobTimeWeighted!.Value * x.TobUsableSeconds!.Value), tw);
            }

            var ov = present.Where(x => x.Ofi is not null && x.GrossOfi is not null).ToList();
            var ofi = ov.Count == 0 ? Metric.None : new Metric(ov.Sum(x => x.Ofi!.Value), ov.Sum(x => x.Ofi!.Value), ov.Sum(x => x.GrossOfi!.Value));
            var dur = present.Where(x => x.DurationSeconds > 0d).ToList();
            rows.Add(new(w, from, endBarSeq, cov, price, vol, strict, enriched, oi, micro, ofi, dur.Count == 0 ? null : dur.Sum(x => (double)x.Volume) / dur.Sum(x => x.DurationSeconds)));
        }

        return rows;
    }

    static FuturesWindowSummary Empty(SummaryWindow w, int? from, int to, Coverage c) => new(w, from, to, c, default, 0, default, default, default, default, default, null);
}
