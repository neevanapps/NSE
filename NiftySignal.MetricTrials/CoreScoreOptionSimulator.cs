using NiftySignal.BacktestData;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;

namespace NiftySignal.MetricTrials;

/// <summary>
/// 2026-09-13: started as the 3 CONFIRMED/strongest-watch candidates from that day's live-vs-lab
/// pass; extended the same day (per user's own explicit weight proposal) to 8 terms, then reduced
/// to 6 after the 11 Sep investigation (see below). No z-scores anywhere -- every raw metric is
/// converted to a signed value in [-1,1] via its SESSION-SO-FAR percentile rank
/// (<see cref="SessionRankTracker"/>, already used elsewhere in this project for exactly this
/// "no hardcoded magnitude, no look-ahead" reason) rather than a fixed clip scale.
///
/// **Deliberate simplification, flagged for the record**: `docs/SCORE_CANDIDATES.md` documents two
/// slightly different session-rank conventions across these metrics -- this file's original 3 rank
/// the VALUE'S MAGNITUDE against a history of magnitudes and reattach the sign
/// (<c>RankSigned</c> below), while `FutureCvdNet5Min`'s own documented formula ranks the SIGNED
/// value directly against a history of signed values (<c>(percentile_rank(raw) - 50) * 2</c>). For
/// internal consistency across every term in one composite, every ranked term here uses the
/// magnitude-with-sign convention uniformly, not a per-metric mix. For a roughly symmetric
/// day-to-day distribution the two are close but not identical; revisit if a specific metric's
/// behavior looks wrong against its own documented evidence.
///
/// Weights, signs, and bands are exactly what today's testing found (see SCORE_CANDIDATES.md),
/// not re-derived here:
/// - DepthImbalance: call minus put resting depth, Itm2Atm1 band -- CONFIRMED, positive already
///   means "bullish" as tested, no inversion.
/// - ItmSkew: PutAvgIv minus CallAvgIv, Itm2Atm1 band -- CONFIRMED among non-expiry ThisWeek days
///   only. **Nulled outright on a 0-DTE (expiry) day** per user's own instruction -- IV is known to
///   distort severely on expiry day (see the "expiry-day regime exclusion" finding), so the whole
///   day is excluded rather than fed a known-bad reading; the renormalize-by-present-weight step
///   below absorbs the missing term the same way it absorbs any other cadence-level gap. **Weight
///   CUT in half (0.13 -> 0.065, 2026-09-13)** -- the 11 Sep investigation found this term read
///   bearish on literally 0% of 1500 cadences THE ENTIRE SESSION, dead wrong the whole day despite
///   a persistent uptrend, yet it's a slow LEVEL quantity that barely moves, so it was never going
///   to self-correct intraday. "Confirmed on average across days" doesn't mean "right every day" --
///   cut, not dropped, since the multi-day correlation is real; a whole-session miss like 11 Sep
///   shouldn't carry full weight going forward.
/// - FutureCvdNet5Min: <see cref="CadenceContext.FutureCvdProxyNet5Min"/> -- PROMOTED, sign
///   confirmed positive, read directly (no inversion).
/// - NotionalVolumeRatio: callNotional/putNotional, ATM+/-5 -- watch, TESTED NEGATIVE correlation
///   with price -- inverted (put/call instead of call/put) so positive consistently means bullish.
/// - GammaExposure: sum(call: +Gamma*OI, put: -Gamma*OI), ATM+/-10 chain -- watch, sign confirmed
///   positive on all 4 days tested, no inversion. **Weight CUT in half (0.12 -> 0.06, 2026-09-13)**
///   -- same 11 Sep finding as ItmSkew (bearish 93.3% of the whole session), and it's only
///   watch-tier evidence to begin with, so cut rather than kept at full weight.
/// - TrendReversion15m: signed net Future price change / path length over a trailing real 15
///   minutes, negated -- provisionally promoted reversion (not continuation) signal. **Re-included
///   (2026-09-13)** after the 11 Sep investigation showed dropping this term (and BasisChange) did
///   NOT fix the stuck-put problem -- the real culprit was ItmSkew/GammaExposure's whole-session
///   lockup, unrelated to this term. No evidence justified removing it; restored at its original
///   weight while the actual cause gets cut instead.
/// - BasisChange: FutureChange - SpotChange, per cadence -- provisionally promoted, sign confirmed
///   negative, negated here to keep "positive score = bullish" consistent across every term.
///   **Re-included (2026-09-13)**, same reasoning as TrendReversion15m above.
/// - OiChangeDiff15m: rolling 15-min sum of (CallOiDelta - PutOiDelta), ATM+/-2 -- watch, sign
///   UNRESOLVED per SCORE_CANDIDATES.md (day-dependent). Used here with the NAIVE (uninverted)
///   sign, matching the majority (3 of 4 days) at the specific 15-min-bucket horizon this metric
///   uses -- but this is explicitly the shakiest sign call of the eight; watch this term's own
///   contribution closely in the results, don't just trust the composite's total.
///
/// 2026-09-13 (later): the per-cadence score computation was extracted into
/// <see cref="BuildScoreCadences"/> so a SECOND trading strategy (<see cref="SimulateDayCrossover"/>)
/// could reuse the exact same 8-metric composite without duplicating it -- "just an experiment"
/// per explicit instruction, comparing this composite's own fast (5-min) vs slow (15-min) smoothed
/// value, trading the crossover between them, instead of the hysteresis-threshold state machine
/// <see cref="SimulateDay"/> already uses. Both consume the identical score series; only what they
/// DO with it differs.
/// </summary>
public sealed record CoreScoreWeights(
    double DepthImbalanceWeight = 0.25,
    double ItmSkewWeight = 0.065,
    double FutureCvdNet5MinWeight = 0.12,
    double NotionalVolumeRatioWeight = 0.14,
    double GammaExposureWeight = 0.06,
    double TrendReversion15mWeight = 0.10,
    double BasisChangeWeight = 0.08,
    double OiChangeDiff15mWeight = 0.10,
    // 2026-09-17, experimental -- zero by default (preserves every existing caller's/test's
    // behavior unchanged). See BuildScoreCadences's own doc comment on ItmSkewChangeHistory: these
    // are NOT the same terms as ItmSkewWeight/GammaExposureWeight above (those stay on the LEVEL,
    // now closed per docs/SCORE_CANDIDATES.md) -- these score the rolling CHANGE instead, the
    // reformulation that showed a real, dual-target-confirmed signal for ItmSkew specifically.
    double ItmSkewChange15mWeight = 0.0,
    double GammaExposureChange5mWeight = 0.0,
    // 2026-09-17, experimental -- false by default (preserves every existing caller's/test's
    // behavior unchanged). FutureCvdNet5Min was found reading below a coin flip on 4 of 5 days, at
    // every target/horizon checked, in THIS composite's own current data -- see
    // docs/SCORE_CANDIDATES.md's 2026-09-17 root-cause section (ruled out the percentile-rank
    // transform and the row-count-vs-time forward-window methodology as explanations; most likely
    // the underlying data changed since the original 2026-09-12 "positive on 3 of 4 days"
    // confirmation, making that verdict stale). A flag, not a permanent sign flip in the formula
    // itself below -- the actual keep-vs-invert decision needs a real backtest first, same
    // discipline as every other change here.
    bool InvertFutureCvdNet5Min = false,
    // Provisional starting point, not a tuned value -- same "starting point, not the answer"
    // framing as every other k in this project's history (see CompositeScoreCalculator.DefaultK's
    // own doc comment).
    double K = 1.0)
{
    /// <summary>Sums to 0.915, not 1.00 -- left as-is rather than silently rescaled, same reasoning as every other total here. The renormalize-by-present-weight step divides by whatever total is actually present each cadence, so this doesn't break anything.</summary>
    public double Total => DepthImbalanceWeight + ItmSkewWeight + FutureCvdNet5MinWeight + NotionalVolumeRatioWeight
        + GammaExposureWeight + TrendReversion15mWeight + BasisChangeWeight + OiChangeDiff15mWeight
        + ItmSkewChange15mWeight + GammaExposureChange5mWeight;
}

