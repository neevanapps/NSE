namespace NiftySignal.Scoring;

/// <summary>
/// The clipped z-scores that feed the composite score, one per named window in
/// <c>NiftySignal.Features.FeatureWindowLengths</c>. A component blocks the composite score
/// (null until it has a z-score) iff it's required -- weight &gt; 0 and not named as a
/// separate exception (currently just VixChange, which can be legitimately absent for a
/// day's whole instrument universe even at its real 0.05 weight) -- see
/// <c>CompositeScoreCalculator</c>'s <c>IsOptional</c>/<c>TryComputeRaw</c>. This is no longer
/// simply "the original six" (plan section 6): audit finding F47 (2026-09-10) made
/// <see cref="PriceMomentumZ"/> optional once its weight was cut to 0 by F4, since a component
/// contributing nothing to the score has no business blocking every other component's warm-up.
/// <see cref="GammaExposureZ"/>, <see cref="VolumePcrZ"/>, and <see cref="SpreadRatioZ"/> are
/// optional the same way (weight 0.0 today, diagnostic-only -- see ScoreWeights.Default's own
/// doc comment).
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
    double? VolumePcrZ = null,
    double? SpreadRatioZ = null,
    double? VannaExposureZ = null,
    double? CharmExposureZ = null,
    double? CvdProxyZ = null,
    double? StraddleRichnessZ = null);
