using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public class RatioMetricMathTests
{
    const double RMax = 2.0;

    [Fact]
    public void ClipLogRatio_ReturnsZero_WhenRatioIsExactlyOne()
    {
        Assert.Equal(0.0, RatioMetricMath.ClipLogRatio(1.0, RMax)!.Value, 1e-9);
    }

    [Fact]
    public void ClipLogRatio_ReturnsOne_WhenRatioEqualsRMax()
    {
        Assert.Equal(1.0, RatioMetricMath.ClipLogRatio(RMax, RMax)!.Value, 1e-9);
    }

    [Fact]
    public void ClipLogRatio_ReturnsNegativeOne_WhenRatioEqualsOneOverRMax()
    {
        Assert.Equal(-1.0, RatioMetricMath.ClipLogRatio(1.0 / RMax, RMax)!.Value, 1e-9);
    }

    [Fact]
    public void ClipLogRatio_StaysClampedAtOne_WellBeyondRMax()
    {
        Assert.Equal(1.0, RatioMetricMath.ClipLogRatio(RMax * 100, RMax)!.Value, 1e-9);
    }

    [Fact]
    public void ClipLogRatio_StaysClampedAtNegativeOne_WellBelowOneOverRMax()
    {
        Assert.Equal(-1.0, RatioMetricMath.ClipLogRatio(1.0 / (RMax * 100), RMax)!.Value, 1e-9);
    }

    [Fact]
    public void ClipLogRatio_ReturnsNull_WhenRatioIsNull()
    {
        Assert.Null(RatioMetricMath.ClipLogRatio(null, RMax));
    }

    [Fact]
    public void ClipLogRatio_ReturnsNull_WhenRatioIsZero()
    {
        Assert.Null(RatioMetricMath.ClipLogRatio(0.0, RMax));
    }

    [Fact]
    public void ClipLogRatio_ReturnsNull_WhenRatioIsNegative()
    {
        Assert.Null(RatioMetricMath.ClipLogRatio(-0.5, RMax));
    }

    const double Scale = 6.0;

    [Fact]
    public void ClipScaledDifference_ReturnsZero_WhenDiffIsZero()
    {
        Assert.Equal(0.0, RatioMetricMath.ClipScaledDifference(0.0, Scale)!.Value, 1e-9);
    }

    [Fact]
    public void ClipScaledDifference_ReturnsOne_WhenDiffEqualsScale()
    {
        Assert.Equal(1.0, RatioMetricMath.ClipScaledDifference(Scale, Scale)!.Value, 1e-9);
    }

    [Fact]
    public void ClipScaledDifference_ReturnsNegativeOne_WhenDiffEqualsNegativeScale()
    {
        Assert.Equal(-1.0, RatioMetricMath.ClipScaledDifference(-Scale, Scale)!.Value, 1e-9);
    }

    [Fact]
    public void ClipScaledDifference_StaysClampedAtOne_WellBeyondScale()
    {
        Assert.Equal(1.0, RatioMetricMath.ClipScaledDifference(Scale * 100, Scale)!.Value, 1e-9);
    }

    [Fact]
    public void ClipScaledDifference_ComputesTheExactFraction_WithinBounds()
    {
        Assert.Equal(0.5, RatioMetricMath.ClipScaledDifference(Scale / 2, Scale)!.Value, 1e-9);
    }

    [Fact]
    public void ClipScaledDifference_ReturnsNull_WhenDiffIsNull()
    {
        Assert.Null(RatioMetricMath.ClipScaledDifference(null, Scale));
    }
}
