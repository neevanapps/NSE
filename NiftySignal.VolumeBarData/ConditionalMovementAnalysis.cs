namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, incremental-information / conditional analysis (see
/// docs/VolumeCandle_0DTE_Findings.md's "Conditional Analysis" section). HARD FREEZE: this file
/// adds exactly ONE new pure calculation -- tercile-bucket classification. Every actual return
/// computation reuses <see cref="UnderlyingOptionRelationshipSummary.ComputeFuturesChange"/>
/// (prior AND forward movement are the SAME function, just called with a negative vs positive
/// horizon) and <see cref="ForwardValidationAnalysis.ComputeTerciles"/>/
/// <see cref="ForwardValidationAnalysis.ComputeForwardMetrics"/>, all unchanged.
///
/// Control-group design (confirmed with the user before implementation, given the explicit
/// "stop and ask if ambiguous" instruction): for a given pattern, the control population is
/// EVERY event sharing the pattern's own CONTEMPORANEOUS 1-bar direction (Up for Pattern A, Down
/// for Pattern B) that is NOT the pattern itself and is not a data-incomplete/contract-transition
/// row, with terciles of |prior-N-event movement| computed WITHIN that same direction-constrained
/// population (so Pattern A and its control share one tercile scale; Pattern B and its own control
/// share a separate one) -- holding contemporaneous direction constant is necessary because the
/// forensic task already showed Up-only and Down-only populations have systematically different
/// forward tilts on their own, independent of CE/PE.
/// </summary>
public static class ConditionalMovementAnalysis
{
    /// <summary>Low/Mid/High tercile label from a value and the pooled Low33/High67 thresholds already computed (via <see cref="ForwardValidationAnalysis.ComputeTerciles"/>) over the RELEVANT population -- never re-derives its own thresholds, just classifies against ones the caller already computed.</summary>
    public static string ClassifyTercileBucket(decimal absoluteValue, decimal low33, decimal high67) => absoluteValue switch
    {
        var v when v <= low33 => "Low",
        var v when v >= high67 => "High",
        _ => "Mid",
    };
}
