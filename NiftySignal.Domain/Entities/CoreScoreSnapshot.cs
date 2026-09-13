namespace NiftySignal.Domain.Entities;

/// <summary>
/// One cadence tick's worth of Core-score output (2026-09-13 live-wiring plan A2) -- a third,
/// independent scoring pipeline alongside <see cref="ScoreSnapshot"/>'s 14-component composite
/// and its ratio-composite sidecar. Deliberately its own table, not a dozen more nullable columns
/// bolted onto <see cref="ScoreSnapshot"/> -- matching that entity's own doc comment ("splitting
/// them apart later is cheap"), applied here from the start rather than retrofitted.
///
/// Unlike the ratio composite, this one is NOT inert with respect to real trades -- two live
/// trading engines (Hysteresis, Crossover; see `docs/replication_plan.md` Batch 5) read
/// <see cref="CoreScore"/>/<see cref="CoreScoreFast"/>/<see cref="CoreScoreSlow"/> directly.
/// </summary>
public sealed class CoreScoreSnapshot
{
    public long Id { get; set; }

    public required DateTimeOffset ComputedAt { get; set; }

    // --- Raw values (pre-rank) ------------------------------------------------------------
    // TrendReversion15m has no *Signed counterpart of its own separate from its raw value in
    // the sense the other 7 do -- it's never ranked (see TrendReversion15mSigned's own doc
    // comment) but its raw IS already the final signed contribution, so both are still
    // persisted for consistency/diagnostics even though they'd read identically.

    public double? DepthImbalanceRaw { get; set; }
    public double? ItmSkewRaw { get; set; }
    public double? FutureCvdNet5MinRaw { get; set; }
    public double? NotionalVolumeRatioRaw { get; set; }
    public double? GammaExposureRaw { get; set; }
    public double? TrendReversion15mRaw { get; set; }
    public double? BasisChangeRaw { get; set; }
    public double? OiChangeDiff15mRaw { get; set; }

    // --- Signed [-1,1] values (post session-rank, except TrendReversion15m) ----------------
    // Diagnostic -- lets a dashboard or a future investigation see the same rank-and-sign
    // breakdown this session's SQL investigations relied on without re-deriving it.

    public double? DepthImbalanceSigned { get; set; }
    public double? ItmSkewSigned { get; set; }
    public double? FutureCvdNet5MinSigned { get; set; }
    public double? NotionalVolumeRatioSigned { get; set; }
    public double? GammaExposureSigned { get; set; }

    /// <summary>Equal to <see cref="TrendReversion15mRaw"/> -- this is the one term of the eight that skips session-rank entirely (already bounded [-1,1] by construction). Persisted separately anyway for the same uniform-shape reason every other *Signed column exists.</summary>
    public double? TrendReversion15mSigned { get; set; }
    public double? BasisChangeSigned { get; set; }
    public double? OiChangeDiff15mSigned { get; set; }

    // --- Combined score (mirrors CompositeScoreRawInstant/Raw/CompositeScore naming) -------

    public double? CoreScoreRawInstant { get; set; }
    public double? CoreScoreRaw { get; set; }
    public double? CoreScore { get; set; }
    public bool IsWarmedUp { get; set; }
    public string? WeightSetVersion { get; set; }

    /// <summary>10-minute trailing moving average of <see cref="CoreScore"/> itself (the already-tanh'd value) -- the Crossover strategy's own fast read. Null until the trailing window has at least one observation.</summary>
    public double? CoreScoreFast { get; set; }

    /// <summary>30-minute trailing moving average of <see cref="CoreScore"/>, same construction as <see cref="CoreScoreFast"/> at a longer window -- the Crossover strategy's own slow read.</summary>
    public double? CoreScoreSlow { get; set; }
}
