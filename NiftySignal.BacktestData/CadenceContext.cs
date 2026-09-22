namespace NiftySignal.BacktestData;

/// <summary>
/// One row per 15-second cadence, built purely from raw historical ticks (2026-09-12 design,
/// agreed field-by-field over several rounds -- see docs/REVIEW_FINDINGS.md's 2026-09-12
/// section for the full discussion and rationale behind each field). This is the "parent"
/// table -- a per-strike/option-type child table (with full OHLC per leg, needed for realistic
/// paper-trade fill simulation) is designed but deliberately deferred to a later phase.
///
/// Deliberately leakage-safe: every field here is computable using only information that
/// existed at or before this row's own <see cref="Timestamp"/> -- nothing here ever reads
/// ahead. A "close for day" field was considered and explicitly removed for exactly this
/// reason (any day-close value would require information that doesn't exist yet for any row
/// before the session actually ends).
///
/// Lives in its own dedicated database (niftysignal_backtest_analysis), never
/// NiftySignal.Persistence's live schema -- per the standing rule not to touch anything
/// Host/Dashboard-adjacent until backtesting has found a real edge.
/// </summary>
public sealed class CadenceContext
{
    public long Id { get; set; }

    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>The trading day this cadence belongs to -- avoids every consumer having to derive it from <see cref="Timestamp"/>, matches <c>Instrument.AsOfDate</c>'s own convention.</summary>
    public required DateOnly AsOfDate { get; set; }

    // --- ATM strike candidates ---------------------------------------------------------------
    // Three different notions of "the underlying" -- spot (used for strike selection
    // everywhere in production today), the tracked monthly future, and the put-call-parity
    // synthetic forward (what every live Greeks/IV calculation actually prices against). On a
    // day with real spot-future basis, these can genuinely be three different strikes.
    public decimal? AtmStrikeBySpot { get; set; }

    public decimal? AtmStrikeByFuture { get; set; }

    public decimal? AtmStrikeBySyntheticForward { get; set; }

    // --- Expiry / DTE -------------------------------------------------------------------------
    public required DateOnly NearestExpiryDate { get; set; }

    /// <summary>Calendar hours to expiry (weekends counted) -- identical convention to production's <c>NiftySignal.Pricing.TimeToExpiry.YearsUntilExpiry</c>, just expressed in hours rather than years. Use this for anything that needs to match live Greeks/IV numbers.</summary>
    public double? HoursToExpiryCalendar { get; set; }

    /// <summary>
    /// Hours to expiry counting only actual NSE trading-session time (09:15-15:30 IST on
    /// trading days), weekends excluded -- NOT calendar time. No NSE holiday calendar exists
    /// in this codebase, so market holidays are NOT excluded here either (same acknowledged
    /// gap as production's own trading-day-T discussion, see TimeToExpiry's doc comment). Use
    /// this for rules reasoning about trading opportunity remaining, not raw time decay.
    /// </summary>
    public double? HoursToExpiryTrading { get; set; }

    // --- Data quality -------------------------------------------------------------------------
    // Per-instrument, not combined -- VIX ticks far less often than Spot/Future even when
    // healthy (~every 20-25s), so a single combined count couldn't tell "VIX was quiet"
    // (normal) apart from "Spot was quiet" (a real problem).
    public int TicksObservedSpot { get; set; }

    public int TicksObservedFuture { get; set; }

    public int TicksObservedVix { get; set; }

    // --- Spot (LTP-based -- Spot is a calculated index with no real two-sided tradable quote) -
    public decimal? SpotCloseFromLastCadence { get; set; }

    /// <summary>Intrabar: this cadence's own close minus its own open. Not a cadence-to-cadence delta.</summary>
    public decimal? SpotChangeFromLastCadence { get; set; }

    public decimal? SpotOpenFromLastCadence { get; set; }

    public decimal? SpotHighFromLastCadence { get; set; }

    public decimal? SpotLowFromLastCadence { get; set; }

    public decimal? SpotChangeForDay { get; set; }

    public decimal? SpotOpenForDay { get; set; }

    public decimal? SpotHighForDay { get; set; }

    public decimal? SpotLowForDay { get; set; }

    // --- Future (mid-price based: (bid1+ask1)/2, LTP fallback) --------------------------------
    // Mid, not LTP -- production already found and fixed a real bug where the future's raw
    // last-traded print alternated between two levels a few points apart within seconds,
    // reading as a false move to basis/momentum. Using LTP here would reintroduce it.
    public decimal? FutureCloseFromLastCadence { get; set; }

    public decimal? FutureChangeFromLastCadence { get; set; }

    public decimal? FutureOpenFromLastCadence { get; set; }

    public decimal? FutureHighFromLastCadence { get; set; }

    public decimal? FutureLowFromLastCadence { get; set; }

    public decimal? FutureChangeForDay { get; set; }

    public decimal? FutureOpenForDay { get; set; }

    public decimal? FutureHighForDay { get; set; }

    public decimal? FutureLowForDay { get; set; }

    // --- India VIX (LTP-based -- not a tradable instrument, no real order book) ---------------
    public decimal? VixCloseFromLastCadence { get; set; }

    public decimal? VixChangeFromLastCadence { get; set; }

    public decimal? VixOpenFromLastCadence { get; set; }

    public decimal? VixHighFromLastCadence { get; set; }

    public decimal? VixLowFromLastCadence { get; set; }

    public decimal? VixChangeForDay { get; set; }

