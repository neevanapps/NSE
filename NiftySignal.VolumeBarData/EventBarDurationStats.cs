namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, forensic-verification addendum. Pure calculator: what temporal scale does a
/// 1300-contract futures event bar actually span? The user's own point -- "if a 3-bar fast MA
/// represents only a few seconds during active periods, then 3/10 is not equivalent to a
/// conventional 3/10 time-based MA at all" -- needs this measured directly, not assumed. Excludes
/// the day's final, necessarily-partial bar (spec section 5) from the distribution -- its duration
/// is an artifact of when the session happened to end, not a real threshold-crossing interval.
/// </summary>
public static class EventBarDurationStats
{
    public sealed record HistogramBucket(string Label, int Count);

    public sealed record Result(
        int BarCount, double MinMs, double P1Ms, double P5Ms, double P10Ms, double P25Ms,
        double MedianMs, double P75Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs,
        IReadOnlyList<HistogramBucket> Histogram);

    public static Result Compute(IReadOnlyList<FutureEventBar> bars)
    {
        var durations = bars.Where(b => !b.IsFinalPartialBar).Select(b => b.DurationMs).Order().ToList();
        if (durations.Count == 0)
        {
            return new Result(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        }

        double Percentile(double p)
        {
            // Nearest-rank method -- simple, deterministic, no interpolation ambiguity to argue about.
            var rank = (int)Math.Ceiling(p / 100.0 * durations.Count) - 1;
            return durations[Math.Clamp(rank, 0, durations.Count - 1)];
        }

        var buckets = new (string Label, double MaxMsExclusive)[]
        {
            ("<1 sec", 1000), ("1-2 sec", 2000), ("2-5 sec", 5000), ("5-10 sec", 10000),
            ("10-30 sec", 30000), ("30-60 sec", 60000), ("1-2 min", 120000), ("2-5 min", 300000),
            (">5 min", double.PositiveInfinity),
        };
        var histogram = buckets.Select((b, i) =>
        {
            var lower = i == 0 ? 0 : buckets[i - 1].MaxMsExclusive;
            var count = durations.Count(d => d >= lower && d < b.MaxMsExclusive);
            return new HistogramBucket(b.Label, count);
        }).ToList();

        return new Result(
            durations.Count, durations[0], Percentile(1), Percentile(5), Percentile(10), Percentile(25),
            Percentile(50), Percentile(75), Percentile(90), Percentile(95), Percentile(99), durations[^1],
            histogram);
    }
}
