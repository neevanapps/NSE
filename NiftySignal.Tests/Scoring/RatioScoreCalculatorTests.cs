using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class RatioScoreCalculatorTests
{
    static readonly DateTimeOffset ComputedAt = new(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(5.5));

    static readonly RatioComponentInputs AllFivePresent = new(
        NotionalVolumeRatio: 0.5,
        SizedOiFlowRatio: -0.3,
        ResidualDifference: 0.8,
        IvSkew25Delta: -0.6,
        SpreadRatioAtm: 0.2);

    [Fact]
    public void Calculate_MatchesManualComputation_WhenAllFiveArePresent()
    {
        var weights = RatioScoreWeights.Default; // 0.2 each, sums to 1.0

        var result = RatioScoreCalculator.Calculate(AllFivePresent, weights, ComputedAt, k: 0.5);

        var expectedRaw = ((0.2 * 0.5) + (0.2 * -0.3) + (0.2 * 0.8) + (0.2 * -0.6) + (0.2 * 0.2)) / 1.0;
        var expectedScore = 100.0 * Math.Tanh(expectedRaw / 0.5);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        Assert.Equal(expectedScore, result.Score.Value, 1e-9);
    }

    [Fact]
    public void Calculate_UsesRawOverride_InsteadOfRecomputingFromInputs_WhenSupplied()
    {
        var weights = RatioScoreWeights.Default;
        var withoutOverride = RatioScoreCalculator.Calculate(AllFivePresent, weights, ComputedAt, k: 0.5);
        var withOverride = RatioScoreCalculator.Calculate(AllFivePresent, weights, ComputedAt, k: 0.5, rawOverride: 0.0);

        Assert.NotEqual(withoutOverride.Score, withOverride.Score);
        Assert.Equal(0.0, withOverride.Score!.Value, 1e-9);
        Assert.True(withOverride.IsWarmedUp);
        Assert.Equal(withoutOverride.Components, withOverride.Components);
    }

    [Fact]
    public void Calculate_RawOverrideDoesNotForceAScore_WhenFewerThanThreeComponentsArePresent()
    {
        var onlyTwoPresent = new RatioComponentInputs(0.5, -0.3, null, null, null);

        var result = RatioScoreCalculator.Calculate(onlyTwoPresent, RatioScoreWeights.Default, ComputedAt, rawOverride: 5.0);

        Assert.False(result.IsWarmedUp);
        Assert.Null(result.Score);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Calculate_ReturnsNullScore_WhenFewerThanThreeComponentsArePresent(int presentCount)
    {
        var values = new double?[] { 0.5, -0.3, 0.8, -0.6, 0.2 };
        for (var i = 0; i < values.Length; i++)
        {
            if (i >= presentCount)
            {
                values[i] = null;
            }
        }

        var inputs = new RatioComponentInputs(values[0], values[1], values[2], values[3], values[4]);
        var result = RatioScoreCalculator.Calculate(inputs, RatioScoreWeights.Default, ComputedAt);

        Assert.False(result.IsWarmedUp);
        Assert.Null(result.Score);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Calculate_ReturnsANonNullScore_RenormalizedByPresentWeight_WhenAtLeastThreeArePresent(int presentCount)
    {
        // Every present component set to the same value (1.0) -- with equal weights, the
        // renormalized weighted average must equal that value regardless of how many of the
        // five are present. This is the test that actually proves renormalization works, not
        // just that the code compiles: a 3-of-5 bar's raw must not come out diluted relative
        // to a 5-of-5 bar just because two components' worth of weight is missing.
        var values = new double?[] { 1.0, 1.0, 1.0, 1.0, 1.0 };
        for (var i = presentCount; i < values.Length; i++)
        {
            values[i] = null;
        }

        var inputs = new RatioComponentInputs(values[0], values[1], values[2], values[3], values[4]);
        var result = RatioScoreCalculator.Calculate(inputs, RatioScoreWeights.Default, ComputedAt, k: 0.5);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        // raw == 1.0 regardless of presentCount (3, 4, or 5) since every present s_i == 1.0 and
        // weights are equal -- so the score is identical across all three cases too.
        Assert.Equal(100.0 * Math.Tanh(1.0 / 0.5), result.Score.Value, 1e-9);
    }

    [Fact]
    public void Calculate_IsAntisymmetric_NegatingEveryInputNegatesTheScore()
    {
        var negated = new RatioComponentInputs(
            -AllFivePresent.NotionalVolumeRatio, -AllFivePresent.SizedOiFlowRatio, -AllFivePresent.ResidualDifference,
            -AllFivePresent.IvSkew25Delta, -AllFivePresent.SpreadRatioAtm);

        var positive = RatioScoreCalculator.Calculate(AllFivePresent, RatioScoreWeights.Default, ComputedAt);
        var negative = RatioScoreCalculator.Calculate(negated, RatioScoreWeights.Default, ComputedAt);

        Assert.Equal(-positive.Score!.Value, negative.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_ScoreStaysWithinBounds_EvenAtFullSaturation()
    {
        var maxedOut = new RatioComponentInputs(1.0, 1.0, 1.0, 1.0, 1.0);

        var result = RatioScoreCalculator.Calculate(maxedOut, RatioScoreWeights.Default, ComputedAt);

        Assert.NotNull(result.Score);
        Assert.True(result.Score.Value < 100.0);
        Assert.True(result.Score.Value > 96.0); // tanh(2) ~= 0.964 at k=0.5, full 5-way agreement
    }

    [Fact]
    public void Calculate_ClipsOutOfRangeComponentValues_BeforeWeighting()
    {
        var withOutOfRange = AllFivePresent with { ResidualDifference = 50.0 };
        var withClipped = AllFivePresent with { ResidualDifference = 1.0 };

        var actual = RatioScoreCalculator.Calculate(withOutOfRange, RatioScoreWeights.Default, ComputedAt);
        var expected = RatioScoreCalculator.Calculate(withClipped, RatioScoreWeights.Default, ComputedAt);

        Assert.Equal(expected.Score!.Value, actual.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_PropagatesTheWeightSetVersion()
    {
        var weights = RatioScoreWeights.Default with { Version = "ratio-test-custom" };

        var result = RatioScoreCalculator.Calculate(AllFivePresent, weights, ComputedAt);

        Assert.Equal("ratio-test-custom", result.WeightSetVersion);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Calculate_ThrowsForNonPositiveK(double k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RatioScoreCalculator.Calculate(AllFivePresent, RatioScoreWeights.Default, ComputedAt, k));
    }

    [Fact]
    public void ComputeRaw_MatchesTheRenormalizedWeightedAverageInsideCalculate()
    {
        var calculated = RatioScoreCalculator.Calculate(AllFivePresent, RatioScoreWeights.Default, ComputedAt, k: 0.5, rawOverride: 0.0);
        var raw = RatioScoreCalculator.ComputeRaw(AllFivePresent, RatioScoreWeights.Default);

        Assert.NotNull(raw);
        // Recompute score from the raw ComputeRaw returned, and check it matches what Calculate
        // itself would have produced without the override -- proves the two paths agree.
        var expectedScore = 100.0 * Math.Tanh(raw!.Value / 0.5);
        var withoutOverride = RatioScoreCalculator.Calculate(AllFivePresent, RatioScoreWeights.Default, ComputedAt, k: 0.5);
        Assert.Equal(expectedScore, withoutOverride.Score!.Value, 1e-9);
    }

    [Fact]
    public void ComputeRaw_ReturnsNull_WhenFewerThanThreeComponentsArePresent()
    {
        var onlyTwoPresent = new RatioComponentInputs(0.5, -0.3, null, null, null);

        Assert.Null(RatioScoreCalculator.ComputeRaw(onlyTwoPresent, RatioScoreWeights.Default));
    }

    [Fact]
    public void DefaultWeights_AreEqual_AndSumToOne()
    {
        var weights = RatioScoreWeights.Default;

        Assert.Equal(0.2, weights.NotionalVolumeRatio, 1e-9);
        Assert.Equal(0.2, weights.SizedOiFlowRatio, 1e-9);
        Assert.Equal(0.2, weights.ResidualDifference, 1e-9);
        Assert.Equal(0.2, weights.IvSkew25Delta, 1e-9);
        Assert.Equal(0.2, weights.SpreadRatioAtm, 1e-9);
        Assert.Equal(1.0, weights.Total, 1e-9);
    }
}
