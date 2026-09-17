using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CoreScoreTrendReversionTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observe_IsNull_BeforeAnyChangeHasBeenObserved()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        Assert.Null(tracker.Observe(Start, null));
    }

    [Fact]
    public void Observe_IsNegativeOne_ForASingleCleanPositiveMove()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        // net = path = 5 -- a perfectly clean, one-directional move -- so the ratio is 1.0,
        // negated to -1.0 (a clean recent trend reads as a REVERSION signal).
        var result = tracker.Observe(Start, 5.0);

        Assert.Equal(-1.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_IsZero_WhenOppositeMovesCancelButPathIsNonZero()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, 5.0);
        var result = tracker.Observe(Start.AddMinutes(1), -5.0);

        Assert.Equal(0.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_StillReadsExistingWindow_OnAQuietCadenceWithNoFreshChange()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, 5.0);
        // No fresh change this cadence -- must still read the window built up so far, not go null.
        var result = tracker.Observe(Start.AddMinutes(1), null);

        Assert.Equal(-1.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_EvictsEntriesOlderThanTheWindow_EvenOnAQuietCadence()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, 5.0);
        // 16 minutes later (past the 15-minute window), with no fresh change -- eviction must still
        // run, leaving the window empty.
        var result = tracker.Observe(Start.AddMinutes(16), null);

        Assert.Null(result);
    }

    [Fact]
    public void Observe_OnlyCountsEntriesStillInsideTheWindow_AfterPartialEviction()
    {
        var tracker = new CoreScoreTrendReversionTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, 10.0); // will be evicted
        tracker.Observe(Start.AddMinutes(10), 2.0); // still inside the window at t=16
        var result = tracker.Observe(Start.AddMinutes(16), null);

        Assert.Equal(-1.0, result!.Value, 1e-9);
    }
}
