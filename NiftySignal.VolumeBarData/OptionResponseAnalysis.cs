namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, option-response validation (see docs/VolumeCandle_0DTE_Findings.md's "Option
/// Response Validation" section). HARD FREEZE: this file adds exactly ONE new pure calculation --
/// classifying WHY a <see cref="UnderlyingOptionRelationshipSummary.SeriesChange"/> came back
/// unavailable. Every actual return computation reuses
/// <see cref="UnderlyingOptionRelationshipSummary.ComputeCeChange"/>/<see cref="UnderlyingOptionRelationshipSummary.ComputePeChange"/>/
/// <see cref="UnderlyingOptionRelationshipSummary.ComputeFuturesChange"/> and
/// <see cref="ForwardValidationAnalysis.ComputeForwardMetrics"/>/<see cref="ForwardValidationAnalysis.ComputeTerciles"/>/
/// <see cref="ConditionalMovementAnalysis.ClassifyTercileBucket"/>, all unchanged.
/// </summary>
public static class OptionResponseAnalysis
{
    /// <summary>
    /// Explains why a forward option change could not be computed (spec section 2's own explicit
    /// "exclude observations where the response cannot be measured reliably... explicitly report
    /// exclusions" requirement) -- reads ONLY the flags <see cref="UnderlyingOptionRelationshipSummary.ComputeCeChange"/>/
    /// <see cref="UnderlyingOptionRelationshipSummary.ComputePeChange"/> already return, never
    /// recomputes or second-guesses them.
    /// </summary>
    public static string ClassifyExclusionReason(UnderlyingOptionRelationshipSummary.SeriesChange change)
    {
        if (change.PercentChange is not null)
        {
            return "Available";
        }
        if (!change.SameContract)
        {
            return "ContractTransition";
        }
        if (!change.StartAvailable || !change.EndAvailable)
        {
            return "MissingOrStaleData";
        }
        return "Unavailable"; // defensive -- the three checks above already cover every reason ComputeCeChange/ComputePeChange can return a null PercentChange.
    }
}
