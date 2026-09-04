namespace NiftySignal.Scoring;

/// <summary>
/// A versioned weight set for the components. Weights live in config in the real system and
/// are versioned there; this record is just the shape -- every persisted score records the
/// <see cref="Version"/> that produced it, so a later re-tune is always traceable back to
/// which weights were live when a signal fired.
/// </summary>
public sealed record ScoreWeights(
    string Version,
    double OiBuildupNet,
    double Pcr,
    double FuturesBasis,
    double IvSkew,
    double PriceMomentum,
    double DepthImbalance,
    double VixChange = 0.0)
{
    /// <summary>
    /// The plan's own starting weights (section 6: 25/20/15/15/15/10), rescaled by 0.95 to
    /// make room for a deliberately conservative 0.05 VixChange weight (2026-09-04) -- VIX
    /// is a genuinely new, empirically-unvalidated signal source here (unlike k, there's no
    /// accumulated live data yet to derive a weight from), so it starts small rather than
    /// displacing a proportional share of an already-tuned six. Still sums to 1.0; see
    /// <see cref="Total"/>'s own test coverage.
    /// </summary>
    public static ScoreWeights Default { get; } = new(
        Version: "plan-section-6-default+vix-2026-09-04",
        OiBuildupNet: 0.2375,
        Pcr: 0.19,
        FuturesBasis: 0.1425,
        IvSkew: 0.1425,
        PriceMomentum: 0.1425,
        DepthImbalance: 0.095,
        VixChange: 0.05);

    public double Total => OiBuildupNet + Pcr + FuturesBasis + IvSkew + PriceMomentum + DepthImbalance + VixChange;
}
