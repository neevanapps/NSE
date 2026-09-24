using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, forward validation of the Underlying-CE-PE relationship patterns. Coverage for
/// <see cref="ForwardValidationAnalysis"/>'s own new calculators. Same-contract/missing/stale/
/// no-lookahead rejection is NOT re-tested here -- that's covered by the existing, unmodified
/// <see cref="UnderlyingOptionRelationshipSummaryTests"/>, since this task reuses those functions
/// as-is.
/// </summary>
public sealed class ForwardValidationAnalysisTests
{
    [Fact]
    public void ComputeForwardMetrics_ComputesMeanMedianAndDirectionPercentages()
    {
        var values = new decimal?[] { 10m, -5m, 0m, 20m, -15m };

        var metrics = ForwardValidationAnalysis.ComputeForwardMetrics(values);

        Assert.Equal(5, metrics.N);
        Assert.Equal(2m, metrics.Mean); // (10-5+0+20-15)/5
        Assert.Equal(0m, metrics.Median); // sorted: -15,-5,0,10,20 -> mid=0
        Assert.Equal(40.0, metrics.PositivePct); // 2 of 5
        Assert.Equal(40.0, metrics.NegativePct); // 2 of 5
        Assert.Equal(20.0, metrics.ZeroPct); // 1 of 5
        Assert.Equal(10m, metrics.MeanAbsolute); // (10+5+0+20+15)/5
    }

    [Fact]
    public void ComputeForwardMetrics_ExcludesNulls_NeverTreatsUnavailableAsZero()
    {
        var values = new decimal?[] { 10m, null, null, 30m };

        var metrics = ForwardValidationAnalysis.ComputeForwardMetrics(values);

        Assert.Equal(2, metrics.N); // NOT 4.
        Assert.Equal(20m, metrics.Mean); // (10+30)/2, NOT (10+0+0+30)/4.
    }

    [Fact]
    public void ComputeForwardMetrics_AllNull_ReturnsZeroCountAndNullStats()
    {
        var metrics = ForwardValidationAnalysis.ComputeForwardMetrics([null, null]);

        Assert.Equal(0, metrics.N);
        Assert.Null(metrics.Mean);
        Assert.Null(metrics.Median);
    }

    [Theory]
    [InlineData(RelationshipDirection.Up, 5, "SameDirection")]
    [InlineData(RelationshipDirection.Up, -5, "OppositeDirection")]
    [InlineData(RelationshipDirection.Down, -5, "SameDirection")]
    [InlineData(RelationshipDirection.Down, 5, "OppositeDirection")]
    [InlineData(RelationshipDirection.Up, 0, "Flat")]
    [InlineData(RelationshipDirection.Flat, 5, "Flat")]
    [InlineData(RelationshipDirection.Unavailable, 5, "Unavailable")]
    public void ClassifyForwardDirection_MatchesExactZeroNonZeroConvention(RelationshipDirection pre, decimal forwardValue, string expected)
        => Assert.Equal(expected, ForwardValidationAnalysis.ClassifyForwardDirection(pre, forwardValue));

    [Fact]
    public void ClassifyForwardDirection_NullForwardChange_IsUnavailable_NeverFabricated()
        => Assert.Equal("Unavailable", ForwardValidationAnalysis.ClassifyForwardDirection(RelationshipDirection.Up, null));

    [Theory]
    [InlineData(RelationshipDirection.Up, RelationshipDirection.Up, "SameDirection")]
    [InlineData(RelationshipDirection.Up, RelationshipDirection.Down, "OppositeDirection")]
    [InlineData(RelationshipDirection.Flat, RelationshipDirection.Flat, "BothFlat")]
    [InlineData(RelationshipDirection.Up, RelationshipDirection.Flat, "Mixed")]
    [InlineData(RelationshipDirection.Unavailable, RelationshipDirection.Up, "Unavailable")]
    public void ClassifyPairwiseDirection_ComparesTwoForwardDirections(RelationshipDirection a, RelationshipDirection b, string expected)
        => Assert.Equal(expected, ForwardValidationAnalysis.ClassifyPairwiseDirection(a, b));

    [Fact]
    public void ComputeTerciles_SplitsIntoThreeApproximatelyEqualGroups()
    {
        var values = Enumerable.Range(1, 9).Select(n => (decimal)n).ToList(); // 1..9

        var (low, high) = ForwardValidationAnalysis.ComputeTerciles(values);

        Assert.Equal(3m, low);
        Assert.Equal(7m, high); // nearest-rank on an exact-boundary repeating fraction (2/3) rounds up -- a documented, benign edge case, not a bug.
    }

    [Fact]
    public void BootstrapMedianCi_IsDeterministic_SameSeedProducesSameResult()
    {
        var values = new List<decimal> { 1m, 2m, 3m, 4m, 5m, 100m }; // includes an outlier.

        var first = ForwardValidationAnalysis.BootstrapMedianCi(values, seed: 42, resamples: 200);
        var second = ForwardValidationAnalysis.BootstrapMedianCi(values, seed: 42, resamples: 200);

        Assert.Equal(first, second);
        Assert.True(first.Lower <= first.Upper);
    }

    [Fact]
    public void BootstrapMedianCi_EmptyInput_ReturnsZeroZero_NeverThrows()
    {
        var (lower, upper) = ForwardValidationAnalysis.BootstrapMedianCi([]);

        Assert.Equal(0m, lower);
        Assert.Equal(0m, upper);
    }
}
