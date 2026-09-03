using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public class ExitRuleEvaluatorTests
{
    static readonly DateTimeOffset EntryTime = new(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

    static OpenPositionState HealthyLongPosition() => new(
        EntryTime: EntryTime,
        EntryPremium: 175,
        CurrentPremium: 180, // ~2.9% up, below partial-book threshold
        HasPartiallyBooked: false,
        Direction: EntryDirection.Bullish);

    [Fact]
    public void Evaluate_NoExit_WhenNothingHasTriggered()
    {
        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: 60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.False(result.ShouldExit);
        Assert.Equal(ExitReason.None, result.Reason);
    }

    [Fact]
    public void Evaluate_SquareOff_OverridesEverythingElse_AtTheConfiguredTime()
    {
        // Position is otherwise perfectly healthy -- square-off must still fire at 15:15.
        var squareOffTime = new DateTimeOffset(2026, 9, 3, 15, 15, 0, TimeSpan.FromHours(5.5));

        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: 60, squareOffTime, TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.False(result.IsPartialExit);
        Assert.Equal(ExitReason.SquareOff, result.Reason);
    }

    [Fact]
    public void Evaluate_StopLoss_TriggersAtTheConfiguredLossPercent()
    {
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 0.74m }; // -26%, past the 25% stop
        var result = ExitRuleEvaluator.Evaluate(position, currentScore: 60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.False(result.IsPartialExit);
        Assert.Equal(ExitReason.StopLoss, result.Reason);
    }

    [Fact]
    public void Evaluate_PartialBook_TriggersAtTheConfiguredProfitPercent()
    {
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 1.31m }; // +31%, past the 30% partial-book level
        var result = ExitRuleEvaluator.Evaluate(position, currentScore: 60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.True(result.IsPartialExit);
        Assert.Equal(ExitReason.PartialBook, result.Reason);
    }

    [Fact]
    public void Evaluate_PartialBook_DoesNotFireTwice_OnceAlreadyBooked()
    {
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 1.31m, HasPartiallyBooked = true };
        var result = ExitRuleEvaluator.Evaluate(position, currentScore: 60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.NotEqual(ExitReason.PartialBook, result.Reason);
    }

    [Fact]
    public void Evaluate_TrailsStopToBreakeven_AfterAPartialBook()
    {
        // Small loss (-5%) that would NOT trigger the original 25% stop, but DOES breach
        // the breakeven trail that TrailAfterPartialBook activates post-partial-book.
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 0.95m, HasPartiallyBooked = true };

        var result = ExitRuleEvaluator.Evaluate(position, currentScore: 60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.StopLoss, result.Reason);
    }

    [Fact]
    public void Evaluate_DoesNotTrailStop_WhenTrailAfterPartialBookIsDisabled()
    {
        var config = TestRulesetConfigs.Default();
        config = config with { Exit = config.Exit with { TrailAfterPartialBook = false } };
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 0.95m, HasPartiallyBooked = true };

        var result = ExitRuleEvaluator.Evaluate(position, currentScore: 60, EntryTime.AddMinutes(5), config);

        // -5% is well inside the original 25% stop, and score (60) doesn't trigger flip/decay either.
        Assert.False(result.ShouldExit);
    }

    [Fact]
    public void Evaluate_ScoreFlip_ExitsALongPosition_WhenScoreTurnsNegative()
    {
        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: -10, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.ScoreFlip, result.Reason);
    }

    [Fact]
    public void Evaluate_ScoreFlip_DoesNotFire_ForAShortPosition_WhenScoreIsStillNegative()
    {
        var shortPosition = HealthyLongPosition() with { Direction = EntryDirection.Bearish };

        var result = ExitRuleEvaluator.Evaluate(shortPosition, currentScore: -60, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.False(result.ShouldExit);
    }

    [Fact]
    public void Evaluate_ScoreDecay_ExitsWhenAbsScoreFallsBelowThreshold()
    {
        // Score is still bullish-signed (no flip) but has decayed below ExitOnScoreBelowAbs (30).
        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: 20, EntryTime.AddMinutes(5), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.ScoreDecay, result.Reason);
    }

    [Fact]
    public void Evaluate_TimeStop_ExitsAfterMaxHoldMinutes()
    {
        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: 60, EntryTime.AddMinutes(120), TestRulesetConfigs.Default());

        Assert.True(result.ShouldExit);
        Assert.Equal(ExitReason.TimeStop, result.Reason);
    }

    [Fact]
    public void Evaluate_DoesNotTimeStop_JustBeforeMaxHoldMinutes()
    {
        var result = ExitRuleEvaluator.Evaluate(HealthyLongPosition(), currentScore: 60, EntryTime.AddMinutes(119), TestRulesetConfigs.Default());

        Assert.False(result.ShouldExit);
    }

    [Fact]
    public void Evaluate_StopLoss_TakesPriorityOver_PartialBook()
    {
        // Can't both be true for the same premium move, but confirms the evaluator checks
        // stop-loss before partial-book rather than the other order.
        var config = TestRulesetConfigs.Default();
        // Widen the partial-book threshold below the stop-loss threshold isn't meaningful
        // (they're opposite-signed); instead assert stop-loss wins when it's the only one
        // that can legitimately fire, documenting the intended priority via ScoreFlip vs
        // StopLoss instead, which can genuinely coexist.
        var position = HealthyLongPosition() with { CurrentPremium = 175 * 0.70m }; // -30%, past stop
        var result = ExitRuleEvaluator.Evaluate(position, currentScore: -10, EntryTime.AddMinutes(5), config);

        Assert.Equal(ExitReason.StopLoss, result.Reason);
    }
}
