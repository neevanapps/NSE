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

    /// <summary>Intraday responsiveness. The lookback used to compute the *raw* momentum ("futures price now minus this-long-ago") -- distinct from <see cref="PriceMomentumZScoreWindow"/> below, which governs how that raw value gets z-scored, not how it's computed.</summary>
    public static readonly TimeSpan PriceMomentum = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Rolling window the raw PriceMomentum value (above) is z-scored against (2026-09-08,
    /// audit finding F4) -- the exact same bug already fixed for VixChange on 04 Sep, never
    /// carried across here. Z-scoring against a window the *same* length as the raw lookback
    /// compares an almost-fully-autocorrelated series against itself: each new 15s reading's
    /// 15-minute lookback overlaps the previous one by all but 15 seconds, so that series has
    /// almost no internal variance during any smooth stretch, collapsing the window's StdDev
    /// toward zero and slamming ordinary moves into the +/-3 clip (PriceMomentumRaw's measured
    /// lag-1 autocorrelation: 0.964). A window several times longer spans enough distinct
    /// regimes that one flat patch can't dominate the variance estimate -- same reasoning as
    /// VixChangeZScoreWindow, see its own doc comment.
    /// </summary>
    public static readonly TimeSpan PriceMomentumZScoreWindow = TimeSpan.FromHours(2);

    /// <summary>Slower-moving structural signal.</summary>
    public static readonly TimeSpan Pcr = TimeSpan.FromMinutes(30);

    /// <summary>OI updates are not tick-frequency.</summary>
    public static readonly TimeSpan OiBuildupNet = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far back <c>OiLookbackWindow</c> compares raw OI against, for both
    /// <c>LiveFeatureEngine.ComputeOiBuildupNet</c> and <c>ComputeRatioSizedOiFlowRaw</c> --
    /// distinct from <see cref="OiBuildupNet"/> above, which governs how the resulting *raw*
    /// value gets z-scored, not how it's computed. Audit finding F50 (2026-09-10, user-caught
    /// live): NSE/the broker only refresh OI every ~3 minutes -- comparing against the previous
    /// 15s cadence was structurally almost always a no-op, the exact same root cause the
    /// Dashboard's own "OI Change %" panel was already fixed for once (see
    /// LiveDataService.cs's own comment on the same underlying reality). Comfortably longer
    /// than the confirmed ~3 minutes so the comparison reliably spans at least one real
    /// update -- a provisional constant like every other assumed-but-unvalidated number in this
    /// class, not yet derived from measuring the real print-to-print gap in live data.
    /// </summary>
    public static readonly TimeSpan OiComparisonWindow = TimeSpan.FromMinutes(4);

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

    /// <summary>
    /// Net Vanna and Charm exposure across the full nearest-expiry chain (2026-09-08,
    /// diagnostic-only -- see ScoreWeights.Default's VannaExposure/CharmExposure weights).
    /// Same nature as GammaExposure -- OI-weighted, changes mostly as OI shifts rather than
    /// tick-by-tick -- so the same 30-minute window applies to both.
    /// </summary>
    public static readonly TimeSpan VannaExposure = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan CharmExposure = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Quote-rule aggressor-volume proxy (2026-09-08, diagnostic-only -- see
    /// ScoreWeights.Default's CvdProxy weight), across the full nearest-expiry chain. A genuine
    /// interval delta driven by trading activity, same as VolumePcr (its closest sibling, whose
    /// full-chain volume-delta tracking this reuses directly) -- same 30-minute window.
    /// </summary>
    public static readonly TimeSpan CvdProxy = TimeSpan.FromMinutes(30);

    /// <summary>
    /// ATM straddle richness-vs-expected (2026-09-08, diagnostic-only -- see
    /// ScoreWeights.Default's StraddleRichness weight). Driven by option quotes moving
    /// cadence-to-cadence, not by OI updates, so it's the noisier, quote-driven kind of metric --
    /// same 15-minute window as IvSkew/SpreadRatio rather than the 30-minute OI-driven ones.
    /// </summary>
    public static readonly TimeSpan StraddleRichness = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back the tracked future's own price-vs-VWAP deviation is z-scored (audit finding
    /// F55's price-led dynamic-hybrid mode, 2026-09-11). Deliberately a rolling window, not a
    /// full-session one -- short enough to warm up in the first half hour (matching every other
    /// window in this class) and to stay reactive to the *current* regime rather than averaging
    /// across the whole day including calmer or more volatile earlier stretches, which is
    /// actually the more F59-aligned choice here (a fixed full-session window would get
    /// progressively less sensitive as the day went on, the same trap the DynamicHybrid rank
    /// cutoff fell into against a growing distribution).
    /// </summary>
    public static readonly TimeSpan FuturesVwapDeviation = TimeSpan.FromMinutes(30);
}
