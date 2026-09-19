using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Which volume-cadence candidate drives the traded score -- 2026-09-17 plan's "single-metric
/// isolation" protocol: one metric at a time, real trade P&amp;L, before any combining. Each one
/// maps to a signed score in [-1,1] differently -- see <see cref="TradeSimulator.ComputeScore"/>.
/// </summary>
public enum VolumeBarMetric
{
    /// <summary>Already bounded [-1,1] by construction (see <see cref="VolumeBarTrendReversionTracker"/>'s own doc comment) -- used directly, no session-rank needed, same convention the existing time-cadence TrendReversion15m term already follows.</summary>
    TrendReversion,

    /// <summary>Session-rank normalized (real distribution not yet characterized on this clock) -- the mid-point-rule aggressor-volume proxy, ported unchanged from the time cadence.</summary>
    FutureCvdNet,

    /// <summary>Session-rank normalized -- the resting-book snapshot average.</summary>
    DepthImbalance,

    /// <summary>Session-rank normalized -- the CHANGE-in-book measure, new 2026-09-17.</summary>
    OrderFlowImbalance,

    /// <summary>
    /// Session-rank normalized. Classic futures price/OI-buildup read (<see cref="OiBuildupClassifier"/>,
    /// reused as-is from the option-strike version, never applied to the future's own OI before
    /// this track): LongBuildup (price up, OI up) and ShortCovering (price up, OI down) both read
    /// bullish; ShortBuildup (price down, OI up) and LongUnwinding (price down, OI down) both read
    /// bearish; Neutral (either leg unchanged) reads as 0 without touching the rank tracker, same
    /// "don't fabricate a signal that isn't there" convention <see cref="SignedRank"/> already
    /// uses for a raw zero. Magnitude is the OI change itself, not the price change -- conviction
    /// here is "how much OI moved," independent of how big the price move was.
    /// </summary>
    FutureOiBuildupQuadrant,

    /// <summary>Session-rank normalized -- close price minus the whole-session running VWAP already tracked by <see cref="NiftySignal.Features.FutureFlowAccumulator.Vwap"/> (persisted as <see cref="VolumeBarRow.VwapAtClose"/>). Positive = price trading above session VWAP (rich), negative = below (cheap).</summary>
    VwapDeviation,

    /// <summary>
    /// Session-rank normalized. Not naturally signed by itself (a fast bar says nothing about
    /// direction) -- signed by the bar's OWN price-change direction per the 2026-09-17 plan's
    /// explicit choice, magnitude is urgency (1/<see cref="VolumeBarRow.DurationSeconds"/>, so a
    /// FASTER bar -- more participant activity per unit volume -- reads as a stronger reading in
    /// whichever direction that bar moved). A structurally different quantity from
    /// <see cref="TrendReversion"/> (that's a bar-count-windowed price ratio; this is a single
    /// bar's own fill speed), even though both end up signed by price direction.
    /// </summary>
    BarDurationUrgency,

    /// <summary>
    /// Session-rank normalized. Touch-only resting-book imbalance (Bid1Qty/Ask1Qty), as distinct
    /// from <see cref="DepthImbalance"/>'s summed-5-level version -- new 2026-09-17, 9th
    /// future-side candidate, added because the two can diverge sharply at the same instant (a
    /// thin best offer behind a heavy deeper book reads bullish at the touch, bearish in
    /// aggregate) and DepthImbalance is already the strongest validated candidate on this clock,
    /// making the touch-only variant worth its own evaluation cycle rather than assumed redundant.
    /// See docs/VOLUME_BAR_FINDINGS.md for the live-snapshot discussion that motivated this.
    /// </summary>
    TopOfBookImbalance,

    /// <summary>
    /// Session-rank normalized. A Kyle's-lambda-style price-impact-per-unit-volume proxy: this
    /// bar's own price change divided by this bar's own volume, signed by price-change direction
    /// per the same 2026-09-17 choice as <see cref="BarDurationUrgency"/> (Kyle's lambda itself is
    /// normally an unsigned liquidity read, not a direction bet -- flagged as a real caveat, not
    /// an oversight: see docs/VOLUME_BAR_FINDINGS.md). Because bars are built to a roughly FIXED
    /// volume threshold by construction, the denominator barely varies bar-to-bar within one
    /// threshold, so this is expected to track closely with raw signed price change rather than
    /// contributing much new information -- a hypothesis to verify, not assume, hence still worth
    /// running through the same evaluation cycle as everything else.
    /// </summary>
    PriceImpact,

    /// <summary>
    /// Session-rank normalized. Raw pre-rank value is <see cref="DepthImbalance"/> minus
    /// <see cref="TopOfBookImbalance"/> (both already on a [-1,1] scale per bar) -- how much more
    /// bullish the aggregate 5-level book reads than the touch at the same instant. New
    /// 2026-09-17, motivated by the same live-snapshot divergence that motivated
    /// <see cref="TopOfBookImbalance"/> itself: touch strongly bid-heavy while the deeper book was
    /// offer-heavy, described in that discussion as a "tension zone" that could resolve EITHER
    /// direction, not an obviously-signed bet.
    ///
    /// The sign was originally built the other way (TOB minus Depth, "trust the thin touch"), and
    /// the calibration sweep flatly rejected it -- at the 2600 threshold, that version's win rate
    /// fell to 28.6-38.2% across its whole target-zone band with strongly negative net (-131 to
    /// -217 pts), a consistent enough result across percentiles to read as a real, if inverted,
    /// signal rather than noise. Flipped here to DepthImbalance-minus-TOB ("fade the thin touch,
    /// trust the deeper book") per that evidence -- same empirical-sign discipline already used
    /// for TrendReversion's own negation, re-verified by re-running the sweep after the flip
    /// rather than assumed from the first run's numbers. Needs both TopOfBookImbalance and
    /// DepthImbalance non-null on the same bar; null if either is missing (no depth-bearing tick
    /// that bar). No new accumulator or schema change needed -- both inputs are already persisted
    /// per bar.
    /// </summary>
    TobDepthDivergence,

    /// <summary>
    /// 2026-09-17 composite-scoring experiment: a weighted blend of the 5 metrics still under
    /// active consideration after this track's own evaluation cycle (TobDepthDivergence,
    /// DepthImbalance, OrderFlowImbalance, BarDurationUrgency, TopOfBookImbalance -- the other 5
    /// were already closed out as "not confirmed"/"not viable," see docs/VOLUME_BAR_FINDINGS.md).
    /// Weights are NOT guessed -- each is derived from four real numbers this track already
    /// produced for that metric: backtested net points per trade (edge size), 1 minus the top
    /// trade's share of total net (concentration discount -- punishes a result that leans on one
    /// lucky trade), the ratio of the weaker to the stronger of its 0-DTE/non-0-DTE win rates
    /// (DTE-consistency discount), and a small capped nudge from the 2026-09-17 out-of-sample
    /// result (the only number here that wasn't fit to the same 6 backtest days everything else
    /// was calibrated on). Full derivation table in docs/VOLUME_BAR_FINDINGS.md's "Composite
    /// score" section -- see <see cref="ComputeCompositeScore"/> for the constants themselves.
    ///
    /// TobDepthDivergence's own formula already contains DepthImbalance and TopOfBookImbalance as
    /// terms, so this composite is not 5 independent signals -- deliberately still includes all
    /// 5 (explicit choice, not an oversight) and lets the weighting itself down-weight the
    /// overlapping information rather than dropping a component outright.
    ///
    /// Each component is session-rank normalized with its OWN independent tracker (same isolation
    /// every standalone metric run already gets), then combined as a weighted average over
    /// whichever components are non-null on a given bar (missing ones are excluded and the
    /// remaining weights renormalized, not treated as zero -- consistent with every other
    /// "don't fabricate a signal that isn't there" null-handling convention in this file). The
    /// blended result is NOT already percentile-shaped the way a single SignedRank output is (an
    /// average of percentile-like values doesn't preserve that property), so it gets its own
    /// dedicated magnitude-rank tracker for the entry-percentile gate -- same treatment
    /// <see cref="TrendReversion"/> already gets for the same underlying reason.
    /// </summary>
    Composite,

