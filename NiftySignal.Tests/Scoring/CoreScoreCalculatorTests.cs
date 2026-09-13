using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CoreScoreCalculatorTests
{
    static readonly DateTimeOffset ComputedAt = new(2026, 9, 13, 10, 0, 0, TimeSpan.FromHours(5.5));

    static readonly CoreScoreComponentInputs AllEightPresent = new(
        DepthImbalance: 0.5,
        ItmSkew: -0.3,
        FutureCvdNet5Min: 0.8,
        NotionalVolumeRatio: -0.6,
        GammaExposure: 0.2,
        TrendReversion15m: -0.1,
        BasisChange: 0.4,
        OiChangeDiff15m: -0.9);

    [Fact]
    public void Calculate_MatchesManualComputation_WhenAllEightArePresent()
    {
        var weights = CoreScoreWeights.Default;

        var result = CoreScoreCalculator.Calculate(AllEightPresent, weights, ComputedAt, k: 1.0);

        var expectedRaw = ((weights.DepthImbalance * 0.5) + (weights.ItmSkew * -0.3) + (weights.FutureCvdNet5Min * 0.8)
            + (weights.NotionalVolumeRatio * -0.6) + (weights.GammaExposure * 0.2) + (weights.TrendReversion15m * -0.1)
            + (weights.BasisChange * 0.4) + (weights.OiChangeDiff15m * -0.9)) / weights.Total;
        var expectedScore = 100.0 * Math.Tanh(expectedRaw / 1.0);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        Assert.Equal(expectedScore, result.Score.Value, 1e-9);
    }

    [Fact]
    public void Calculate_UsesRawOverride_InsteadOfRecomputingFromInputs_WhenSupplied()
    {
        var weights = CoreScoreWeights.Default;
        var withoutOverride = CoreScoreCalculator.Calculate(AllEightPresent, weights, ComputedAt, k: 1.0);
        var withOverride = CoreScoreCalculator.Calculate(AllEightPresent, weights, ComputedAt, k: 1.0, rawOverride: 0.0);

        Assert.NotEqual(withoutOverride.Score, withOverride.Score);
        Assert.Equal(0.0, withOverride.Score!.Value, 1e-9);
        Assert.True(withOverride.IsWarmedUp);
        Assert.Equal(withoutOverride.Components, withOverride.Components);
    }

    [Fact]
    public void Calculate_ReturnsNullScore_WhenNoComponentsArePresent()
    {
        var nonePresent = new CoreScoreComponentInputs(null, null, null, null, null, null, null, null);

        var result = CoreScoreCalculator.Calculate(nonePresent, CoreScoreWeights.Default, ComputedAt);

        Assert.False(result.IsWarmedUp);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Calculate_PublishesAScore_WhenOnlyASingleComponentIsPresent()
    {
        // Deliberately proves this differs from RatioScoreCalculator's own MinRequiredComponents=3
        // floor -- the Core score backtest (CoreScoreOptionSimulator.cs) has no minimum-count gate
        // at all, only presentWeight > 0. A single present term must be enough to publish.
        var onlyOnePresent = new CoreScoreComponentInputs(0.5, null, null, null, null, null, null, null);

        var result = CoreScoreCalculator.Calculate(onlyOnePresent, CoreScoreWeights.Default, ComputedAt, k: 1.0);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        // Only DepthImbalance present -- renormalized weighted average is just its own s_i value.
        Assert.Equal(100.0 * Math.Tanh(0.5 / 1.0), result.Score.Value, 1e-9);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void Calculate_ReturnsANonNullScore_RenormalizedByPresentWeight_RegardlessOfHowManyArePresent(int presentCount)
    {
        // Every present component set to the same value (1.0) -- with any subset present, the
        // renormalized weighted average must equal that value regardless of how many of the eight
        // are present. This is the test that actually proves renormalization works, not just that
        // the code compiles.
        var values = new double?[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
        for (var i = presentCount; i < values.Length; i++)
        {
            values[i] = null;
        }

        var inputs = new CoreScoreComponentInputs(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]);
        var result = CoreScoreCalculator.Calculate(inputs, CoreScoreWeights.Default, ComputedAt, k: 1.0);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        Assert.Equal(100.0 * Math.Tanh(1.0 / 1.0), result.Score.Value, 1e-9);
    }

    [Fact]
    public void Calculate_IsAntisymmetric_NegatingEveryInputNegatesTheScore()
    {
        var negated = new CoreScoreComponentInputs(
            -AllEightPresent.DepthImbalance, -AllEightPresent.ItmSkew, -AllEightPresent.FutureCvdNet5Min,
            -AllEightPresent.NotionalVolumeRatio, -AllEightPresent.GammaExposure, -AllEightPresent.TrendReversion15m,
            -AllEightPresent.BasisChange, -AllEightPresent.OiChangeDiff15m);

        var positive = CoreScoreCalculator.Calculate(AllEightPresent, CoreScoreWeights.Default, ComputedAt);
        var negative = CoreScoreCalculator.Calculate(negated, CoreScoreWeights.Default, ComputedAt);

        Assert.Equal(-positive.Score!.Value, negative.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_ScoreStaysWithinBounds_EvenAtFullSaturation()
    {
        var maxedOut = new CoreScoreComponentInputs(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0);

        var result = CoreScoreCalculator.Calculate(maxedOut, CoreScoreWeights.Default, ComputedAt);

        Assert.NotNull(result.Score);
        Assert.True(result.Score.Value < 100.0);
        Assert.True(result.Score.Value > 0.0);
    }

    [Fact]
    public void Calculate_ClipsOutOfRangeComponentValues_BeforeWeighting()
    {
        var withOutOfRange = AllEightPresent with { FutureCvdNet5Min = 50.0 };
        var withClipped = AllEightPresent with { FutureCvdNet5Min = 1.0 };

        var actual = CoreScoreCalculator.Calculate(withOutOfRange, CoreScoreWeights.Default, ComputedAt);
        var expected = CoreScoreCalculator.Calculate(withClipped, CoreScoreWeights.Default, ComputedAt);

        Assert.Equal(expected.Score!.Value, actual.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_PropagatesTheWeightSetVersion()
    {
        var weights = CoreScoreWeights.Default with { Version = "core-score-test-custom" };

        var result = CoreScoreCalculator.Calculate(AllEightPresent, weights, ComputedAt);

        Assert.Equal("core-score-test-custom", result.WeightSetVersion);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Calculate_ThrowsForNonPositiveK(double k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CoreScoreCalculator.Calculate(AllEightPresent, CoreScoreWeights.Default, ComputedAt, k));
    }

    [Fact]
    public void ComputeRaw_MatchesTheRenormalizedWeightedAverageInsideCalculate()
    {
        var calculated = CoreScoreCalculator.Calculate(AllEightPresent, CoreScoreWeights.Default, ComputedAt, k: 1.0, rawOverride: 0.0);
        var raw = CoreScoreCalculator.ComputeRaw(AllEightPresent, CoreScoreWeights.Default);

        Assert.NotNull(raw);
        var expectedScore = 100.0 * Math.Tanh(raw!.Value / 1.0);
        var withoutOverride = CoreScoreCalculator.Calculate(AllEightPresent, CoreScoreWeights.Default, ComputedAt, k: 1.0);
        Assert.Equal(expectedScore, withoutOverride.Score!.Value, 1e-9);
    }

    [Fact]
    public void ComputeRaw_ReturnsNull_WhenNoComponentsArePresent()
    {
        var nonePresent = new CoreScoreComponentInputs(null, null, null, null, null, null, null, null);

        Assert.Null(CoreScoreCalculator.ComputeRaw(nonePresent, CoreScoreWeights.Default));
    }

    [Fact]
    public void DefaultWeights_MatchTheBacktestExactly_AndDoNotSumToOne()
    {
        // Deliberate: the backtest's own weights sum to 0.915, not 1.0, and are never rescaled --
        // see CoreScoreWeights.Total's own doc comment. This test exists specifically to catch a
        // future "helpful" rescale to 1.0, which would silently diverge from what was backtested.
        var weights = CoreScoreWeights.Default;

        Assert.Equal(0.25, weights.DepthImbalance, 1e-9);
        Assert.Equal(0.065, weights.ItmSkew, 1e-9);
        Assert.Equal(0.12, weights.FutureCvdNet5Min, 1e-9);
        Assert.Equal(0.14, weights.NotionalVolumeRatio, 1e-9);
        Assert.Equal(0.06, weights.GammaExposure, 1e-9);
        Assert.Equal(0.10, weights.TrendReversion15m, 1e-9);
        Assert.Equal(0.08, weights.BasisChange, 1e-9);
        Assert.Equal(0.10, weights.OiChangeDiff15m, 1e-9);
        Assert.Equal(0.915, weights.Total, 1e-9);
    }

    [Fact]
    public void DefaultK_MatchesTheBacktestExactly()
    {
        Assert.Equal(1.0, CoreScoreCalculator.DefaultK, 1e-9);
    }
}