    public decimal? VixOpenForDay { get; set; }

    public decimal? VixHighForDay { get; set; }

    public decimal? VixLowForDay { get; set; }

    // --- Future volume --------------------------------------------------------------------------
    /// <summary>Raw cumulative-for-the-day volume, straight from the tick feed.</summary>
    public long? FutureVolumeCumulativeDay { get; set; }

    /// <summary>Volume traded within just this 15s window (cumulative now minus cumulative at the previous cadence, floored at 0 against a feed reset -- same guard every volume diff in production already uses).</summary>
    public long? FutureVolumeDeltaThisCadence { get; set; }

    // --- Future VWAP ----------------------------------------------------------------------------
    /// <summary>Tick-accurate, cumulative-for-the-day volume-weighted average price of the tracked future -- accumulated per real tick, not approximated from per-cadence OHLC.</summary>
    public double? FutureVwap { get; set; }

    // --- Future open interest --------------------------------------------------------------------
    public long? FutureOpenInterest { get; set; }

    /// <summary>
    /// Null until real elapsed time reaches ~<c>NiftySignal.Features.FeatureWindowLengths.OiComparisonWindow</c>
    /// (4 min) of history for the future -- OI genuinely refreshes only every few minutes on
    /// this feed (the same discovery already made and fixed in production, audit finding F50);
    /// comparing against a shorter gap would mostly read as a fabricated no-op.
    /// </summary>
    public long? FutureOiChangeFromLastKnown { get; set; }

    public long? FutureOiChangeForDay { get; set; }

    // --- Future order-book depth -------------------------------------------------------------------
    /// <summary>Average total resting bid quantity (5 depth levels) across every real tick observed this cadence -- raw context for <see cref="FutureDepthImbalanceFromLastCadence"/>, not guaranteed to reproduce it bit-for-bit (averaging a ratio differs from a ratio of averages).</summary>
    public double? FutureTotalBidQty { get; set; }

    public double? FutureTotalAskQty { get; set; }

    /// <summary>
    /// Average of <c>(bidQty-askQty)/(bidQty+askQty)</c> computed at every real tick the future
    /// had this cadence -- not a single boundary snapshot. Built from the complete historical
    /// tick record (not a 3-second-stride approximation, which live production uses only
    /// because it can't wait around on a real-time feed). Null if the future carried no depth
    /// snapshot at all this cadence.
    /// </summary>
    public double? FutureDepthImbalanceFromLastCadence { get; set; }

    /// <summary>Rolling mean of <see cref="FutureDepthImbalanceFromLastCadence"/> over the trailing 5 real minutes (by timestamp, not cadence count) -- a null cadence value is skipped, never zero-filled. Null until real elapsed time reaches 5 minutes of history.</summary>
    public double? FutureDepthImbalanceMean5Min { get; set; }

    /// <summary>Same as <see cref="FutureDepthImbalanceMean5Min"/>, 15-minute window.</summary>
    public double? FutureDepthImbalanceMean15Min { get; set; }

    // --- Future trade-aggressor volume proxy (2026-09-12) ------------------------------------
    // Depth imbalance (above) measures resting liquidity -- who's waiting. This measures
    // classified executed volume -- who's actually trading. They answer different questions and
    // are not substitutes for each other; see docs/REVIEW_FINDINGS.md's 2026-09-12 depth-imbalance
    // correlation section for why both are likely needed before a real weighting decision.

    /// <summary>
    /// Net buy-minus-sell classified volume this cadence, via the same "quote rule" as
    /// production's existing option-chain <c>CvdProxy</c>: each tick's entire volume delta since
    /// the previous tick counts buy-leaning if LastPrice sat at/above the bid-ask midpoint,
    /// sell-leaning otherwise. FlatTrade's feed has no per-trade tape -- confirmed directly
    /// against real ticks that a single reported volume delta routinely bundles thousands of
    /// lots that plausibly traded across many individual prints, some potentially in the
    /// opposite direction. This is a proxy, not true CVD -- cross-reference against
    /// <see cref="TicksObservedFuture"/> (a cadence with a large classified delta but very few
    /// ticks is exactly where this proxy is least trustworthy). Null when no tick this cadence
    /// had both a two-sided depth quote and nonzero volume to classify -- not a guessed zero.
    /// </summary>
    public long? FutureCvdProxyThisCadence { get; set; }

    /// <summary>Running total of <see cref="FutureCvdProxyThisCadence"/> across the whole trading day -- read by its slope, same convention real CVD indicators use, not smoothed with a rolling window.</summary>
    public long? FutureCvdProxyCumulativeDay { get; set; }

    /// <summary>
    /// Rolling SUM (not mean) of <see cref="FutureCvdProxyThisCadence"/> over the trailing 5 real
    /// minutes -- added 2026-09-12 after <see cref="FutureCvdProxyCumulativeDay"/>'s own
    /// correlation check showed its forward-looking relationship with price flipped sign across
    /// days, plausibly because a whole-day running total keeps dragging along a trend from hours
    /// earlier even after the market has genuinely turned. This isolates recent flow instead.
    /// Null cadences are skipped, never zero-filled; null here until real elapsed time reaches 5
    /// minutes of history.
    /// </summary>
    public long? FutureCvdProxyNet5Min { get; set; }

    /// <summary>Same as <see cref="FutureCvdProxyNet5Min"/>, 15-minute window.</summary>
    public long? FutureCvdProxyNet15Min { get; set; }
}