    /// <summary>
    /// 2026-09-17, built after the composite (a linear blend) came out WORSE than trading
    /// DepthImbalance alone -- see docs/VOLUME_BAR_FINDINGS.md. The session-phase checks found
    /// DepthImbalance's edge is almost entirely in the 09:30-10:00 open (78.6% win there) and it
    /// LOSES money in Mid/Close; BarDurationUrgency shows close to the opposite pattern (loses in
    /// the open, wins in Mid/Close). Averaging two metrics with opposite time-of-day biases every
    /// bar plausibly cancels real signal rather than combining it. This is a hard SWITCH instead
    /// of a blend: DepthImbalance's own score drives bars before 10:00 IST, BarDurationUrgency's
    /// own score drives bars at or after 10:00 IST -- never an average of both on the same bar.
    /// Each metric's score is already percentile-shaped by construction (SignedRank), so unlike
    /// <see cref="Composite"/> this needs no separate magnitude-rank tracker -- whichever
    /// component is active on a given bar, its own output already IS the percentile.
    /// </summary>
    SessionGatedDepthDuration,

    /// <summary>
    /// 2026-09-17, folds TopOfBookImbalance (#9) into the session-gated switch as a CONFIRMATION
    /// filter, not a third blended or switched component. Same score as
    /// <see cref="SessionGatedDepthDuration"/> (DepthImbalance pre-10:00 IST, BarDurationUrgency
    /// after) -- the only difference is the entry gate: a new position during the open (before
    /// 10:00 IST) additionally requires TopOfBookImbalance's own score to agree in sign with the
    /// switched score, or the bar is skipped. TOB's own edge was found concentrated specifically
    /// in the open (63.6% win rate there vs. barely positive in Mid/Close), so confirmation is
    /// only required in that window -- requiring it outside the open would filter
    /// BarDurationUrgency's genuine trades against what is close to noise for TOB there, not a
    /// real check. See <see cref="PassesConfirmation"/>.
    /// </summary>
    SessionGatedDepthDurationConfirmed,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 1 (first variant): raw change in the ATM
    /// options complex's own implied volatility between consecutive future bars
    /// (<see cref="OptionAtmBarRow.AtmIv"/>, this bar minus the previous one). Session-rank
    /// normalized like every other flow-style metric. The sign here is a HYPOTHESIS, not an
    /// assumed fact -- raw IV change has no obvious a priori direction (rising IV could mean
    /// "anticipatory move building, follow it" or "fear building, fade it"), so this is tested
    /// as-is and the calibration sweep's own win rate settles it, same empirical-sign discipline
    /// already used for TobDepthDivergence (re-verify by re-running after any flip, never infer
    /// from the rejected version's numbers).
    /// </summary>
    AtmIvChangeRaw,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 1, second variant: raw ΔIV (this bar's AtmIv
    /// minus the previous one, same as <see cref="AtmIvChangeRaw"/>) signed by the concurrent
    /// FUTURE price direction -- sign(ΔPrice) x ΔIV, not just |ΔIV|. A genuinely different
    /// hypothesis from the raw version in 2 of 4 quadrants: price falling + IV falling
    /// (capitulation calming) reads bullish; price rising + IV falling (complacent rally, often a
    /// topping signal) reads bearish -- neither of which the raw (IV-sign-only) version can
    /// express. Same empirical-sign discipline as every other candidate: tested as-built, not
    /// assumed correct.
    /// </summary>
    AtmIvChangePriceSigned,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 1, third variant (optional per the plan): the
    /// SECOND difference of AtmIv -- this bar's own ΔIV minus the previous bar's ΔIV, i.e. is the
    /// IV change itself speeding up or slowing down. Needs one extra bar of history beyond
    /// <see cref="AtmIvChangeRaw"/> (a ΔIV to compare against), so the first 2 bars of a day
    /// always score null here, not just the first 1. Same empirical-sign discipline.
    /// </summary>
    AtmIvAcceleration,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 2: this bar's own ATM+/-1-band notional Call
    /// volume minus notional Put volume (<see cref="OptionBandFlowBarRow"/> -- a genuine FLOW
    /// quantity accumulated tick-by-tick across the bar's window, not a point-in-time snapshot
    /// like the ATM IV metrics). No differencing against the previous bar needed here -- each
    /// bar's own flow imbalance is already the signal, the same shape
    /// <see cref="FutureCvdNet"/>/<see cref="OrderFlowImbalance"/> already use. Session-rank
    /// normalized. Original sign hypothesis (more Call notional than Put notional reads bullish)
    /// was empirically rejected 2026-09-18: full 24-cell sweep showed win rate hovering at/below
    /// 50% with no clean percentile pattern, the same rejection signature seen with
    /// TobDepthDivergence and price-signed ATM IV change before their own flips. Flipped per the
    /// same empirical-sign discipline; this enum now scores Put-heavier flow as the bullish
    /// direction. No causal story assumed -- flipped because the data said so, same as the two
    /// prior flips.
    /// </summary>
    NotionalCallPutVolumeDelta,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 3, sub-variant A: this bar's own
    /// (<see cref="OptionOiBarRow.CallOiChangeNotional"/> − <see cref="OptionOiBarRow.PutOiChangeNotional"/>)
    /// -- already a flow (raw ΔOI x the price at the moment of that change, summed per side), same
    /// shape as <see cref="NotionalCallPutVolumeDelta"/>, not the ATM IV metrics' level-differencing
    /// pattern. (An earlier build persisted OI x price as a LEVEL and differenced it here instead --
    /// wrong, see <see cref="OptionOiBarRow"/>'s own doc comment for why; corrected before this was
    /// ever calibrated to a "confirmed" verdict.) Untested sign hypothesis: Call-side OI building
    /// faster than Put-side reads bullish (fresh call buying/writing outweighing put activity) --
    /// tested as-built, flipped only if the sweep rejects it, same discipline as every other metric
    /// in this track.
    /// </summary>
    NotionalOiDelta,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 3, sub-variant B: <see cref="OptionOiBarRow.BuildupNet"/>
    /// directly -- already a flow-shaped, sign-weighted vote per bar (no differencing needed). Reuses
    /// the same <see cref="NiftySignal.Features.OiBuildupClassifier"/> the original time-cadence
    /// `OiBuildupNet` candidate used (option's own price/OI change, per strike per side, Put-side
    /// sign-flipped before summing) -- CLOSED with no edge on that clock/band (ATM±2, 15s cadence,
    /// see `docs/SCORE_CANDIDATES.md`). Tried again here on a genuinely different clock (future
    /// volume bars) and narrower band (ATM±1) rather than assumed dead on arrival, per the "a
    /// metric confirmed/closed on one clock isn't automatically promoted/excluded on the other"
    /// convention already documented in `docs/VOLUME_BAR_FINDINGS.md`.
    /// </summary>
    OiBuildupQuadrant,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 4, sub-variant A: this bar's own
    /// <see cref="OptionSkew25DeltaBarRow.SkewRatio"/> (putIv/callIv at the nearest-25-delta
    /// strike per side) minus the previous bar's -- a LEVEL differenced into a flow, same shape as
    /// the ATM IV change metrics. Reuses the time-cadence pipeline's own already-CONFIRMED
    /// `IvSkew25Delta` candidate's exact strike-selection method and ratio orientation (see
    /// <see cref="OptionSkew25DeltaBarRow"/>'s own doc comment) -- here testing whether its
    /// BAR-TO-BAR CHANGE also carries signal on this clock, even though that candidate's own notes
    /// flagged the level as where the real information lived on the 15s cadence. Untested sign
    /// hypothesis: rising put-skew (ratio increasing) reads bearish, matching the confirmed
    /// classic-textbook direction already found twice on the time-cadence track.
    /// </summary>
    Skew25DeltaChangeRaw,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 4, sub-variant B: same ΔSkewRatio as
    /// <see cref="Skew25DeltaChangeRaw"/>, but sign-adjusted by the future's own bar-over-bar price
    /// direction (<c>sign(ΔFuturePrice) x ΔSkewRatio</c>) -- same "does price-direction context
    /// change which reading is informative" hypothesis already tested for
    /// <see cref="AtmIvChangePriceSigned"/>, tried again here since that one needed an empirical
    /// sign flip and this is a structurally different quantity (skew ratio, not raw IV). Tested
    /// as-built (not pre-negated) -- flipped only if the sweep rejects it, same discipline as every
    /// other metric in this track.
    /// </summary>
    Skew25DeltaChangePriceSigned,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 4, sub-variant C: the LEVEL itself,
    /// <c>log(SkewRatio)</c> (log, not raw ratio, so it's zero-centered around "no skew" --
    /// <c>SignedRank</c> needs a signed quantity, and a raw ratio is always positive; matches the
    /// codebase's own established log-ratio convention for level-based skew/PCR-style metrics).
    /// Tried because the time-cadence `IvSkew25Delta` candidate's own notes explicitly flagged the
    /// LEVEL as carrying the real signal for this metric family, with its own bar-to-bar change
    /// weaker and less consistent -- a genuinely different formulation from the two Change variants
    /// above, not just a third sign guess.
    /// </summary>
    Skew25DeltaLevel,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 5 (last of the plan's 5 core options metrics),
    /// sub-variant A: <c>currentFuturePrice − <see cref="OptionMaxPainBarRow.MaxPainStrike"/></c>,
    /// already a signed level (no differencing needed, same shape as
    /// <see cref="DepthImbalance"/>). Untested "pinning" sign hypothesis: price trading ABOVE max
    /// pain gets pulled back DOWN toward it (bearish), price BELOW gets pulled UP (bullish) -- i.e.
    /// the raw distance itself should read bearish when positive. Tested as-built; flipped only if
    /// the sweep rejects it, same discipline as every other metric in this track.
    /// </summary>
    DistanceToMaxPain,

    /// <summary>
    /// 2026-09-18, options phase, Phase 1 metric 5, sub-variant B: same shape as
    /// <see cref="DistanceToMaxPain"/> but against <see cref="OptionMaxPainBarRow.HighestOiStrike"/>
    /// (the single strike with the largest combined Call+Put OI) instead of true max pain -- the
    /// cheaper, more literal "highest OI strike" proxy some traders use in place of the full
    /// payout-minimization calculation. Same untested pinning-sign hypothesis, tested independently.
    /// </summary>
    DistanceToHighestOiStrike,
}

public sealed record VolumeBarTrade(
    DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice,
    DateTimeOffset ExitTime, decimal ExitPrice, string ExitReason, double EntryScore)
{
    /// <summary>Points on the traded option's own price -- no lot size, no transaction costs, always a LONG position, same convention `NiftySignal.MetricTrials.CoreScoreTrade` already uses.</summary>
    public decimal NetPnlPoints => ExitPrice - EntryPrice;

    public decimal NetPnlPercent => NetPnlPoints / EntryPrice * 100m;
}

/// <summary>
/// The volume-cadence plan's own trade-simulation layer (2026-09-17): walks one day's already
/// -populated <see cref="VolumeBarRow"/> series, computes ONE chosen metric's signed score per bar,
/// and trades a hysteresis rule against REAL option prices (<see cref="OptionPriceSeries"/>,
/// tick-accurate, not rounded to a cadence) -- deliberately the SAME rule shape as
/// `NiftySignal.MetricTrials.CoreScoreOptionSimulator.SimulateDay` (enter at an extreme, exit only
/// once the score crosses to the OPPOSITE extreme, one position at a time) so a result here is
/// comparable to the time-cadence pipeline's own numbers, not a different strategy shape
/// confounding a different clock.
///
/// 2026-09-17 (user's own explicit requirement, same discipline already established for
/// `docs/REVIEW_FINDINGS.md`'s DynamicHybrid mode: "no hardcoded metric or value for entry or
/// exit -- every entry and exit must be dynamic"): the entry/exit gate is a PERCENTILE against
/// each day's own score-magnitude distribution so far, not a fixed absolute number. For the
/// SignedRank-normalized metrics (FutureCvdNet/DepthImbalance/OrderFlowImbalance),
/// |scaledScore| already IS that percentile by construction (SignedRank computes
/// sign * rank(|raw|)/100) -- no second rank layer needed. TrendReversion is a raw bounded ratio,
/// not rank-based, so it gets its own SessionRankTracker to become percentile-based the same way --
/// see <see cref="EntryPercentile"/>. This makes `entryPercentile` mean the same thing for every
/// metric: "only trade the top (100-entryPercentile)% most extreme readings today," self
/// -calibrating per day and per metric instead of assuming one fixed score level means the same
/// thing across all four.
///
/// Strike selection is simplified for this first pass: nearest-to-future-price (ATM), not
/// price-range matching like the time-cadence simulator's own [100,150] convention -- a
/// deliberate simplification flagged for a later pass, not an oversight.
/// </summary>
public static class TradeSimulator
{
    // 2026-09-17, user's own standing trading-hours rule (not previously enforced -- confirmed
    // absent from the simulator before this change, entries were firing as early as 09:15 and
    // positions were running to EndOfData at ~15:30): no new position may open before 09:30 IST
    // or after 15:00 IST, and any open position is force-closed at 15:15 IST regardless of what
    // its own score says. Always on, not an optional parameter -- this is a standing constraint
    // on the simulator's own trading hours, not a per-run experiment toggle like stopLossPercent.
    static readonly TimeSpan EntryWindowStart = new(9, 30, 0);
    static readonly TimeSpan EntryWindowEnd = new(15, 0, 0);
    static readonly TimeSpan ForceCloseAt = new(15, 15, 0);

    // 2026-09-17, VolumeBarMetric.SessionGatedDepthDuration's own switch point -- the boundary
    // between the "open" and "mid/close" session-phase buckets already established by the
    // session-phase checks that motivated this metric (see docs/VOLUME_BAR_FINDINGS.md).
    static readonly TimeSpan SessionGateSwitchTime = new(10, 0, 0);

    static TimeSpan IstTimeOfDay(DateTimeOffset timestamp) => timestamp.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay;


    /// <param name="sharedPriceCache">
    /// 2026-09-17, perf: an option's own tick-derived price series for one day never depends on
    /// which metric/percentile/bar-threshold is being simulated -- only on (day, token). A
    /// calibration sweep calls this method dozens of times per day (once per percentile, per bar
    /// threshold), and without a cache that outlives a single call, every one of those re-scans
    /// raw ticks for the same handful of ATM strikes the day already touched. Pass a dictionary
    /// that outlives the whole sweep (see `Program.cs`'s own `RunRangeAsync`) to reuse loads
    /// across calls; omit it (null, the default) to get the old call-scoped-only behavior, e.g.
    /// for a single one-off simulation where reuse doesn't matter.
    /// </param>
    /// <param name="stopLossPercent">
    /// 2026-09-17, risk-overlay experiment (step 4 of the DepthImbalance/BarDurationUrgency
    /// robustness sequence): the soft stop is expressed as a PERCENT of entry premium, not a fixed
    /// point amount -- this pipeline's own real trades range from a 0.05 option to a 230+ one (see
    /// docs/VOLUME_BAR_FINDINGS.md), so a fixed-point stop would be meaningless at one end and
    /// never trigger at the other. Checked at bar granularity (same as every other exit rule this
    /// simulator already has -- the pipeline doesn't tick-check between bars), against
    /// <see cref="OptionPriceSeries.PriceAtOrBefore"/> the same as a normal exit. Omit (null,
    /// default) for the original no-stop behavior. Checked BEFORE the score-invalidation exit each
    /// bar, since capital protection should fire regardless of whether the signal has flipped yet.
    /// </param>
    public static async Task<List<VolumeBarTrade>> SimulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        VolumeBarMetric metric, double entryPercentile, int trendWindowBars, CancellationToken cancellationToken,
        IDictionary<(DateOnly Day, string Token), OptionPriceSeries>? sharedPriceCache = null,
        decimal? stopLossPercent = null,
        long? rollingSubBarThreshold = null)
    {
        List<VolumeBarRow> bars;
        if (rollingSubBarThreshold is { } subBarSize)
        {
            // 2026-09-18, rolling-window mode -- see RollingVolumeWindowBuilder's own doc comment.
            // barVolumeThreshold keeps its usual meaning (the LOGICAL bar size, e.g. 2600) in both
            // modes; here it's built by sliding a window across already-populated sub-bars of
            // rollingSubBarThreshold's own size instead of reading a pre-populated fixed bar of
            // the full threshold.
            if (barVolumeThreshold % subBarSize != 0)
            {
                throw new ArgumentException($"barVolumeThreshold ({barVolumeThreshold}) must be an exact multiple of rollingSubBarThreshold ({subBarSize}).", nameof(barVolumeThreshold));
            }

            var subBars = await volumeBarDb.VolumeBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == subBarSize)
                .OrderBy(b => b.BarIndex)
                .ToListAsync(cancellationToken);

            var windowSubBarCount = (int)(barVolumeThreshold / subBarSize);
            bars = RollingVolumeWindowBuilder.BuildRollingWindows(subBars, windowSubBarCount, barVolumeThreshold);
        }
        else
        {
            bars = await volumeBarDb.VolumeBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .OrderBy(b => b.BarIndex)
                .ToListAsync(cancellationToken);
        }

        if (bars.Count == 0)
        {
            return [];
        }

        // 2026-09-18, options phase: OptionAtmBarRow lives in the SAME VolumeBarDbContext/database
        // as the future bars (locked "futures bars stay the clock" decision) -- one row per future
        // BarIndex at this threshold, joined by that shared identity. Only loaded for metrics that
        // actually need it (rolling-window mode doesn't have a matching OptionAtmBars threshold to
        // join against, so it's skipped there too -- options-on-rolling-bars is not yet built).
        var isAtmIvMetric = metric is VolumeBarMetric.AtmIvChangeRaw or VolumeBarMetric.AtmIvChangePriceSigned or VolumeBarMetric.AtmIvAcceleration;
        Dictionary<int, OptionAtmBarRow>? optionAtmByBarIndex = null;
        if (isAtmIvMetric && rollingSubBarThreshold is null)
        {
            optionAtmByBarIndex = await volumeBarDb.OptionAtmBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .ToDictionaryAsync(b => b.BarIndex, cancellationToken);
        }

        Dictionary<int, OptionBandFlowBarRow>? optionBandFlowByBarIndex = null;
        if (metric == VolumeBarMetric.NotionalCallPutVolumeDelta && rollingSubBarThreshold is null)
        {
            optionBandFlowByBarIndex = await volumeBarDb.OptionBandFlowBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .ToDictionaryAsync(b => b.BarIndex, cancellationToken);
        }

        var isOiMetric = metric is VolumeBarMetric.NotionalOiDelta or VolumeBarMetric.OiBuildupQuadrant;
        Dictionary<int, OptionOiBarRow>? optionOiByBarIndex = null;
        if (isOiMetric && rollingSubBarThreshold is null)
        {
            optionOiByBarIndex = await volumeBarDb.OptionOiBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .ToDictionaryAsync(b => b.BarIndex, cancellationToken);
        }

        var isSkew25DeltaMetric = metric is VolumeBarMetric.Skew25DeltaChangeRaw or VolumeBarMetric.Skew25DeltaChangePriceSigned or VolumeBarMetric.Skew25DeltaLevel;
        Dictionary<int, OptionSkew25DeltaBarRow>? optionSkew25DeltaByBarIndex = null;
        if (isSkew25DeltaMetric && rollingSubBarThreshold is null)
        {
            optionSkew25DeltaByBarIndex = await volumeBarDb.OptionSkew25DeltaBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .ToDictionaryAsync(b => b.BarIndex, cancellationToken);
        }

        var isMaxPainMetric = metric is VolumeBarMetric.DistanceToMaxPain or VolumeBarMetric.DistanceToHighestOiStrike;
        Dictionary<int, OptionMaxPainBarRow>? optionMaxPainByBarIndex = null;
        if (isMaxPainMetric && rollingSubBarThreshold is null)
        {
            optionMaxPainByBarIndex = await volumeBarDb.OptionMaxPainBars
                .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
                .ToDictionaryAsync(b => b.BarIndex, cancellationToken);
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return [];
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();

        var dayStart = bars[0].StartTimestamp;
        var dayEnd = bars[^1].EndTimestamp;

        var priceCache = sharedPriceCache ?? new Dictionary<(DateOnly, string), OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            var key = (asOfDate, token);
            if (priceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var series = await OptionPriceSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            priceCache[key] = series;
            return series;
        }

        Domain.Entities.Instrument? PickAtm(OptionType side, decimal futurePrice) => chain
            .Where(o => o.OptionType == side)
            .OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice))
            .FirstOrDefault();

