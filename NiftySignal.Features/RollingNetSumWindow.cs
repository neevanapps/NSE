namespace NiftySignal.Features;

/// <summary>
/// A time-based rolling SUM (not mean) of per-cadence values, evicting anything older than
/// <paramref name="window"/> as it's fed -- same eviction shape as <see cref="WelfordRollingWindow"/>/
/// <c>OiLookbackWindow</c>, but tracking a plain running sum since a flow quantity (net classified
/// volume) is naturally read as a total over a window, not an average per cadence. Built
/// specifically because the day-long <see cref="FutureCvdProxyAccumulator.CumulativeNet"/> drags
/// along everything since market open -- a real 2026-09-12 finding: its forward correlation with
/// price flipped sign across days, plausibly because a strong morning trend can still dominate the
/// running total hours later even after the market has genuinely turned. A rolling window isolates
/// recent flow instead.
///
/// 2026-09-17, Phase 3 unification: moved here from
/// `NiftySignal.BacktestData/CadencePopulator.cs` (unchanged logic) so
/// `NiftySignal.Host/LiveFeatureEngine.cs` can reference the SAME class instead of maintaining its
/// own hand-inlined Queue-plus-two-timestamps port of this exact eviction/warm-up shape.
/// </summary>
public sealed class RollingNetSumWindow(TimeSpan window)
{
    readonly Queue<(DateTimeOffset Timestamp, long Value)> _points = new();
    long _sum;
    DateTimeOffset? _firstSeenAt;
    DateTimeOffset? _latestTimestamp;

    /// <summary>True once real elapsed time since the first observation reaches the full window duration -- same "don't trust a partially-filled window" rule every other rolling window in this codebase applies.</summary>
    public bool IsWarmedUp => _firstSeenAt is { } firstSeenAt && _latestTimestamp is { } latestTimestamp && latestTimestamp - firstSeenAt >= window;

    /// <summary>Sum of every value currently inside the window. Only meaningful once <see cref="IsWarmedUp"/>.</summary>
    public long Sum => _sum;

    public void Add(DateTimeOffset timestamp, long value)
    {
        _firstSeenAt ??= timestamp;
        _latestTimestamp = timestamp;

        EvictOlderThan(timestamp - window);

        _points.Enqueue((timestamp, value));
        _sum += value;
    }

    void EvictOlderThan(DateTimeOffset cutoff)
    {
        while (_points.Count > 0 && _points.Peek().Timestamp < cutoff)
        {
            var (_, value) = _points.Dequeue();
            _sum -= value;
        }
    }
}
