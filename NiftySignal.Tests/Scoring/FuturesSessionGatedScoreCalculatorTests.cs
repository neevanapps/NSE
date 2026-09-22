using NiftySignal.Features;
using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

/// <summary>
/// Futures-crossover live-wiring task (2026-09-21): proves <see cref="FuturesSessionGatedScoreCalculator"/>
/// reproduces the exact formula `NiftySignal.VolumeBarData/TradeSimulator.cs`'s own (now-relocated)
/// private <c>ComputeSessionGatedScore</c>/<c>ComputeBarDurationScore</c>/TOB-gate branch used, as a
/// fast regression safety net for the extraction -- same role
/// <c>OptionsThreeWayScoreCalculatorTests</c> plays for the options side's own 2026-09-20 extraction.
/// </summary>
public class FuturesSessionGatedScoreCalculatorTests
{
    static readonly TimeSpan SwitchTime = new(10, 0, 0);

    [Fact]
    public void ComputeScore_BeforeSwitchTime_UsesNegatedNothing_JustDepthImbalance_SessionRankNormalized()
    {
        var depthRank = new SessionRankTracker();
        var durationRank = new SessionRankTracker();

        var inputs = new FuturesSessionGatedScoreInputs(new TimeSpan(9, 45, 0), 100m, null, 5.0, 0.4, null);
        var score = FuturesSessionGatedScoreCalculator.ComputeScore(inputs, depthRank, durationRank, SwitchTime);

        // First reading: SignedRank ranks against an empty tracker (rank 0), so score is 0 by
        // construction (sign * 0/100), but the magnitude 0.4 is recorded on the DEPTH tracker only.
        Assert.Equal(0.0, score);
        Assert.Equal(1, depthRank.Count);
        Assert.Equal(0, durationRank.Count);
    }

    [Fact]
    public void ComputeScore_AtOrAfterSwitchTime_UsesBarDurationUrgency_SignedByPriceDirection()
    {
        var depthRank = new SessionRankTracker();
        var durationRank = new SessionRankTracker();

        // Price up (101 vs prior 100), duration 2s -> magnitude 1/2=0.5, sign +1.
        var inputs = new FuturesSessionGatedScoreInputs(new TimeSpan(10, 5, 0), 101m, 100m, 2.0, 0.4, null);
        var score = FuturesSessionGatedScoreCalculator.ComputeScore(inputs, depthRank, durationRank, SwitchTime);

        Assert.Equal(0.0, score); // First reading on the duration tracker -> rank 0.
        // Both legs are computed every bar regardless of which is active (same "both trackers fed
        // every bar" convention the pre-extraction inline code used) -- depth's own non-zero
        // reading (0.4) still touches its tracker even though duration is the traded leg here.
        Assert.Equal(1, depthRank.Count);
        Assert.Equal(1, durationRank.Count);
        Assert.Equal(0.5, durationRank.Values[0], 1e-9);
    }

    [Fact]
    public void ComputeScore_AfterSwitchTime_NullPreviousClose_ReturnsNull()
    {
        var depthRank = new SessionRankTracker();
        var durationRank = new SessionRankTracker();

        var inputs = new FuturesSessionGatedScoreInputs(new TimeSpan(10, 5, 0), 101m, null, 2.0, 0.4, null);
        var score = FuturesSessionGatedScoreCalculator.ComputeScore(inputs, depthRank, durationRank, SwitchTime);

        Assert.Null(score);
    }

    [Fact]
    public void ComputeScore_ZeroPriceChange_ReadsAsGenuineZero_NotNull()
    {
        var depthRank = new SessionRankTracker();
        var durationRank = new SessionRankTracker();

        var inputs = new FuturesSessionGatedScoreInputs(new TimeSpan(10, 5, 0), 100m, 100m, 2.0, null, null);
        var score = FuturesSessionGatedScoreCalculator.ComputeScore(inputs, depthRank, durationRank, SwitchTime);

        // SignedRank.Compute's own "a raw zero reads as a literal 0.0 without touching the tracker"
        // convention (see SignedRank.cs) -- a genuine zero score, but the tracker itself stays empty.
        Assert.Equal(0.0, score);
        Assert.Equal(0, durationRank.Count);
    }

    [Fact]
    public void PassesTobConfirmation_OutsideOpenWindow_AlwaysTrue()
    {
        Assert.True(FuturesSessionGatedScoreCalculator.PassesTobConfirmation(new TimeSpan(10, 0, 1), scaledScore: 42.0, tobConfirmScore: null, SwitchTime));
        Assert.True(FuturesSessionGatedScoreCalculator.PassesTobConfirmation(new TimeSpan(14, 0, 0), scaledScore: -5.0, tobConfirmScore: 3.0, SwitchTime));
    }

    [Fact]
    public void PassesTobConfirmation_InOpenWindow_RequiresSignAgreement()
    {
        Assert.True(FuturesSessionGatedScoreCalculator.PassesTobConfirmation(new TimeSpan(9, 45, 0), scaledScore: 10.0, tobConfirmScore: 0.2, SwitchTime));
        Assert.False(FuturesSessionGatedScoreCalculator.PassesTobConfirmation(new TimeSpan(9, 45, 0), scaledScore: 10.0, tobConfirmScore: -0.2, SwitchTime));
        Assert.False(FuturesSessionGatedScoreCalculator.PassesTobConfirmation(new TimeSpan(9, 45, 0), scaledScore: 10.0, tobConfirmScore: null, SwitchTime));
    }

    [Fact]
    public void ComputeTobConfirmationScore_SessionRankNormalizesTopOfBookImbalance()
    {
        var tobRank = new SessionRankTracker();
        var inputs = new FuturesSessionGatedScoreInputs(new TimeSpan(9, 45, 0), 100m, null, 5.0, null, 0.3);

        var score = FuturesSessionGatedScoreCalculator.ComputeTobConfirmationScore(inputs, tobRank);

        Assert.Equal(0.0, score); // First reading -> rank 0.
        Assert.Equal(1, tobRank.Count);
        Assert.Equal(0.3, tobRank.Values[0], 1e-9);
    }
}
