namespace NiftySignal.Scoring;

/// <summary>
/// The 8 signed [-1,1] inputs to the Core score (2026-09-13 live-wiring plan A4), matching
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own per-cadence composition exactly.
/// All 8 are optional -- <see cref="CoreScoreCalculator"/> publishes a score once ANY weight is
/// present (`presentWeight > 0`), not a minimum count like the ratio composite's "3 of 5" gate;
/// see <see cref="CoreScoreCalculator.TryComputeRaw"/> for why this is deliberately more
/// permissive than <c>RatioScoreCalculator</c>. 7 of the 8 are session-rank-transformed by the
/// caller before being passed in here (<see cref="TrendReversion15m"/> is the one exception --
/// it's already naturally bounded [-1,1] by construction and is passed through unranked).
/// </summary>
public sealed record CoreScoreComponentInputs(
    double? DepthImbalance,
    double? ItmSkew,
    double? FutureCvdNet5Min,
    double? NotionalVolumeRatio,
    double? GammaExposure,
    double? TrendReversion15m,
    double? BasisChange,
    double? OiChangeDiff15m);
