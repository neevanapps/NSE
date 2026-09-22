namespace NiftySignal.Scoring;

/// <summary>
/// TrendReversion15m: signed net change / path length over a trailing real-time window, negated (a
/// clean recent trend reads as a REVERSION signal, not continuation -- the confirmed, if
/// counter-intuitive, sign from `docs/SCORE_CANDIDATES.md`). Already bounded [-1,1] by construction
/// (|net| &lt;= path always), so callers use the result directly, no session-rank needed.
///
/// 2026-09-17, Phase 2 unification: ported from the identical queue-based sliding window both
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs` and `NiftySignal.Host/LiveFeatureEngine.cs`
/// already had (diffed directly before extracting -- same enqueue-conditionally,
/// evict-and-read-unconditionally shape, same 15-minute window). Unlike
/// <see cref="CoreScoreRawFormulas"/>'s stateless functions, this one legitimately needs to be a
/// shared STATEFUL class (one instance per session/day, same lifetime as
/// `NiftySignal.Features.SessionRankTracker`) since the sliding window itself -- not just the
/// formula applied to it -- is exactly what both sides had duplicated.
/// </summary>
public sealed class CoreScoreTrendReversionTracker(TimeSpan window)
{
    readonly Queue<(DateTimeOffset Timestamp, double Change)> _window = new();

    /// <param name="now">This cadence's timestamp -- used both to enqueue and to evict entries older than <paramref name="now"/> minus the tracker's window.</param>
    /// <param name="change">This cadence's own signed change, or null if this cadence has none (e.g. no fresh tick this cadence). NOT enqueued when null -- but eviction, and the net/path-length read below, still run unconditionally every call, so a quiet cadence still reads whatever the window already holds from earlier cadences instead of going null outright.</param>
    public double? Observe(DateTimeOffset now, double? change)
    {
        if (change is { } c)
        {
            _window.Enqueue((now, c));
        }

        while (_window.Count > 0 && now - _window.Peek().Timestamp > window)
        {
            _window.Dequeue();
        }

        if (_window.Count == 0)
        {
            return null;
        }

        var net = _window.Sum(w => w.Change);
        var pathLength = _window.Sum(w => Math.Abs(w.Change));
        return pathLength > 0 ? -1.0 * (net / pathLength) : null;
    }
}
