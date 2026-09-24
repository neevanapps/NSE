namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, multi-day behaviour validation addendum (user's own explicit point 12). Reports
/// <see cref="SynchronizedOptionEventBar.MissingData"/>/<see cref="SynchronizedOptionEventBar.IsStale"/>
/// coverage across the dataset actually used for the behaviour tables in this same run -- so a
/// reader can judge how much of the underlying data was real versus a documented gap, without
/// re-deriving it from the raw bars themselves.
/// </summary>
public static class OptionBarQualityAudit
{
    public sealed record DayResult(DateOnly TradingDate, int TotalBars, int MissingCount, int StaleCount);

    public sealed record Result(int TotalBars, int MissingCount, int StaleCount, List<DayResult> ByDay)
    {
        public double MissingPercent => TotalBars > 0 ? 100.0 * MissingCount / TotalBars : 0;
        public double StalePercent => TotalBars > 0 ? 100.0 * StaleCount / TotalBars : 0;
    }

    public static Result Compute(IReadOnlyList<SynchronizedOptionEventBar> bars)
    {
        var byDay = bars.GroupBy(b => b.TradingDate)
            .OrderBy(g => g.Key)
            .Select(g => new DayResult(g.Key, g.Count(), g.Count(b => b.MissingData), g.Count(b => b.IsStale)))
            .ToList();

        return new Result(bars.Count, bars.Count(b => b.MissingData), bars.Count(b => b.IsStale), byDay);
    }
}
