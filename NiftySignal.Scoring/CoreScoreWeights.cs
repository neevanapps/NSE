namespace NiftySignal.Scoring;

/// <summary>
/// The 8-term Core score's weights (2026-09-13 live-wiring plan A4), ported directly from
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own `CoreScoreWeights` record -- same
/// 8 named weights, same values, same <see cref="CoreScoreCalculator.DefaultK"/>. Values are
/// copied exactly from the backtest, not re-derived: `DepthImbalance`/`ItmSkew` are the two
/// CONFIRMED candidates (`ItmSkew` cut in half after the 11 Sep investigation found it locked
/// bearish for an entire session); the rest are watch/provisionally-promoted candidates, several
/// combined into this score for the first time here. See `docs/replication_plan.md` for the full
/// per-term rationale, band, and sign for each.
/// </summary>
public sealed record CoreScoreWeights(
    string Version,
    double DepthImbalance,
    double ItmSkew,
    double FutureCvdNet5Min,
    double NotionalVolumeRatio,
    double GammaExposure,
    double TrendReversion15m,
    double BasisChange,
    double OiChangeDiff15m)
{
    public static CoreScoreWeights Default { get; } = new(
        Version: "core-score-live-2026-09-13",
        DepthImbalance: 0.25,
        ItmSkew: 0.065,
        FutureCvdNet5Min: 0.12,
        NotionalVolumeRatio: 0.14,
        GammaExposure: 0.06,
        TrendReversion15m: 0.10,
        BasisChange: 0.08,
        OiChangeDiff15m: 0.10);

    /// <summary>Sums to 0.915, not 1.00 -- left as-is rather than silently rescaled, matching the backtest's own `Total` doc comment ("left as-is rather than silently rescaled"). <see cref="CoreScoreCalculator"/> renormalizes by whichever weight is actually present each cadence, so this doesn't need to sum to 1.</summary>
    public double Total => DepthImbalance + ItmSkew + FutureCvdNet5Min + NotionalVolumeRatio
        + GammaExposure + TrendReversion15m + BasisChange + OiChangeDiff15m;
}
