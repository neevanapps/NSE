using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-23, forensic validation of the Pattern A/B forward-futures finding. Coverage for <see cref="ForensicValidationAnalysis"/>'s new calculators (percentiles, mean-contribution, deterministic permutation distribution).</summary>
public sealed class ForensicValidationAnalysisTests
{
    [Fact]
    public void ComputePercentiles_MatchesHandComputedValues()
    {
        var values = Enumerable.Range(1, 10).Select(n => (decimal)n).ToList(); // 1..10

        var result = ForensicValidationAnalysis.ComputePercentiles(values);

        Assert.Equal(10, result.N);
        Assert.Equal(1m, result.Min);
        Assert.Equal(10m, result.Max);
        Assert.Equal(5m, result.Median); // sorted 1..10, nearest-rank median -> 5th of 10.
    }

    [Fact]
    public void ComputePercentiles_EmptyInput_ReturnsZeroedResult_NeverThrows()
    {
        var result = ForensicValidationAnalysis.ComputePercentiles([]);
        Assert.Equal(0, result.N);
    }

    [Fact]
    public void ContributionOfLargestToTotal_OneDominantOutlier_ShowsNearFullContribution()
    {
        var values = new List<decimal> { 1m, 1m, 1m, 1m, 96m }; // outlier dominates the sum.

        var contribution = ForensicValidationAnalysis.ContributionOfLargestToTotal(values, topFraction: 0.2); // top 20% = 1 observation.

        Assert.Equal(96m, contribution); // 96/100 * 100.
    }

    [Fact]
    public void ContributionOfLargestToTotal_EvenlySpreadValues_ShowsProportionalContribution()
    {
        var values = Enumerable.Repeat(10m, 10).ToList(); // all equal -- no single value dominates.

        var contribution = ForensicValidationAnalysis.ContributionOfLargestToTotal(values, topFraction: 0.1); // top 10% = 1 of 10.

        Assert.Equal(10m, contribution); // 10/100 * 100 -- exactly its proportional share, no more.
    }

    [Fact]
    public void PermutationMedianDistribution_IsDeterministic_SameSeedProducesSameResult()
    {
        var population = Enumerable.Range(1, 100).Select(n => (decimal)n).ToList();

        var first = ForensicValidationAnalysis.PermutationMedianDistribution(population, sampleSize: 20, seed: 7, permutations: 500);
        var second = ForensicValidationAnalysis.PermutationMedianDistribution(population, sampleSize: 20, seed: 7, permutations: 500);

        Assert.Equal(first, second);
        Assert.Equal(500, first.Count);
    }

    [Fact]
    public void PermutationMedianDistribution_SampleSizeExceedsPopulation_ReturnsEmpty_NeverThrows()
    {
        var result = ForensicValidationAnalysis.PermutationMedianDistribution([1m, 2m], sampleSize: 10, seed: 1, permutations: 100);
        Assert.Empty(result);
    }

    [Fact]
    public void PercentileRankOf_ValueBelowEveryDraw_IsZero_ValueAboveEveryDraw_Is100()
    {
        var distribution = new List<decimal> { 1m, 2m, 3m, 4m, 5m };

        Assert.Equal(0.0, ForensicValidationAnalysis.PercentileRankOf(distribution, 0m));
        Assert.Equal(100.0, ForensicValidationAnalysis.PercentileRankOf(distribution, 100m));
        Assert.Equal(60.0, ForensicValidationAnalysis.PercentileRankOf(distribution, 3.5m)); // 3 of 5 values (1,2,3) are below 3.5.
    }
}
