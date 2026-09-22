using NiftySignal.Domain.Enums;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public sealed class CoreScoreCrossoverRulesTests
{
    static readonly TimeOnly SquareOffTime = new(15, 15);

    [Fact]
    public void Observe_DoesNotFireACross_OnTheFirstWarmedUpReading()
    {
        // Matches CoreScoreOptionSimulator.cs's own `previousDiffSign is null` branch exactly --
        // the very first comparison just establishes the baseline, it's never itself a crossover.
        var rules = new CoreScoreCrossoverRules();

        var observation = rules.Observe(fastAverage: 10.0, slowAverage: 5.0);

        Assert.False(observation.Crossed);
        Assert.Equal(1, rules.PreviousDiffSign);
    }

    [Fact]
    public void Observe_DoesNotFireACross_WhileFastStaysOnTheSameSideOfSlow()
    {
        var rules = new CoreScoreCrossoverRules();
        rules.Observe(fastAverage: 10.0, slowAverage: 5.0); // establishes bullish baseline

        var stillBullish = rules.Observe(fastAverage: 12.0, slowAverage: 6.0);

        Assert.False(stillBullish.Crossed);
    }

    [Fact]
    public void Observe_FiresABullishCross_WhenFastMovesFromBelowToAboveSlow()
    {
        var rules = new CoreScoreCrossoverRules();
        rules.Observe(fastAverage: 5.0, slowAverage: 10.0); // establishes bearish baseline

        var flip = rules.Observe(fastAverage: 11.0, slowAverage: 10.0);

        Assert.True(flip.Crossed);
        Assert.Equal(EntryDirection.Bullish, flip.Direction);
        Assert.Equal(1, rules.PreviousDiffSign);
    }

    [Fact]
    public void Observe_FiresABearishCross_WhenFastMovesFromAboveToBelowSlow()
    {
        var rules = new CoreScoreCrossoverRules();
        rules.Observe(fastAverage: 10.0, slowAverage: 5.0); // establishes bullish baseline

        var flip = rules.Observe(fastAverage: 4.0, slowAverage: 5.0);

        Assert.True(flip.Crossed);
        Assert.Equal(EntryDirection.Bearish, flip.Direction);
        Assert.Equal(-1, rules.PreviousDiffSign);
    }

    [Fact]
    public void Observe_TreatsAnExactTieAsNoSignalYet_NotACrossEitherWay()
    {
        var rules = new CoreScoreCrossoverRules();
        rules.Observe(fastAverage: 10.0, slowAverage: 5.0); // establishes bullish baseline

        var tie = rules.Observe(fastAverage: 7.0, slowAverage: 7.0);

        Assert.False(tie.Crossed);
        // A tie must not overwrite the established baseline -- the next real reading should still
        // compare against the pre-tie sign, matching CoreScoreOptionSimulator.cs's own
        // `if (diffSign == 0) { continue; }` (skips the whole cadence, never touches previousDiffSign).
        Assert.Equal(1, rules.PreviousDiffSign);
    }

    [Fact]
    public void Seed_RestoresContinuityAcrossARestart_WithoutTreatingItAsAFreshCross()
    {
        // The exact scenario the second review's correction was about: after a restart, seeding
        // previousDiffSign from replayed history must NOT itself be reported as a crossover --
        // it's a restoration of prior state, not a new observation.
        var beforeRestart = new CoreScoreCrossoverRules();
        beforeRestart.Observe(fastAverage: 5.0, slowAverage: 10.0); // bearish
        beforeRestart.Observe(fastAverage: 11.0, slowAverage: 10.0); // flips bullish

        var afterRestart = new CoreScoreCrossoverRules();
        afterRestart.Seed(beforeRestart.PreviousDiffSign);

        Assert.Equal(1, afterRestart.PreviousDiffSign);

        // The next real observation compares against the SEEDED sign, not a blank slate -- a
        // continued-bullish reading must not spuriously fire.
        var continuedBullish = afterRestart.Observe(fastAverage: 12.0, slowAverage: 10.0);
        Assert.False(continuedBullish.Crossed);

        // And a genuine reversal is still detected correctly post-restart.
        var genuineReversal = afterRestart.Observe(fastAverage: 9.0, slowAverage: 10.0);
        Assert.True(genuineReversal.Crossed);
        Assert.Equal(EntryDirection.Bearish, genuineReversal.Direction);
    }

    [Theory]
    [InlineData(15, 14, 59, false)]
    [InlineData(15, 15, 0, true)]
    [InlineData(15, 16, 0, true)]
    public void IsPastSquareOff_FiresAtOrAfterSquareOffTime_NotBefore(int hour, int minute, int second, bool expected)
    {
        var now = new DateTimeOffset(2026, 9, 15, hour, minute, second, TimeSpan.FromHours(5.5)).ToUniversalTime();

        Assert.Equal(expected, CoreScoreCrossoverRules.IsPastSquareOff(now, SquareOffTime));
    }
}
