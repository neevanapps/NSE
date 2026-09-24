using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class TradeLifecycleDiagnosticsTests
{
    [Theory]
    [InlineData(30, "<1 min")]
    [InlineData(59, "<1 min")]
    [InlineData(60, "1-2 min")]
    [InlineData(119, "1-2 min")]
    [InlineData(120, "2-5 min")]
    [InlineData(299, "2-5 min")]
    [InlineData(300, "5-10 min")]
    [InlineData(599, "5-10 min")]
    [InlineData(600, "10-15 min")]
    [InlineData(899, "10-15 min")]
    [InlineData(900, "15-30 min")]
    [InlineData(1799, "15-30 min")]
    [InlineData(1800, "30-60 min")]
    [InlineData(3599, "30-60 min")]
    [InlineData(3600, ">=60 min")]
    [InlineData(7200, ">=60 min")]
    public void BucketHoldingTime_MatchesTheTasksOwnEightBuckets(int seconds, string expectedBucket)
        => Assert.Equal(expectedBucket, TradeLifecycleDiagnostics.BucketHoldingTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CountAlternationsBefore_CountsFlipsStrictlyBeforeTheGivenIndex()
    {
        var patterns = new[] { "PatternA", "PatternB", "PatternA", "PatternA", "PatternB" };

        Assert.Equal(0, TradeLifecycleDiagnostics.CountAlternationsBefore(patterns, 0));
        Assert.Equal(0, TradeLifecycleDiagnostics.CountAlternationsBefore(patterns, 1)); // only index 0 exists before index 1 -- no pair yet.
        Assert.Equal(1, TradeLifecycleDiagnostics.CountAlternationsBefore(patterns, 2)); // A->B counted.
        Assert.Equal(2, TradeLifecycleDiagnostics.CountAlternationsBefore(patterns, 3)); // A->B, B->A.
        Assert.Equal(2, TradeLifecycleDiagnostics.CountAlternationsBefore(patterns, 4)); // A->A adds nothing.
    }

    [Fact]
    public void FindContainingEpisode_ReturnsTheEpisodeWhoseRangeContainsTheEventId()
    {
        var day = new DateOnly(2026, 9, 8);
        var now = DateTimeOffset.UtcNow;
        var episodes = new List<EpisodeAnalysis.Episode>
        {
            new(day, 0, "PatternA", 0, 2, now, now, 3, 100, 100, null, null, null, null, null, null, false, null, 25000, "CE", "PE", false),
            new(day, 1, "PatternA", 5, 5, now, now, 1, 100, 100, null, null, null, null, null, null, false, null, 25000, "CE", "PE", false),
        };

        Assert.Equal(0, TradeLifecycleDiagnostics.FindContainingEpisode(episodes, 1)!.EpisodeId);
        Assert.Equal(1, TradeLifecycleDiagnostics.FindContainingEpisode(episodes, 5)!.EpisodeId);
        Assert.Null(TradeLifecycleDiagnostics.FindContainingEpisode(episodes, 3));
    }

    [Fact]
    public void CountNGram_CountsContiguousSubsequenceOccurrences()
    {
        var patterns = new[] { "PatternA", "PatternB", "PatternA", "PatternB", "PatternA" };

        Assert.Equal(2, TradeLifecycleDiagnostics.CountNGram(patterns, ["PatternA", "PatternB"]));
        Assert.Equal(2, TradeLifecycleDiagnostics.CountNGram(patterns, ["PatternB", "PatternA"]));
        Assert.Equal(1, TradeLifecycleDiagnostics.CountNGram(patterns, ["PatternA", "PatternB", "PatternA", "PatternB"]));
        Assert.Equal(0, TradeLifecycleDiagnostics.CountNGram(patterns, ["PatternB", "PatternB"]));
    }

    [Fact]
    public void CountNGram_EmptyOrTooLong_ReturnsZero()
    {
        var patterns = new[] { "PatternA" };
        Assert.Equal(0, TradeLifecycleDiagnostics.CountNGram(patterns, ["PatternA", "PatternB"]));
        Assert.Equal(0, TradeLifecycleDiagnostics.CountNGram(patterns, []));
    }
}