public sealed record CoreScoreSimulationOptions(
    double EntryScoreThreshold = 30.0,
    // 2026-09-13: fixed-cadence hold removed per explicit instruction ("no mechanical time gate").
    // Exit uses a hysteresis band (see the state machine below) -- a position closes only once the
    // score crosses all the way to the OPPOSITE threshold. ForceCloseTime remains as a backstop
    // (data/session boundary, not an artificial hold length) since a position can't carry overnight
    // here.
    decimal EntryPriceRangeLow = 100m,
    decimal EntryPriceRangeHigh = 150m,
    CoreScoreWeights? Weights = null,
    // 2026-09-14, experimental (see SimulateDay's own UpdateScoreSmoothing note): 1 (default) means
    // unsmoothed, exactly today's live behavior -- both entry and the hysteresis exit read the
    // instant per-cadence score. >1 trails the last N cadences' raw scores (a plain count-based
    // average, NOT a real-time window like CoreScoreCrossoverOptions' fast/slow -- "4 cadences"
    // means the last 4 computed values, however far apart in wall-clock time a quiet session made
    // them). Applied to BOTH entry and exit checks, not just one -- a smoothed entry gated by a
    // razor-sharp exit (or vice versa) would just be a different, undocumented strategy shape.
    int ScoreSmoothingCadences = 1,
    // 2026-09-16, experimental (live-caught 2026-09-15: two Hysteresis trades held 135/172 minutes
    // on a stuck score lost 58%/72% of premium with nothing to stop it -- see this class's own
    // "no SL/TP" note above, now qualified by this parameter). Percent of ENTRY PREMIUM, e.g. 30
    // means "exit once the position is down 30% from its own entry price" -- independent of the
    // score, checked every cadence a fresh quote exists for the held contract (same quote source
    // MFE/MAE already use). null (default) disables it entirely, preserving every existing
    // caller's/test's behavior unchanged. Deliberately a percent of premium, not a fixed point
    // amount -- premiums here range from EntryPriceRangeLow to EntryPriceRangeHigh, so a fixed
    // point stop would be a wildly different fraction of risk depending on which strike got picked.
    decimal? StopLossPct = null,
    // 2026-09-16, added alongside StopLossPct after backtesting showed the stop ALONE makes a
    // stuck-score session worse, not better: a tight stop on a persistently-miscalibrated score
    // (09-15's own composite stayed bullish essentially all session while the market fell -- see
    // the correlation check that found this) just re-enters the SAME losing side over and over,
    // each loss compounding off a lower base than the last -- literally a bigger cumulative loss
    // than one long, unstopped ride would have been. Same semantics as the LIVE engine's own
    // RiskLimits.MaxConsecutiveLosses (CoreScoreHysteresisTradingEngine.EvaluateEntryAsync) --
    // once this many CLOSED trades in a row lost money, no new entry until a win resets the streak.
    // null (default) disables it, preserving every existing caller's/test's behavior unchanged.
    int? MaxConsecutiveLosses = null,
    // 2026-09-16, experimental (scoping audit finding F-A's own follow-up): 0 (default) means
    // unsmoothed, exactly today's behavior -- DepthImbalanceRaw is BuildScoreCadences' own
    // single-15s-cadence instant read, unchanged. >0 routes that same raw value through a
    // CoreScoreRollingMeanTracker of this many minutes BEFORE ranking, instead of ranking the
    // instant value -- see CoreScoreRollingMeanTracker's own doc comment for why (correlation
    // evidence in docs/SCORE_CANDIDATES.md showed this term's forward correlation roughly doubles
    // at a 10-minute window versus the raw instant read, unlike PriceMomentum's own smoothing,
    // which made things worse -- this flag exists to confirm whether that correlation gain
    // actually shows up in real trade P&L, not just in the correlation number).
    int DepthImbalanceSmoothingMinutes = 0)
{
    public CoreScoreWeights EffectiveWeights => Weights ?? new CoreScoreWeights();
}

/// <summary>
/// 2026-09-13, experimental: trade the crossover of the SAME 8-metric composite score smoothed
/// over two different real-time trailing windows (fast/slow), mirroring the fast/slow
/// momentum-crossover idea already used elsewhere in this project (F55, for the separate ratio
/// composite) -- but applied here to this session's Core score instead of building a third,
/// unrelated pipeline. Always positioned once the first crossover fires: a new cross flips the
/// existing position (closes it, immediately opens the opposite side) rather than requiring a
/// separate flat-then-re-enter step -- the standard convention for a moving-average-crossover
/// system, and consistent with this project's own earlier "ALWAYS POSITIONED" precedent
/// (FutureDirectSimulator's variants 5/6). No SL/TP here either: the only exits are the next
/// opposite crossover, or ForceClose at the session boundary.
/// </summary>
public sealed record CoreScoreCrossoverOptions(
    int FastWindowMinutes = 5,
    int SlowWindowMinutes = 15,
    decimal EntryPriceRangeLow = 100m,
    decimal EntryPriceRangeHigh = 150m,
    CoreScoreWeights? Weights = null,
    // See CoreScoreSimulationOptions' own doc comment on this same field.
    int DepthImbalanceSmoothingMinutes = 0)
{
    public CoreScoreWeights EffectiveWeights => Weights ?? new CoreScoreWeights();
}

/// <summary>2026-09-15, experimental -- see <see cref="CoreScoreOptionSimulator.SimulateDayCombined"/>'s own doc comment.</summary>
public sealed record CoreScoreCombinedOptions(
    double EntryScoreThreshold = 30.0,
    int FastWindowMinutes = 10,
    int SlowWindowMinutes = 30,
    decimal EntryPriceRangeLow = 100m,
    decimal EntryPriceRangeHigh = 150m,
    CoreScoreWeights? Weights = null,
    bool ExitOnEitherOpposite = true,
    // See CoreScoreSimulationOptions' own doc comment on this same field.
    int DepthImbalanceSmoothingMinutes = 0)
{
    public CoreScoreWeights EffectiveWeights => Weights ?? new CoreScoreWeights();
}

public sealed record CoreScoreTrade(
    DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice,
    DateTimeOffset ExitTime, decimal ExitPrice, string ExitReason,
    decimal Mfe, decimal Mae, double EntryScore)
{
    /// <summary>Points on the traded option's own price -- no lot size, no transaction costs. Always a LONG position (buying premium, matching how this system actually trades) -- never negated, unlike a long/short future simulator.</summary>
    public decimal NetPnlPoints => ExitPrice - EntryPrice;

    /// <summary>Percent return on the entry premium -- (ExitPrice-EntryPrice)/EntryPrice*100. Same long-only, no-lot-size, no-cost caveats as NetPnlPoints; EntryPrice is always > 0 by construction (only priced strikes are ever entered), so no zero-guard needed.</summary>
    public decimal NetPnlPercent => NetPnlPoints / EntryPrice * 100m;
}

/// <summary>
/// One cadence's full diagnostic breakdown (2026-09-13, Batch 3 historical replay validation) --
/// every raw/signed value plus the final CoreScore and its fast/slow smoothed reads, for diffing
/// against the live LiveFeatureEngine port's own CoreScoreSnapshot at matching timestamps. See
/// <see cref="CoreScoreOptionSimulator.BuildDiagnostics"/>.
/// </summary>
public sealed record CoreScoreCadenceDiagnostics(
    DateTimeOffset Timestamp,
    double? DepthImbalanceRaw, double? DepthImbalanceSigned,
    double? ItmSkewRaw, double? ItmSkewSigned,
    double? FutureCvdNet5MinRaw, double? FutureCvdNet5MinSigned,
    double? NotionalVolumeRatioRaw, double? NotionalVolumeRatioSigned,
    double? GammaExposureRaw, double? GammaExposureSigned,
    double? TrendReversion15mRaw, double? TrendReversion15mSigned,
    double? BasisChangeRaw, double? BasisChangeSigned,
    double? OiChangeDiff15mRaw, double? OiChangeDiff15mSigned,
    double? CoreScore, double? CoreScoreFast, double? CoreScoreSlow,
    // TEMPORARY DIAGNOSTIC (2026-09-16) -- see SessionRankTracker.Count's own comment. Safe to
    // remove once the BasisChangeSigned live-vs-backtest divergence is resolved.
    int BasisChangeRankCount = 0, IReadOnlyList<double>? BasisChangeRankValues = null);

