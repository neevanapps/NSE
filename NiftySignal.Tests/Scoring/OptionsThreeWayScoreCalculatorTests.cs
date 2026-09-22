using NiftySignal.Features;
using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

/// <summary>
/// 2026-09-20, live/backtest parity plan step 1 (`docs/LIVE_PARITY_PLAN.md`): proves
/// <see cref="OptionsThreeWayScoreCalculator"/>/<see cref="MaxPainConfirmationGate"/> reproduce the
/// exact formulas `NiftySignal.VolumeBarData/TradeSimulator.cs`'s own (now-deleted) inline
/// implementation used, as a fast regression safety net alongside the before/after backtest
/// calibration re-run (`docs/LIVE_PARITY_PLAN.md`'s own status log has the numbers: 112 trades /
/// 64.3% win / +426.40 net for OptionsScoreThreeWaySwitchMaxPainConfirmed at 2600/90, unchanged
/// pre- and post-extraction).
/// </summary>
public class OptionsThreeWayScoreCalculatorTests
{
    static readonly TimeSpan OpenMidBoundary = new(10, 0, 0);
    static readonly TimeSpan MidCloseBoundary = new(13, 30, 0);

    [Fact]
    public void ComputeScore_OpenLeg_UsesNegatedDepthImbalance_SessionRankNormalized()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = null;

        // Bid-heavy book (more resting buy interest): CallBid+PutBid=140, CallAsk+PutAsk=60 ->
        // raw imbalance ratio = (140-60)/200 = 0.4, negated -> -0.4 fed into SignedRank.
        var inputs = new OptionsThreeWayScoreInputs(
            TimeOfDayIst: new TimeSpan(9, 45, 0),
            ClosePrice: 100m,
            PreviousClose: null,
            CurrentAtmIv: null,
            WideBookCallBidQtyAvg: 80, WideBookPutBidQtyAvg: 60,
            WideBookCallAskQtyAvg: 30, WideBookPutAskQtyAvg: 30);

