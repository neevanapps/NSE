using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public class VolumeBarTrendReversionTrackerTests
{
    [Fact]
    public void Observe_IsNull_BeforeAnyChangeHasBeenObserved()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 15);

        Assert.Null(tracker.Observe(null));
    }

    [Fact]
    public void Observe_IsNegativeOne_ForASingleCleanPositiveMove()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 15);

        // net = path = 5 -- a perfectly clean, one-directional move -- so the ratio is 1.0,
        // negated to -1.0 (a clean recent trend reads as a REVERSION signal), same convention as
        // CoreScoreTrendReversionTracker.
        var result = tracker.Observe(5.0);

        Assert.Equal(-1.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_IsZero_WhenOppositeMovesCancelButPathIsNonZero()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 15);

        tracker.Observe(5.0);
        var result = tracker.Observe(-5.0);

        Assert.Equal(0.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_StillReadsExistingWindow_OnABarWithNoFreshChange()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 15);

        tracker.Observe(5.0);
        // No fresh change this bar (e.g. the series' first bar has no predecessor) -- must still
        // read the window built up so far, not go null.
        var result = tracker.Observe(null);

        Assert.Equal(-1.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_EvictsTheOldestEntry_OnceWindowBarsIsExceeded()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 2);

        tracker.Observe(10.0); // will be evicted once a 3rd change arrives
        tracker.Observe(2.0);
        // Window is now full (2 bars): [10.0, 2.0]. A 3rd change evicts the oldest (10.0),
        // leaving [2.0, -2.0] -- net = 0, path = 4 -- ratio 0, negated is still 0.
        var result = tracker.Observe(-2.0);

        Assert.Equal(0.0, result!.Value, 1e-9);
    }

    [Fact]
    public void Observe_OnlyCountsEntriesStillInsideTheWindow_AfterEviction()
    {
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 1);

        tracker.Observe(10.0); // evicted once the 2nd change arrives (window holds only 1 bar)
        var result = tracker.Observe(2.0);

        Assert.Equal(-1.0, result!.Value, 1e-9); // window = [2.0] only -- clean move, negated
    }

    [Fact]
    public void Observe_NullChange_DoesNotEvictAnything_UnlikeTheTimeWindowedVersion()
    {
        // Deliberately different from CoreScoreTrendReversionTracker: eviction there is driven by
        // wall-clock age, so it runs even on a null-change cadence. Here eviction is driven purely
        // by how many REAL changes have been enqueued, so a null observation is a true no-op.
        var tracker = new VolumeBarTrendReversionTracker(windowBars: 1);

        tracker.Observe(5.0);
        var result = tracker.Observe(null);

        Assert.Equal(-1.0, result!.Value, 1e-9); // unchanged -- the single real entry is still there
    }
}
