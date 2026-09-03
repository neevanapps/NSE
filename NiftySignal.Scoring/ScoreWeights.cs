namespace NiftySignal.Scoring;

/// <summary>
/// A versioned weight set for the six components in plan section 6. Weights live in
/// config in the real system and are versioned there; this record is just the shape --
/// every persisted score records the <see cref="Version"/> that produced it, so a later
/// re-tune is always traceable back to which weights were live when a signal fired.
/// </summary>
public sealed record ScoreWeights(
    string Version,
    double OiBuildupNet,
    double Pcr,
    double FuturesBasis,
    double IvSkew,
    double PriceMomentum,
    double DepthImbalance)
{
    /// <summary>The plan's own starting weights (section 6): 25/20/15/15/15/10.</summary>
    public static ScoreWeights Default { get; } = new(
        Version: "plan-section-6-default",
        OiBuildupNet: 0.25,
        Pcr: 0.20,
        FuturesBasis: 0.15,
        IvSkew: 0.15,
        PriceMomentum: 0.15,
        DepthImbalance: 0.10);

    public double Total => OiBuildupNet + Pcr + FuturesBasis + IvSkew + PriceMomentum + DepthImbalance;
}
