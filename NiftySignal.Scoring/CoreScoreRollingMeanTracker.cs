namespace NiftySignal.Scoring;

/// <summary>
/// Experimental (2026-09-16, scoping DepthImbalance/NotionalVolumeRatio smoothing per audit
/// finding F-A follow-up): a plain rolling-time-window MEAN of a per-cadence STATE value (not a
/// flow -- see <see cref="CoreScoreRollingNetDiffTracker"/> for the sum-based flow equivalent),
/// same queue/evict shape as <see cref="CoreScoreTrendReversionTracker"/>. A cadence with no value
/// this tick is NOT enqueued (skip, don't zero-fill -- same "quiet cadence" convention every
/// rolling window in this codebase already follows, e.g. audit finding F51's own per-metric FIFO
/// smoothing), but eviction and the mean itself still run every call, so a quiet cadence still
/// reads whatever the window already holds rather than going null outright.
///
/// Not yet used by any live or default-weighted path -- this class exists purely so
/// <c>CoreScoreOptionSimulator</c>'s <c>--depth-imbalance-smoothing-minutes=N</c> experimental
/// flag has something to smooth <c>DepthImbalance</c>'s otherwise-instant (single 15s cadence) raw
/// value with, for backtesting before any live change is proposed. See
/// <c>docs/SCORE_CANDIDATES.md</c>'s DepthImbalance/NotionalVolumeRatio smoothing scope for the
/// correlation evidence that motivated testing this at all.
/// </summary>
public sealed class CoreScoreRollingMeanTracker(TimeSpan window)
{
    readonly Queue<(DateTimeOffset Timestamp, double Value)> _window = new();

    /// <param name="now">This cadence's timestamp -- used both to enqueue and to evict entries older than <paramref name="now"/> minus the tracker's window.</param>
    /// <param name="value">This cadence's own value, or null if this cadence has none. NOT enqueued when null.</param>
    public double? Observe(DateTimeOffset now, double? value)
    {
        if (value is { } v)
        {
            _window.Enqueue((now, v));
        }

        while (_window.Count > 0 && now - _window.Peek().Timestamp > window)
        {
            _window.Dequeue();
        }

        return _window.Count > 0 ? _window.Average(w => w.Value) : null;
    }
}