public sealed record CoreScoreDayResult(DateOnly AsOfDate, IReadOnlyList<CoreScoreTrade> Trades)
{
    public decimal NetPnlPoints => Trades.Sum(t => t.NetPnlPoints);
    public int WinCount => Trades.Count(t => t.NetPnlPoints > 0);
    public decimal AvgMfe => Trades.Count > 0 ? Trades.Average(t => t.Mfe) : 0;
    public decimal AvgMae => Trades.Count > 0 ? Trades.Average(t => t.Mae) : 0;

    /// <summary>Straight sum of every trade's own NetPnlPercent -- per explicit instruction (2026-09-13): the total is the total of each trade's percentage profit, not a capital-weighted return. Each trade counts once regardless of its own entry premium's size.</summary>
    public decimal TotalPnlPercent => Trades.Sum(t => t.NetPnlPercent);

    public decimal AvgPnlPercent => Trades.Count > 0 ? Trades.Average(t => t.NetPnlPercent) : 0;
}

public static class CoreScoreOptionSimulator
{
    // Itm2Atm1 band definition (calls {-2,-1,0}, puts {0,+1,+2}) moved to the shared
    // NiftySignal.Scoring.CoreScoreBands.InItm2Atm1 as part of 2026-09-17 Phase 2 unification --
    // DepthImbalance/ItmSkew above are the only two callers here, both now routed through
    // NiftySignal.Scoring.CoreScoreRawFormulas, which applies the band internally.

    const int NotionalBandOffset = 5; // ATM+/-5, matches RatioWideStrikeBand
    const int GammaBandOffset = 10; // ATM+/-10, matches the live GammaExposure test's full-chain universe
    // 2026-09-13: no band was finalized in SCORE_CANDIDATES.md for the 15-min-bucket OI-diff read
    // specifically -- ATM+/-2 chosen here to match the PersistedStrikeBand convention already used
    // elsewhere in this project (e.g. live Pcr's F5 fix), not independently re-derived. Flagged as
    // provisional, same spirit as every other "starting point, not the answer" constant here.
    const int OiDiffBandOffset = 2;
    static readonly TimeSpan TrendReversionWindow = TimeSpan.FromMinutes(15);
    static readonly TimeSpan OiDiffWindow = TimeSpan.FromMinutes(15);

    sealed record FilledRow(DateTimeOffset Timestamp, OptionType OptionType, decimal StrikePrice, int Offset,
        decimal? Mark, double? Depth, double? Iv, long? VolumeDeltaRaw, double? Gamma, long? OpenInterest,
        long? OpenInterestDeltaRaw);

    /// <summary>
    /// One cadence's resolved composite score plus everything a trading strategy needs to act on
    /// it -- shared by both SimulateDay and SimulateDayCrossover so the 8-metric computation
    /// exists in exactly one place. The 8 raw/signed pairs (2026-09-13, Batch 3 historical replay
    /// validation) are carried here purely as diagnostics -- SimulateDay/SimulateDayCrossover
    /// never read them, only BuildDiagnostics does.
    /// </summary>
    sealed record ScoreCadence(DateTimeOffset Timestamp, TimeOnly LocalTime, bool EntryWindowOpen, bool MustForceClose,
        double? Score, List<FilledRow> Rows,
        double? DepthImbalanceRaw, double? DepthImbalanceSigned,
        double? ItmSkewRaw, double? ItmSkewSigned,
        double? FutureCvdNet5MinRaw, double? FutureCvdNet5MinSigned,
        double? NotionalVolumeRatioRaw, double? NotionalVolumeRatioSigned,
        double? GammaExposureRaw, double? GammaExposureSigned,
        double? TrendReversion15mRaw, double? TrendReversion15mSigned,
        double? BasisChangeRaw, double? BasisChangeSigned,
        double? OiChangeDiff15mRaw, double? OiChangeDiff15mSigned,
        // TEMPORARY DIAGNOSTIC (2026-09-16) -- see SessionRankTracker.Count's own comment.
        int BasisChangeRankCount = 0, IReadOnlyList<double>? BasisChangeRankValues = null);

    /// <summary>
    /// 2026-09-13: entry per explicit instruction picks the strike priced near [low,high], not ATM --
    /// among this cadence's rows for the chosen side, prefer any strike actually inside the range
    /// (closest to its midpoint); if none quote inside the range this cadence, fall back to whichever
    /// strike is closest to it. Returns null only if the side has no priced strikes at all this cadence.
    /// </summary>
    static (decimal Price, decimal Strike)? PickEntryStrike(List<FilledRow> rows, OptionType side, decimal low, decimal high)
    {
        var candidates = rows.Where(r => r.OptionType == side && r.Mark is > 0).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var mid = (low + high) / 2m;
        var inRange = candidates.Where(r => r.Mark >= low && r.Mark <= high).ToList();
        var pool = inRange.Count > 0 ? inRange : candidates;
        var best = pool.OrderBy(r => Math.Abs(r.Mark!.Value - mid)).First();
        return (best.Mark!.Value, best.StrikePrice);
    }

    /// <summary>Ranks |raw| against this metric's own session-so-far distribution of magnitudes, reattaches raw's sign -- see the class doc comment for why this convention was applied uniformly rather than mixing per-metric formulas. Reads the rank BEFORE adding (self-inclusion-safe, no same-bar leakage).</summary>
    static double? RankSigned(double? raw, SessionRankTracker rank)
    {
        if (raw is not { } value || value == 0)
        {
            return raw is 0 ? 0.0 : null;
        }

        var signed = Math.Sign(value) * (rank.Rank(Math.Abs(value)) / 100.0);
        rank.Add(Math.Abs(value));
        return signed;
    }

    /// <summary>
    /// Builds the full per-cadence composite score series for one day, plus the strike-identity-safe
    /// price lookup every trading strategy needs once a position is open. This is the entire
    /// 8-metric computation from the class doc comment, in one place -- SimulateDay and
    /// SimulateDayCrossover both consume this unchanged; neither recomputes any raw metric itself.
    /// </summary>
    static (List<ScoreCadence> Cadences, Dictionary<(OptionType, decimal), Dictionary<DateTimeOffset, decimal?>> PriceByStrikeAndTime)
        BuildScoreCadences(IReadOnlyList<StrikeCadenceSnapshot> dayStrikeRows, IReadOnlyList<CadenceContext> dayCadenceContexts,
            DateOnly thisWeekExpiry, CoreScoreWeights weights, int depthImbalanceSmoothingMinutes = 0)
    {
        var asOfDate = dayStrikeRows[0].AsOfDate;
        var isExpiryDay = asOfDate == thisWeekExpiry; // 0 DTE -- nulls ItmSkew below, per user's own instruction.

        // Experimental (see CoreScoreSimulationOptions.DepthImbalanceSmoothingMinutes' own doc
        // comment) -- null when the flag is off (0), preserving today's exact unsmoothed behavior.
        // One instance per call, i.e. per day, matching every other per-day tracker in this method.
        var depthImbalanceSmoother = depthImbalanceSmoothingMinutes > 0
            ? new NiftySignal.Scoring.CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(depthImbalanceSmoothingMinutes))
            : null;

        var thisWeekRows = dayStrikeRows.Where(r => r.ExpiryDate == thisWeekExpiry).ToList();
        var cadenceContextByTimestamp = dayCadenceContexts.ToDictionary(c => c.Timestamp);

