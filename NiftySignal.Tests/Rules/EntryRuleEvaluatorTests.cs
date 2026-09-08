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
        IsExpiryDay: false,
        DailyProfitTargetReached: false,
        ScoreSustainedCadenceCount: 3);

    [Fact]
    public void Evaluate_AllowsEntry_WhenEveryConditionIsMet()
    {
        var result = EntryRuleEvaluator.Evaluate(HappyPath(), TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
        Assert.Equal(EntryDirection.Bullish, result.Direction);
        Assert.Empty(result.FailedConditions);
    }

    [Fact]
    public void Evaluate_ConvertsNowToIst_RegardlessOfWhatOffsetItArrivesWith()
    {
        // Regression for a live-caught bug (2026-09-07): production always passes Now as UTC
        // (DateTimeOffset.UtcNow, per this codebase's Npgsql timestamptz convention throughout)
        // -- but every existing test in this file constructs Now directly at the +5:30 IST
        // offset, which happens to sidestep the bug entirely and never exercises the real
        // conversion. 04:30 UTC and 10:00 IST are the exact same instant as WithinWindow above;
        // this must allow entry exactly like the IST-offset happy path does. Before the fix,
        // TimeOnly.FromDateTime(context.Now.DateTime) read the UTC wall-clock time (04:30)
        // directly against the IST-intended 09:30 cutoff and wrongly rejected it -- which in
        // production meant "entries start at 9:30" was only ever satisfied once the clock hit
        // 9:30 UTC = 15:00 IST, at or past NSE's close. No entry had ever been possible during
        // the actual trading session as a result.
        var sameInstantAsUtc = new DateTimeOffset(2026, 9, 3, 4, 30, 0, TimeSpan.Zero);
        var context = HappyPath() with { Now = sameInstantAsUtc };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
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
    public void Evaluate_Blocks_WhenDailyProfitTargetHasBeenReached()
    {
        // The upside mirror of the loss breaker -- a good day should stop opening new risk,
        // not keep trading until it's given back.
        var context = HappyPath() with { DailyProfitTargetReached = true };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("profit target"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenDailyProfitTargetHasNotBeenReached()
    {
        // Boundary companion to the test above: the flag being false must not block on its own.
        var context = HappyPath() with { DailyProfitTargetReached = false };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
        Assert.DoesNotContain(result.FailedConditions, f => f.Contains("profit target"));
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

    [Fact]
    public void Evaluate_Blocks_WhenIvRankAboveMaxIvRankForEntry()
    {
        // Audit finding F3: buying premium when IV is already rich is how a directionally
        // correct trade still loses to a vol crush.
        var context = HappyPath() with { CurrentIvRank = 70.01 }; // MaxIvRankForEntry is 70

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("IV rank"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenIvRankIsExactlyAtMaxIvRankForEntry()
    {
        var context = HappyPath() with { CurrentIvRank = 70 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenCurrentIvRankIsNull_NotEnoughHistoryYetToRankAgainst()
    {
        var context = HappyPath() with { CurrentIvRank = null };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }

    [Fact]
    public void Evaluate_Blocks_WhenConsecutiveLossesReachesMaxConsecutiveLosses()
    {
        var context = HappyPath() with { ConsecutiveLossesToday = 4 }; // MaxConsecutiveLosses is 4

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.False(result.ShouldEnter);
        Assert.Contains(result.FailedConditions, f => f.Contains("MaxConsecutiveLosses"));
    }

    [Fact]
    public void Evaluate_AllowsEntry_WhenConsecutiveLossesIsOneBelowMaxConsecutiveLosses()
    {
        var context = HappyPath() with { ConsecutiveLossesToday = 3 };

        var result = EntryRuleEvaluator.Evaluate(context, TestRulesetConfigs.Default());

        Assert.True(result.ShouldEnter);
    }
}
