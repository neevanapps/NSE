using NiftySignal.Domain.Enums;

namespace NiftySignal.Scoring;

/// <summary>
/// Pure per-metric raw-value formulas for the Core score (2026-09-17, Phase 2 unification --
/// see `docs/SCORE_CANDIDATES.md`'s "Unifying backtest and live" section for the full plan). Each
/// side (`NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs` for backtest,
/// `NiftySignal.Host/LiveFeatureEngine.cs` for live) keeps its OWN data source, forward-fill state,
/// and band-selection loop exactly as before (backtest reads pre-aggregated `StrikeCadenceSnapshot`
/// rows already forward-filled at the row level; live maintains per-token accumulators and
/// forward-fill dictionaries fed tick-by-tick, each gated by its own band check -- <see
/// cref="CoreScoreBands.InItm2Atm1"/> for DepthImbalance/ItmSkew, a plain `Math.Abs(offset) &lt;= N`
/// symmetric band for GammaExposure/NotionalVolumeRatio -- at the point of deciding whether to even
/// consider a strike this cadence. Band selection is deliberately NOT moved in here: folding it into
/// these methods would subtly change WHEN each side's own forward-fill runs, not just what the final
/// formula computes. Each caller passes in the already-filtered, already-resolved per-strike values
/// for this cadence; these methods only do the generic aggregate arithmetic each formula shares.
/// </summary>
public static class CoreScoreRawFormulas
{
    /// <summary>
    /// DepthImbalance: average call resting-depth imbalance minus average put resting-depth
    /// imbalance, over whichever Itm2Atm1-band strikes the caller already selected. Null unless
    /// both <paramref name="callDepth"/> and <paramref name="putDepth"/> are non-empty.
    /// </summary>
    public static double? DepthImbalance(IReadOnlyCollection<double> callDepth, IReadOnlyCollection<double> putDepth) =>
        AverageDifference(callDepth, putDepth);

    /// <summary>
    /// ItmSkew: average put IV minus average call IV (note the sign -- opposite of
    /// <see cref="DepthImbalance"/>'s call-minus-put convention, matching both sides' own
    /// `putAvg - callAvg` formula exactly), over whichever Itm2Atm1-band strikes the caller already
    /// selected. Null unless both <paramref name="callIv"/> and <paramref name="putIv"/> are
    /// non-empty. Callers remain responsible for their own 0-DTE null-out (IV is known to distort
    /// severely on expiry day) -- that's a call-time decision (today's date vs expiry), not part of
    /// this formula.
    /// </summary>
    public static double? ItmSkew(IReadOnlyCollection<double> callIv, IReadOnlyCollection<double> putIv) =>
        AverageDifference(putIv, callIv);

    /// <summary>
    /// GammaExposure: signed sum of gamma*openInterest (call:+1, put:-1) over whichever
    /// ATM+/-CoreGammaBandOffset strikes the caller already selected, resolved (fresh or
    /// forward-filled). Null only when <paramref name="strikes"/> is empty -- a real net-zero
    /// exposure across a non-empty set is a legitimate value, matching both sides' own
    /// `any`/`Count &gt; 0` null guard (not "null when the SUM happens to be zero").
    /// </summary>
    public static double? GammaExposure(IEnumerable<(OptionType Type, double Gamma, double OpenInterest)> strikes)
    {
        double net = 0;
        var any = false;
        foreach (var (type, gamma, openInterest) in strikes)
        {
            net += type == OptionType.Call ? gamma * openInterest : -(gamma * openInterest);
            any = true;
        }

        return any ? net : null;
    }

    /// <summary>
    /// NotionalVolumeRatio: log(putNotional/callNotional) over whichever ATM+/-N strikes the caller
    /// already selected, each contributing its own volumeDelta*mark notional. Null unless both
    /// summed totals are positive (matching both sides' own `&gt; 0` guard exactly).
    /// </summary>
    public static double? NotionalVolumeRatio(IReadOnlyCollection<double> callNotional, IReadOnlyCollection<double> putNotional)
    {
        var call = callNotional.Sum();
        var put = putNotional.Sum();
        return call > 0 && put > 0 ? Math.Log(put / call) : null;
    }

    /// <summary>
    /// BasisChangeRaw: futureChange - spotChange (NOT sign-flipped -- callers negate this
    /// themselves when computing the signed/ranked value, same as every other term here, keeping
    /// this function's output exactly what `CoreScoreSnapshot.BasisChangeRaw`/`ScoreCadence`'s own
    /// diagnostic field publishes). Deliberately takes the two already-computed DECIMAL changes and
    /// does the subtraction in decimal, ONE cast to double at the very end -- this exact arithmetic
    /// order is the fix for a real, previously-shipped bug (2026-09-17): computing each side's
    /// change as double independently, THEN subtracting as double, introduced noise too small to
    /// trip `CoreScoreReplayDiff`'s own epsilon on <c>BasisChangeRaw</c> directly, but large enough
    /// to cascade through `SessionRankTracker`'s dense, near-tied rank distribution into a 54%
    /// mismatch on <c>BasisChangeSigned</c> (4016 of 7385 cadences). Sharing this one-line function
    /// is deliberate insurance against that exact class of bug recurring, not just a
    /// formula-sharing exercise like the others in this file.
    /// </summary>
    public static double? BasisChange(decimal? futureChange, decimal? spotChange) =>
        futureChange is { } fc && spotChange is { } sc ? (double)(fc - sc) : null;

    static double? AverageDifference(IReadOnlyCollection<double> a, IReadOnlyCollection<double> b) =>
        a.Count > 0 && b.Count > 0 ? a.Average() - b.Average() : null;
}
