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
    double VixChange = 0.0,
    double GammaExposure = 0.0,
    double VolumePcr = 0.0,
    double SpreadRatio = 0.0)
{
    /// <summary>
    /// The plan's own starting weights (section 6: 25/20/15/15/15/10), rescaled by 0.95 to
    /// make room for a deliberately conservative 0.05 VixChange weight (2026-09-04) -- VIX
    /// is a genuinely new, empirically-unvalidated signal source here (unlike k, there's no
    /// accumulated live data yet to derive a weight from), so it starts small rather than
    /// displacing a proportional share of an already-tuned six.
    ///
    /// PriceMomentum cut from 0.1425 to 0.07 (2026-09-07), after the first full live session's
    /// data showed it correlates strongly with the price move that already happened (backward
    /// r=+0.48 at 5min) but not with what comes next (forward r=-0.08) -- it's a lagging
    /// confirmation signal, not a leading one, consistent with a live-caught incident the same
    /// day where a momentum dip dragged the score down through a move that turned out to still
    /// be flat-to-up. The freed 0.0725 went to OiBuildupNet and DepthImbalance -- the two
    /// components whose raw values actually change most cadences (see the same day's staleness
    /// check), rather than to Pcr/FuturesBasis/IvSkew, whose apparent forward correlation that
    /// day is suspected to be a single trending session's spurious correlation, not signal.
    /// Still sums to 1.0; see <see cref="Total"/>'s own test coverage.
    /// </summary>
    /// <summary>
    /// GammaExposure (2026-09-07) starts at weight 0.0 -- unlike VixChange, which launched with
    /// a small nonzero weight, GEX has zero days of validated evidence yet (the day-one attempt
    /// to check it used strike_snapshots' persisted ATM+/-2 band, too narrow a slice of the
    /// chain to mean anything -- see ComputeGammaExposure, which is computed from the *full*
    /// nearest-expiry chain instead, precisely so a real multi-day evaluation becomes possible).
    /// It's included in the composite and persisted every cadence starting now purely so there's
    /// a real GammaExposureZ history to evaluate before ever giving it a nonzero weight.
    /// </summary>
    /// <summary>
    /// VolumePcr (2026-09-07) starts at weight 0.0 for the same reason GammaExposure does --
    /// zero days of validated evidence. Unlike GammaExposure, a same-day offline check found
    /// mixed evidence even on methodology: a notional-weighted version of the existing
    /// (count-weighted) Pcr correlated far more strongly with forward price than count-weighted
    /// Pcr does, but for traded volume specifically, count- and notional-weighted put/call
    /// ratios actually disagreed in sign at the 15-minute horizon. VolumePcr launches
    /// notional-weighted (matching the stronger OI-based result, and the more economically
    /// meaningful reading -- rupee value traded, not just contract count), but that
    /// count-vs-notional disagreement is exactly the kind of thing worth watching once there's
    /// real multi-day VolumePcrZ history to look at.
    /// </summary>
    /// <summary>
    /// SpreadRatio (2026-09-07) starts at weight 0.0 for the same reason GammaExposure and
    /// VolumePcr do -- zero days of validated evidence. Unlike VolumePcr's mixed methodology
    /// signal, this one came back clean on a same-day check: put-spread/call-spread ratio
    /// correlated +0.315 with the 15-minute forward price move, consistently signed across
    /// 1/5/15-minute horizons (unlike VolumePcr's count-vs-notional sign disagreement), and on
    /// a much less autocorrelated series than the raw-level metrics whose apparent correlation
    /// turned out to be a single trending day's spurious signal -- the cleanest same-day result
    /// of the three new diagnostic components. Still launches at 0.0 regardless: one day is one
    /// day, not multi-day validated evidence.
    /// </summary>
    public static ScoreWeights Default { get; } = new(
        Version: "plan-section-6-default+vix-2026-09-04+momentum-cut-2026-09-07+diagnostics-2026-09-07",
        OiBuildupNet: 0.2775,
        Pcr: 0.19,
        FuturesBasis: 0.1425,
        IvSkew: 0.1425,
        PriceMomentum: 0.07,
        DepthImbalance: 0.1275,
        VixChange: 0.05,
        GammaExposure: 0.0,
        VolumePcr: 0.0,
        SpreadRatio: 0.0);

    public double Total => OiBuildupNet + Pcr + FuturesBasis + IvSkew + PriceMomentum + DepthImbalance + VixChange + GammaExposure + VolumePcr + SpreadRatio;
}
