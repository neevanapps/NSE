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

    /// <summary>
    /// Synthetic-forward (put-call parity S) minus spot mid (2026-09-09 external review,
    /// alongside reverting audit finding F11's original fix). A quote-quality/parity-gap
    /// diagnostic, not a basis measurement -- <see cref="FuturesBasisRaw"/> above stays the
    /// honestly-named real future's mid minus spot mid. Typically small and near zero on
    /// clean data; a wider reading usually means a stale or wide wing-strike quote fed the
    /// put-call-parity solve, not a real sentiment signal. Weight 0 by construction: no Z
    /// counterpart, never feeds the composite -- see LiveFeatureEngine.Sample's doc comment.
    /// </summary>
    public double? ParityGapRaw { get; set; }

    /// <summary>
    /// Put/call IV skew, anchored to the expiry's own ~1-sigma expected move (spot x sigma x
    /// sqrt(t)) rather than a fixed point offset -- see LiveFeatureEngine.ComputeIvSkew (audit
    /// finding F8). Renamed from IvSkewRaw (2026-09-09 external review) to stay unambiguous
    /// against the ratio sidecar's RatioIvSkew25dRaw, a materially different (25-delta) skew
    /// reading -- the two must never be read as the same series.
    /// </summary>
    public double? IvSkewOneSigmaRaw { get; set; }

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

    /// <summary>
    /// Put-spread/call-spread ratio (2026-09-07, diagnostic-only -- see ScoreWeights.Default's
    /// SpreadRatio weight), across the full nearest-expiry chain -- see
    /// LiveFeatureEngine.ComputeSpreadRatio. Optional: never blocks the composite, same as
    /// VixChangeRaw/GammaExposureRaw/VolumePcrRaw.
    /// </summary>
    public double? SpreadRatioRaw { get; set; }

    /// <summary>
    /// Net Vanna exposure (2026-09-08, diagnostic-only, weight 0 -- same launch discipline as
    /// GammaExposureRaw) across the full nearest-expiry chain -- see
    /// LiveFeatureEngine.ComputeVannaExposure. Same per-option-type dealer-positioning sign
    /// convention as GammaExposureRaw (call +, put -); Vanna itself is identical for call and
    /// put at a given strike (see OptionGreeks' doc comment), same as Gamma is.
    /// </summary>
    public double? VannaExposureRaw { get; set; }

    /// <summary>
    /// Net Charm (delta decay per day) exposure (2026-09-08, diagnostic-only, weight 0) across
    /// the full nearest-expiry chain -- see LiveFeatureEngine.ComputeCharmExposure. Particularly
    /// relevant on expiry day, when OTM deltas decay fastest in the final hours before close.
    /// </summary>
    public double? CharmExposureRaw { get; set; }

    /// <summary>
    /// Quote-rule aggressor-volume proxy (2026-09-08, diagnostic-only) across the full
    /// nearest-expiry chain -- see LiveFeatureEngine.ComputeVolumePcrAndCvdProxy. Not true CVD:
    /// the feed has no per-trade tape (touchline LTP/cumulative volume/5-level depth only), so
    /// aggressor side is inferred from whether LTP sat closer to ask or bid each cadence, not
    /// from tagged prints. Positive = net buy-leaning volume classified bullish (calls bought,
    /// puts sold), same per-option-type sign convention as OiBuildupNetRaw.
    /// </summary>
    public double? CvdProxyRaw { get; set; }

    /// <summary>
    /// ATM straddle's actual price change this cadence minus what its own Delta and Theta (from
    /// the previous cadence, applied to the realized underlying move and elapsed time) predicted
    /// -- see LiveFeatureEngine.ComputeStraddleRichness. Unlike every other component here, this
    /// is a volatility-demand signal, not a directional one: positive means the straddle priced
    /// richer than a delta+theta-only prediction (vol expanding), negative means it decayed
    /// faster than predicted (vol crush). Null whenever the ATM strike rolled to a new strike
    /// since the previous cadence -- comparing two different straddles would be meaningless.
    /// </summary>
    public double? StraddleRichnessRaw { get; set; }

    /// <summary>
    /// The strike level where net Gamma Exposure crosses zero (2026-09-08, diagnostic-only),
    /// found by evaluating GammaExposureRaw's own aggregation at each tracked strike as a
    /// hypothetical spot and linearly interpolating between the two adjacent strikes where its
    /// sign flips -- see LiveFeatureEngine.ComputeGammaFlipLevel. A price level, not a magnitude,
    /// so unlike every other diagnostic here it has no Z counterpart and was never a candidate
    /// for the composite sum; it's read by comparing SpotPrice to it, not by z-scoring it. Null
    /// when no sign flip exists within the currently tracked strike range.
    /// </summary>
    public double? GammaFlipLevel { get; set; }

    /// <summary>
    /// Where the current ATM IV sits within its own recent range, 0-100 (2026-09-08, audit
    /// finding F3) -- see LiveFeatureEngine.ComputeIvRank. Already a normalized percentage by
    /// construction, so unlike every other raw value here it has no Z counterpart and never
    /// feeds the composite sum; it's read directly by EntryRuleEvaluator's MaxIvRankForEntry
    /// gate. Null until the rolling window has at least two distinct observations to rank
    /// against -- a single point (or a perfectly flat window) can't produce a meaningful rank.
    /// </summary>
    public double? IvRankRaw { get; set; }

    /// <summary>
    /// The raw ATM reference vol IvRankRaw was ranked against this cadence (2026-09-09
    /// external review amendment to audit finding F3) -- see LiveFeatureEngine.ComputeIvRank.
    /// Persisted so today's own observations can be replayed into the cold-start fallback
    /// distribution on restart (SeedHistory) and so a prior day's session mean can later be
    /// computed for the next day's 20-session ranking distribution (see
    /// MarketDataIngestionWorker's prior-session seed query) -- without this column, "rank
    /// against the last 20 sessions" would have no persisted per-session series to average.
    /// </summary>
    public double? AtmIv { get; set; }

    /// <summary>
    /// How many prior sessions' worth of ATM IV history <see cref="IvRankRaw"/> was actually
    /// ranked against this cadence (2026-09-09 external review amendment to audit finding F3):
    /// 0 until at least one prior session's mean is seeded, up to
    /// LiveFeatureEngine.MaxPriorSessionsForIvRank once mature. Below
    /// LiveFeatureEngine.MinPriorSessionsForIvRank, IvRankRaw is a same-day-only rank (not
    /// trustworthy yet -- a genuinely high-vol day would still read as "normal" for its own
    /// first couple of hours) -- EntryRuleEvaluator's MaxIvRankForEntry gate reads this
    /// alongside IvRankRaw and skips the gate entirely below that threshold, rather than
    /// acting on an unreliable same-day rank.
    /// </summary>
    public int IvRankSessionCount { get; set; }

    public double? OiBuildupNetZ { get; set; }
    public double? PcrZ { get; set; }
    public double? FuturesBasisZ { get; set; }
    public double? IvSkewZ { get; set; }
    public double? PriceMomentumZ { get; set; }
    public double? DepthImbalanceZ { get; set; }
    public double? VixChangeZ { get; set; }
    public double? GammaExposureZ { get; set; }
    public double? VolumePcrZ { get; set; }
    public double? SpreadRatioZ { get; set; }
    public double? VannaExposureZ { get; set; }
    public double? CharmExposureZ { get; set; }
    public double? CvdProxyZ { get; set; }
    public double? StraddleRichnessZ { get; set; }

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

    // --- Ratio-based composite score (weekend build, 2026-09-09) -----------------------------
    // A second, independent scoring pipeline running alongside the composite above -- not a
    // replacement. LiveTradingEngine reads none of the columns below (see
    // LiveTradingEngineTests' trading-safety parity test, which asserts this directly rather
    // than leaving it as a code-reading claim). See NiftySignal.Scoring.RatioScoreCalculator
    // and LiveFeatureEngine's ratio-metric methods for how each is computed.
    //
    // Audit finding F51 (2026-09-10, user-caught live, then refined by user instruction): all
    // five Ratio*Raw columns below hold a smoothed value (LiveFeatureEngine.SmoothRatioMetric,
    // ~RatioCompositeSmoothingCadences / 12 minutes), not the single-cadence instant read --
    // before this fix, an individual metric's clipped s_i could swing sign entirely between
    // adjacent 15s cadences even though the combined score shown alongside it
    // (RatioCompositeScore) was, at the time, additionally smoothed at the combined level too --
    // which read as contradictory on the dashboard. The instant read still feeds each metric's
    // own smoothing FIFO every cadence but is no longer persisted anywhere on its own. The
    // combined score's own separate smoothing layer was retired once this landed (see
    // RatioCompositeScoreRaw's own doc comment) -- it's stable now because every one of its
    // inputs already is, not because of an additional smoothing step of its own.

    /// <summary>Call notional / put notional, ATM+/-5, log-ratio-clipped -- see LiveFeatureEngine.ComputeRatioNotionalVolumeRaw. Null when combined notional is below RatioMetricScales.MinNotionalForVolumeRatio (no real signal that bar, not a divide-by-zero guard) AND no sample has ever cleared that floor yet this session (F51: a bar below the floor is simply skipped, not zero-filled, so the smoothed value persists across a quiet bar once at least one real sample exists).</summary>
    public double? RatioNotionalVolumeRaw { get; set; }

    /// <summary>Sized, spot-classified constructive OI flow ratio, ATM+/-5, log-ratio-clipped -- see LiveFeatureEngine.ComputeRatioSizedOiFlowRaw. Null when combined constructive flow is below RatioMetricScales.MinContractsForOiFlow and no sample has ever cleared that floor yet this session -- see RatioNotionalVolumeRaw's own doc comment (F51) for why a single quiet bar no longer blanks this out.</summary>
    public double? RatioSizedOiFlowRaw { get; set; }

    /// <summary>ATM call residual minus ATM put residual (rupees), each leg's actual mark change minus its own Delta+Gamma+Theta+Vega-predicted change -- see LiveFeatureEngine.ComputeResidualDifference. A difference, not a ratio (a ratio blows up near zero). Null on an ATM strike roll (same guard as StraddleRichnessRaw) only once every smoothed sample has aged out of the window -- see RatioNotionalVolumeRaw's own doc comment (F51).</summary>
    public double? RatioResidualDifferenceRaw { get; set; }

    /// <summary>25-delta put IV / 25-delta call IV -- see LiveFeatureEngine.ComputeIvSkewRatio25Delta. A materially different quantity from IvSkewOneSigmaRaw (~16-delta, F8) -- never treat the two as the same series.</summary>
    public double? RatioIvSkew25dRaw { get; set; }

    /// <summary>OI-weighted put spread% / call spread%, ATM+/-2 -- see LiveFeatureEngine.ComputeRatioSpreadAtmRaw. Reuses the same per-cadence spread samples SpreadRatioRaw does.</summary>
    public double? RatioSpreadAtmRaw { get; set; }

    /// <summary>The five (smoothed, F51) clipped inputs combined for this cadence -- transparency/future-analysis only, same role as CompositeScoreRawInstant. Already meaningfully stable on its own, since each of its five inputs is itself a ~12-minute average.</summary>
    public double? RatioCompositeScoreRawInstant { get; set; }

    /// <summary>
    /// Identical to <see cref="RatioCompositeScoreRawInstant"/> as of audit finding F51
    /// (2026-09-10, user instruction): this used to be that value smoothed again over its own
    /// combined-level 12-cadence FIFO, but once every one of the five inputs feeding it was
    /// already a ~12-minute average in its own right, smoothing the combination a second time
    /// only added lag with no benefit -- the combined-level FIFO was retired. Kept as its own
    /// column (rather than removed, which would need a migration and touch every reader) so
    /// existing call sites reading "the tradable raw, if this pipeline is ever wired into a
    /// decision" don't need to know which of the two columns to prefer.
    /// </summary>
    public double? RatioCompositeScoreRaw { get; set; }

    /// <summary><c>100 * tanh(RatioCompositeScoreRaw / k)</c>, k = RatioScoreCalculator.DefaultK. Null until at least RatioScoreCalculator.MinRequiredComponents of the five Ratio*Raw values above are non-null this cadence.</summary>
    public double? RatioCompositeScore { get; set; }

    public bool RatioIsWarmedUp { get; set; }

    /// <summary>Nullable unlike the required WeightSetVersion above -- this composite can legitimately fail to warm up (fewer than 3 of 5 metrics present), in which case there's no weight set to attribute the (absent) score to.</summary>
    public string? RatioWeightSetVersion { get; set; }

    /// <summary>
    /// Count (0-5) of the five ratio metrics that were non-null this cadence, after the same
    /// RatioMetricMath clip each goes through before RatioScoreCalculator ever sees it -- so this
    /// counts the same "present" RatioScoreCalculator.MinRequiredComponents checks, not raw-value
    /// nullness (a raw value can be non-null yet clip to null, e.g. a non-positive ratio).
    /// Audit finding F41 -- makes a 3-of-5 bar (e.g. only skew+spread+residual) distinguishable
    /// from a 5-of-5 bar without re-deriving presence from the five Ratio*Raw columns' nullness.
    /// </summary>
    public int RatioComponentsPresent { get; set; }
}
