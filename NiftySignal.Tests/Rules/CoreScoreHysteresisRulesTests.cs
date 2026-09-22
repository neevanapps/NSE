using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public sealed class CoreScoreHysteresisRulesTests
{
    const double Threshold = 30.0;
    static readonly TimeOnly SquareOffTime = new(15, 15);
    static readonly DateTimeOffset WellBeforeSquareOff = ToUtc(new DateTimeOffset(2026, 9, 15, 10, 0, 0, TimeSpan.FromHours(5.5)));
    static readonly DateTimeOffset AtSquareOff = ToUtc(new DateTimeOffset(2026, 9, 15, 15, 15, 0, TimeSpan.FromHours(5.5)));

    static DateTimeOffset ToUtc(DateTimeOffset ist) => ist.ToUniversalTime();

    [Fact]
    public void EvaluateEntry_Rejects_WhenScoreIsExactlyOneBelowThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateEntry(Threshold - 0.01, Threshold);
        Assert.False(decision.ShouldEnter);
        Assert.Equal(EntryDirection.None, decision.Direction);
    }

    [Fact]
    public void EvaluateEntry_Accepts_WhenScoreIsExactlyAtThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateEntry(Threshold, Threshold);
        Assert.True(decision.ShouldEnter);
        Assert.Equal(EntryDirection.Bullish, decision.Direction);
    }

    [Fact]
    public void EvaluateEntry_ReturnsBearish_ForANegativeScoreAtThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateEntry(-Threshold, Threshold);
        Assert.True(decision.ShouldEnter);
        Assert.Equal(EntryDirection.Bearish, decision.Direction);
    }

    [Fact]
    public void EvaluateExit_HoldsABullishPosition_WhenScoreIsStillAboveTheNEGATIVEThreshold()
    {
        // The whole point of the hysteresis band: a held Bullish position must NOT exit just
        // because the score dropped below +threshold -- only once it crosses to -threshold.
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bullish, score: 1.0, Threshold, WellBeforeSquareOff, SquareOffTime);
        Assert.False(decision.ShouldExit);
    }

    [Fact]
    public void EvaluateExit_HoldsABullishPosition_WhenScoreIsExactlyAtTheNegativeThreshold()
    {
        // Exactly -threshold does NOT invalidate -- the check is strictly-less-than, matching
        // CoreScoreOptionSimulator.cs's `liveScore < -options.EntryScoreThreshold` exactly.
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bullish, score: -Threshold, Threshold, WellBeforeSquareOff, SquareOffTime);
        Assert.False(decision.ShouldExit);
    }

    [Fact]
    public void EvaluateExit_ClosesABullishPosition_WhenScoreCrossesJustBelowTheNegativeThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bullish, score: -Threshold - 0.01, Threshold, WellBeforeSquareOff, SquareOffTime);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.ScoreInvalidated, decision.Reason);
    }

    [Fact]
    public void EvaluateExit_HoldsABearishPosition_WhenScoreIsExactlyAtThePositiveThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bearish, score: Threshold, Threshold, WellBeforeSquareOff, SquareOffTime);
        Assert.False(decision.ShouldExit);
    }

    [Fact]
    public void EvaluateExit_ClosesABearishPosition_WhenScoreCrossesJustAboveThePositiveThreshold()
    {
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bearish, score: Threshold + 0.01, Threshold, WellBeforeSquareOff, SquareOffTime);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.ScoreInvalidated, decision.Reason);
    }

    [Fact]
    public void EvaluateExit_FiresSquareOff_AtExactlySquareOffTime_RegardlessOfScore()
    {
        // Score is strongly in the held direction's favor -- SquareOff still fires, the one
        // shared, non-negotiable safety exit.
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bullish, score: 90.0, Threshold, AtSquareOff, SquareOffTime);
        Assert.True(decision.ShouldExit);
        Assert.Equal(ExitReason.SquareOff, decision.Reason);
    }

    [Fact]
    public void EvaluateExit_ChecksSquareOffBeforeTheHysteresisBand()
    {
        // Both conditions are true at once (past SquareOff AND score invalidated) -- SquareOff
        // must win, matching ExitRuleEvaluator's own priority-order convention (checked first).
        var decision = CoreScoreHysteresisRules.EvaluateExit(EntryDirection.Bullish, score: -Threshold - 5, Threshold, AtSquareOff, SquareOffTime);
        Assert.Equal(ExitReason.SquareOff, decision.Reason);
    }
}
