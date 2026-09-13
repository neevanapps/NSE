using NiftySignal.Backtest;

namespace NiftySignal.Tests.Backtest;

public sealed class SessionRankTrackerTests
{
    [Fact]
    public void Rank_IsZero_BeforeAnyValueHasBeenAdded()
    {
        var tracker = new SessionRankTracker();
        Assert.Equal(0, tracker.Rank(50));
    }

    [Fact]
    public void ValueAtPercentile_IsNull_BeforeAnyValueHasBeenAdded()
    {
        var tracker = new SessionRankTracker();
        Assert.Null(tracker.ValueAtPercentile(90));
    }

    [Fact]
    public void Rank_Returns100_ForTheOnlyValueAddedSoFar()
    {
        var tracker = new SessionRankTracker();
        tracker.Add(42);
        Assert.Equal(100, tracker.Rank(42));
    }

    [Fact]
    public void Rank_ReflectsPositionWithinTenEvenlySpacedValues()
    {
        var tracker = new SessionRankTracker();
        for (var i = 1; i <= 10; i++)
        {
            tracker.Add(i);
        }

        // The smallest value (1) is <= itself only -> 1/10 = 10th percentile.
        Assert.Equal(10, tracker.Rank(1));
        // The largest value (10) is <= itself and everything smaller -> 100th percentile.
        Assert.Equal(100, tracker.Rank(10));
        // The median-ish value (5) has exactly 5 of 10 values <= it -> 50th percentile.
        Assert.Equal(50, tracker.Rank(5));
    }

    [Fact]
    public void ValueAtPercentile_IsTheInverseOfRank_ForAKnownDistribution()
    {
        var tracker = new SessionRankTracker();
        for (var i = 1; i <= 10; i++)
        {
            tracker.Add(i);
        }

        // p0 (0th percentile, linear interpolation convention) is the minimum.
        Assert.Equal(1, tracker.ValueAtPercentile(0));
        // p100 is the maximum.
        Assert.Equal(10, tracker.ValueAtPercentile(100));
        // p50 of 1..10 (linear interpolation) sits halfway between the 5th and 6th values.
        Assert.Equal(5.5, tracker.ValueAtPercentile(50));
    }

    [Fact]
    public void Rank_AdaptsAutomatically_AsMoreExtremeValuesAreAddedLater()
    {
        // The core "dynamic" property this whole tracker exists for: the same raw value's rank
        // changes as the session's own realized range widens -- unlike a fixed score threshold,
        // there's no number here that becomes wrong when the regime shifts mid-session.
        var tracker = new SessionRankTracker();
        tracker.Add(10);
        tracker.Add(20);
        tracker.Add(30);

        var rankBefore = tracker.Rank(20); // 2 of 3 values <= 20 -> ~66.7th percentile

        tracker.Add(100);
        tracker.Add(200);
        tracker.Add(300);

        var rankAfter = tracker.Rank(20); // now only 2 of 6 values <= 20 -> ~33.3rd percentile

        Assert.True(rankAfter < rankBefore, $"Rank of 20 should have dropped once larger values joined the session (before={rankBefore:F1}, after={rankAfter:F1}).");
    }
}