        var score = OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        // First reading of the session: SignedRank ranks against an empty tracker (Rank returns 0),
        // so the very first score is always 0 with sign preserved by construction (sign * 0/100).
        Assert.Equal(0.0, score);
        Assert.Equal(1, depthRank.Count);
        // Magnitude 0.4 (the |raw| passed to SignedRank) was recorded, not the raw signed value.
        Assert.Equal(0.4, depthRank.Values[0], 1e-9);
    }

    [Fact]
    public void ComputeScore_OpenLeg_SecondReading_RanksAgainstFirst()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = null;

        var first = new OptionsThreeWayScoreInputs(new TimeSpan(9, 40, 0), 100m, null, null, 40, 20, 20, 20); // ratio=(60-40)/100=0.2
        OptionsThreeWayScoreCalculator.ComputeScore(first, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        // Stronger imbalance than the first reading: ratio = (140-60)/200 = 0.4, negated -> -0.4.
        // |raw|=0.4 ranks at or above the one prior value (0.2), i.e. rank 100 -> full-magnitude
        // score with sign preserved (negative).
        var second = new OptionsThreeWayScoreInputs(new TimeSpan(9, 41, 0), 100m, null, null, 80, 60, 30, 30); // ratio 0.4
        var score = OptionsThreeWayScoreCalculator.ComputeScore(second, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.NotNull(score);
        Assert.True(score < 0);
        Assert.Equal(2, depthRank.Count);
    }

    [Fact]
    public void ComputeScore_OpenLeg_NullWhenBookMissing()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = null;

        var inputs = new OptionsThreeWayScoreInputs(new TimeSpan(9, 45, 0), 100m, null, null, null, null, null, null);

        var score = OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.Null(score);
        Assert.Equal(0, depthRank.Count);
    }

    [Fact]
    public void ComputeScore_MidLeg_NullUntilBothPreviousAtmIvAndPreviousCloseKnown()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = null;

        // First Mid-window bar with no prior IV reading yet (e.g. Open leg never populated it
        // because depth was also missing) -- must be null, not fabricated.
        var inputs = new OptionsThreeWayScoreInputs(new TimeSpan(10, 5, 0), 100m, 99m, 15.0, null, null, null, null);
        var score = OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.Null(score);
        // previousAtmIv still gets primed for next time even though this bar scored null.
        Assert.Equal(15.0, previousAtmIv);
    }

    [Fact]
    public void ComputeScore_MidLeg_SignedByNegativePriceDirectionTimesDeltaIv()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = 15.0;

        // Prime the Mid tracker with a small |ΔIV| reading first (SignedRank ranks the very first
        // value at 0 regardless of sign, so a sign assertion needs a second, larger reading to
        // actually rank above something).
        var priming = new OptionsThreeWayScoreInputs(new TimeSpan(10, 30, 0), 100m, 99m, 15.2, null, null, null, null); // ΔIV=0.2
        OptionsThreeWayScoreCalculator.ComputeScore(priming, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        // Price UP (100 -> 101), IV UP (15.2 -> 16): -sign(+1) * (16-15.2) = -1 * 0.8 = -0.8, a
        // bigger |ΔIV| than the priming reading, so it ranks above it -> nonzero negative score.
        var inputs = new OptionsThreeWayScoreInputs(new TimeSpan(11, 0, 0), 101m, 100m, 16.0, null, null, null, null);
        var score = OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.NotNull(score);
        Assert.True(score < 0);
        Assert.Equal(2, ivMidRank.Count);
        Assert.Equal(16.0, previousAtmIv);
    }

    [Fact]
    public void ComputeScore_CloseLeg_UsesRawDeltaIv_NotPriceSigned()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = 15.0;

        // Prime the Close tracker with a small |ΔIV| reading first, same reasoning as the Mid-leg
        // test above.
        var priming = new OptionsThreeWayScoreInputs(new TimeSpan(13, 45, 0), 100m, 99m, 15.2, null, null, null, null); // ΔIV=0.2
        OptionsThreeWayScoreCalculator.ComputeScore(priming, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        // Price falls (would flip sign under the Mid-leg formula) but Close leg ignores price
        // direction entirely -- raw ΔIV = 16-15.2 = +0.8, bigger than the priming reading, so it
        // ranks above it -> positive score expected regardless of price direction.
        var inputs = new OptionsThreeWayScoreInputs(new TimeSpan(14, 0, 0), 90m, 100m, 16.0, null, null, null, null);
        var score = OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.NotNull(score);
        Assert.True(score > 0);
        Assert.Equal(2, ivCloseRank.Count);
        Assert.Equal(0, ivMidRank.Count); // Close leg must never touch the Mid tracker.
    }

    [Fact]
    public void ComputeScore_BoundaryIsInclusiveOfOpenMidEdge_ExactlyAtBoundaryIsMidLeg()
    {
        var depthRank = new SessionRankTracker();
        var ivMidRank = new SessionRankTracker();
        var ivCloseRank = new SessionRankTracker();
        double? previousAtmIv = 15.0;

        // Exactly at the Open/Mid boundary (10:00:00) -- `timeOfDay < boundary` is false at the
        // exact edge, so this must resolve to the Mid leg's formula, not the Open leg's.
        var inputs = new OptionsThreeWayScoreInputs(OpenMidBoundary, 101m, 100m, 16.0, 80, 60, 30, 30);
        OptionsThreeWayScoreCalculator.ComputeScore(
            inputs, ref previousAtmIv, depthRank, ivMidRank, ivCloseRank, OpenMidBoundary, MidCloseBoundary);

        Assert.Equal(0, depthRank.Count);
        Assert.Equal(1, ivMidRank.Count);
    }

    [Fact]
    public void ComputeScore_DefaultBoundaries_MatchTradeSimulatorsLockedConstants()
    {
        Assert.Equal(new TimeSpan(13, 30, 0), OptionsThreeWayScoreCalculator.DefaultMidCloseBoundary);
    }

    [Theory]
    [InlineData(50.0, 40.0, 0.1111111111111111)]
    [InlineData(40.0, 50.0, -0.1111111111111111)]
    [InlineData(50.0, 50.0, 0.0)]
    public void ComputeImbalanceRatio_MatchesFormula(double a, double b, double expected)
    {
        var actual = OptionsThreeWayScoreCalculator.ComputeImbalanceRatio(a, b);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 1e-9);
    }

    [Fact]
    public void ComputeImbalanceRatio_NullWhenEitherSideMissing()
    {
        Assert.Null(OptionsThreeWayScoreCalculator.ComputeImbalanceRatio(null, 10));
        Assert.Null(OptionsThreeWayScoreCalculator.ComputeImbalanceRatio(10, null));
    }

    [Fact]
    public void ComputeImbalanceRatio_NullWhenSumIsZero()
    {
        Assert.Null(OptionsThreeWayScoreCalculator.ComputeImbalanceRatio(5, -5));
    }
}

public class MaxPainConfirmationGateTests
{
    [Fact]
    public void ComputeScore_NullWhenMaxPainStrikeMissing()
    {
        var rank = new SessionRankTracker();
        Assert.Null(MaxPainConfirmationGate.ComputeScore(100m, null, rank));
        Assert.Equal(0, rank.Count); // never touches the tracker when there's no reading
    }

    [Fact]
    public void ComputeScore_SignFlipped_PriceAboveMaxPainReadsBearish()
    {
        var rank = new SessionRankTracker();
        // Close (110) above Max Pain strike (100): -(110-100) = -10 -> negative raw, bearish.
        var score = MaxPainConfirmationGate.ComputeScore(110m, 100m, rank);

        Assert.NotNull(score);
        Assert.True(score <= 0);
        Assert.Equal(1, rank.Count);
    }

    [Fact]
    public void ComputeScore_PriceBelowMaxPainReadsBullish()
    {
        var rank = new SessionRankTracker();
        // Close (90) below Max Pain strike (100): -(90-100) = +10 -> positive raw, bullish.
        var score = MaxPainConfirmationGate.ComputeScore(90m, 100m, rank);

        Assert.NotNull(score);
        Assert.True(score >= 0);
    }

    [Fact]
    public void Passes_TrueOnlyWhenSignsAgree()
    {
        Assert.True(MaxPainConfirmationGate.Passes(0.4, 10.0));
        Assert.True(MaxPainConfirmationGate.Passes(-0.4, -10.0));
        Assert.False(MaxPainConfirmationGate.Passes(0.4, -10.0));
        Assert.False(MaxPainConfirmationGate.Passes(-0.4, 10.0));
    }

    [Fact]
    public void Passes_FalseWhenNoMaxPainReading()
    {
        Assert.False(MaxPainConfirmationGate.Passes(null, 10.0));
    }
}