        // Forward-fill Mark/Depth/Iv/Gamma/OpenInterest per (OptionType, StrikePrice) -- all LEVEL
        // quantities, same "gaps and islands" carry-forward already used throughout this project's
        // SQL analysis. VolumeDelta and OpenInterestDelta are NOT forward-filled -- both are
        // genuine per-cadence flows; null/absent means zero contribution that cadence, matching the
        // live engine's own null-guard-then-skip convention.
        var filled = new List<FilledRow>();
        foreach (var group in thisWeekRows.GroupBy(r => (r.OptionType, r.StrikePrice)))
        {
            decimal? lastMark = null;
            double? lastDepth = null;
            double? lastIv = null;
            double? lastGamma = null;
            long? lastOi = null;
            foreach (var row in group.OrderBy(r => r.Timestamp))
            {
                lastMark = row.MarkPrice ?? lastMark;
                lastDepth = row.DepthImbalanceFromLastCadence ?? lastDepth;
                lastIv = row.ImpliedVolatility ?? lastIv;
                lastGamma = row.Gamma ?? lastGamma;
                lastOi = row.OpenInterest ?? lastOi;
                filled.Add(new FilledRow(row.Timestamp, group.Key.OptionType, group.Key.StrikePrice, row.StrikeOffsetFromAtm,
                    lastMark, lastDepth, lastIv, row.VolumeDelta, lastGamma, lastOi, row.OpenInterestDelta));
            }
        }

