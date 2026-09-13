using NiftySignal.BacktestData;
using NiftySignal.Domain.Enums;

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
    // Provisional starting point, not a tuned value -- same "starting point, not the answer"
    // framing as every other k in this project's history (see CompositeScoreCalculator.DefaultK's
    // own doc comment).
    double K = 1.0)
{
    /// <summary>Sums to 0.915, not 1.00 -- left as-is rather than silently rescaled, same reasoning as every other total here. The renormalize-by-present-weight step divides by whatever total is actually present each cadence, so this doesn't break anything.</summary>
    public double Total => DepthImbalanceWeight + ItmSkewWeight + FutureCvdNet5MinWeight + NotionalVolumeRatioWeight
        + GammaExposureWeight + TrendReversion15mWeight + BasisChangeWeight + OiChangeDiff15mWeight;
}

public sealed record CoreScoreSimulationOptions(
    double EntryScoreThreshold = 30.0,
    // 2026-09-13: fixed-cadence hold removed per explicit instruction ("no mechanical time gate").
    // Exit uses a hysteresis band (see the state machine below) -- a position closes only once the
    // score crosses all the way to the OPPOSITE threshold. Still no SL/TP: the trigger is the
    // score, never the option's own price. ForceCloseTime remains as a backstop (data/session
    // boundary, not an artificial hold length) since a position can't carry overnight here.
    decimal EntryPriceRangeLow = 100m,
    decimal EntryPriceRangeHigh = 150m,
    CoreScoreWeights? Weights = null)
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
    CoreScoreWeights? Weights = null)
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
    // Itm2Atm1: calls {-2,-1,0}, puts {0,+1,+2} -- ITM is below ATM for calls, above for puts.
    // Confirmed band definition, matches docs/CHILD_TABLE_SCHEMA.md exactly.
    static bool InItm2Atm1(OptionType type, int offset) =>
        type == OptionType.Call ? offset is >= -2 and <= 0 : offset is >= 0 and <= 2;

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

    /// <summary>One cadence's resolved composite score plus everything a trading strategy needs to act on it -- shared by both SimulateDay and SimulateDayCrossover so the 8-metric computation exists in exactly one place.</summary>
    sealed record ScoreCadence(DateTimeOffset Timestamp, TimeOnly LocalTime, bool EntryWindowOpen, bool MustForceClose,
        double? Score, List<FilledRow> Rows);

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
            DateOnly thisWeekExpiry, CoreScoreWeights weights)
    {
        var asOfDate = dayStrikeRows[0].AsOfDate;
        var isExpiryDay = asOfDate == thisWeekExpiry; // 0 DTE -- nulls ItmSkew below, per user's own instruction.

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

        var trendWindow = new Queue<(DateTimeOffset Timestamp, double Change)>();
        var oiDiffWindow = new Queue<(DateTimeOffset Timestamp, long Call, long Put)>();

        var cadences = new List<ScoreCadence>();

        foreach (var cadence in byTimestamp)
        {
            var timestamp = cadence.Key;
            var localTime = TimeOnly.FromTimeSpan(timestamp.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
            var entryWindowOpen = localTime >= SimulationRules.FirstEntryTime && localTime < SimulationRules.LastEntryTime;
            var mustForceClose = localTime >= SimulationRules.ForceCloseTime;

            var rows = cadence.ToList();
            cadenceContextByTimestamp.TryGetValue(timestamp, out var ctx);

            // --- Depth imbalance, Itm2Atm1 (CONFIRMED) ---
            var callDepth = rows.Where(r => r.OptionType == OptionType.Call && InItm2Atm1(OptionType.Call, r.Offset) && r.Depth is not null)
                .Select(r => r.Depth!.Value).ToList();
            var putDepth = rows.Where(r => r.OptionType == OptionType.Put && InItm2Atm1(OptionType.Put, r.Offset) && r.Depth is not null)
                .Select(r => r.Depth!.Value).ToList();
            double? depthImbalanceRaw = callDepth.Count > 0 && putDepth.Count > 0 ? callDepth.Average() - putDepth.Average() : null;

            // --- ITM skew, Itm2Atm1 (CONFIRMED among non-expiry days -- nulled on 0 DTE) ---
            double? itmSkewRaw = null;
            if (!isExpiryDay)
            {
                var callIv = rows.Where(r => r.OptionType == OptionType.Call && InItm2Atm1(OptionType.Call, r.Offset) && r.Iv is not null)
                    .Select(r => r.Iv!.Value).ToList();
                var putIv = rows.Where(r => r.OptionType == OptionType.Put && InItm2Atm1(OptionType.Put, r.Offset) && r.Iv is not null)
                    .Select(r => r.Iv!.Value).ToList();
                itmSkewRaw = callIv.Count > 0 && putIv.Count > 0 ? putIv.Average() - callIv.Average() : null;
            }

            // --- FutureCvdNet5Min (PROMOTED, sign confirmed positive, read directly) ---
            double? futureCvdRaw = ctx?.FutureCvdProxyNet5Min;

            // --- NotionalVolumeRatio, ATM+/-5, callNotional/putNotional (watch, inverted) ---
            var callNotional = rows.Where(r => r.OptionType == OptionType.Call && Math.Abs(r.Offset) <= NotionalBandOffset && r.VolumeDeltaRaw is > 0 && r.Mark is > 0)
                .Sum(r => (double)r.VolumeDeltaRaw!.Value * (double)r.Mark!.Value);
            var putNotional = rows.Where(r => r.OptionType == OptionType.Put && Math.Abs(r.Offset) <= NotionalBandOffset && r.VolumeDeltaRaw is > 0 && r.Mark is > 0)
                .Sum(r => (double)r.VolumeDeltaRaw!.Value * (double)r.Mark!.Value);
            // Tested NEGATIVE correlation with price -- inverted here (put/call instead of
            // call/put) so a positive log-ratio consistently means "bullish" like the other terms.
            double? notionalLogRatioRaw = callNotional > 0 && putNotional > 0 ? Math.Log(putNotional / callNotional) : null;

            // --- GammaExposure, ATM+/-10, sum(call:+Gamma*OI, put:-Gamma*OI) (watch, positive sign) ---
            var gexRows = rows.Where(r => Math.Abs(r.Offset) <= GammaBandOffset && r.Gamma is not null && r.OpenInterest is not null).ToList();
            double? gammaExposureRaw = gexRows.Count > 0
                ? gexRows.Sum(r => (r.OptionType == OptionType.Call ? 1.0 : -1.0) * r.Gamma!.Value * r.OpenInterest!.Value)
                : null;

            // --- TrendReversion15m: signed net Future change / path length, trailing 15 real min ---
            // Already bounded [-1,1] by construction (net <= path always) -- used directly as this
            // term's signed contribution, no session-rank needed. Negated: a clean recent trend
            // reads as a REVERSION signal (opposite direction), not continuation -- the confirmed,
            // if counter-intuitive, sign from SCORE_CANDIDATES.md.
            if (ctx?.FutureChangeFromLastCadence is { } futureChange)
            {
                trendWindow.Enqueue((timestamp, (double)futureChange));
            }
            while (trendWindow.Count > 0 && timestamp - trendWindow.Peek().Timestamp > TrendReversionWindow)
            {
                trendWindow.Dequeue();
            }
            double? trendReversionSigned = null;
            if (trendWindow.Count > 0)
            {
                var net = trendWindow.Sum(w => w.Change);
                var path = trendWindow.Sum(w => Math.Abs(w.Change));
                if (path > 0)
                {
                    trendReversionSigned = -1.0 * (net / path);
                }
            }

            // --- BasisChange: FutureChange - SpotChange, per cadence (provisionally promoted, negated) ---
            double? basisChangeRaw = ctx?.FutureChangeFromLastCadence is { } fChg && ctx?.SpotChangeFromLastCadence is { } sChg
                ? (double)(fChg - sChg)
                : null;

            // --- OiChangeDiff15m: rolling 15-min sum of (CallOiDelta-PutOiDelta), ATM+/-2 ---
            // Sign UNRESOLVED per SCORE_CANDIDATES.md -- used naive (uninverted) here, matching the
            // majority (3 of 4 days) at this specific 15-min-bucket horizon. Weakest-evidence term
            // of the eight; watch its own contribution in the output.
            var callOiDeltaThisCadence = rows.Where(r => r.OptionType == OptionType.Call && Math.Abs(r.Offset) <= OiDiffBandOffset && r.OpenInterestDeltaRaw is not null)
                .Sum(r => r.OpenInterestDeltaRaw!.Value);
            var putOiDeltaThisCadence = rows.Where(r => r.OptionType == OptionType.Put && Math.Abs(r.Offset) <= OiDiffBandOffset && r.OpenInterestDeltaRaw is not null)
                .Sum(r => r.OpenInterestDeltaRaw!.Value);
            oiDiffWindow.Enqueue((timestamp, callOiDeltaThisCadence, putOiDeltaThisCadence));
            while (oiDiffWindow.Count > 0 && timestamp - oiDiffWindow.Peek().Timestamp > OiDiffWindow)
            {
                oiDiffWindow.Dequeue();
            }
            double? oiChangeDiff15mRaw = oiDiffWindow.Sum(w => (double)(w.Call - w.Put));

            // Rank against PRIOR observations only (read before add), same self-inclusion-safe
            // ordering already established this session for the DynamicHybrid same-bar fix.
            var depthSigned = RankSigned(depthImbalanceRaw, depthRank);
            var skewSigned = RankSigned(itmSkewRaw, skewRank);
            var cvdSigned = RankSigned(futureCvdRaw, cvdRank);
            var notionalSigned = RankSigned(notionalLogRatioRaw, notionalRank);
            var gammaSigned = RankSigned(gammaExposureRaw, gammaRank);
            var basisSigned = RankSigned(basisChangeRaw is { } b ? -b : null, basisRank); // negated: sign confirmed negative
            var oiDiffSigned = RankSigned(oiChangeDiff15mRaw, oiDiffRank);
            var trendSigned = trendReversionSigned; // already bounded, not ranked

            // Renormalize by the weight of whichever terms are actually present this cadence --
            // same optional-component pattern CompositeScoreCalculator/RatioScoreCalculator both
            // already use, rather than silently treating a missing term as zero.
            var presentWeight = (depthSigned is not null ? weights.DepthImbalanceWeight : 0)
                + (skewSigned is not null ? weights.ItmSkewWeight : 0)
                + (cvdSigned is not null ? weights.FutureCvdNet5MinWeight : 0)
                + (notionalSigned is not null ? weights.NotionalVolumeRatioWeight : 0)
                + (gammaSigned is not null ? weights.GammaExposureWeight : 0)
                + (trendSigned is not null ? weights.TrendReversion15mWeight : 0)
                + (basisSigned is not null ? weights.BasisChangeWeight : 0)
                + (oiDiffSigned is not null ? weights.OiChangeDiff15mWeight : 0);

            double? score = null;
            if (presentWeight > 0)
            {
                var raw = ((depthSigned ?? 0) * weights.DepthImbalanceWeight
                    + (skewSigned ?? 0) * weights.ItmSkewWeight
                    + (cvdSigned ?? 0) * weights.FutureCvdNet5MinWeight
                    + (notionalSigned ?? 0) * weights.NotionalVolumeRatioWeight
                    + (gammaSigned ?? 0) * weights.GammaExposureWeight
                    + (trendSigned ?? 0) * weights.TrendReversion15mWeight
                    + (basisSigned ?? 0) * weights.BasisChangeWeight
                    + (oiDiffSigned ?? 0) * weights.OiChangeDiff15mWeight) / presentWeight;
                score = 100.0 * Math.Tanh(raw / weights.K);
            }

            cadences.Add(new ScoreCadence(timestamp, localTime, entryWindowOpen, mustForceClose, score, rows));
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
        var (cadences, priceByStrikeAndTime) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, options.EffectiveWeights);

        var trades = new List<CoreScoreTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, OptionType Side, decimal StrikePrice, double EntryScore,
            int CadencesHeld, decimal RunningMfe, decimal RunningMae)? open = null;

        foreach (var cadence in cadences)
        {
            var timestamp = cadence.Timestamp;
            var score = cadence.Score;
            var rows = cadence.Rows;

            if (open is { } position)
            {
                // Track this SPECIFIC contract's own price at this timestamp -- never "whatever is
                // ATM now", the exact strike-identity guard this whole project already learned it
                // needs the hard way.
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

                if (cadence.MustForceClose || scoreInvalidated)
                {
                    var exitPrice = priceByStrikeAndTime.TryGetValue((position.Side, position.StrikePrice), out var s2) && s2.TryGetValue(timestamp, out var m2) && m2 is { } exitMark
                        ? exitMark
                        : position.EntryPrice; // no fresh quote this exact cadence -- fall back to entry, never fabricate a price
                    trades.Add(new CoreScoreTrade(position.EntryTime, position.EntryPrice, position.Side, position.StrikePrice,
                        timestamp, exitPrice, cadence.MustForceClose ? "ForceClose" : "ScoreInvalidated",
                        position.RunningMfe, position.RunningMae, position.EntryScore));
                    open = null;
                }
            }
            else if (cadence.EntryWindowOpen && !cadence.MustForceClose && score is { } sc && Math.Abs(sc) >= options.EntryScoreThreshold)
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
        var (cadences, priceByStrikeAndTime) = BuildScoreCadences(dayStrikeRows, dayCadenceContexts, thisWeekExpiry, options.EffectiveWeights);

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

            if (cadence.Score is { } scoreValue)
            {
                fastWindow.Enqueue((timestamp, scoreValue));
                slowWindow.Enqueue((timestamp, scoreValue));
            }
            while (fastWindow.Count > 0 && timestamp - fastWindow.Peek().Timestamp > fastWindowSpan)
            {
                fastWindow.Dequeue();
            }
            while (slowWindow.Count > 0 && timestamp - slowWindow.Peek().Timestamp > slowWindowSpan)
            {
                slowWindow.Dequeue();
            }

            if (cadence.MustForceClose)
            {
                CloseOpen(timestamp, "ForceClose");
                continue;
            }

            if (fastWindow.Count == 0 || slowWindow.Count == 0)
            {
                continue; // not warmed up yet -- no crossover can be evaluated
            }

            var fastAvg = fastWindow.Average(w => w.Score);
            var slowAvg = slowWindow.Average(w => w.Score);
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
}
