namespace NiftySignal.Scoring;

/// <summary>
/// OiChangeDiff15m: rolling real-time-window SUM of (call - put) (2026-09-17, Phase 2 unification).
/// Ported from the identical queue-based sliding window both
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs` and
/// `NiftySignal.Host/LiveFeatureEngine.cs` already had (diffed directly before extracting -- same
/// 15-minute window, same unconditional enqueue every cadence, unlike
/// <see cref="CoreScoreTrendReversionTracker"/>'s CONDITIONAL enqueue -- there is always a real
/// call/put delta pair to record here, even when both happen to be zero, so eviction and the sum
/// read never need a separate "quiet cadence" case).
///
/// Takes <see cref="long"/> call/put values (matching both sides' own per-cadence OI-delta sums,
/// which are exact integer arithmetic) and casts to <see cref="double"/> only once, per entry, at
/// the point of summing across the window -- same order both sides already used
/// (<c>(double)(w.Call - w.Put)</c>), preserved here rather than casting earlier and risking a
/// repeat of the exact class of bug <see cref="CoreScoreRawFormulas.BasisChange"/> was written to
/// prevent.
/// </summary>
public sealed class CoreScoreRollingNetDiffTracker(TimeSpan window)
{
    readonly Queue<(DateTimeOffset Timestamp, long Call, long Put)> _window = new();

    public double Observe(DateTimeOffset now, long call, long put)
    {
        _window.Enqueue((now, call, put));

        while (_window.Count > 0 && now - _window.Peek().Timestamp > window)
        {
            _window.Dequeue();
        }

        return _window.Sum(w => (double)(w.Call - w.Put));
    }
}
