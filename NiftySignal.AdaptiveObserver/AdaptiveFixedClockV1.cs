namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// The fixed futures-volume clock contract: every completed bar holds exactly 13,650 NIFTY futures contracts (exact-volume split semantics unchanged), the clock starts at the
/// 09:15 IST open, and no opening-volume estimator selects the bar size. It is a NEW model identity; <c>adaptive-v1</c> sessions are never reinterpreted.
/// <para>The 09:30 residual anchor keeps its own semantics and is frozen separately (by the engine, causally, at the first input after 09:30); before it exists residual values are unavailable.</para>
/// </summary>
public static class AdaptiveFixedClockV1
{
    public const string ModelVersion = "adaptive-fixed13650-v1";
    public const long BarVolume = 13_650;
    public const int RollingWindowBars = 10;
    public const string EstimatorName = "fixed-volume-13650-v1";

    /// <summary>
    /// PROVISIONAL starting guess (not derived from data): the fewest prior fixed-clock sessions that may calibrate the daily Strong threshold. With fewer, Strong/Weak is left
    /// uncalibrated (threshold null, no Strong/Weak labels) rather than borrowing the adaptive-v1 distribution.
    /// </summary>
    public const int MinimumPriorSessionsForStrongThreshold = 10;

    /// <summary>How many of the most recent prior trading days the history bootstrap rebuilds at most.</summary>
    public const int MaxHistoryDaysToRebuild = 30;

    public static bool IsFixedClock(string? modelVersion) => string.Equals(modelVersion, ModelVersion, StringComparison.Ordinal);

    /// <summary>Every model identity the Dashboard / Host can read.</summary>
    public static bool IsSupported(string? modelVersion) => IsFixedClock(modelVersion) || string.Equals(modelVersion, OpeningVolumeProjectionV1.ModelVersion, StringComparison.Ordinal);

    public static string Describe(string? modelVersion) => IsFixedClock(modelVersion)
        ? "Fixed Volume 13,650 | start 09:15"
        : string.Equals(modelVersion, OpeningVolumeProjectionV1.ModelVersion, StringComparison.Ordinal) ? "Adaptive v1 (opening-volume estimate)" : modelVersion ?? "unknown";
}

/// <summary>How the Host picks the clock of a NEW session. Existing sessions always keep the model they were frozen with.</summary>
public enum AdaptiveClockMode
{
    /// <summary>Legacy behaviour: bar size estimated from the 09:15-09:30 volume, session frozen at 09:30.</summary>
    OpeningVolumeV1 = 0,

    /// <summary>Fixed 13,650-contract bars from 09:15.</summary>
    Fixed13650 = 1,
}
