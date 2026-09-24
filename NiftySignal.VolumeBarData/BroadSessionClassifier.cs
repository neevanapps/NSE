namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, A-DTE-validation experiment. A coarser, 3-group session split than the existing
/// 7-bucket <see cref="Vc0DteBehaviorSummary.SessionBucket"/> (kept unmodified and still used
/// everywhere else) -- requested explicitly to avoid over-segmenting the already-small non-0-DTE
/// sample. Pure, deterministic, no new session-boundary convention beyond the three the task
/// itself specified.
/// </summary>
public static class BroadSessionClassifier
{
    public static string Classify(DateTimeOffset timestamp, TimeSpan istOffset)
    {
        var ist = timestamp.ToOffset(istOffset).TimeOfDay;
        if (ist < new TimeSpan(12, 0, 0)) { return "Morning (09:15-12:00)"; }
        if (ist < new TimeSpan(14, 0, 0)) { return "Midday (12:00-14:00)"; }
        return "Afternoon (14:00-15:15)";
    }
}
