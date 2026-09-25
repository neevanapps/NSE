using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-24, coverage for <see cref="RollingVoteAndRunAnalysis"/>'s rolling A/B vote (causal,
/// backward-only, current event excluded) and the run-length-trigger mechanism (fires the instant
/// a run first reaches a given length, never re-fires for a longer run of the same pattern).
/// </summary>
public sealed class RollingVoteAndRunAnalysisTests
{
    static readonly DateOnly Day = new(2026, 9, 8);
    const string PatternA = RollingVoteAndRunAnalysis.PatternA;
    const string PatternB = RollingVoteAndRunAnalysis.PatternB;
    const string Other = "UnderlyingFlat_CEFlat_PEFlat";

    static RelationshipObservation Row(int eventId, string category, DateTimeOffset timestamp) => new(
        Day, eventId, timestamp, timestamp, 0,
        25000m, 25000m, 25000m, 25000m, 1300, 60000,
        SpotAvailable: false, SpotMissingData: false, null, null, null, null, null, 0,
        25000m,
        "CE-A", 100m, false, false, 1, false,
        "PE-A", 50m, false, false, 1, false,
        null, null, null, null,
        null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable",
        category);

    static DateTimeOffset T(int minutes, int seconds = 0) => new DateTimeOffset(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5)).AddMinutes(minutes).AddSeconds(seconds);

    [Fact]
    public void ComputeRollingVotes_ExcludesCurrentEvent_AndOnlyCountsStrictlyPriorRows()
    {
        // A at t=0, B at t=0:30 -- vote AT the B event (window 1 min) must see only the prior A, never itself.
        var rows = new List<RelationshipObservation> { Row(0, PatternA, T(0)), Row(1, PatternB, T(0, 30)) };

        var votes = RollingVoteAndRunAnalysis.ComputeRollingVotes(rows, windowMinutes: 1);

        Assert.Equal(0, votes[0].ACount + votes[0].BCount); // first event: nothing before it.
        Assert.Equal("TIE", votes[0].Winner);
        Assert.Equal(1, votes[1].ACount);
        Assert.Equal(0, votes[1].BCount);
        Assert.Equal("A", votes[1].Winner); // sees the PRIOR A, not the current B.
        Assert.Equal("B", votes[1].CurrentEventPattern);
    }

    [Fact]
    public void ComputeRollingVotes_DropsEventsOutsideTheWindow()
    {
        // A at t=0 (2 minutes before), A at t=2:00, B at t=2:05 -- with a 1-minute window the t=0 event must have aged out.
        var rows = new List<RelationshipObservation> { Row(0, PatternA, T(0)), Row(1, PatternA, T(2, 0)), Row(2, PatternB, T(2, 5)) };

        var votes = RollingVoteAndRunAnalysis.ComputeRollingVotes(rows, windowMinutes: 1);

        Assert.Equal(1, votes[2].ACount); // only the t=2:00 A is within 1 minute of t=2:05; the t=0 A has aged out.
        Assert.Equal(0, votes[2].BCount);
    }

    [Fact]
    public void ComputeRollingVotes_ComputesMarginAndDominanceCorrectly()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, PatternA, T(0)), Row(1, PatternA, T(0, 10)), Row(2, PatternA, T(0, 20)), Row(3, PatternA, T(0, 30)),
            Row(4, PatternB, T(0, 40)), Row(5, PatternA, T(0, 50)),
        };

        var votes = RollingVoteAndRunAnalysis.ComputeRollingVotes(rows, windowMinutes: 5);

        Assert.Equal(4, votes[5].ACount); // rows 0-3 all A, row 4 is B, all within 5 minutes.
        Assert.Equal(1, votes[5].BCount);
        Assert.Equal("A", votes[5].Winner);
        Assert.Equal(3, votes[5].AMinusB);
        Assert.Equal(3, votes[5].AbsoluteMargin);
        Assert.Equal(3.0 / 5.0, votes[5].Dominance, precision: 6);
    }

    [Fact]
    public void ComputeRollingVotes_TieWhenEqualCounts_IncludingZeroZero()
    {
        var rows = new List<RelationshipObservation> { Row(0, Other, T(0)) };
        var votes = RollingVoteAndRunAnalysis.ComputeRollingVotes(rows, windowMinutes: 1);
        Assert.Equal("TIE", votes[0].Winner);
        Assert.Equal(0.0, votes[0].Dominance);
    }

    [Fact]
    public void ComputeRunLengthTriggers_FiresAtEachReachedLength_ForARunOfSeven()
    {
        var rows = Enumerable.Range(0, 7).Select(i => Row(i, PatternA, T(0, i * 10))).ToList();

        var triggers = RollingVoteAndRunAnalysis.ComputeRunLengthTriggers(rows);

        // Reaches 1..6 (capped), never re-fires past 6 even though the run is 7 long.
        Assert.Equal([1, 2, 3, 4, 5, 6], triggers.Select(t => t.ReachedLength).ToArray());
        Assert.All(triggers, t => Assert.Equal(7, t.FinalRunLength));
        Assert.Equal(0, triggers[0].EventId); // reach=1 at the run's own first event.
        Assert.Equal(4, triggers[4].EventId); // reach=5 at the run's 5th event (index 4).
        Assert.Equal(5, triggers[5].EventId); // reach=6 at the run's 6th event (index 5) -- the 7th event never re-triggers.
    }

    [Fact]
    public void ComputeRunLengthTriggers_ShortRunOnlyReachesItsOwnFinalLength()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA, T(0)), Row(1, PatternA, T(0, 10)), Row(2, PatternA, T(0, 20)), Row(3, Other, T(0, 30)) };

        var triggers = RollingVoteAndRunAnalysis.ComputeRunLengthTriggers(rows);

        Assert.Equal([1, 2, 3], triggers.Select(t => t.ReachedLength).ToArray());
        Assert.All(triggers, t => Assert.Equal(3, t.FinalRunLength));
    }

    [Fact]
    public void ComputeRunLengthTriggers_CategoryChangeEndsTheRun_EvenAtoB()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA, T(0)), Row(1, PatternA, T(0, 10)), Row(2, PatternB, T(0, 20)) };

        var triggers = RollingVoteAndRunAnalysis.ComputeRunLengthTriggers(rows);

        Assert.Equal(2, triggers.Count(t => t.Pattern == PatternA));
        var bTrigger = Assert.Single(triggers, t => t.Pattern == PatternB);
        Assert.Equal(1, bTrigger.ReachedLength);
        Assert.Equal(2, bTrigger.EventId);
    }

    [Fact]
    public void ComputeRunLengthTriggers_NonPatternRowNeverContributesATrigger()
    {
        var rows = new List<RelationshipObservation> { Row(0, Other, T(0)), Row(1, Other, T(0, 10)) };
        var triggers = RollingVoteAndRunAnalysis.ComputeRunLengthTriggers(rows);
        Assert.Empty(triggers);
    }
}
