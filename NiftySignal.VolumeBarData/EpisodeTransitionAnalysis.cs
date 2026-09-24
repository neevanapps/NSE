namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, Pre -&gt; Pattern -&gt; Post transition analysis (see
/// docs/VolumeCandle_0DTE_Findings.md's "Pre-Pattern-Post Transition Analysis" section). Two
/// small, pure, deterministic calculations, both over values the EXISTING
/// <see cref="UnderlyingOptionRelationshipSummary"/> Compute*Change functions and
/// <see cref="RelationshipDirectionExtensions.Classify"/> already produce -- no new return/
/// change formula, no new event-bar/pattern/control definition.
/// </summary>
public static class EpisodeTransitionAnalysis
{
    /// <summary>
    /// Re-orients a raw signed percent-change so a POSITIVE result always means "moved in the
    /// pattern's own defining contemporaneous direction" (Up for Pattern A, Down for Pattern B)
    /// -- lets pre-pattern movement be compared on one sign convention regardless of which
    /// pattern produced it. <paramref name="patternIsUp"/> is true for Pattern A, false for
    /// Pattern B.
    /// </summary>
    public static decimal? OrientToDefiningDirection(decimal? rawPercentChange, bool patternIsUp)
        => rawPercentChange is null ? null : patternIsUp ? rawPercentChange : -rawPercentChange;

    /// <summary>
    /// Re-orients a raw signed percent-change so a POSITIVE result always means "moved OPPOSITE
    /// the pattern's own defining contemporaneous direction" (the reversal direction: Down for
    /// Pattern A, Up for Pattern B) -- used for post-pattern movement.
    /// </summary>
    public static decimal? OrientToOpposingDirection(decimal? rawPercentChange, bool patternIsUp)
        => rawPercentChange is null ? null : patternIsUp ? -rawPercentChange : rawPercentChange;

    /// <summary>
    /// Sign-comparison divergence over one window, reused for both pre- and post-pattern
    /// windows -- built entirely from the EXISTING <see cref="RelationshipDirectionExtensions.Classify"/>
    /// (same Up/Down/Flat/Unavailable convention the pattern's own definition already uses), not
    /// a new sign rule. "Divergent" if CE and PE classify to opposite non-flat directions,
    /// "Aligned" if they classify the same, "Flat"/"Unavailable" otherwise.
    /// </summary>
    public static string ClassifyCePeDivergence(decimal? ceChange, decimal? peChange)
    {
        var ceDir = RelationshipDirectionExtensions.Classify(ceChange);
        var peDir = RelationshipDirectionExtensions.Classify(peChange);
        if (ceDir == RelationshipDirection.Unavailable || peDir == RelationshipDirection.Unavailable) { return "Unavailable"; }
        if (ceDir == RelationshipDirection.Flat || peDir == RelationshipDirection.Flat) { return "Flat"; }
        return ceDir != peDir ? "Divergent" : "Aligned";
    }
}
