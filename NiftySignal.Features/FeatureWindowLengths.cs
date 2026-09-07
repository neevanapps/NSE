namespace NiftySignal.Features;

/// <summary>
/// Per-metric rolling-window lengths -- originally plan section 5.4's starting values,
/// tuned from there as live behavior warrants (2026-09-04: IvSkew shortened from 60 to 15
/// minutes so it warms up in the same session it's tested in, not the next day).
/// </summary>
public static class FeatureWindowLengths
{
    /// <summary>Fast-moving, noisy.</summary>
    public static readonly TimeSpan DepthImbalance = TimeSpan.FromMinutes(5);

    /// <summary>Intraday responsiveness.</summary>
    public static readonly TimeSpan PriceMomentum = TimeSpan.FromMinutes(15);

    /// <summary>Slower-moving structural signal.</summary>
    public static readonly TimeSpan Pcr = TimeSpan.FromMinutes(30);

    /// <summary>OI updates are not tick-frequency.</summary>
    public static readonly TimeSpan OiBuildupNet = TimeSpan.FromMinutes(30);

    /// <summary>Slow structural signal.</summary>
    public static readonly TimeSpan IvSkew = TimeSpan.FromMinutes(15);

    /// <summary>Moderate.</summary>
    public static readonly TimeSpan FuturesBasis = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The lookback used to compute the *raw* VIX change ("VIX now minus VIX this-long-ago").
    /// VIX ticks slowly (observed ~20-25s between updates, often with no price change) -- a
    /// short lookback would mostly read zero. Distinct from <see cref="VixChangeZScoreWindow"/>
    /// below, which governs how that raw value gets z-scored, not how it's computed.
    /// </summary>
    public static readonly TimeSpan VixChange = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Rolling window the raw VixChange value (above) is z-scored against. Deliberately much
    /// longer than the 30-minute raw lookback (2026-09-07 live-caught): since each new 15s
    /// sample's 30-minute lookback overlaps the previous one by all but 15 seconds, z-scoring
    /// against a window the *same* length as the raw lookback compares an almost-fully-
    /// autocorrelated series against itself -- during any stretch where VIX drifts smoothly,
    /// that series has almost no internal variance, so the window's StdDev collapses toward
    /// zero and an ordinary subsequent move slams into the +/-3 clip. A window several times
    /// longer spans enough distinct regimes (calm stretches and active ones) that a single
    /// flat patch can't dominate the variance estimate. Safe to warm up on a normal timescale
    /// despite the length -- LiveFeatureEngine.SeedHistory replays persisted VixChangeRaw
    /// history into this window on restart rather than waiting on real time.
    /// </summary>
    public static readonly TimeSpan VixChangeZScoreWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Net gamma exposure across the full nearest-expiry chain (2026-09-07, diagnostic-only --
    /// see ScoreWeights.Default's GammaExposure weight). Changes mostly as OI shifts (OI
    /// updates are not tick-frequency, same reasoning as OiBuildupNet) rather than as gamma
    /// itself, since gamma moves smoothly with spot.
    /// </summary>
    public static readonly TimeSpan GammaExposure = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Notional (rupee-value, not contract-count) put/call traded-volume ratio (2026-09-07,
    /// diagnostic-only -- see ScoreWeights.Default's VolumePcr weight). A genuine interval
    /// delta like OiBuildupNet and GammaExposure, not a point-in-time read -- same 30-minute
    /// window as the existing (contract-count) Pcr, its closest sibling.
    /// </summary>
    public static readonly TimeSpan VolumePcr = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Put-spread/call-spread ratio (2026-09-07, diagnostic-only -- see ScoreWeights.Default's
    /// SpreadRatio weight). A point-in-time read like IvSkew (its closest structural sibling --
    /// both compare something across the put vs call side), not an interval delta, so it's
    /// smoothed the same within-cadence way via Sample()/SampleSpreads(), not left instantaneous
    /// like OiBuildupNet/GammaExposure/VolumePcr.
    /// </summary>
    public static readonly TimeSpan SpreadRatio = TimeSpan.FromMinutes(15);
}
