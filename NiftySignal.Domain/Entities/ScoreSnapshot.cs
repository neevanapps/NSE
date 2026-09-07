namespace NiftySignal.Domain.Entities;

/// <summary>
/// One cadence tick's worth of feature/score output, combining plan section 3.2's separate
/// "features" (raw pre-z-score values) and "scores" (weighted z-score components +
/// composite) tables into one row -- a scoped-down v1 given today's time budget; splitting
/// them apart later is cheap if that separation proves valuable, and nothing downstream
/// depends on them being separate yet.
/// </summary>
public sealed class ScoreSnapshot
{
    public long Id { get; set; }

    public required DateTimeOffset ComputedAt { get; set; }

    public double? OiBuildupNetRaw { get; set; }
    public double? PcrRaw { get; set; }
    public double? FuturesBasisRaw { get; set; }
    public double? IvSkewRaw { get; set; }
    public double? PriceMomentumRaw { get; set; }
    public double? DepthImbalanceRaw { get; set; }

    /// <summary>VIX change over its lookback window, negated (rising VIX = bearish) -- see LiveFeatureEngine.ComputeVixChange. Optional: never blocks the composite (see CompositeScoreCalculator).</summary>
    public double? VixChangeRaw { get; set; }

    /// <summary>
    /// Net gamma exposure (2026-09-07, diagnostic-only -- see ScoreWeights.Default's
    /// GammaExposure weight) across the *full* nearest-expiry chain, not just the persisted
    /// ATM+/-2 strike_snapshots band -- see LiveFeatureEngine.ComputeGammaExposure. Optional:
    /// never blocks the composite, same as VixChangeRaw.
    /// </summary>
    public double? GammaExposureRaw { get; set; }

    /// <summary>
    /// Notional put/call traded-volume ratio (2026-09-07, diagnostic-only -- see
    /// ScoreWeights.Default's VolumePcr weight), computed across the full nearest-expiry
    /// chain -- see LiveFeatureEngine.ComputeVolumePcr. Notional (volume x mark price), not
    /// raw contract count: a same-day check found the notional-weighted version of the
    /// existing OI-based Pcr correlated far more with forward price moves than the
    /// count-weighted one, so this new metric launches with that weighting rather than
    /// count. Optional: never blocks the composite, same as VixChangeRaw/GammaExposureRaw.
    /// </summary>
    public double? VolumePcrRaw { get; set; }

    public double? OiBuildupNetZ { get; set; }
    public double? PcrZ { get; set; }
    public double? FuturesBasisZ { get; set; }
    public double? IvSkewZ { get; set; }
    public double? PriceMomentumZ { get; set; }
    public double? DepthImbalanceZ { get; set; }
    public double? VixChangeZ { get; set; }
    public double? GammaExposureZ { get; set; }
    public double? VolumePcrZ { get; set; }

    /// <summary>
    /// The pre-tanh weighted z-sum (2026-09-04) -- persisted so the dynamic-k rolling window
    /// (see LiveFeatureEngine) can be replayed on restart via SeedHistory, same as the six
    /// per-metric raw values already are. Without this, k would silently reset to
    /// CompositeScoreCalculator.DefaultK for 30 minutes after every service restart.
    ///
    /// Smoothed since 2026-09-07 -- a simple moving average over the last several cadences
    /// (see LiveFeatureEngine's CompositeSmoothingCadences), not the single-cadence value. A
    /// fresh composite recomputed from scratch every 15s with no memory of its own recent
    /// behavior was too noisy to sustain past the entry rules' hold-above-threshold window
    /// even when the underlying direction was genuinely right (live-caught 2026-09-07: a real,
    /// sustained ~120-point down move never produced a trade because single-cadence spikes
    /// kept resetting the sustain timer). This is now the tradable value -- see
    /// <see cref="CompositeScoreRawInstant"/> for the un-smoothed one.
    /// </summary>
    public double? CompositeScoreRaw { get; set; }

    public double? CompositeScore { get; set; }

    /// <summary>
    /// The single-cadence composite raw before smoothing (2026-09-07) -- kept purely for
    /// transparency/future analysis (comparing smoothed vs instantaneous), not read by any
    /// live decision. See <see cref="CompositeScoreRaw"/> for the smoothed, tradable value.
    /// </summary>
    public double? CompositeScoreRawInstant { get; set; }

    /// <summary>
    /// Spot at the moment this cadence was computed (2026-09-05). Captured here rather than
    /// re-derived from the tick stream so score and price are aligned by construction -- the
    /// dashboard's score chart overlays them, and the reversal analysis in
    /// docs/REVERSAL_ANALYSIS.md needs exactly this pairing without a time-window join.
    /// </summary>
    public double? SpotPrice { get; set; }

    public bool IsWarmedUp { get; set; }

    public required string WeightSetVersion { get; set; }
}
