using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class CrossoverResolutionDiagnosticsTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5));
    static PriceCrossoverEngine.Step Step(double diff) => new(10, 10, diff, false, false);

    [Fact]
    public void AlignStepAtOrBefore_ReturnsTheLastStepAtOrBeforeTheTarget()
    {
        var steps = new List<(DateTimeOffset, PriceCrossoverEngine.Step)>
        {
            (T0.AddMinutes(1), Step(0.1)),
            (T0.AddMinutes(5), Step(-0.2)),
            (T0.AddMinutes(9), Step(0.3)),
        };

        var result = CrossoverResolutionDiagnostics.AlignStepAtOrBefore(steps, T0.AddMinutes(7));

        Assert.Equal(-0.2, result!.Value.DiffFraction);
    }

    [Fact]
    public void AlignStepAtOrBefore_NeverReturnsAStepThatCompletesAfterTheTarget_NoLookahead()
    {
        var steps = new List<(DateTimeOffset, PriceCrossoverEngine.Step)> { (T0.AddMinutes(10), Step(0.5)) };

        var result = CrossoverResolutionDiagnostics.AlignStepAtOrBefore(steps, T0.AddMinutes(5));

        Assert.Null(result);
    }

    [Fact]
    public void AlignStepAtOrBefore_ExactBoundaryTimestamp_IsIncluded()
    {
        var steps = new List<(DateTimeOffset, PriceCrossoverEngine.Step)> { (T0.AddMinutes(5), Step(0.5)) };

        var result = CrossoverResolutionDiagnostics.AlignStepAtOrBefore(steps, T0.AddMinutes(5));

        Assert.Equal(0.5, result!.Value.DiffFraction);
    }

    [Theory]
    [InlineData(-0.5, "Confirms")]
    [InlineData(0.5, "DoesNotConfirm")]
    [InlineData(0.0, "DoesNotConfirm")]
    public void ClassifyConfirmation_BearishStateConfirmsPatternA(double diff, string expected)
        => Assert.Equal(expected, CrossoverResolutionDiagnostics.ClassifyConfirmation(Step(diff)));

    [Fact]
    public void ClassifyConfirmation_NullStepIsUnavailable_NotForced()
        => Assert.Equal("Unavailable", CrossoverResolutionDiagnostics.ClassifyConfirmation(null));

    [Fact]
    public void IsFreshCrossDown_ReflectsTheStepsOwnFlag()
    {
        var crossed = new PriceCrossoverEngine.Step(10, 10, -0.1, false, true);
        var notCrossed = new PriceCrossoverEngine.Step(10, 10, -0.1, false, false);
        Assert.True(CrossoverResolutionDiagnostics.IsFreshCrossDown(crossed));
        Assert.False(CrossoverResolutionDiagnostics.IsFreshCrossDown(notCrossed));
        Assert.False(CrossoverResolutionDiagnostics.IsFreshCrossDown(null));
    }

    static PriceCrossoverEngine.Step SignStep(double? fast, double? slow) => new(fast, slow, null, false, false);

    [Fact]
    public void ClassifyConfirmationBySign_FastBelowSlow_ConfirmsWhenConventionSaysSo()
        => Assert.Equal("Confirms", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(-0.5, -0.1), confirmsWhenFastBelowSlow: true));

    [Fact]
    public void ClassifyConfirmationBySign_FastBelowSlow_DoesNotConfirmWhenConventionIsFlipped()
        => Assert.Equal("DoesNotConfirm", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(-0.5, -0.1), confirmsWhenFastBelowSlow: false));

    [Fact]
    public void ClassifyConfirmationBySign_FastAboveSlow_OppositeOfBelowCase()
    {
        Assert.Equal("DoesNotConfirm", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(0.5, 0.1), confirmsWhenFastBelowSlow: true));
        Assert.Equal("Confirms", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(0.5, 0.1), confirmsWhenFastBelowSlow: false));
    }

    [Fact]
    public void ClassifyConfirmationBySign_ExactlyEqual_IsDoesNotConfirm_NeverForced()
        => Assert.Equal("DoesNotConfirm", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(0.2, 0.2), confirmsWhenFastBelowSlow: true));

    [Fact]
    public void ClassifyConfirmationBySign_NeverDividesBySlowMa_StaysStableNearZero()
    {
        // A slowMa of a near-zero-centered return-spread series sitting extremely close to zero
        // must not blow up or misclassify -- this is the whole reason ClassifyConfirmation's
        // ratio-based DiffFraction is unsafe for this series and this sign-only helper exists.
        var result = CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(-0.0000001, 0.0000001), confirmsWhenFastBelowSlow: true);
        Assert.Equal("Confirms", result);
    }

    [Fact]
    public void ClassifyConfirmationBySign_NullOrMissingMa_IsUnavailable()
    {
        Assert.Equal("Unavailable", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(null, confirmsWhenFastBelowSlow: true));
        Assert.Equal("Unavailable", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(null, 0.2), confirmsWhenFastBelowSlow: true));
        Assert.Equal("Unavailable", CrossoverResolutionDiagnostics.ClassifyConfirmationBySign(SignStep(0.2, null), confirmsWhenFastBelowSlow: true));
    }

    [Theory]
    [InlineData(-0.5, -0.1, -1)]
    [InlineData(0.5, 0.1, 1)]
    [InlineData(0.2, 0.2, 0)]
    public void StepSign_ReflectsSignOfFastMinusSlow(double fast, double slow, int expected)
        => Assert.Equal(expected, CrossoverResolutionDiagnostics.StepSign(SignStep(fast, slow)));

    [Fact]
    public void StepSign_NullOrMissingMa_IsZero()
    {
        Assert.Equal(0, CrossoverResolutionDiagnostics.StepSign(null));
        Assert.Equal(0, CrossoverResolutionDiagnostics.StepSign(SignStep(null, 0.2)));
    }

    [Fact]
    public void CountConsecutiveSameSign_CountsBackwardRunIncludingTheEventItself()
    {
        var steps = new List<PriceCrossoverEngine.Step?>
        {
            SignStep(0.5, 0.1),   // 0: positive
            SignStep(-0.5, -0.1), // 1: negative -- run starts here
            SignStep(-0.3, -0.1), // 2: negative
            SignStep(-0.2, -0.1), // 3: negative (query event)
        };

        Assert.Equal(3, CrossoverResolutionDiagnostics.CountConsecutiveSameSign(steps, 3));
    }

    [Fact]
    public void CountConsecutiveSameSign_EventItselfHasNoSign_ReturnsZero_NeverApproximatedAsOne()
    {
        var steps = new List<PriceCrossoverEngine.Step?> { SignStep(-0.5, -0.1), null };
        Assert.Equal(0, CrossoverResolutionDiagnostics.CountConsecutiveSameSign(steps, 1));
    }

    [Fact]
    public void CountConsecutiveSameSign_OutOfRangeEventId_ReturnsZero()
    {
        var steps = new List<PriceCrossoverEngine.Step?> { SignStep(-0.5, -0.1) };
        Assert.Equal(0, CrossoverResolutionDiagnostics.CountConsecutiveSameSign(steps, 5));
        Assert.Equal(0, CrossoverResolutionDiagnostics.CountConsecutiveSameSign(steps, -1));
    }
}
