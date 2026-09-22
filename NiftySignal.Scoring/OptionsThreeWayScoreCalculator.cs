using NiftySignal.Features;

namespace NiftySignal.Scoring;

/// <summary>
/// The locked <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> family's own 3-way session-gated
/// switch score -- 2026-09-20, step 1 of the live/backtest parity plan (`docs/LIVE_PARITY_PLAN.md`):
/// extracted verbatim out of `NiftySignal.VolumeBarData/TradeSimulator.cs`'s private
/// <c>ComputeOptionsThreeWayScore</c> (formula and constants unchanged -- a pure relocation, not a
/// rewrite) so both the offline backtest and a future live pipeline can call the exact same code
/// instead of risking two hand-synced copies drifting apart. Proven byte-identical to the
/// pre-extraction in-place version via `NiftySignal.Tests/Scoring/OptionsThreeWayScoreCalculatorTests.cs`
/// and a full before/after backtest calibration re-run (see that plan doc's own status log).
///
/// Never blended, never averaged across legs -- exactly one leg's own score is the traded score for
/// a given bar, decided purely by session time-of-day:
/// <list type="bullet">
/// <item>Open (before <c>openMidBoundary</c>, 10:00 IST by default): ATM+/-2 depth imbalance
/// (<c>-(CallBid+PutBid-CallAsk-PutAsk)/(CallBid+PutBid+CallAsk+PutAsk)</c>, session-rank normalized)
/// -- the single strongest Open-window candidate found across this whole project.</item>
/// <item>Mid (<c>openMidBoundary</c> to <see cref="DefaultMidCloseBoundary"/>, 13:30 IST by
/// default): price-signed ΔIV (<c>-sign(ΔFuturePrice) x ΔAtmIv</c>), session-rank normalized.</item>
/// <item>Close (at/after <see cref="DefaultMidCloseBoundary"/>): raw ΔIV (<c>ΔAtmIv</c>, NOT
/// price-signed -- the session-phase split found the un-signed variant was this project's single
/// best Close performer), session-rank normalized.</item>
/// </list>
/// </summary>
public static class OptionsThreeWayScoreCalculator
{
    /// <summary>
    /// The Mid/Close leg boundary -- 13:30 IST, the same bucket edge the session-phase split
    /// establishes and every 3-way-switch-family metric in `TradeSimulator.cs` shares. Unlike the
    /// Open/Mid boundary (which the backtest's own calibration sweeps via
    /// <c>--optionsSwitchTime</c>), this edge has never been made a sweep parameter upstream, so it
    /// is a fixed constant here too, rather than inventing new flexibility this extraction wasn't
    /// asked to add.
    /// </summary>
    public static readonly TimeSpan DefaultMidCloseBoundary = new(13, 30, 0);

    /// <summary>
    /// Computes one bar's score. <paramref name="previousAtmIv"/> is threaded through by ref, same
    /// "keep the IV tracker primed through the Open window too" convention the original inline code
    /// used -- so the very first Mid-window bar already has a real previous-IV reading to diff
    /// against instead of starting cold, even though the Open leg's own score comes from depth
    /// imbalance, not IV. <paramref name="openMidBoundary"/> defaults to 10:00 IST (matching
    /// `TradeSimulator`'s own <c>SessionGateSwitchTime</c>) but is a real parameter here because the
    /// backtest already sweeps it (<c>optionsSwitchTime</c>); <paramref name="midCloseBoundary"/>
    /// defaults to <see cref="DefaultMidCloseBoundary"/> and is exposed for the same reason,
    /// consistency, even though nothing upstream sweeps it today.
    /// </summary>
    public static double? ComputeScore(
        OptionsThreeWayScoreInputs bar,
        ref double? previousAtmIv,
        SessionRankTracker depthRank,
        SessionRankTracker ivMidRank,
        SessionRankTracker ivCloseRank,
        TimeSpan? openMidBoundary = null,
        TimeSpan? midCloseBoundary = null)
    {
        var effectiveOpenMidBoundary = openMidBoundary ?? new TimeSpan(10, 0, 0);
        var effectiveMidCloseBoundary = midCloseBoundary ?? DefaultMidCloseBoundary;

        double? score;
        if (bar.TimeOfDayIst < effectiveOpenMidBoundary)
        {
            score = ComputeImbalanceRatio(
                    bar.WideBookCallBidQtyAvg + bar.WideBookPutBidQtyAvg,
                    bar.WideBookCallAskQtyAvg + bar.WideBookPutAskQtyAvg) is { } depthRatio
                ? SignedRank.Compute(-depthRatio, depthRank)
                : null;
        }
        else if (bar.TimeOfDayIst < effectiveMidCloseBoundary)
        {
            score = previousAtmIv is { } prevIvMid && bar.CurrentAtmIv is { } curIvMid && bar.PreviousClose is { } prevCloseMid
                ? SignedRank.Compute(-Math.Sign(bar.ClosePrice - prevCloseMid) * (curIvMid - prevIvMid), ivMidRank)
                : null;
        }
        else
        {
            // Close leg: RAW ΔIV, not price-signed -- the session-phase split's own best Close
            // performer was the raw (un-signed) variant, not the Mid leg's formula.
            score = previousAtmIv is { } prevIvClose && bar.CurrentAtmIv is { } curIvClose
                ? SignedRank.Compute(curIvClose - prevIvClose, ivCloseRank)
                : null;
        }

        previousAtmIv = bar.CurrentAtmIv ?? previousAtmIv;
        return score;
    }

    /// <summary>
    /// <c>(a-b)/(a+b)</c>, null-propagating (either side missing means no reading this bar, never
    /// fabricated) and null when a+b is exactly 0 (no depth-bearing tick at all -- distinct from a
    /// genuine zero imbalance). Relocated here alongside <see cref="ComputeScore"/>'s Open leg,
    /// which is its only caller in this project -- `TradeSimulator.cs` keeps its own copy for the
    /// several other metrics (Blend, 2-way switch, AtmComplexDepthImbalance, etc.) that also use
    /// this exact formula but are explicitly out of scope for this extraction; both copies compute
    /// the identical 1-line formula, so there is no behavioral drift risk between them.
    /// </summary>
    public static double? ComputeImbalanceRatio(double? a, double? b)
    {
        if (a is not { } av || b is not { } bv || av + bv == 0)
        {
            return null;
        }

        return (av - bv) / (av + bv);
    }
}
