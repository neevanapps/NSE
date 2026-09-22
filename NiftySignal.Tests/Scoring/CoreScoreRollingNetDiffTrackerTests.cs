using NiftySignal.Scoring;

namespace NiftySignal.Tests.Scoring;

public class CoreScoreRollingNetDiffTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 17, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observe_ReturnsThisCadencesOwnDiff_OnTheFirstCall()
    {
        var tracker = new CoreScoreRollingNetDiffTracker(TimeSpan.FromMinutes(15));

        var result = tracker.Observe(Start, call: 100, put: 40);

        Assert.Equal(60.0, result, 1e-9);
    }

    [Fact]
    public void Observe_AccumulatesAcrossCadencesStillInsideTheWindow()
    {
        var tracker = new CoreScoreRollingNetDiffTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, call: 100, put: 40); // net +60
        var result = tracker.Observe(Start.AddMinutes(5), call: 20, put: 50); // net -30

        Assert.Equal(60.0 - 30.0, result, 1e-9);
    }

    [Fact]
    public void Observe_EvictsEntriesOlderThanTheWindow()
    {
        var tracker = new CoreScoreRollingNetDiffTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, call: 100, put: 40); // net +60, will fall outside the window
        var result = tracker.Observe(Start.AddMinutes(16), call: 10, put: 10); // net 0, inside

        Assert.Equal(0.0, result, 1e-9);
    }

    [Fact]
    public void Observe_RecordsAZeroCadence_UnconditionallyUnlikeTheTrendReversionTracker()
    {
        var tracker = new CoreScoreRollingNetDiffTracker(TimeSpan.FromMinutes(15));

        tracker.Observe(Start, call: 100, put: 40); // net +60
        // A cadence with no OI movement at all still gets enqueued (unlike TrendReversion15m's
        // conditional enqueue) -- it just contributes a net-zero entry, not "nothing happened".
        var result = tracker.Observe(Start.AddMinutes(1), call: 0, put: 0);

        Assert.Equal(60.0, result, 1e-9);
    }
}
