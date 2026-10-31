namespace NiftySignal.MarketSummary;

/// <summary>One valid India VIX observation. <see cref="AvailableAt"/> = max(exchange time, receive time): the causal instant from which the value could have been used (same convention as the adaptive observer).</summary>
public readonly record struct VixObservation(DateTimeOffset AvailableAt, double Value);

public enum VixStatus { Fresh, Stale, Missing }

/// <summary>The VIX value in force at one instant: the latest valid observation available at or before it, and how old that observation was.</summary>
public sealed record VixPoint(double? Value, DateTimeOffset? ObservedAt, double? AgeSeconds, VixStatus Status)
{
    public static readonly VixPoint Missing = new(null, null, null, VixStatus.Missing);
}

/// <summary>The boundaries of one summary window: the start instant of its first bar and the end instant of the target bar. Null = the bar's time is not known (the VIX is then unavailable, never guessed).</summary>
public sealed record WindowInstant(SummaryWindow Window, DateTimeOffset? Start, DateTimeOffset? End);

/// <summary>VIX for one window: level at the window end, level at the window start, absolute change (end - start) and percentage change (change / start x 100).</summary>
public sealed record VixWindowSummary(SummaryWindow Window, VixPoint Start, VixPoint End, double? Change, double? ChangePct);

/// <summary>
/// India VIX as-of alignment. Rules (docs/MARKET_SUMMARY_METRICS.md section 6): a point is the latest valid (finite, &gt; 0) observation with <c>AvailableAt &lt;= instant</c> - never a later one - and is
/// usable only when it is no older than <see cref="FreshnessLimit"/>; otherwise the point is Stale (value withheld) or Missing. Change and percentage need BOTH points usable and a positive start. Nothing is
/// backfilled, interpolated or carried across a stale gap.
/// </summary>
public static class VixSummaryCalculator
{
    /// <summary>
    /// 60 s. PROVISIONAL starting value derived from the real feed cadence (local copy, 23 sessions 2026-09-04..2026-10-09, 09:15-15:30 IST, distinct exchange timestamps): median gap 2-3 s, p95 7-22 s,
    /// p99 13-46 s, largest gap 35-336 s. 60 s exceeds the p99 of every sampled session, so a normally ticking feed is never stale, while the rare multi-minute silences are reported as unavailable. It is
    /// deliberately NOT the 5 s spot rule (the VIX ticks far slower than the spot). To be re-derived as more sessions accumulate.
    /// </summary>
    public static readonly TimeSpan FreshnessLimit = TimeSpan.FromSeconds(60);

    /// <param name="observations">Valid observations ordered by <see cref="VixObservation.AvailableAt"/> ascending.</param>
    public static VixPoint AsOf(IReadOnlyList<VixObservation> observations, DateTimeOffset? instant, TimeSpan limit)
    {
        if (instant is not { } at || observations.Count == 0) return VixPoint.Missing;
        int lo = 0, hi = observations.Count;                          // first index with AvailableAt > at
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (observations[mid].AvailableAt <= at) lo = mid + 1; else hi = mid;
        }

        for (var i = lo - 1; i >= 0; i--)                              // latest VALID observation at or before the instant
        {
            var o = observations[i];
            if (!(o.Value > 0d) || !double.IsFinite(o.Value)) continue;
            var age = (at - o.AvailableAt).TotalSeconds;
            return age <= limit.TotalSeconds ? new VixPoint(o.Value, o.AvailableAt, age, VixStatus.Fresh) : new VixPoint(null, o.AvailableAt, age, VixStatus.Stale);
        }

        return VixPoint.Missing;
    }

    public static IReadOnlyList<VixWindowSummary> Compute(IReadOnlyList<VixObservation> observations, IReadOnlyList<WindowInstant> instants, TimeSpan limit) =>
        instants.Select(w =>
        {
            var start = AsOf(observations, w.Start, limit);
            var end = AsOf(observations, w.End, limit);
            double? change = start.Value is { } s && end.Value is { } e ? e - s : null;
            double? pct = change is { } c && start.Value is > 0d ? c / start.Value.Value * 100d : null;
            return new VixWindowSummary(w.Window, start, end, change, pct);
        }).ToList();
}
