using NiftySignal.Rules;

namespace NiftySignal.Tests.Rules;

public class ScoreSustainTrackerTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Observe_ReturnsZeroDurationAndCountOne_OnTheFirstQualifyingObservation()
    {
        var tracker = new ScoreSustainTracker();

        var sustained = tracker.Observe(Start, 60, minAbsScore: 55);

        Assert.Equal(TimeSpan.Zero, sustained.Duration);
        Assert.Equal(1, sustained.CadenceCount);
    }

    [Fact]
    public void Observe_AccumulatesDurationAndCadenceCount_WhileScoreKeepsQualifyingWithTheSameSign()
    {
        var tracker = new ScoreSustainTracker();

        tracker.Observe(Start, 60, minAbsScore: 55);
        tracker.Observe(Start.AddSeconds(15), 62, minAbsScore: 55);
        var sustained = tracker.Observe(Start.AddSeconds(45), 58, minAbsScore: 55);

        Assert.Equal(TimeSpan.FromSeconds(45), sustained.Duration);
        Assert.Equal(3, sustained.CadenceCount);
    }

    [Fact]
    public void Observe_Resets_WhenScoreDropsBelowThreshold()
    {
        var tracker = new ScoreSustainTracker();

        tracker.Observe(Start, 60, minAbsScore: 55);
        tracker.Observe(Start.AddSeconds(30), 40, minAbsScore: 55); // drops below threshold
        var sustained = tracker.Observe(Start.AddSeconds(35), 60, minAbsScore: 55); // qualifies again, fresh streak

        Assert.Equal(TimeSpan.Zero, sustained.Duration);
        Assert.Equal(1, sustained.CadenceCount);
    }

    [Fact]
    public void Observe_Resets_WhenScoreSignFlips_EvenIfStillAboveThreshold()
    {
        var tracker = new ScoreSustainTracker();

        tracker.Observe(Start, 60, minAbsScore: 55);
        var sustainedAfterFlip = tracker.Observe(Start.AddSeconds(20), -60, minAbsScore: 55);

        Assert.Equal(TimeSpan.Zero, sustainedAfterFlip.Duration);
        Assert.Equal(1, sustainedAfterFlip.CadenceCount);
    }

    [Fact]
    public void Observe_TracksTheNegativeStreak_SymmetricallyToThePositiveOne()
    {
        var tracker = new ScoreSustainTracker();

        tracker.Observe(Start, -60, minAbsScore: 55);
        var sustained = tracker.Observe(Start.AddSeconds(50), -65, minAbsScore: 55);

        Assert.Equal(TimeSpan.FromSeconds(50), sustained.Duration);
        Assert.Equal(2, sustained.CadenceCount);
    }

    [Fact]
    public void Observe_CadenceCountStaysLow_WhenElapsedTimeAloneWouldSatisfySustain_ButFewRealCadencesArrived()
    {
        // Audit finding F12 (2026-09-08), the exact scenario the cadence count exists for: a
        // feed stall lets elapsed wall-clock time climb past what 45s of real 15s-spaced
        // cadences would produce, while only 2 genuine observations actually arrived. Duration
        // alone would satisfy a 45s sustain requirement; CadenceCount correctly shows it
        // shouldn't. EntryRuleEvaluator requires both.
        var tracker = new ScoreSustainTracker();

        tracker.Observe(Start, 60, minAbsScore: 55);
        var sustained = tracker.Observe(Start.AddSeconds(50), 62, minAbsScore: 55);

        Assert.Equal(TimeSpan.FromSeconds(50), sustained.Duration);
        Assert.Equal(2, sustained.CadenceCount);
    }
}
