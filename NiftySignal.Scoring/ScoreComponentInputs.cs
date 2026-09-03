namespace NiftySignal.Scoring;

/// <summary>
/// The six clipped z-scores that feed the composite score (plan section 6), one per named
/// window in <c>NiftySignal.Features.FeatureWindowLengths</c>. Each is null when that
/// metric's <c>WelfordRollingWindow</c> hasn't warmed up yet -- the composite score is not
/// emitted until every component here is non-null (plan section 5.4).
/// </summary>
public sealed record ScoreComponentInputs(
    double? OiBuildupNetZ,
    double? PcrZ,
    double? FuturesBasisZ,
    double? IvSkewZ,
    double? PriceMomentumZ,
    double? DepthImbalanceZ);
