namespace NiftySignal.Scoring;

/// <summary>
/// Weights for the ratio-based composite's five components (weekend build, 2026-09-09).
/// Starting equal at 0.2 each -- audit finding F37 (correlation-derived weights) needs several
/// live sessions of Ratio*Raw history first, not this weekend.
/// </summary>
public sealed record RatioScoreWeights(
    string Version,
    double NotionalVolumeRatio,
    double SizedOiFlowRatio,
    double ResidualDifference,
    double IvSkew25Delta,
    double SpreadRatioAtm)
{
    public static RatioScoreWeights Default { get; } = new(
        Version: "ratio-live-2026-09-09",
        NotionalVolumeRatio: 0.2,
        SizedOiFlowRatio: 0.2,
        ResidualDifference: 0.2,
        IvSkew25Delta: 0.2,
        SpreadRatioAtm: 0.2);

    public double Total => NotionalVolumeRatio + SizedOiFlowRatio + ResidualDifference + IvSkew25Delta + SpreadRatioAtm;
}
