namespace NiftySignal.Domain.Enums;

/// <summary>
/// Mirrors <c>NiftySignal.Features.OiBuildupClassification</c> -- that's where the actual
/// classification logic lives (<c>OiBuildupClassifier.Classify</c>, reused as-is here, not
/// reimplemented), but Domain can't reference Features (Features references Domain, not the
/// other way around), so <see cref="StrikeSnapshot"/> needs its own copy of the shape. Named
/// differently from the Features enum (not just a different namespace) because Host and
/// Dashboard both import Domain.Enums and Features together, and two same-named enums in scope
/// at once is an unresolvable ambiguous-reference error, not just visual noise. Kept in lockstep
/// with the Features enum by LiveFeatureEngine's mapping at the single call site that persists it.
/// </summary>
public enum OiBuildupQuadrant
{
    Neutral,
    LongBuildup,
    ShortBuildup,
    LongUnwinding,
    ShortCovering,
}
