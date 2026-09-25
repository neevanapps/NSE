namespace NiftySignal.VolumeBarData;

public static class OptionSurfaceBandStats
{
    public static (decimal? Median, decimal? Iqr, decimal? Min, decimal? Max, int Pos, int Neg, int Zero, int Valid) Compute(IReadOnlyList<decimal?> pctReturns)
    {
        var v = pctReturns.Where(x => x is not null).Select(x => x!.Value).OrderBy(x => x).ToList();
        if (v.Count == 0) { return (null, null, null, null, 0, 0, 0, 0); }
        decimal Pct(decimal f) => v[Math.Clamp((int)Math.Ceiling((double)f * v.Count) - 1, 0, v.Count - 1)];
        var median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2m;
        var iqr = Pct(0.75m) - Pct(0.25m);
        return (median, iqr, v.Min(), v.Max(), v.Count(x => x > 0), v.Count(x => x < 0), v.Count(x => x == 0), v.Count);
    }
}
