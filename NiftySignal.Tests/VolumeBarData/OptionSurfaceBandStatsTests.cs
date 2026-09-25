using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public class OptionSurfaceBandStatsTests
{
    [Fact]
    public void Compute_AllNull_ReturnsZeroValidAndNullStats()
    {
        var result = OptionSurfaceBandStats.Compute([null, null, null]);

        Assert.Equal(0, result.Valid);
        Assert.Null(result.Median);
        Assert.Null(result.Iqr);
    }

    [Fact]
    public void Compute_OddCount_MedianIsMiddleValue()
    {
        var result = OptionSurfaceBandStats.Compute([1m, 3m, 5m]);

        Assert.Equal(3m, result.Median);
        Assert.Equal(3, result.Valid);
    }

    [Fact]
    public void Compute_EvenCount_MedianIsAverageOfMiddleTwo()
    {
        var result = OptionSurfaceBandStats.Compute([1m, 2m, 3m, 4m]);

        Assert.Equal(2.5m, result.Median);
    }

    [Fact]
    public void Compute_NullsAreExcludedFromValidCountAndStats()
    {
        var result = OptionSurfaceBandStats.Compute([null, 2m, null, 4m, 6m]);

        Assert.Equal(3, result.Valid);
        Assert.Equal(4m, result.Median);
        Assert.Equal(2m, result.Min);
        Assert.Equal(6m, result.Max);
    }

    [Fact]
    public void Compute_PosNegZeroCounts_ClassifyEachValueCorrectly()
    {
        var result = OptionSurfaceBandStats.Compute([-2m, -1m, 0m, 1m, 2m]);

        Assert.Equal(2, result.Pos);
        Assert.Equal(2, result.Neg);
        Assert.Equal(1, result.Zero);
        Assert.Equal(5, result.Valid);
    }

    [Fact]
    public void Compute_Iqr_IsSpreadBetween25thAnd75thPercentile()
    {
        var result = OptionSurfaceBandStats.Compute([1m, 2m, 3m, 4m, 5m]);

        // 25th pct index = ceil(0.25*5)-1 = 1 -> value 2; 75th pct index = ceil(0.75*5)-1 = 3 -> value 4.
        Assert.Equal(2m, result.Iqr);
    }
}
