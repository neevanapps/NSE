using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CompositeScoreCalculatorTests
{
    static readonly DateTimeOffset ComputedAt = new(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

    static readonly ScoreComponentInputs FullyWarmInputs = new(
        OiBuildupNetZ: 1.0,
        PcrZ: -0.5,
        FuturesBasisZ: 0.8,
        IvSkewZ: -1.2,
        PriceMomentumZ: 2.0,
        DepthImbalanceZ: 0.3);

    [Fact]
    public void Calculate_MatchesManualComputation_ForFullyWarmInputs()
    {
        var weights = ScoreWeights.Default;

        var result = CompositeScoreCalculator.Calculate(FullyWarmInputs, weights, ComputedAt, k: 1.0);

        var expectedRaw = (weights.OiBuildupNet * 1.0) + (weights.Pcr * -0.5) + (weights.FuturesBasis * 0.8)
            + (weights.IvSkew * -1.2) + (weights.PriceMomentum * 2.0) + (weights.DepthImbalance * 0.3);
        var expectedScore = 100.0 * Math.Tanh(expectedRaw / 1.0);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
        Assert.Equal(expectedScore, result.Score.Value, 1e-9);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(false, false, false, false, false, true)]
    public void Calculate_ReturnsNullScore_WhenAnySingleComponentIsNotWarmedUp(
        bool missingOi, bool missingPcr, bool missingBasis, bool missingSkew, bool missingMomentum, bool missingDepth)
    {
        var inputs = FullyWarmInputs with
        {
            OiBuildupNetZ = missingOi ? null : FullyWarmInputs.OiBuildupNetZ,
            PcrZ = missingPcr ? null : FullyWarmInputs.PcrZ,
            FuturesBasisZ = missingBasis ? null : FullyWarmInputs.FuturesBasisZ,
            IvSkewZ = missingSkew ? null : FullyWarmInputs.IvSkewZ,
            PriceMomentumZ = missingMomentum ? null : FullyWarmInputs.PriceMomentumZ,
            DepthImbalanceZ = missingDepth ? null : FullyWarmInputs.DepthImbalanceZ,
        };

        var result = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, ComputedAt);

        Assert.False(result.IsWarmedUp);
        Assert.Null(result.Score);
    }

    [Fact]
    public void Calculate_ReturnsNullScore_WhenNoComponentsAreWarmedUp()
    {
        var allNull = new ScoreComponentInputs(null, null, null, null, null, null);

        var result = CompositeScoreCalculator.Calculate(allNull, ScoreWeights.Default, ComputedAt);

        Assert.False(result.IsWarmedUp);
        Assert.Null(result.Score);
        Assert.All(result.Components, c => Assert.Null(c.ZScore));
    }

    [Fact]
    public void Calculate_IsAntisymmetric_NegatingEveryInputNegatesTheScore()
    {
        var negated = new ScoreComponentInputs(
            -FullyWarmInputs.OiBuildupNetZ, -FullyWarmInputs.PcrZ, -FullyWarmInputs.FuturesBasisZ,
            -FullyWarmInputs.IvSkewZ, -FullyWarmInputs.PriceMomentumZ, -FullyWarmInputs.DepthImbalanceZ);

        var positive = CompositeScoreCalculator.Calculate(FullyWarmInputs, ScoreWeights.Default, ComputedAt);
        var negative = CompositeScoreCalculator.Calculate(negated, ScoreWeights.Default, ComputedAt);

        Assert.Equal(-positive.Score!.Value, negative.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_ScoreStaysWithinBounds_EvenAtFullSaturation()
    {
        var maxedOut = new ScoreComponentInputs(3.0, 3.0, 3.0, 3.0, 3.0, 3.0);

        var result = CompositeScoreCalculator.Calculate(maxedOut, ScoreWeights.Default, ComputedAt);

        Assert.NotNull(result.Score);
        Assert.True(result.Score.Value < 100.0);
        Assert.True(result.Score.Value > 99.0); // should be close to saturated, e.g. ~99.5 at k=1
    }

    [Fact]
    public void Calculate_ClipsOutOfRangeZScores_BeforeWeighting()
    {
        // WelfordRollingWindow.ComputeZScore already clips to +/-3, but this method is
        // defensive: an out-of-range input should be clamped, not silently over-weighted.
        var withOutOfRangeZ = FullyWarmInputs with { PriceMomentumZ = 50.0 };
        var withClippedZ = FullyWarmInputs with { PriceMomentumZ = 3.0 };

        var actual = CompositeScoreCalculator.Calculate(withOutOfRangeZ, ScoreWeights.Default, ComputedAt);
        var expected = CompositeScoreCalculator.Calculate(withClippedZ, ScoreWeights.Default, ComputedAt);

        Assert.Equal(expected.Score!.Value, actual.Score!.Value, 1e-9);
    }

    [Fact]
    public void Calculate_PropagatesTheWeightSetVersion()
    {
        var weights = ScoreWeights.Default with { Version = "2026-09-03.custom" };

        var result = CompositeScoreCalculator.Calculate(FullyWarmInputs, weights, ComputedAt);

        Assert.Equal("2026-09-03.custom", result.WeightSetVersion);
    }

    [Fact]
    public void Calculate_ComponentBreakdown_ContainsAllSevenNamedComponents()
    {
        var result = CompositeScoreCalculator.Calculate(FullyWarmInputs, ScoreWeights.Default, ComputedAt);

        var names = result.Components.Select(c => c.Name).ToHashSet();
        Assert.Equal(7, result.Components.Count);
        Assert.Contains("OiBuildupNet", names);
        Assert.Contains("Pcr", names);
        Assert.Contains("FuturesBasis", names);
        Assert.Contains("IvSkew", names);
        Assert.Contains("PriceMomentum", names);
        Assert.Contains("DepthImbalance", names);
        Assert.Contains("VixChange", names);
    }

    [Fact]
    public void Calculate_StaysWarmedUp_WhenOnlyVixChangeZIsMissing()
    {
        // VixChangeZ is deliberately optional (2026-09-04) -- unlike the six required
        // components, its absence must not block the composite score.
        var inputs = FullyWarmInputs with { VixChangeZ = null };

        var result = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, ComputedAt);

        Assert.True(result.IsWarmedUp);
        Assert.NotNull(result.Score);
    }

    [Fact]
    public void Calculate_IncludesVixChangesWeightedContribution_WhenPresent()
    {
        var weights = ScoreWeights.Default;
        var inputs = FullyWarmInputs with { VixChangeZ = 1.5 };

        var result = CompositeScoreCalculator.Calculate(inputs, weights, ComputedAt, k: 1.0);

        var expectedRaw = (weights.OiBuildupNet * FullyWarmInputs.OiBuildupNetZ!.Value)
            + (weights.Pcr * FullyWarmInputs.PcrZ!.Value)
            + (weights.FuturesBasis * FullyWarmInputs.FuturesBasisZ!.Value)
            + (weights.IvSkew * FullyWarmInputs.IvSkewZ!.Value)
            + (weights.PriceMomentum * FullyWarmInputs.PriceMomentumZ!.Value)
            + (weights.DepthImbalance * FullyWarmInputs.DepthImbalanceZ!.Value)
            + (weights.VixChange * 1.5);
        var expectedScore = 100.0 * Math.Tanh(expectedRaw / 1.0);

        Assert.Equal(expectedScore, result.Score!.Value, 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_ThrowsForNonPositiveK(double k)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CompositeScoreCalculator.Calculate(FullyWarmInputs, ScoreWeights.Default, ComputedAt, k));
    }

    [Fact]
    public void ComputeRaw_MatchesTheWeightedSumInsideCalculate()
    {
        var raw = CompositeScoreCalculator.ComputeRaw(FullyWarmInputs, ScoreWeights.Default);
        var viaCalculate = CompositeScoreCalculator.Calculate(FullyWarmInputs, ScoreWeights.Default, ComputedAt, k: 1.0);

        Assert.NotNull(raw);
        // 100*tanh(raw/1) == score at k=1, so raw is recoverable from the score for this check.
        Assert.Equal(100.0 * Math.Tanh(raw!.Value), viaCalculate.Score!.Value, 1e-9);
    }

    [Fact]
    public void ComputeRaw_ReturnsNull_WhenAnyComponentIsNotWarmedUp()
    {
        var raw = CompositeScoreCalculator.ComputeRaw(FullyWarmInputs with { PcrZ = null }, ScoreWeights.Default);

        Assert.Null(raw);
    }

    [Fact]
    public void DefaultWeights_AreThePlansSection6TableRescaledForVix_AndSumToOne()
    {
        // Plan section 6's 25/20/15/15/15/10, each x0.95, plus a conservative 0.05 for the
        // new VixChange component (2026-09-04) -- see ScoreWeights.Default's own doc comment.
        var weights = ScoreWeights.Default;

        Assert.Equal(0.2375, weights.OiBuildupNet, 1e-9);
        Assert.Equal(0.19, weights.Pcr, 1e-9);
        Assert.Equal(0.1425, weights.FuturesBasis, 1e-9);
        Assert.Equal(0.1425, weights.IvSkew, 1e-9);
        Assert.Equal(0.1425, weights.PriceMomentum, 1e-9);
        Assert.Equal(0.095, weights.DepthImbalance, 1e-9);
        Assert.Equal(0.05, weights.VixChange, 1e-9);
        Assert.Equal(1.0, weights.Total, 1e-9);
    }
}