        var trendTracker = new VolumeBarTrendReversionTracker(trendWindowBars);
        var rankTracker = new SessionRankTracker();
        // Only used to make TrendReversion's own magnitude percentile-based, same reason as the
        // class doc comment -- CVD/DepthImbalance/OrderFlowImbalance never touch this, their own
        // rankTracker above already produces a percentile-shaped score.
        var trendMagnitudeRank = new SessionRankTracker();

        // Only allocated meaningfully when metric == Composite -- see that enum value's own doc
        // comment. Each sub-metric needs its OWN independent tracker (same isolation a standalone
        // run of that metric would get), plus compositeMagnitudeRank for the blended result's own
        // entry-percentile gate (same role trendMagnitudeRank plays for TrendReversion).
        var compositeDepthRank = new SessionRankTracker();
        var compositeOfiRank = new SessionRankTracker();
        var compositeTobRank = new SessionRankTracker();
        var compositeDivergenceRank = new SessionRankTracker();
        var compositeDurationRank = new SessionRankTracker();
        var compositeMagnitudeRank = new SessionRankTracker();

        // Only allocated meaningfully when metric == SessionGatedDepthDuration (or its Confirmed
        // variant) -- each component is fed on EVERY bar (not just its own active window) so its
        // own running distribution reflects the whole day, the same way the standalone metric's
        // own calibrated threshold was derived -- only which one's OUTPUT gets used as the traded
        // score switches by time. sessionTobRank is only meaningful for the Confirmed variant --
        // TOB's own score there is a confirmation GATE, not part of the traded score itself.
        var sessionDepthRank = new SessionRankTracker();
        var sessionDurationRank = new SessionRankTracker();
        var sessionTobRank = new SessionRankTracker();

