namespace NiftySignal.Scoring;

/// <summary>
/// The five clipped [-1,1] inputs to the ratio-based composite (weekend build, 2026-09-09).
/// All five are optional, unlike <see cref="ScoreComponentInputs"/>' six-required/eight-optional
/// split -- <see cref="RatioScoreCalculator"/> publishes a score once at least 3 of 5 are
/// non-null, not "all required ones warm." See <see cref="RatioScoreCalculator"/>.ComputeRaw
/// for the exact renormalization.
/// </summary>
public sealed record RatioComponentInputs(
    double? NotionalVolumeRatio,
    double? SizedOiFlowRatio,
    double? ResidualDifference,
    double? IvSkew25Delta,
    double? SpreadRatioAtm);
