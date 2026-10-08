namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Pure read-projection helpers for the compact adaptive grids. No database or wall-clock state;
/// every value is derived from already-persisted fields (08-Oct plan, section 5.2).
/// </summary>
public static class AdaptiveGridProjection
{
    /// <summary>
    /// Urgency = bar volume / elapsed seconds. A zero-duration split bar (or any non-positive /
    /// non-finite duration) is a valid bar whose rate is undefined, so it is unavailable rather
    /// than infinite. Derived on read; deliberately not persisted.
    /// </summary>
    public static double? Urgency(long volume, double durationSeconds) =>
        NiftySignal.AdaptiveObserver.AdaptiveMetricMath.Urgency(volume, durationSeconds);
}