        // Only meaningful for the 3 AtmIv* metrics -- see their own doc comments. One tracker
        // reused across whichever single variant is active per call (mutually exclusive per
        // SimulateDayAsync invocation, same as every other metric-specific tracker in this file).
        var atmIvChangeRank = new SessionRankTracker();
        double? previousAtmIv = null;
        double? previousDeltaIv = null;

        // Only meaningful for NotionalCallPutVolumeDelta.
        var bandFlowRank = new SessionRankTracker();

        // Only meaningful for the 2 OI metrics -- see their own doc comments. Both are already
        // flow-shaped in OptionOiBarRow (no bar-to-bar differencing needed here, unlike the ATM IV
        // metrics), so only the rank trackers are needed, not a previous-value tracker.
        var oiDeltaRank = new SessionRankTracker();
        var oiBuildupRank = new SessionRankTracker();

        // Only meaningful for the 3 Skew25Delta* metrics -- see their own doc comments.
        var skew25DeltaRank = new SessionRankTracker();
        double? previousSkewRatio = null;

        // Only meaningful for the 2 max-pain/highest-OI distance metrics -- already a signed level
        // (distance from price to a strike), no differencing needed, same shape as DepthImbalance.
        var maxPainRank = new SessionRankTracker();
        var highestOiStrikeRank = new SessionRankTracker();

