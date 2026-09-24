using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class EpisodeTransitionAnalysisTests
{
    [Theory]
    [InlineData(2.5, true, 2.5)]   // Pattern A (Up-defining): raw change already in the defining direction, unchanged.
    [InlineData(2.5, false, -2.5)] // Pattern B (Down-defining): sign flipped so "positive" still means "in the defining direction".
    [InlineData(-1.0, true, -1.0)]
    [InlineData(-1.0, false, 1.0)]
    public void OrientToDefiningDirection_FlipsSignOnlyForPatternB(decimal raw, bool patternIsUp, decimal expected)
        => Assert.Equal(expected, EpisodeTransitionAnalysis.OrientToDefiningDirection(raw, patternIsUp));

    [Fact]
    public void OrientToDefiningDirection_PropagatesNull()
        => Assert.Null(EpisodeTransitionAnalysis.OrientToDefiningDirection(null, true));

    [Theory]
    [InlineData(2.5, true, -2.5)]  // Pattern A's reversal direction is DOWN -- flip sign so positive = reversed.
    [InlineData(2.5, false, 2.5)]  // Pattern B's reversal direction is UP -- raw change already reversal-oriented.
    [InlineData(-1.0, true, 1.0)]
    [InlineData(-1.0, false, -1.0)]
    public void OrientToOpposingDirection_FlipsSignOnlyForPatternA(decimal raw, bool patternIsUp, decimal expected)
        => Assert.Equal(expected, EpisodeTransitionAnalysis.OrientToOpposingDirection(raw, patternIsUp));

    [Fact]
    public void OrientToOpposingDirection_PropagatesNull()
        => Assert.Null(EpisodeTransitionAnalysis.OrientToOpposingDirection(null, false));

    [Theory]
    [InlineData(1.0, -1.0, "Divergent")]
    [InlineData(-1.0, 1.0, "Divergent")]
    [InlineData(1.0, 1.0, "Aligned")]
    [InlineData(-1.0, -1.0, "Aligned")]
    [InlineData(0.0, 1.0, "Flat")]
    [InlineData(1.0, 0.0, "Flat")]
    [InlineData(null, 1.0, "Unavailable")]
    [InlineData(1.0, null, "Unavailable")]
    public void ClassifyCePeDivergence_ReusesExistingDirectionClassification(double? ce, double? pe, string expected)
        => Assert.Equal(expected, EpisodeTransitionAnalysis.ClassifyCePeDivergence((decimal?)ce, (decimal?)pe));
}
