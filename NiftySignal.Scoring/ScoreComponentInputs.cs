namespace NiftySignal.Scoring;

/// <summary>
/// The clipped z-scores that feed the composite score, one per named window in
/// <c>NiftySignal.Features.FeatureWindowLengths</c>. The original six (plan section 6) are
/// each null only when that metric's <c>WelfordRollingWindow</c> hasn't warmed up yet -- the
/// composite score is not emitted until every one of those six is non-null (plan section
/// 5.4). <see cref="VixChangeZ"/> (2026-09-04), <see cref="GammaExposureZ"/> and
/// <see cref="VolumePcrZ"/> (both 2026-09-07) are different: all three are genuinely optional
/// -- silently excluded from the composite (contributes 0) rather than blocking it. VIX
/// tracking can be absent for a given day's instrument universe entirely; GammaExposure and
/// VolumePcr both start at weight 0.0 (diagnostic-only, no validated evidence yet -- see
/// ScoreWeights.Default's own doc comment) so neither can ever block the six components that
/// are actually trading. See <c>CompositeScoreCalculator</c>'s optional-component handling.
/// </summary>
public sealed record ScoreComponentInputs(
    double? OiBuildupNetZ,
    double? PcrZ,
    double? FuturesBasisZ,
    double? IvSkewZ,
    double? PriceMomentumZ,
    double? DepthImbalanceZ,
    double? VixChangeZ = null,
    double? GammaExposureZ = null,
    double? VolumePcrZ = null);
