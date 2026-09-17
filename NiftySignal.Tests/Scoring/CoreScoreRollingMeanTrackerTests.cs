using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CoreScoreRollingMeanTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observe_IsNull_BeforeAnyValueHasBeenObserved()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        Assert.Null(tracker.Observe(Start, null));
    }

    [Fact]
    public void Observe_ReturnsTheValueItself_ForASingleObservation()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        var result = tracker.Observe(Start, 0.4);

        Assert.Equal(0.4, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_AveragesEveryValueStillInsideTheWindow()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        tracker.Observe(Start, 0.2);
        tracker.Observe(Start.AddMinutes(1), 0.6);
        var result = tracker.Observe(Start.AddMinutes(2), 0.4);

        Assert.Equal(0.4, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_StillReadsExistingWindow_OnAQuietCadenceWithNoFreshValue()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        tracker.Observe(Start, 0.5);
        // No fresh value this cadence -- must still read the window built up so far, not go null.
        var result = tracker.Observe(Start.AddMinutes(1), null);

        Assert.Equal(0.5, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_EvictsEntriesOlderThanTheWindow_EvenOnAQuietCadence()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        tracker.Observe(Start, 0.5);
        // 11 minutes later (past the 10-minute window), with no fresh value -- eviction must still
        // run, leaving the window empty.
        var result = tracker.Observe(Start.AddMinutes(11), null);

        Assert.Null(result);
    }

    [Fact]
    public void Observe_OnlyCountsEntriesStillInsideTheWindow_AfterPartialEviction()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        tracker.Observe(Start, 1.0); // will be evicted
        tracker.Observe(Start.AddMinutes(6), 0.2); // still inside the window at t=11
        var result = tracker.Observe(Start.AddMinutes(11), null);

        Assert.Equal(0.2, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_SkipsNullCadences_RatherThanZeroFillingTheAverage()
    {
        var tracker = new CoreScoreRollingMeanTracker(TimeSpan.FromMinutes(10));

        tracker.Observe(Start, 1.0);
        tracker.Observe(Start.AddMinutes(1), null); // must not drag the average toward 0
        var result = tracker.Observe(Start.AddMinutes(2), 1.0);

        Assert.Equal(1.0, result!.Value, 1e-9);
    }
}
