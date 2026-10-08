namespace NiftySignal.AdaptiveObserver;

/// <summary>Tiny pure metric helpers shared by the Dashboard read projection and the commentary frame builder so both use one implementation.</summary>
public static class AdaptiveMetricMath
{
    /// <summary>Urgency = bar volume / elapsed seconds. A non-positive or non-finite duration (zero-duration split bar) is unavailable, never infinite.</summary>
    public static double? Urgency(long volume, double durationSeconds) =>
        double.IsFinite(durationSeconds) && durationSeconds > 0d ? volume / durationSeconds : null;
}