        var trades = new List<VolumeBarTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, string Token, double EntryScore)? open = null;
        decimal? previousClose = null;
        long? previousOi = null;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var isLastBar = i == bars.Count - 1;

            var isSessionGated = metric is VolumeBarMetric.SessionGatedDepthDuration or VolumeBarMetric.SessionGatedDepthDurationConfirmed;
            double? score;
            if (metric == VolumeBarMetric.Composite)
            {
                score = ComputeCompositeScore(bar, previousClose, compositeDepthRank, compositeOfiRank, compositeTobRank, compositeDivergenceRank, compositeDurationRank);
            }
            else if (isSessionGated)
            {
                score = ComputeSessionGatedScore(bar, previousClose, sessionDepthRank, sessionDurationRank);
            }
            else if (isAtmIvMetric)
            {
                var currentAtmIv = optionAtmByBarIndex is not null && optionAtmByBarIndex.TryGetValue(bar.BarIndex, out var optionBar) ? optionBar.AtmIv : null;
                double? deltaIv = previousAtmIv is { } prevIv && currentAtmIv is { } curIv ? curIv - prevIv : null;

                score = metric switch
                {
                    VolumeBarMetric.AtmIvChangeRaw => deltaIv is { } d ? SignedRank.Compute(d, atmIvChangeRank) : null,
                    // 2026-09-18: built the other way first (sign(dPrice) x dIV, no negation) --
                    // that version showed win rate below 50% almost everywhere on the full 7-day
                    // sweep (39-47%, no clean pattern), the same rejection signature that led to
                    // flipping TobDepthDivergence. Flipped here, re-verified by re-running rather
                    // than inferred from the rejected version's numbers.
                    VolumeBarMetric.AtmIvChangePriceSigned => deltaIv is { } d && previousClose is { } prevClose
                        ? SignedRank.Compute(-Math.Sign(bar.ClosePrice - prevClose) * d, atmIvChangeRank)
                        : null,
                    VolumeBarMetric.AtmIvAcceleration => deltaIv is { } d && previousDeltaIv is { } prevD
                        ? SignedRank.Compute(d - prevD, atmIvChangeRank)
                        : null,
                    _ => null,
                };

                previousAtmIv = currentAtmIv ?? previousAtmIv;
                previousDeltaIv = deltaIv ?? previousDeltaIv;
            }
            else if (metric == VolumeBarMetric.NotionalCallPutVolumeDelta)
            {
                score = optionBandFlowByBarIndex is not null && optionBandFlowByBarIndex.TryGetValue(bar.BarIndex, out var flowBar)
                    ? SignedRank.Compute((double)(flowBar.PutNotionalVolume - flowBar.CallNotionalVolume), bandFlowRank)
                    : null;
            }
            else if (metric == VolumeBarMetric.NotionalOiDelta)
            {
                score = optionOiByBarIndex is not null && optionOiByBarIndex.TryGetValue(bar.BarIndex, out var oiBar)
                    ? SignedRank.Compute((double)(oiBar.CallOiChangeNotional - oiBar.PutOiChangeNotional), oiDeltaRank)
                    : null;
            }
            else if (metric == VolumeBarMetric.OiBuildupQuadrant)
            {
                // 2026-09-18: built as-is first (BuildupNet direct) -- weak, no clean pattern
                // (40-52% win rate almost everywhere, several negative-net cells), the same
                // rejection signature as every other flipped metric in this track. Flipped here,
                // re-verified by re-running the full sweep rather than inferred.
                score = optionOiByBarIndex is not null && optionOiByBarIndex.TryGetValue(bar.BarIndex, out var buildupBar) && buildupBar.BuildupNet is { } bn
                    ? SignedRank.Compute(-bn, oiBuildupRank)
                    : null;
            }
            else if (isSkew25DeltaMetric)
            {
                var currentRatio = optionSkew25DeltaByBarIndex is not null && optionSkew25DeltaByBarIndex.TryGetValue(bar.BarIndex, out var skewBar) ? skewBar.SkewRatio : null;
                double? deltaRatio = previousSkewRatio is { } prevR && currentRatio is { } curR ? curR - prevR : null;

                score = metric switch
                {
                    VolumeBarMetric.Skew25DeltaChangeRaw => deltaRatio is { } d ? SignedRank.Compute(d, skew25DeltaRank) : null,
                    // 2026-09-18: built un-negated first -- win rate hovered 41-53% with no clean
                    // percentile pattern (several cells got WORSE as the gate tightened), the same
                    // rejection signature as every other flipped metric in this track. Flipped
                    // here, re-verified by re-running the full sweep rather than inferred.
                    VolumeBarMetric.Skew25DeltaChangePriceSigned => deltaRatio is { } d && previousClose is { } prevClose
                        ? SignedRank.Compute(-Math.Sign(bar.ClosePrice - prevClose) * d, skew25DeltaRank)
                        : null,
                    // Level, log-transformed and zero-centered (log(1)=0) -- a raw ratio is always
                    // positive, which would make SignedRank's own sign() degenerate to "always
                    // bullish." log(ratio) matches the codebase's own established convention for
                    // ratio-based levels (see PCR-OI's log(CallOi/PutOi) in docs/SCORE_CANDIDATES.md).
                    VolumeBarMetric.Skew25DeltaLevel => currentRatio is { } r && r > 0 ? SignedRank.Compute(Math.Log(r), skew25DeltaRank) : null,
                    _ => null,
                };

                previousSkewRatio = currentRatio ?? previousSkewRatio;
            }
            else if (metric == VolumeBarMetric.DistanceToMaxPain)
            {
                // 2026-09-18: built un-negated first (raw distance = bearish when positive) --
                // win rate was consistently below 50% and got WORSE as the gate tightened (33%
                // down to 0% at the 99th percentile, all three bar sizes), the clearest
                // wrong-sign signature seen in this whole track. Flipped here, re-verified by
                // re-running the full sweep rather than inferred.
                score = optionMaxPainByBarIndex is not null && optionMaxPainByBarIndex.TryGetValue(bar.BarIndex, out var mpBar) && mpBar.MaxPainStrike is { } mp
                    ? SignedRank.Compute(-(double)(bar.ClosePrice - mp), maxPainRank)
                    : null;
            }
            else if (metric == VolumeBarMetric.DistanceToHighestOiStrike)
            {
                // 2026-09-18: built un-negated first, same pinning-sign hypothesis as
                // DistanceToMaxPain -- weak, win rate degraded to 0% at the tightest percentiles.
                // Flipped here (matching DistanceToMaxPain's own flip), re-verified by re-running.
                score = optionMaxPainByBarIndex is not null && optionMaxPainByBarIndex.TryGetValue(bar.BarIndex, out var oiStrikeBar) && oiStrikeBar.HighestOiStrike is { } hs
                    ? SignedRank.Compute(-(double)(bar.ClosePrice - hs), highestOiStrikeRank)
                    : null;
            }
            else
            {
                score = ComputeScore(metric, bar, previousClose, previousOi, trendTracker, rankTracker);
            }
            // TOB confirmation score -- only meaningful for SessionGatedDepthDurationConfirmed,
            // but computed via SignedRank the same way every other metric's own tracker is fed
            // (self-inclusion-safe, ranks before adding).
            var tobConfirmScore = metric == VolumeBarMetric.SessionGatedDepthDurationConfirmed
                ? SignedRank.Compute(bar.TopOfBookImbalance, sessionTobRank)
                : (double?)null;
            previousClose = bar.ClosePrice;
            previousOi = bar.OpenInterestAtClose;
            var scaledScore = score is { } s ? 100.0 * s : (double?)null;
            var magnitudeRank = metric == VolumeBarMetric.Composite ? compositeMagnitudeRank : trendMagnitudeRank;
            var percentile = EntryPercentile(metric, scaledScore, magnitudeRank);

            if (open is { } position)
            {
                var series = await GetSeriesAsync(position.Token);
                var currentPrice = series.PriceAtOrBefore(bar.EndTimestamp) ?? position.EntryPrice;

                var stoppedOut = stopLossPercent is { } stop && currentPrice <= position.EntryPrice * (1m - stop);
                var timedOut = IstTimeOfDay(bar.EndTimestamp) >= ForceCloseAt;
                var invalidated = scaledScore is { } liveScore && percentile is { } p && p >= entryPercentile
                    && (position.Side == OptionType.Call ? liveScore < 0 : liveScore > 0);

                if (stoppedOut || timedOut || invalidated || isLastBar)
                {
                    var exitReason = stoppedOut ? "StopLoss" : timedOut ? "TimeCutoff" : isLastBar ? "EndOfData" : "ScoreInvalidated";
                    trades.Add(new VolumeBarTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                        bar.EndTimestamp, currentPrice, exitReason, position.EntryScore));
                    open = null;
                }
            }
            else if (!isLastBar && scaledScore is { } sc && percentile is { } p && p >= entryPercentile
                && IstTimeOfDay(bar.EndTimestamp) >= EntryWindowStart && IstTimeOfDay(bar.EndTimestamp) <= EntryWindowEnd
                && PassesConfirmation(metric, bar.EndTimestamp, sc, tobConfirmScore))
            {
                var side = sc > 0 ? OptionType.Call : OptionType.Put;
                var candidate = PickAtm(side, bar.ClosePrice);
                if (candidate is not null)
                {
                    var series = await GetSeriesAsync(candidate.Token);
                    var entryPrice = series.PriceAtOrBefore(bar.EndTimestamp);
                    if (entryPrice is { } ep && ep > 0)
                    {
                        open = (bar.EndTimestamp, ep, side, candidate.StrikePrice!.Value, candidate.Token, sc);
                    }
                }
            }
        }

        return trades;
    }

    /// <summary>
    /// 2026-09-18, user's own experiment: a dual-window moving-average CROSSOVER rule applied to
    /// the locked futures composite score (<see cref="ComputeSessionGatedScore"/>, the SAME score
    /// <see cref="VolumeBarMetric.SessionGatedDepthDurationConfirmed"/> trades, including its own
    /// TOB open-window confirmation gate -- this is deliberately the real validated FuturesScore,
    /// not a new composite), rather than the percentile-threshold entry every other metric in this
    /// file uses. Fast/slow are SIMPLE MOVING AVERAGES of the last N per-bar scaled scores (e.g.
    /// 4 bars vs. 12 bars, on the SAME bar sequence -- not two different bar thresholds), a classic
    /// dual-MA crossover shape. A crossing only qualifies as a signal once the fast/slow GAP at the
    /// crossing bar is at least <paramref name="thresholdPoints"/> (on the same -100..+100 scale
    /// every other score in this file uses) -- a noise filter, not a percentile (this rule has no
    /// percentile concept at all, unlike everything else here). Entry/exit mechanics (ATM strike
    /// selection, real option fills, the standing 09:30-15:00 entry window / 15:15 force-close, one
    /// position at a time, exit only on the opposite qualifying signal or end-of-day) are otherwise
    /// identical to <see cref="SimulateDayAsync"/> for comparability.
    /// </summary>
    public static async Task<List<VolumeBarTrade>> SimulateCrossoverDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        int fastBars, int slowBars, double thresholdPoints, CancellationToken cancellationToken,
        Dictionary<(DateOnly, string), OptionPriceSeries>? sharedPriceCache = null)
    {
        var bars = await volumeBarDb.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (bars.Count == 0)
        {
            return [];
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return [];
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();

        var dayStart = bars[0].StartTimestamp;
        var dayEnd = bars[^1].EndTimestamp;

        var priceCache = sharedPriceCache ?? new Dictionary<(DateOnly, string), OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            var key = (asOfDate, token);
            if (priceCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var series = await OptionPriceSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            priceCache[key] = series;
            return series;
        }

        Domain.Entities.Instrument? PickAtm(OptionType side, decimal futurePrice) => chain
            .Where(o => o.OptionType == side)
            .OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice))
            .FirstOrDefault();

        var depthRank = new SessionRankTracker();
        var durationRank = new SessionRankTracker();
        var tobRank = new SessionRankTracker();
        var scoreWindow = new List<double>(slowBars);
        double? previousDiff = null;

        var trades = new List<VolumeBarTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, string Token, double EntryScore)? open = null;
        decimal? previousClose = null;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var isLastBar = i == bars.Count - 1;

            var score = ComputeSessionGatedScore(bar, previousClose, depthRank, durationRank);
            var tobConfirmScore = SignedRank.Compute(bar.TopOfBookImbalance, tobRank);
            previousClose = bar.ClosePrice;

            double? fastMa = null;
            double? slowMa = null;
            double? diff = null;
            var crossedUp = false;
            var crossedDown = false;

            if (score is { } s)
            {
                scoreWindow.Add(100.0 * s);
                if (scoreWindow.Count > slowBars)
                {
                    scoreWindow.RemoveAt(0);
                }

                if (scoreWindow.Count >= slowBars)
                {
                    fastMa = scoreWindow.Skip(scoreWindow.Count - fastBars).Average();
                    slowMa = scoreWindow.Average();
                    diff = fastMa - slowMa;

                    if (previousDiff is { } prevDiff && diff is { } d)
                    {
                        crossedUp = prevDiff <= 0 && d > 0 && Math.Abs(d) >= thresholdPoints;
                        crossedDown = prevDiff >= 0 && d < 0 && Math.Abs(d) >= thresholdPoints;
                    }

                    previousDiff = diff;
                }
            }

            if (open is { } position)
            {
                var series = await GetSeriesAsync(position.Token);
                var currentPrice = series.PriceAtOrBefore(bar.EndTimestamp) ?? position.EntryPrice;

                var timedOut = IstTimeOfDay(bar.EndTimestamp) >= ForceCloseAt;
                var reversed = position.Side == OptionType.Call ? crossedDown : crossedUp;

                if (timedOut || reversed || isLastBar)
                {
                    var exitReason = timedOut ? "TimeCutoff" : isLastBar ? "EndOfData" : "CrossoverReversed";
                    trades.Add(new VolumeBarTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                        bar.EndTimestamp, currentPrice, exitReason, position.EntryScore));
                    open = null;
                }
            }
            else if (!isLastBar && (crossedUp || crossedDown)
                && IstTimeOfDay(bar.EndTimestamp) >= EntryWindowStart && IstTimeOfDay(bar.EndTimestamp) <= EntryWindowEnd
                // Same TOB open-window confirmation the locked SessionGatedDepthDurationConfirmed
                // score itself requires -- see PassesConfirmation's own doc comment for why.
                && (IstTimeOfDay(bar.EndTimestamp) >= SessionGateSwitchTime
                    || (crossedUp ? tobConfirmScore > 0 : tobConfirmScore < 0)))
            {
                var side = crossedUp ? OptionType.Call : OptionType.Put;
                var candidate = PickAtm(side, bar.ClosePrice);
                if (candidate is not null)
                {
                    var series = await GetSeriesAsync(candidate.Token);
                    var entryPrice = series.PriceAtOrBefore(bar.EndTimestamp);
                    if (entryPrice is { } ep && ep > 0)
                    {
                        open = (bar.EndTimestamp, ep, side, candidate.StrikePrice!.Value, candidate.Token, diff ?? 0.0);
                    }
                }
            }
        }

        return trades;
    }

    static double? ComputeScore(VolumeBarMetric metric, VolumeBarRow bar, decimal? previousClose, long? previousOi, VolumeBarTrendReversionTracker trendTracker, SessionRankTracker rankTracker) => metric switch
    {
        VolumeBarMetric.TrendReversion => trendTracker.Observe(previousClose is { } prev ? (double)(bar.ClosePrice - prev) : null),
        VolumeBarMetric.FutureCvdNet => SignedRank.Compute(bar.FutureCvdNet, rankTracker),
        VolumeBarMetric.DepthImbalance => SignedRank.Compute(bar.FutureDepthImbalance, rankTracker),
        VolumeBarMetric.OrderFlowImbalance => SignedRank.Compute(bar.OrderFlowImbalance, rankTracker),
        VolumeBarMetric.TopOfBookImbalance => SignedRank.Compute(bar.TopOfBookImbalance, rankTracker),
        VolumeBarMetric.TobDepthDivergence => bar.TopOfBookImbalance is { } tob && bar.FutureDepthImbalance is { } depth
            ? SignedRank.Compute(depth - tob, rankTracker)
            : null,
        VolumeBarMetric.FutureOiBuildupQuadrant => ComputeOiBuildupScore(bar, previousClose, previousOi, rankTracker),
        VolumeBarMetric.VwapDeviation => bar.VwapAtClose is { } vwap ? SignedRank.Compute((double)bar.ClosePrice - vwap, rankTracker) : null,
        VolumeBarMetric.BarDurationUrgency => ComputeBarDurationScore(bar, previousClose, rankTracker),
        VolumeBarMetric.PriceImpact => ComputePriceImpactScore(bar, previousClose, rankTracker),
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, null),
    };

    /// <summary>LongBuildup/ShortCovering read bullish, ShortBuildup/LongUnwinding read bearish, Neutral is 0 -- see <see cref="VolumeBarMetric.FutureOiBuildupQuadrant"/>'s own doc comment.</summary>
    static double? ComputeOiBuildupScore(VolumeBarRow bar, decimal? previousClose, long? previousOi, SessionRankTracker rankTracker)
    {
        if (previousClose is not { } prevClose || previousOi is not { } prevOi || bar.OpenInterestAtClose is not { } oi)
        {
            return null;
        }

        var classification = OiBuildupClassifier.Classify(bar.ClosePrice - prevClose, oi - prevOi);
        var directionSign = classification switch
        {
            OiBuildupClassification.LongBuildup or OiBuildupClassification.ShortCovering => 1,
            OiBuildupClassification.ShortBuildup or OiBuildupClassification.LongUnwinding => -1,
            _ => 0,
        };

        return SignedRank.Compute(directionSign * Math.Abs((double)(oi - prevOi)), rankTracker);
    }

    /// <summary>Signed by this bar's own price direction, magnitude is fill speed (1/DurationSeconds) -- see <see cref="VolumeBarMetric.BarDurationUrgency"/>'s own doc comment for why duration alone can't carry a sign.</summary>
    static double? ComputeBarDurationScore(VolumeBarRow bar, decimal? previousClose, SessionRankTracker rankTracker)
    {
        if (previousClose is not { } prevClose || bar.DurationSeconds <= 0)
        {
            return null;
        }

        var priceChange = bar.ClosePrice - prevClose;
        if (priceChange == 0)
        {
            return SignedRank.Compute(0.0, rankTracker);
        }

        return SignedRank.Compute(Math.Sign(priceChange) * (1.0 / bar.DurationSeconds), rankTracker);
    }

    /// <summary>Signed price change per unit volume -- see <see cref="VolumeBarMetric.PriceImpact"/>'s own doc comment for the Kyle's-lambda caveat and the fixed-bar-volume denominator concern.</summary>
    static double? ComputePriceImpactScore(VolumeBarRow bar, decimal? previousClose, SessionRankTracker rankTracker)
    {
        if (previousClose is not { } prevClose || bar.Volume <= 0)
        {
            return null;
        }

        return SignedRank.Compute((double)(bar.ClosePrice - prevClose) / bar.Volume, rankTracker);
    }

    // 2026-09-17, REVISED after the trading-hours-gate correction (see
    // docs/VOLUME_BAR_FINDINGS.md's "Correction" and "Gated calibration" sections) -- the original
    // weights below were derived from an ungated simulation that was never actually tradeable
    // under the standing 09:30-15:00 entry / 15:15 force-close rule, and specifically over-weighted
    // TobDepthDivergence, whose apparent edge turned out to be concentrated in exactly the
    // trading windows the gate excludes. Recalibrated on all 7 available days (2026-09-08 through
    // 2026-09-17) under the gated simulator. Since all 7 days are now used for calibration (no day
    // held out), there is no fresh out-of-sample nudge this time -- replaced with a win-rate-floor
    // edge measure, (win rate - 40%), which rewards win-rate QUALITY the same way this track has
    // repeatedly preferred it over raw net when choosing a combo (see e.g. the DepthImbalance/
    // BarDurationUrgency selections throughout). Relative, not required to sum to 1 --
    // ComputeCompositeScore renormalizes by whichever components are actually present on a given
    // bar. Each metric's raw score is (win rate - 40%) x (concentration discount) x (DTE-consistency
    // discount):
    //   TopOfBookImbalance:  14.7 x 0.767 x 0.996 = 11.23 (33.5% of the raw total)
    //   BarDurationUrgency:  14.9 x 0.715 x 0.943 = 10.04 (29.9%)
    //   DepthImbalance:      16.1 x 0.526 x 0.903 =  7.65 (22.8%, win rate is with the newly
    //                        adopted 30% stop-loss on the 650/93 gated combo)
    //   TobDepthDivergence:   5.1 x 0.661 x 0.976 =  3.29 (9.8%)
    //   OrderFlowImbalance:   4.2 x 0.467 x 0.675 =  1.32 (3.9%)
    const double DepthImbalanceWeight = 7.65;
    const double TobDepthDivergenceWeight = 3.29;
    const double OrderFlowImbalanceWeight = 1.32;
    const double BarDurationUrgencyWeight = 10.04;
    const double TopOfBookImbalanceWeight = 11.23;

    /// <summary>See <see cref="VolumeBarMetric.Composite"/>'s own doc comment for the full design. Each component gets its own independent rank tracker; missing components are excluded and the remaining weights renormalized rather than treated as zero.</summary>
    static double? ComputeCompositeScore(
        VolumeBarRow bar, decimal? previousClose,
        SessionRankTracker depthRank, SessionRankTracker ofiRank, SessionRankTracker tobRank,
        SessionRankTracker divergenceRank, SessionRankTracker durationRank)
    {
        var depth = SignedRank.Compute(bar.FutureDepthImbalance, depthRank);
        var ofi = SignedRank.Compute(bar.OrderFlowImbalance, ofiRank);
        var tob = SignedRank.Compute(bar.TopOfBookImbalance, tobRank);
        var divergence = bar.TopOfBookImbalance is { } t && bar.FutureDepthImbalance is { } d
            ? SignedRank.Compute(d - t, divergenceRank)
            : null;
        var duration = ComputeBarDurationScore(bar, previousClose, durationRank);

        var components = new (double? Score, double Weight)[]
        {
            (depth, DepthImbalanceWeight),
            (ofi, OrderFlowImbalanceWeight),
            (tob, TopOfBookImbalanceWeight),
            (divergence, TobDepthDivergenceWeight),
            (duration, BarDurationUrgencyWeight),
        };

        var presentWeight = 0.0;
        var weightedSum = 0.0;
        foreach (var (score, weight) in components)
        {
            if (score is { } s)
            {
                presentWeight += weight;
                weightedSum += s * weight;
            }
        }

        return presentWeight > 0 ? weightedSum / presentWeight : null;
    }

    /// <summary>See <see cref="VolumeBarMetric.SessionGatedDepthDuration"/>'s own doc comment. Both trackers are fed every bar regardless of which one is active, so each one's running distribution matches what the standalone metric would see over the same day.</summary>
    static double? ComputeSessionGatedScore(VolumeBarRow bar, decimal? previousClose, SessionRankTracker depthRank, SessionRankTracker durationRank)
    {
        var depth = SignedRank.Compute(bar.FutureDepthImbalance, depthRank);
        var duration = ComputeBarDurationScore(bar, previousClose, durationRank);
        return IstTimeOfDay(bar.EndTimestamp) < SessionGateSwitchTime ? depth : duration;
    }

    /// <summary>
    /// Entry-time gate for <see cref="VolumeBarMetric.SessionGatedDepthDurationConfirmed"/> -- a
    /// no-op (always true) for every other metric. During the open (before 10:00 IST) a new
    /// position additionally requires TopOfBookImbalance's own score to agree in sign with the
    /// switched score being traded; outside the open, no confirmation is required (TOB's own edge
    /// there is close to noise -- see the enum value's own doc comment for why).
    /// </summary>
    static bool PassesConfirmation(VolumeBarMetric metric, DateTimeOffset barEnd, double scaledScore, double? tobConfirmScore)
    {
        if (metric != VolumeBarMetric.SessionGatedDepthDurationConfirmed || IstTimeOfDay(barEnd) >= SessionGateSwitchTime)
        {
            return true;
        }

        return tobConfirmScore is { } t && Math.Sign(t) == Math.Sign(scaledScore);
    }

    /// <summary>
    /// This bar's score magnitude expressed as a 0-100 percentile of today's own score-magnitude
    /// distribution so far -- the dynamic entry/exit gate. Self-inclusion-safe (ranks before
    /// adding), same convention as every other <see cref="SessionRankTracker"/> caller in this
    /// codebase.
    /// </summary>
    static double? EntryPercentile(VolumeBarMetric metric, double? scaledScore, SessionRankTracker magnitudeRank)
    {
        if (scaledScore is not { } value)
        {
            return null;
        }

        var magnitude = Math.Abs(value);
        if (metric != VolumeBarMetric.TrendReversion && metric != VolumeBarMetric.Composite)
        {
            // Already a percentile by construction (SignedRank) -- no second rank layer.
            return magnitude;
        }

        // TrendReversion is a raw bounded ratio, Composite is a weighted blend of already-ranked
        // values -- neither is percentile-shaped by construction, so both get their own dedicated
        // magnitude tracker (the caller picks which one based on metric).
        var rank = magnitudeRank.Rank(magnitude);
        magnitudeRank.Add(magnitude);
        return rank;
    }
}
