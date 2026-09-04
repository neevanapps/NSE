namespace NiftySignal.Scoring;

/// <summary>
/// The clipped z-scores that feed the composite score, one per named window in
/// <c>NiftySignal.Features.FeatureWindowLengths</c>. The original six (plan section 6) are
/// each null only when that metric's <c>WelfordRollingWindow</c> hasn't warmed up yet -- the
/// composite score is not emitted until every one of those six is non-null (plan section
/// 5.4). <see cref="VixChangeZ"/> (2026-09-04) is different: it's genuinely optional --
/// silently excluded from the composite (contributes 0) rather than blocking it, since VIX
/// tracking can be absent for a given day's instrument universe entirely, or still warming
/// up long after the original six already have hours of history. See
/// <c>CompositeScoreCalculator</c>'s <c>VixComponentName</c> handling.
/// </summary>
public sealed record ScoreComponentInputs(
    double? OiBuildupNetZ,
    double? PcrZ,
    double? FuturesBasisZ,
    double? IvSkewZ,
    double? PriceMomentumZ,
    double? DepthImbalanceZ,
    double? VixChangeZ = null);
