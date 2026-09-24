using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-23, incremental-information/conditional analysis. Coverage for <see cref="ConditionalMovementAnalysis.ClassifyTercileBucket"/> -- the one new pure calculation this task adds (every return/tercile/permutation computation itself is reused, unchanged, from earlier tasks).</summary>
public sealed class ConditionalMovementAnalysisTests
{
    [Theory]
    [InlineData(1.0, 3.0, 7.0, "Low")]
    [InlineData(3.0, 3.0, 7.0, "Low")] // exactly at the boundary -- inclusive of Low.
    [InlineData(5.0, 3.0, 7.0, "Mid")]
    [InlineData(7.0, 3.0, 7.0, "High")] // exactly at the boundary -- inclusive of High.
    [InlineData(9.0, 3.0, 7.0, "High")]
    public void ClassifyTercileBucket_UsesInclusiveBoundaries(double value, double low, double high, string expected)
        => Assert.Equal(expected, ConditionalMovementAnalysis.ClassifyTercileBucket((decimal)value, (decimal)low, (decimal)high));

    [Fact]
    public void ClassifyTercileBucket_NeverRederivesItsOwnThresholds_JustClassifiesAgainstGivenOnes()
    {
        // Same value, different threshold pairs -- confirms the classification is purely a
        // function of the values passed in, not any hidden recomputation.
        Assert.Equal("Low", ConditionalMovementAnalysis.ClassifyTercileBucket(5m, low33: 10m, high67: 20m));
        Assert.Equal("High", ConditionalMovementAnalysis.ClassifyTercileBucket(5m, low33: 1m, high67: 2m));
    }
}