        // Per-cadence snapshot: every strike's forward-filled state at that instant, and a fast
        // lookup for "this specific contract's own price path" (needed once a trade is open, since
        // an open position must track the SAME physical strike it was entered on, never whichever
        // strike happens to be ATM later -- the exact strike-identity discipline this whole project
        // established the hard way for CVD/PCR/OI-diff).
        var byTimestamp = filled.GroupBy(r => r.Timestamp).OrderBy(g => g.Key).ToList();
        var priceByStrikeAndTime = filled
            .GroupBy(r => (r.OptionType, r.StrikePrice))
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Timestamp, r => r.Mark));

        var depthRank = new SessionRankTracker();
        var skewRank = new SessionRankTracker();
        var cvdRank = new SessionRankTracker();
        var notionalRank = new SessionRankTracker();
        var gammaRank = new SessionRankTracker();
        var basisRank = new SessionRankTracker();
        var oiDiffRank = new SessionRankTracker();
        var itmSkewChangeRank = new SessionRankTracker();
        var gammaExposureChangeRank = new SessionRankTracker();

        // Shared with LiveFeatureEngine.cs's own _coreTrendReversionWindow (2026-09-17 Phase 2
        // unification) -- the sliding window itself, not just the formula, was duplicated.
        var trendReversionTracker = new NiftySignal.Scoring.CoreScoreTrendReversionTracker(TrendReversionWindow);
        // Shared with LiveFeatureEngine.cs's own _coreOiChangeDiffWindow (2026-09-17 Phase 2
        // unification) -- the sliding window itself, not just the formula, was duplicated.
        var oiDiffTracker = new NiftySignal.Scoring.CoreScoreRollingNetDiffTracker(OiDiffWindow);

        // 2026-09-17, experimental: ItmSkew's and GammaExposure's LEVELS are both closed (see
        // docs/SCORE_CANDIDATES.md's 2026-09-16 revisions -- ItmSkew structurally one-sided,
        // GammaExposure OI-driven and chain-dependent). But a genuine demand/supply SHIFT plays out
        // over minutes, not as a static level -- user's own diagnosis, confirmed by a rolling-window
        // re-test that found ItmSkew's 15-min change (NOT its level, NOT a tick-to-tick delta) real
        // and same-signed against BOTH future and option price on all 3 non-expiry ThisWeek days
        // (+0.12 to +0.19 vs future, +0.13 to +0.16 vs option). GammaExposure's own change was
        // checked too but is far less consistent (window disagreement, incoherent on NextWeek) --
        // included anyway per explicit instruction, at a correspondingly smaller weight. Two-pointer
        // history buffers below: `*ChangeHistory` grows once per cadence (append-only, chronological
        // by construction since `byTimestamp` is itself ordered), `*ChangeRefIndex` only ever moves
        // forward -- O(1) amortized "value as of (now - window)" lookup, not an O(n) rescan.
        var itmSkewChangeHistory = new List<(DateTimeOffset Timestamp, double Value)>();
        var itmSkewChangeRefIndex = 0;
        var gammaExposureChangeHistory = new List<(DateTimeOffset Timestamp, double Value)>();
        var gammaExposureChangeRefIndex = 0;
        var itmSkewChangeWindow = TimeSpan.FromMinutes(15); // the window that actually showed signal; 5-min was weaker/inconsistent on the same days
        var gammaExposureChangeWindow = TimeSpan.FromMinutes(5); // less-validated than ItmSkew's; 5-min leaned marginally more consistent across days than 15-min

        double? RollingChange(List<(DateTimeOffset Timestamp, double Value)> history, ref int refIndex, DateTimeOffset timestamp, TimeSpan window, double? currentValue)
        {
            double? change = null;
            if (currentValue is { } current)
            {
                while (refIndex + 1 < history.Count && history[refIndex + 1].Timestamp <= timestamp - window)
                {
                    refIndex++;
                }

                if (history.Count > 0 && history[refIndex].Timestamp <= timestamp - window)
                {
                    change = current - history[refIndex].Value;
                }

                history.Add((timestamp, current));
            }

            return change;
        }

        var cadences = new List<ScoreCadence>();

        foreach (var cadence in byTimestamp)
        {
            var timestamp = cadence.Key;
            var localTime = TimeOnly.FromTimeSpan(timestamp.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
            var entryWindowOpen = localTime >= SimulationRules.FirstEntryTime && localTime < SimulationRules.LastEntryTime;
            var mustForceClose = localTime >= SimulationRules.ForceCloseTime;

            var rows = cadence.ToList();
            cadenceContextByTimestamp.TryGetValue(timestamp, out var ctx);

            // --- Depth imbalance, Itm2Atm1 (CONFIRMED) -- band check + final average-difference
            // shared with NiftySignal.Host/LiveFeatureEngine.cs's ComputeCoreDepthImbalanceRaw
            // (2026-09-17 Phase 2 unification). Fully qualified (not a blanket `using
            // NiftySignal.Scoring;`) since this file's own CoreScoreWeights would otherwise
            // collide with NiftySignal.Scoring.CoreScoreWeights -- same reasoning as Phase 1's
            // combine-step call site below. ---
            var callDepth = rows.Where(r => r.OptionType == OptionType.Call && NiftySignal.Scoring.CoreScoreBands.InItm2Atm1(OptionType.Call, r.Offset) && r.Depth is not null)
                .Select(r => r.Depth!.Value).ToList();
            var putDepth = rows.Where(r => r.OptionType == OptionType.Put && NiftySignal.Scoring.CoreScoreBands.InItm2Atm1(OptionType.Put, r.Offset) && r.Depth is not null)
                .Select(r => r.Depth!.Value).ToList();
            double? depthImbalanceRaw = NiftySignal.Scoring.CoreScoreRawFormulas.DepthImbalance(callDepth, putDepth);
            // Experimental smoothing (see CoreScoreSimulationOptions.DepthImbalanceSmoothingMinutes'
            // own doc comment) -- substitutes the rolling-window mean for the instant value used by
            // BOTH ranking and the diagnostic field below, when enabled. depthImbalanceSmoother is
            // null (flag off) leaves this line's result identical to the instant value above.
            if (depthImbalanceSmoother is not null)
            {
                depthImbalanceRaw = depthImbalanceSmoother.Observe(timestamp, depthImbalanceRaw);
            }

            // --- ITM skew, Itm2Atm1 (CONFIRMED among non-expiry days -- nulled on 0 DTE) -- shared
            // formula, 2026-09-17 Phase 2 unification (see DepthImbalance's own comment above). ---
            double? itmSkewRaw = null;
            if (!isExpiryDay)
            {
                var callIv = rows.Where(r => r.OptionType == OptionType.Call && NiftySignal.Scoring.CoreScoreBands.InItm2Atm1(OptionType.Call, r.Offset) && r.Iv is not null)
                    .Select(r => r.Iv!.Value).ToList();
                var putIv = rows.Where(r => r.OptionType == OptionType.Put && NiftySignal.Scoring.CoreScoreBands.InItm2Atm1(OptionType.Put, r.Offset) && r.Iv is not null)
                    .Select(r => r.Iv!.Value).ToList();
                itmSkewRaw = NiftySignal.Scoring.CoreScoreRawFormulas.ItmSkew(callIv, putIv);
            }

            // --- ItmSkewChange15m: rolling 15-min CHANGE in the level above, not the level itself
            // (2026-09-17 -- see this method's own doc comment on the two-pointer history buffers) ---
            var itmSkewChange15mRaw = RollingChange(itmSkewChangeHistory, ref itmSkewChangeRefIndex, timestamp, itmSkewChangeWindow, itmSkewRaw);

            // --- FutureCvdNet5Min (PROMOTED, sign confirmed positive, read directly) ---
            double? futureCvdRaw = ctx?.FutureCvdProxyNet5Min;

            // --- NotionalVolumeRatio, ATM+/-5, callNotional/putNotional (watch, inverted) -- final
            // log-ratio shared with NiftySignal.Host/LiveFeatureEngine.cs's
            // ComputeCoreNotionalVolumeRatioRaw (2026-09-17 Phase 2 unification). ---
            var callNotional = rows.Where(r => r.OptionType == OptionType.Call && Math.Abs(r.Offset) <= NotionalBandOffset && r.VolumeDeltaRaw is > 0 && r.Mark is > 0)
                .Select(r => (double)r.VolumeDeltaRaw!.Value * (double)r.Mark!.Value).ToList();
            var putNotional = rows.Where(r => r.OptionType == OptionType.Put && Math.Abs(r.Offset) <= NotionalBandOffset && r.VolumeDeltaRaw is > 0 && r.Mark is > 0)
                .Select(r => (double)r.VolumeDeltaRaw!.Value * (double)r.Mark!.Value).ToList();
            // Tested NEGATIVE correlation with price -- inverted here (put/call instead of
            // call/put) so a positive log-ratio consistently means "bullish" like the other terms.
            double? notionalLogRatioRaw = NiftySignal.Scoring.CoreScoreRawFormulas.NotionalVolumeRatio(callNotional, putNotional);

            // --- GammaExposure, ATM+/-10, sum(call:+Gamma*OI, put:-Gamma*OI) (watch, positive sign)
            // -- shared with LiveFeatureEngine.cs's ComputeCoreGammaExposureRaw (2026-09-17 Phase 2
            // unification). Known residual parity gap (26/7500 cadences, max diff ~2471, per
            // docs/SCORE_CANDIDATES.md) is a different STRIKE SET at the band's edge, not this
            // arithmetic -- extracting it here doesn't fix or mask that, by design. ---
            var gexRows = rows.Where(r => Math.Abs(r.Offset) <= GammaBandOffset && r.Gamma is not null && r.OpenInterest is not null)
                .Select(r => (r.OptionType, r.Gamma!.Value, (double)r.OpenInterest!.Value)).ToList();
            double? gammaExposureRaw = NiftySignal.Scoring.CoreScoreRawFormulas.GammaExposure(gexRows);

            // --- GammaExposureChange5m: rolling 5-min CHANGE in the level above (2026-09-17) ---
            var gammaExposureChange5mRaw = RollingChange(gammaExposureChangeHistory, ref gammaExposureChangeRefIndex, timestamp, gammaExposureChangeWindow, gammaExposureRaw);

            // --- TrendReversion15m: signed net Future change / path length, trailing 15 real min --
            // shared sliding-window tracker with LiveFeatureEngine.cs (2026-09-17 Phase 2
            // unification). Already bounded [-1,1] by construction (net <= path always) -- used
            // directly as this term's signed contribution, no session-rank needed. ---
            double? trendReversionSigned = trendReversionTracker.Observe(
                timestamp, ctx?.FutureChangeFromLastCadence is { } futureChange ? (double)futureChange : null);

            // --- BasisChange: FutureChange - SpotChange, per cadence (provisionally promoted,
            // negated) -- shared with LiveFeatureEngine.cs's own ComputeCoreTrendAndBasis
            // (2026-09-17 Phase 2 unification); see CoreScoreRawFormulas.BasisChange's own doc
            // comment for why this specific one-line function is shared. ---
            double? basisChangeRaw = NiftySignal.Scoring.CoreScoreRawFormulas.BasisChange(
                ctx?.FutureChangeFromLastCadence, ctx?.SpotChangeFromLastCadence);

            // --- OiChangeDiff15m: rolling 15-min sum of (CallOiDelta-PutOiDelta), ATM+/-2 -- shared
            // sliding-window tracker with LiveFeatureEngine.cs's own _coreOiChangeDiffWindow
            // (2026-09-17 Phase 2 unification). Sign UNRESOLVED per SCORE_CANDIDATES.md -- used
            // naive (uninverted) here, matching the majority (3 of 4 days) at this specific
            // 15-min-bucket horizon. Weakest-evidence term of the eight; watch its own contribution
            // in the output. ---
            var callOiDeltaThisCadence = rows.Where(r => r.OptionType == OptionType.Call && Math.Abs(r.Offset) <= OiDiffBandOffset && r.OpenInterestDeltaRaw is not null)
                .Sum(r => r.OpenInterestDeltaRaw!.Value);
            var putOiDeltaThisCadence = rows.Where(r => r.OptionType == OptionType.Put && Math.Abs(r.Offset) <= OiDiffBandOffset && r.OpenInterestDeltaRaw is not null)
                .Sum(r => r.OpenInterestDeltaRaw!.Value);
            double? oiChangeDiff15mRaw = oiDiffTracker.Observe(timestamp, callOiDeltaThisCadence, putOiDeltaThisCadence);

            // Rank against PRIOR observations only (read before add), same self-inclusion-safe
            // ordering already established this session for the DynamicHybrid same-bar fix.
            var depthSigned = RankSigned(depthImbalanceRaw, depthRank);
            var skewSigned = RankSigned(itmSkewRaw, skewRank);
            var cvdSigned = RankSigned(weights.InvertFutureCvdNet5Min && futureCvdRaw is { } fcvd ? -fcvd : futureCvdRaw, cvdRank);
            var notionalSigned = RankSigned(notionalLogRatioRaw, notionalRank);
            var gammaSigned = RankSigned(gammaExposureRaw, gammaRank);
            var basisSigned = RankSigned(basisChangeRaw is { } b ? -b : null, basisRank); // negated: sign confirmed negative
            var oiDiffSigned = RankSigned(oiChangeDiff15mRaw, oiDiffRank);
            var trendSigned = trendReversionSigned; // already bounded, not ranked
            var itmSkewChangeSigned = RankSigned(itmSkewChange15mRaw, itmSkewChangeRank);
            var gammaExposureChangeSigned = RankSigned(gammaExposureChange5mRaw, gammaExposureChangeRank);

            // Combine + tanh via the SAME shared calculator NiftySignal.Host/LiveFeatureEngine uses
            // live (2026-09-17 unification, Phase 1) -- one implementation of the renormalize-by-
            // present-weight-then-tanh math, not two hand-synced copies. Version is a label only
            // (stamped onto the returned CoreScore, unused by ScoreCadence below) -- fully qualified
            // throughout since this file's own CoreScoreWeights (with its "Weight"-suffixed field
            // names and the backtest-only InvertFutureCvdNet5Min flag) would otherwise collide with
            // NiftySignal.Scoring.CoreScoreWeights under a blanket `using`.
            var scoringWeights = new NiftySignal.Scoring.CoreScoreWeights(
                Version: "backtest-inline",
                DepthImbalance: weights.DepthImbalanceWeight,
                ItmSkew: weights.ItmSkewWeight,
                FutureCvdNet5Min: weights.FutureCvdNet5MinWeight,
                NotionalVolumeRatio: weights.NotionalVolumeRatioWeight,
                GammaExposure: weights.GammaExposureWeight,
                TrendReversion15m: weights.TrendReversion15mWeight,
                BasisChange: weights.BasisChangeWeight,
                OiChangeDiff15m: weights.OiChangeDiff15mWeight,
                ItmSkewChange15m: weights.ItmSkewChange15mWeight,
                GammaExposureChange5m: weights.GammaExposureChange5mWeight);
            var scoringInputs = new NiftySignal.Scoring.CoreScoreComponentInputs(
                DepthImbalance: depthSigned,
                ItmSkew: skewSigned,
                FutureCvdNet5Min: cvdSigned,
                NotionalVolumeRatio: notionalSigned,
                GammaExposure: gammaSigned,
                TrendReversion15m: trendSigned,
                BasisChange: basisSigned,
                OiChangeDiff15m: oiDiffSigned,
                ItmSkewChange15m: itmSkewChangeSigned,
                GammaExposureChange5m: gammaExposureChangeSigned);
            var coreScoreResult = NiftySignal.Scoring.CoreScoreCalculator.Calculate(scoringInputs, scoringWeights, timestamp, k: weights.K);
            double? score = coreScoreResult.Score;

            cadences.Add(new ScoreCadence(timestamp, localTime, entryWindowOpen, mustForceClose, score, rows,
                depthImbalanceRaw, depthSigned,
                itmSkewRaw, skewSigned,
                futureCvdRaw, cvdSigned,
                notionalLogRatioRaw, notionalSigned,
                gammaExposureRaw, gammaSigned,
                trendReversionSigned, trendSigned,
                basisChangeRaw, basisSigned,
                oiChangeDiff15mRaw, oiDiffSigned,
                // TEMPORARY DIAGNOSTIC (2026-09-16)
                BasisChangeRankCount: basisRank.Count, BasisChangeRankValues: basisRank.Values));
        }

        return (cadences, priceByStrikeAndTime);
    }

    /// <param name="dayStrikeRows">Every StrikeCadenceSnapshot row for one AsOfDate, both expiries -- filtered to ThisWeek internally.</param>
    /// <param name="dayCadenceContexts">Every CadenceContext row for the same AsOfDate -- source of Future/Spot fields (FutureCvdNet5Min, BasisChange, TrendReversion15m) that live outside the option chain.</param>
    /// <param name="thisWeekExpiry">Resolved by the caller once per day (nearest ExpiryDate) -- NextWeek is dropped per the 2026-09-13 standing rule, not scored here at all.</param>
    public static CoreScoreDayResult SimulateDay(IReadOnlyList<StrikeCadenceSnapshot> dayStrikeRows,
        IReadOnlyList<CadenceContext> dayCadenceContexts, DateOnly thisWeekExpiry, CoreScoreSimulationOptions options)
    {
        if (dayStrikeRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayStrikeRows));
        }

        var asOfDate = dayStrikeRows[0].AsOfDate;
        var (cadences, priceByStrikeAndTime) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, options.EffectiveWeights, options.DepthImbalanceSmoothingMinutes);

        var trades = new List<CoreScoreTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, double EntryScore,
            int CadencesHeld, decimal RunningMfe, decimal RunningMae)? open = null;
        var scoreSmoothingWindow = new Queue<double>();
        var consecutiveLosses = 0;

        foreach (var cadence in cadences)
        {
            var timestamp = cadence.Timestamp;
            var score = UpdateScoreSmoothing(scoreSmoothingWindow, cadence.Score, options.ScoreSmoothingCadences);
            var rows = cadence.Rows;

            if (open is { } position)
            {
                // Track this SPECIFIC contract's own price at this timestamp -- never "whatever is
                // ATM now", the exact strike-identity guard this whole project already learned it
                // needs the hard way.
                var stopLossHit = false;
                if (priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var series)
                    && series.TryGetValue(timestamp, out var currentMark) && currentMark is { } price)
                {
                    var excursion = price - position.EntryPrice;
                    open = position with
                    {
                        RunningMfe = Math.Max(position.RunningMfe, excursion),
                        RunningMae = Math.Min(position.RunningMae, excursion),
                    };
                    position = open.Value;

                    // Checked off the SAME fresh quote MFE/MAE just used, not a separate lookup --
                    // a cadence with no quote for this contract can't be stopped out on a stale
                    // price any more than MFE/MAE can be updated on one.
                    if (options.StopLossPct is { } stopLossPct)
                    {
                        var excursionPct = excursion / position.EntryPrice * 100m;
                        stopLossHit = excursionPct <= -stopLossPct;
                    }
                }

                position = position with { CadencesHeld = position.CadencesHeld + 1 };
                open = position;

                // 2026-09-13 (revised): hysteresis band, not a symmetric invalidation check --
                // entry fires at +threshold (long) / -threshold (short), but exit only fires once
                // the score crosses all the way to the OPPOSITE threshold (below -threshold for a
                // held long, above +threshold for a held short). A null score this cadence is NOT
                // treated as invalidation -- only an actual opposite-extreme reading closes the trade.
                var scoreInvalidated = score is { } liveScore
                    && (position.Side == OptionType.Call ? liveScore < -options.EntryScoreThreshold : liveScore > options.EntryScoreThreshold);

                if (cadence.MustForceClose || scoreInvalidated || stopLossHit)
                {
                    var exitPrice = priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var s2) && s2.TryGetValue(timestamp, out var m2) && m2 is { } exitMark
                        ? exitMark
                        : position.EntryPrice; // no fresh quote this exact cadence -- fall back to entry, never fabricate a price
                    var closedTrade = new CoreScoreTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                        timestamp, exitPrice, cadence.MustForceClose ? "ForceClose" : stopLossHit ? "StopLoss" : "ScoreInvalidated",
                        position.RunningMfe, position.RunningMae, position.EntryScore);
                    trades.Add(closedTrade);
                    open = null;

                    // Same rolling-streak semantics as the live engine's own F3 (LiveTradingEngine)/
                    // consecutive-losses check: any non-negative close resets it, a loss extends it.
                    consecutiveLosses = closedTrade.NetPnlPoints < 0 ? consecutiveLosses + 1 : 0;
                }
            }
            else if (cadence.EntryWindowOpen && !cadence.MustForceClose && score is { } sc && Math.Abs(sc) >= options.EntryScoreThreshold
                && (options.MaxConsecutiveLosses is not { } maxLosses || consecutiveLosses < maxLosses))
            {
                var side = sc > 0 ? OptionType.Call : OptionType.Put;
                var picked = PickEntryStrike(rows, side, options.EntryPriceRangeLow, options.EntryPriceRangeHigh);
                if (picked is { } chosen)
                {
                    open = (timestamp, chosen.Price, side, chosen.Strike, sc, 0, 0m, 0m);
                }
            }
        }

        if (open is { } stillOpen)
        {
            var lastTimestamp = cadences[^1].Timestamp;
            var exitPrice = priceByStrikeAndTime.TryGetValue((stillOpen.Side, stillOpen.StrikePrice), out var s3) && s3.TryGetValue(lastTimestamp, out var m3) && m3 is { } exitMark
                ? exitMark
                : stillOpen.EntryPrice;
            trades.Add(new CoreScoreTrade(stillOpen.EntryTime, stillOpen.EntryPrice, stillOpen.Side, stillOpen.StrikePrice,
                lastTimestamp, exitPrice, "EndOfData", stillOpen.RunningMfe, stillOpen.RunningMae, stillOpen.EntryScore));
        }

        return new CoreScoreDayResult(asOfDate, trades);
    }

    /// <summary>
    /// 2026-09-14, experimental (see CoreScoreSimulationOptions.ScoreSmoothingCadences' own doc
    /// comment): a plain count-based trailing average over the last N cadences' RAW scores, distinct
    /// from <see cref="UpdateFastSlow"/>'s real-time window -- "N cadences" here means exactly N
    /// observations, skipped-not-zero-filled on a null cadence (same "skip a null cadence, never
    /// zero-fill it" convention as CadencePopulator's own rolling-sum metrics), not N*15s of wall
    /// clock. cadenceCount &lt;= 1 is a no-op fast path returning the raw score unchanged, so the
    /// default (1) behaves exactly like the pre-2026-09-14 code with zero smoothing overhead.
    /// </summary>
    static double? UpdateScoreSmoothing(Queue<double> window, double? rawScore, int cadenceCount)
    {
        if (cadenceCount <= 1)
        {
            return rawScore;
        }

        if (rawScore is { } value)
        {
            window.Enqueue(value);
            while (window.Count > cadenceCount)
            {
                window.Dequeue();
            }
        }

        return window.Count > 0 ? window.Average() : null;
    }

    /// <summary>
    /// Feeds one cadence's score into both real-time trailing windows and returns their current
    /// averages -- extracted (2026-09-13, Batch 3 historical replay validation) from
    /// SimulateDayCrossover's own inline logic so BuildDiagnostics can compute the identical
    /// fast/slow series without a second, independently-maintained copy of this windowing code.
    /// Null in either return slot means that window hasn't seen any observation yet.
    /// </summary>
    static (double? Fast, double? Slow) UpdateFastSlow(
        Queue<(DateTimeOffset Timestamp, double Score)> fastWindow, Queue<(DateTimeOffset Timestamp, double Score)> slowWindow,
        DateTimeOffset timestamp, double? score, TimeSpan fastSpan, TimeSpan slowSpan)
    {
        if (score is { } scoreValue)
        {
            fastWindow.Enqueue((timestamp, scoreValue));
            slowWindow.Enqueue((timestamp, scoreValue));
        }

        while (fastWindow.Count > 0 && timestamp - fastWindow.Peek().Timestamp > fastSpan)
        {
            fastWindow.Dequeue();
        }

        while (slowWindow.Count > 0 && timestamp - slowWindow.Peek().Timestamp > slowSpan)
        {
            slowWindow.Dequeue();
        }

        double? fast = fastWindow.Count > 0 ? fastWindow.Average(w => w.Score) : null;
        double? slow = slowWindow.Count > 0 ? slowWindow.Average(w => w.Score) : null;
        return (fast, slow);
    }

    /// <summary>
    /// 2026-09-13, Batch 3 (historical replay validation, docs/replication_plan.md): every
    /// intermediate value the live LiveFeatureEngine port needs to be diffed against, at every
    /// cadence -- the 8 raw values, the 8 signed values, CoreScore, and CoreScoreFast/Slow (using
    /// the SAME fast/slow windowing SimulateDayCrossover uses, via UpdateFastSlow, so this can't
    /// silently drift from what SimulateDayCrossover itself would compute). Trading decisions are
    /// deliberately NOT included here -- this method only proves the SCORE replicates; Batch 3's
    /// own would-enter/would-exit comparison is done separately by the diff tool itself, driving
    /// CoreScoreHysteresisRules/CoreScoreCrossoverRules against these same values.
    /// </summary>
    public static List<CoreScoreCadenceDiagnostics> BuildDiagnostics(
        IReadOnlyList<StrikeCadenceSnapshot> dayStrikeRows, IReadOnlyList<CadenceContext> dayCadenceContexts,
        DateOnly thisWeekExpiry, CoreScoreWeights? weights = null, int fastWindowMinutes = 10, int slowWindowMinutes = 30)
    {
        if (dayStrikeRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayStrikeRows));
        }

        var (cadences, _) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, weights ?? new CoreScoreWeights());

        var fastWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();
        var slowWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();
        var fastSpan = TimeSpan.FromMinutes(fastWindowMinutes);
        var slowSpan = TimeSpan.FromMinutes(slowWindowMinutes);

        var result = new List<CoreScoreCadenceDiagnostics>();
        foreach (var cadence in cadences)
        {
            var (fast, slow) = UpdateFastSlow(fastWindow, slowWindow, cadence.Timestamp, cadence.Score, fastSpan, slowSpan);
            result.Add(new CoreScoreCadenceDiagnostics(
                cadence.Timestamp,
                cadence.DepthImbalanceRaw, cadence.DepthImbalanceSigned,
                cadence.ItmSkewRaw, cadence.ItmSkewSigned,
                cadence.FutureCvdNet5MinRaw, cadence.FutureCvdNet5MinSigned,
                cadence.NotionalVolumeRatioRaw, cadence.NotionalVolumeRatioSigned,
                cadence.GammaExposureRaw, cadence.GammaExposureSigned,
                cadence.TrendReversion15mRaw, cadence.TrendReversion15mSigned,
                cadence.BasisChangeRaw, cadence.BasisChangeSigned,
                cadence.OiChangeDiff15mRaw, cadence.OiChangeDiff15mSigned,
                cadence.Score, fast, slow,
                // TEMPORARY DIAGNOSTIC (2026-09-16)
                BasisChangeRankCount: cadence.BasisChangeRankCount, BasisChangeRankValues: cadence.BasisChangeRankValues));
        }

        return result;
    }

    /// <summary>
    /// 2026-09-13, experimental (see CoreScoreCrossoverOptions' own doc comment). Smooths the SAME
    /// composite score over two real-time trailing windows (fast/slow, plain moving average of
    /// every non-null score observed in each window) and trades the crossover: when the fast
    /// average crosses from below to above the slow average, go/flip long (call); crossing from
    /// above to below, go/flip short (put). Always positioned once the first crossover fires --
    /// a new cross closes the existing opposite-side position and immediately opens the new one,
    /// the same cadence. No SL/TP, no score-magnitude threshold at all -- purely directional,
    /// purely on the crossover event; ForceClose remains the only non-crossover exit.
    /// </summary>
    public static CoreScoreDayResult SimulateDayCrossover(IReadOnlyList<StrikeCadenceSnapshot> dayStrikeRows,
        IReadOnlyList<CadenceContext> dayCadenceContexts, DateOnly thisWeekExpiry, CoreScoreCrossoverOptions options)
    {
        if (dayStrikeRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayStrikeRows));
        }

        var asOfDate = dayStrikeRows[0].AsOfDate;
        var (cadences, priceByStrikeAndTime) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, options.EffectiveWeights, options.DepthImbalanceSmoothingMinutes);

        var fastWindowSpan = TimeSpan.FromMinutes(options.FastWindowMinutes);
        var slowWindowSpan = TimeSpan.FromMinutes(options.SlowWindowMinutes);
        var fastWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();
        var slowWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();
        int? previousDiffSign = null;

        var trades = new List<CoreScoreTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, double EntryScore,
            decimal RunningMfe, decimal RunningMae)? open = null;

        void CloseOpen(DateTimeOffset timestamp, string reason)
        {
            if (open is not { } position)
            {
                return;
            }

            var exitPrice = priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var series) && series.TryGetValue(timestamp, out var mark) && mark is { } exitMark
                ? exitMark
                : position.EntryPrice; // no fresh quote this exact cadence -- fall back to entry, never fabricate a price
            trades.Add(new CoreScoreTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                timestamp, exitPrice, reason, position.RunningMfe, position.RunningMae, position.EntryScore));
            open = null;
        }

        foreach (var cadence in cadences)
        {
            var timestamp = cadence.Timestamp;
            var rows = cadence.Rows;

            if (open is { } position)
            {
                if (priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var series)
                    && series.TryGetValue(timestamp, out var currentMark) && currentMark is { } price)
                {
                    var excursion = price - position.EntryPrice;
                    open = position with
                    {
                        RunningMfe = Math.Max(position.RunningMfe, excursion),
                        RunningMae = Math.Min(position.RunningMae, excursion),
                    };
                }
            }

            // Extracted into UpdateFastSlow (2026-09-13, Batch 3) so BuildDiagnostics computes
            // the identical fast/slow series -- same windowing code, not a second copy.
            var (fast, slow) = UpdateFastSlow(fastWindow, slowWindow, timestamp, cadence.Score, fastWindowSpan, slowWindowSpan);

            if (cadence.MustForceClose)
            {
                CloseOpen(timestamp, "ForceClose");
                continue;
            }

            if (fast is not { } fastAvg || slow is not { } slowAvg)
            {
                continue; // not warmed up yet -- no crossover can be evaluated
            }

            var diffSign = Math.Sign(fastAvg - slowAvg);

            if (diffSign == 0)
            {
                continue; // exact tie -- not a crossover either way, wait for a real signal
            }

            if (previousDiffSign is null)
            {
                previousDiffSign = diffSign; // first warmed-up reading establishes the baseline, not a cross
                continue;
            }

            if (diffSign != previousDiffSign.Value)
            {
                // A genuine crossover: fast moved from one side of slow to the other.
                CloseOpen(timestamp, "CrossoverFlip");

                if (cadence.EntryWindowOpen)
                {
                    var side = diffSign > 0 ? OptionType.Call : OptionType.Put;
                    var picked = PickEntryStrike(rows, side, options.EntryPriceRangeLow, options.EntryPriceRangeHigh);
                    if (picked is { } chosen)
                    {
                        open = (timestamp, chosen.Price, side, chosen.Strike, fastAvg - slowAvg, 0m, 0m);
                    }
                }

                previousDiffSign = diffSign;
            }
        }

        if (open is { } stillOpen)
        {
            var lastTimestamp = cadences[^1].Timestamp;
            var exitPrice = priceByStrikeAndTime.TryGetValue((stillOpen.Side, stillOpen.StrikePrice), out var s3) && s3.TryGetValue(lastTimestamp, out var m3) && m3 is { } exitMark
                ? exitMark
                : stillOpen.EntryPrice;
            trades.Add(new CoreScoreTrade(stillOpen.EntryTime, stillOpen.EntryPrice, stillOpen.Side, stillOpen.StrikePrice,
                lastTimestamp, exitPrice, "EndOfData", stillOpen.RunningMfe, stillOpen.RunningMae, stillOpen.EntryScore));
        }

        return new CoreScoreDayResult(asOfDate, trades);
    }

    /// <summary>
    /// 2026-09-15, experimental: neither of the two locked-in live strategies alone -- a third,
    /// untested combination requiring BOTH the Hysteresis strategy's own entry condition (|score|
    /// &gt;= threshold) and the Crossover strategy's own CURRENT directional read (sign of
    /// fast-slow) to agree, at the same cadence, before entering. <see
    /// cref="CoreScoreCombinedOptions.ExitOnEitherOpposite"/> selects between two exit variants:
    /// exit the moment EITHER rule turns opposite (quicker, matches whichever side's own tested
    /// exit condition fires first), or only once BOTH have turned opposite (more patient, requires
    /// full agreement reversal before giving up the position). "Opposite" for each rule matches its
    /// own already-tested definition exactly -- Hysteresis's own hysteresis band (score crosses
    /// past the OPPOSITE threshold, not a symmetric zero-cross) and Crossover's own flip (fast/slow
    /// diff sign reverses) -- reimplemented inline here rather than calling
    /// CoreScoreHysteresisRules/CoreScoreCrossoverRules directly, so this backtest-only experiment
    /// never touches the classes the live trading engines actually depend on. NOT "always
    /// positioned" like pure Crossover -- a close leaves the strategy flat until both conditions
    /// agree again, same cadence or later (same-cadence reentry is still allowed, matching both
    /// underlying strategies' own "a closing tick frees the slot" convention).
    /// </summary>
    public static CoreScoreDayResult SimulateDayCombined(IReadOnlyList<StrikeCadenceSnapshot> dayStrikeRows,
        IReadOnlyList<CadenceContext> dayCadenceContexts, DateOnly thisWeekExpiry, CoreScoreCombinedOptions options)
    {
        if (dayStrikeRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayStrikeRows));
        }

        var asOfDate = dayStrikeRows[0].AsOfDate;
        var (cadences, priceByStrikeAndTime) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, options.EffectiveWeights, options.DepthImbalanceSmoothingMinutes);

        var fastWindowSpan = TimeSpan.FromMinutes(options.FastWindowMinutes);
        var slowWindowSpan = TimeSpan.FromMinutes(options.SlowWindowMinutes);
        var fastWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();
        var slowWindow = new Queue<(DateTimeOffset Timestamp, double Score)>();

        var trades = new List<CoreScoreTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, double EntryScore,
            decimal RunningMfe, decimal RunningMae)? open = null;

        void CloseOpen(DateTimeOffset timestamp, string reason)
        {
            if (open is not { } position)
            {
                return;
            }

            var exitPrice = priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var series) && series.TryGetValue(timestamp, out var mark) && mark is { } exitMark
                ? exitMark
                : position.EntryPrice; // no fresh quote this exact cadence -- fall back to entry, never fabricate a price
            trades.Add(new CoreScoreTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                timestamp, exitPrice, reason, position.RunningMfe, position.RunningMae, position.EntryScore));
            open = null;
        }

        foreach (var cadence in cadences)
        {
            var timestamp = cadence.Timestamp;
            var rows = cadence.Rows;

            if (open is { } position)
            {
                if (priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var series)
                    && series.TryGetValue(timestamp, out var currentMark) && currentMark is { } price)
                {
                    var excursion = price - position.EntryPrice;
                    open = position with
                    {
                        RunningMfe = Math.Max(position.RunningMfe, excursion),
                        RunningMae = Math.Min(position.RunningMae, excursion),
                    };
                }
            }

            var (fast, slow) = UpdateFastSlow(fastWindow, slowWindow, timestamp, cadence.Score, fastWindowSpan, slowWindowSpan);

            if (cadence.MustForceClose)
            {
                CloseOpen(timestamp, "ForceClose");
                continue;
            }

            // A's own read this cadence -- the instant score, exactly SimulateDay's own condition.
            var score = cadence.Score;
            // B's own read this cadence -- sign of fast-slow, exactly SimulateDayCrossover's own
            // directional condition (which side fast is CURRENTLY on, not "did a flip just happen").
            int? bSign = fast is { } f && slow is { } s ? Math.Sign(f - s) : null;

            if (open is { } held)
            {
                var aOpposite = score is { } liveScore
                    && (held.Side == OptionType.Call ? liveScore < -options.EntryScoreThreshold : liveScore > options.EntryScoreThreshold);
                var bOpposite = bSign is { } sign && sign != 0
                    && (held.Side == OptionType.Call ? sign < 0 : sign > 0);

                var shouldExit = options.ExitOnEitherOpposite ? aOpposite || bOpposite : aOpposite && bOpposite;
                if (shouldExit)
                {
                    CloseOpen(timestamp, aOpposite && bOpposite ? "BothOpposite" : aOpposite ? "AOpposite" : "BOpposite");
                }
            }

            // Not an "else" -- a position closed above this SAME cadence is eligible to reopen
            // immediately if the fresh, still-current A/B reading already qualifies (matching
            // SimulateDay's own "a closing tick frees the slot" reentry convention).
            if (open is null && cadence.EntryWindowOpen && score is { } sc && Math.Abs(sc) >= options.EntryScoreThreshold
                && bSign is { } bs && bs != 0)
            {
                var aSide = sc > 0 ? OptionType.Call : OptionType.Put;
                var bSide = bs > 0 ? OptionType.Call : OptionType.Put;
                if (aSide == bSide)
                {
                    var picked = PickEntryStrike(rows, aSide, options.EntryPriceRangeLow, options.EntryPriceRangeHigh);
                    if (picked is { } chosen)
                    {
                        open = (timestamp, chosen.Price, aSide, chosen.Strike, sc, 0m, 0m);
                    }
                }
            }
        }

        if (open is { } stillOpen)
        {
            var lastTimestamp = cadences[^1].Timestamp;
            var exitPrice = priceByStrikeAndTime.TryGetValue((stillOpen.Side, stillOpen.StrikePrice), out var s3) && s3.TryGetValue(lastTimestamp, out var m3) && m3 is { } exitMark
                ? exitMark
                : stillOpen.EntryPrice;
            trades.Add(new CoreScoreTrade(stillOpen.EntryTime, stillOpen.EntryPrice, stillOpen.Side, stillOpen.StrikePrice,
                lastTimestamp, exitPrice, "EndOfData", stillOpen.RunningMfe, stillOpen.RunningMae, stillOpen.EntryScore));
        }

        return new CoreScoreDayResult(asOfDate, trades);
    }
}
