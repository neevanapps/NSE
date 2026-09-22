using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

/// <summary>Mirrors NiftySignal.Tests/Backtest/SessionRankTrackerTests.cs exactly -- this is a deliberate duplicate of that class (see SessionRankTracker.cs's own doc comment), so its behavior gets the same coverage in its new home.</summary>
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

        Assert.Equal(10, tracker.Rank(1));
        Assert.Equal(100, tracker.Rank(10));
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

        Assert.Equal(1, tracker.ValueAtPercentile(0));
        Assert.Equal(10, tracker.ValueAtPercentile(100));
        Assert.Equal(5.5, tracker.ValueAtPercentile(50));
    }

    [Fact]
    public void Rank_AdaptsAutomatically_AsMoreExtremeValuesAreAddedLater()
    {
        var tracker = new SessionRankTracker();
        tracker.Add(10);
        tracker.Add(20);
        tracker.Add(30);

        var rankBefore = tracker.Rank(20);

        tracker.Add(100);
        tracker.Add(200);
        tracker.Add(300);

        var rankAfter = tracker.Rank(20);

        Assert.True(rankAfter < rankBefore, $"Rank of 20 should have dropped once larger values joined the session (before={rankBefore:F1}, after={rankAfter:F1}).");
    }
}
