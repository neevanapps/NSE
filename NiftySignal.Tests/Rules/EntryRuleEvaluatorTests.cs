using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public class EntryRuleEvaluatorTests
{
    static readonly DateTimeOffset WithinWindow = new(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

    static EntryContext HappyPath() => new(
        Now: WithinWindow,
        Score: 60,
        ScoreSustainedDuration: TimeSpan.FromSeconds(50),
        KillSwitchEntriesEnabled: true,
        DailyLossLimitBreached: false,
        AllFeaturesWarmedUp: true,
        HasOpenDataGap: false,
        TradesSoFarToday: 0,
        OpenConcurrentPositions: 0,
        LastEntryTimeSameDirection: null,
        IsExpiryDay: false);

    [Fact]
    public void Evaluate_AllowsEntry_WhenEveryConditionIsMet()
    {
        var result = EntryRuleEvaluator.Evaluate(HappyPath(), TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
        Assert.Equal(EntryDirection.Bullish, result.Direction);
        Assert.Empty(result.FailedConditions);
    }

    [Fact]
    public void Evaluate_PicksBearish_ForANegativeScore()
    {
        var context = HappyPath() with { Score = -60 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
        Assert.Equal(EntryDirection.Bearish, result.Direction);
    }

    [Fact]
    public void Evaluate_Blocks_BeforeSessionOpenWindow()
    {
        // Market opens 09:15; NoEntryBeforeMinutes=15 -> entries start 09:30.
        var context = HappyPath() with { Now = new DateTimeOffset(2026, 9, 3, 9, 20, 0, TimeSpan.FromHours(5.5)) };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("session open"));
    }

    [Fact]
    public void Evaluate_Blocks_AfterSessionCloseWindow()
    {
        var context = HappyPath() with { Now = new DateTimeOffset(2026, 9, 3, 15, 1, 0, TimeSpan.FromHours(5.5)) };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("session close"));
    }

    [Fact]
    public void Evaluate_Blocks_OnExpiryDay_AfterTheEarlierExpiryDayCutoff()
    {
        // 14:05 is before the normal 15:00 cutoff but after the expiry-day 14:00 cutoff.
        var context = HappyPath() with
        {
            Now = new DateTimeOffset(2026, 9, 3, 14, 5, 0, TimeSpan.FromHours(5.5)),
            IsExpiryDay = true,
        };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("session close"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_OnExpiryDay_BeforeTheExpiryDayCutoff()
    {
        var context = HappyPath() with
        {
            Now = new DateTimeOffset(2026, 9, 3, 13, 59, 0, TimeSpan.FromHours(5.5)),
            IsExpiryDay = true,
        };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_Blocks_WhenKillSwitchIsDisabled()
    {
        var context = HappyPath() with { KillSwitchEntriesEnabled = false };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("KillSwitch"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenDailyLossCircuitBreakerHasTripped()
    {
        var context = HappyPath() with { DailyLossLimitBreached = true };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("circuit breaker"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenFeaturesAreNotAllWarmedUp()
    {
        var context = HappyPath() with { AllFeaturesWarmedUp = false };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("warmed up"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenADataGapIsOpen()
    {
        var context = HappyPath() with { HasOpenDataGap = true };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("data gap"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenMaxTradesPerDayIsReached()
    {
        var context = HappyPath() with { TradesSoFarToday = 7 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("MaxTradesPerDay"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenMaxConcurrentPositionsIsReached()
    {
        var context = HappyPath() with { OpenConcurrentPositions = 3 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("MaxConcurrentPositions"));
    }

    [Fact]
    public void Evaluate_Blocks_WhenAbsScoreIsBelowMinAbsScore()
    {
        var context = HappyPath() with { Score = 50 }; // MinAbsScore is 55

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("MinAbsScore"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenScoreIsExactlyAtMinAbsScore()
    {
        var context = HappyPath() with { Score = 55 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_Blocks_WhenScoreHasNotBeenSustainedLongEnough()
    {
        var context = HappyPath() with { ScoreSustainedDuration = TimeSpan.FromSeconds(30) }; // needs 45s

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("sustained"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenSustainedDurationIsExactlyAtTheThreshold()
    {
        var context = HappyPath() with { ScoreSustainedDuration = TimeSpan.FromSeconds(45) };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_Blocks_WhenWithinTheReEntryGap()
    {
        var context = HappyPath() with { LastEntryTimeSameDirection = WithinWindow.AddMinutes(-1) }; // gap is 2min

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("ReEntryGap"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_OnceTheReEntryGapHasElapsed()
    {
        var context = HappyPath() with { LastEntryTimeSameDirection = WithinWindow.AddMinutes(-3) };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_ReportsEveryFailedCondition_NotJustTheFirst()
    {
        var context = HappyPath() with { KillSwitchEntriesEnabled = false, HasOpenDataGap = true };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Equal(2, result.FailedConditions.Count);
    }
}
