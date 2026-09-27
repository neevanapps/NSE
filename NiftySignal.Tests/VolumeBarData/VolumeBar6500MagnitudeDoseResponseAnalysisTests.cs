using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500MagnitudeDoseResponseAnalysisTests
{
    [Theory]
    [InlineData(0.00, 1)]
    [InlineData(0.20, 1)]
    [InlineData(0.2000001, 2)]
    [InlineData(0.40, 2)]
    [InlineData(0.4000001, 3)]
    [InlineData(0.60, 3)]
    [InlineData(0.6000001, 4)]
    [InlineData(0.80, 4)]
    [InlineData(0.8000001, 5)]
    [InlineData(1.00, 5)]
    public void MagnitudeQuintile_UsesFrozenTwentyPercentBoundaries(double percentile, int expected)
    {
        Assert.Equal(
            expected,
            VolumeBar6500MagnitudeDoseResponseAnalysis.MagnitudeQuintile(percentile));
    }

    [Fact]
    public void MagnitudeQuintile_RejectsInvalidPercentile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeBar6500MagnitudeDoseResponseAnalysis.MagnitudeQuintile(-0.01));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeBar6500MagnitudeDoseResponseAnalysis.MagnitudeQuintile(1.01));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeBar6500MagnitudeDoseResponseAnalysis.MagnitudeQuintile(double.NaN));
    }

    [Fact]
    public void SpearmanAcrossQuintiles_IsPositiveForMonotonicImprovement()
    {
        double?[] values = [0.10, 0.20, 0.30, 0.40, 0.50];

        var rho = VolumeBar6500MagnitudeDoseResponseAnalysis.SpearmanAcrossQuintiles(values);

        Assert.NotNull(rho);
        Assert.Equal(1.0, rho!.Value, 12);
    }

    [Fact]
    public void SpearmanAcrossQuintiles_IsNegativeForMonotonicDeterioration()
    {
        double?[] values = [5, 4, 3, 2, 1];

        var rho = VolumeBar6500MagnitudeDoseResponseAnalysis.SpearmanAcrossQuintiles(values);

        Assert.NotNull(rho);
        Assert.Equal(-1.0, rho!.Value, 12);
    }

    [Fact]
    public void SpearmanAcrossQuintiles_RequiresAtLeastThreeObservedBuckets()
    {
        double?[] values = [1, null, null, null, 5];

        var rho = VolumeBar6500MagnitudeDoseResponseAnalysis.SpearmanAcrossQuintiles(values);

        Assert.Null(rho);
    }

    [Fact]
    public void CountAdjacentImprovements_DoesNotSkipAcrossMissingBuckets()
    {
        double?[] values = [1, 2, null, 4, 3];

        var result = VolumeBar6500MagnitudeDoseResponseAnalysis.CountAdjacentImprovements(values);

        Assert.Equal(1, result.Improvements);
        Assert.Equal(2, result.Comparisons);
    }

    [Fact]
    public void Analyze_AlwaysProducesFiveFrozenBucketsForEveryCandidateAndHorizon()
    {
        var translation = new VolumeBar6500OptionTranslationAnalysis.AnalysisResult(
            [],
            [],
            [],
            [],
            []);

        var result = VolumeBar6500MagnitudeDoseResponseAnalysis.Analyze([], translation);

        Assert.Equal(
            VolumeBar6500OptionTranslationAnalysis.Candidates.Length *
            VolumeBar6500OptionTranslationAnalysis.Horizons.Length *
            5,
            result.Buckets.Count);

        foreach (var candidate in VolumeBar6500OptionTranslationAnalysis.Candidates)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var buckets = result.Buckets
                    .Where(x =>
                        x.Candidate == candidate.Name &&
                        x.HorizonBars == horizon.Bars)
                    .OrderBy(x => x.MagnitudeQuintile)
                    .ToArray();

                Assert.Equal([1, 2, 3, 4, 5], buckets.Select(x => x.MagnitudeQuintile).ToArray());
            }
        }
    }
}
