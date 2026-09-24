namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, 12:00-14:00 market-state diagnostic (see
/// docs/VolumeCandle_0DTE_Findings.md's "12:00-14:00 Market-State Diagnostic" section). Two
/// small, pure, deterministic calculations, both simple combinations of fields the frozen
/// relationship/event-bar infrastructure already produces -- no new signal, no new bar
/// construction, no approximate reconstruction of anything not already available.
/// </summary>
public static class MarketStateDiagnostics
{
    /// <summary>
    /// One Pattern-A trade's market-state features at/immediately before the signal, plus its
    /// already-established post-exit outcome (reused, not recomputed with new logic). Some
    /// requested variables (CVD-net, OFI-net, depth imbalance, top-of-book imbalance) are
    /// deliberately NOT included here -- they exist elsewhere in this codebase but on a
    /// structurally different bar clock (a separate, earlier score-candidate research track),
    /// and reconstructing them onto the frozen relationship's own event boundaries would be an
    /// approximate cross-framework reconstruction, which the task explicitly forbids. Reported
    /// as unavailable in the console output instead.
    /// </summary>
    public sealed record MarketStateRow(
        DateOnly Date, string SessionBucket, DateTimeOffset SignalTimestamp,
        decimal? FuturesChange1, decimal FuturesRange, double FuturesEventDurationMs, long FuturesVolume, double? VolumeRate,
        decimal FuturesCloseMinusVwap, decimal? PriorMovement3,
        decimal? CeChange1, decimal? PeChange1, decimal? CePeDivergenceMagnitude,
        string PriorRawRelationshipState, string PriorRawStateClass,
        int EpisodeLength, double EpisodeDurationMs, int EventIndexWithinEpisode,
        double? MinutesSincePreviousA, double? MinutesSincePreviousB, double? MinutesSincePreviousTransition,
        decimal?[] PostExitReturn, decimal? MfeAfterExit, decimal? MaeAfterExit, string ContinuationLabelAt10m);

    /// <summary>Contracts per second -- a simple ratio of two already-existing fields (FuturesVolume, FuturesEventDurationMs), not a new invented metric. Null when duration is not positive.</summary>
    public static double? ComputeVolumeRate(long volume, double durationMs)
        => durationMs > 0 ? volume / (durationMs / 1000.0) : null;

    /// <summary>Simple sum of the two existing per-leg change magnitudes -- documented plainly as a sum, not a new composite indicator. Null if either leg's change is unavailable.</summary>
    public static decimal? ComputeCePeDivergenceMagnitude(decimal? ceChange1, decimal? peChange1)
        => ceChange1 is { } ce && peChange1 is { } pe ? Math.Abs(ce) + Math.Abs(pe) : null;

    /// <summary>"PatternA"/"PatternB" verbatim, or "Other" for every other raw RelationshipCategory (neutral/incomplete/transition states) -- a simple 3-way classification of an already-existing string field.</summary>
    public static string ClassifyPriorStateClass(string priorRawCategory) => priorRawCategory switch
    {
        var c when c == ForwardValidationAnalysis.State2_BullishDivergence_PatternA => "PatternA",
        var c when c == ForwardValidationAnalysis.State4_BearishDivergence_PatternB => "PatternB",
        _ => "Other",
    };
}
