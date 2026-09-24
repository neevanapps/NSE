using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, episode-level/cluster-level validation. Coverage for
/// <see cref="EpisodeAnalysis.DetectEpisodes"/>'s start/continuation/termination/gap/transition
/// handling, using hand-built synthetic <see cref="RelationshipObservation"/> sequences.
/// </summary>
public sealed class EpisodeAnalysisTests
{
    static readonly DateOnly Day = new(2026, 9, 8);
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string Other = "UnderlyingDown_CEDown_PEUp";

    static RelationshipObservation Row(int eventId, string category, bool ceTransition = false, bool peTransition = false, decimal futuresClose = 25000m, decimal? cePrice = 100m, decimal? pePrice = 50m, string ceToken = "CE-A", string peToken = "PE-A") => new(
        Day, eventId, new DateTimeOffset(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5)).AddSeconds(eventId), new DateTimeOffset(2026, 9, 8, 9, 15, 1, TimeSpan.FromHours(5.5)).AddSeconds(eventId), 0,
        futuresClose, futuresClose, futuresClose, futuresClose, 1300, 60000,
        SpotAvailable: false, SpotMissingData: false, null, null, null, null, null, 0,
        25000m,
        ceToken, cePrice, cePrice is null, false, cePrice is null ? 0 : 1, ceTransition,
        peToken, pePrice, pePrice is null, false, pePrice is null ? 0 : 1, peTransition,
        null, null, null, null,
        null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable",
        category);

    static List<Vc0DteBehaviorRecorder.ObservationRow> NoCrossovers => [];

    [Fact]
    public void DetectEpisodes_SingleRun_IsOneEpisode()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA), Row(1, PatternA), Row(2, PatternA), Row(3, PatternA) };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        var episode = Assert.Single(episodes);
        Assert.Equal(0, episode.StartEventId);
        Assert.Equal(3, episode.EndEventId);
        Assert.Equal(4, episode.EventCount);
    }

    [Fact]
    public void DetectEpisodes_MatchesTheSpecsOwnWorkedExample()
    {
        // A A A A B B A A invalid A A  (using "Other" as a stand-in for B/invalid's own distinct categories)
        var rows = new List<RelationshipObservation>
        {
            Row(0, PatternA), Row(1, PatternA), Row(2, PatternA), Row(3, PatternA),
            Row(4, Other), Row(5, Other),
            Row(6, PatternA), Row(7, PatternA),
            Row(8, "OptionDataIncomplete"),
            Row(9, PatternA), Row(10, PatternA),
        };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        Assert.Equal(3, episodes.Count); // A#1 (0-3), A#2 (6-7), A#3 (9-10) -- NOT bridged across Other/invalid.
        Assert.Equal((0, 3), (episodes[0].StartEventId, episodes[0].EndEventId));
        Assert.Equal((6, 7), (episodes[1].StartEventId, episodes[1].EndEventId));
        Assert.Equal((9, 10), (episodes[2].StartEventId, episodes[2].EndEventId));
    }

    [Fact]
    public void DetectEpisodes_RelationshipChange_EndsTheEpisode()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA), Row(1, PatternA), Row(2, Other) };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        var episode = Assert.Single(episodes);
        Assert.Equal(1, episode.EndEventId); // event2 (Other) is NOT included.
    }

    [Fact]
    public void DetectEpisodes_InvalidDataGap_EndsTheEpisode_NeverBridged()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA), Row(1, "OptionDataIncomplete"), Row(2, PatternA) };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        Assert.Equal(2, episodes.Count); // two separate single-event episodes, not one bridged across the gap.
        Assert.Equal(0, episodes[0].StartEventId);
        Assert.Equal(2, episodes[1].StartEventId);
    }

    [Fact]
    public void DetectEpisodes_ContractTransitionEvent_CategoryIsNeverThePattern_AlwaysBreaksContinuity()
    {
        // A transitioned event is classified "ContractTransition" by the (unmodified) recorder,
        // never as the pattern itself -- confirming this always ends an episode, by construction.
        var rows = new List<RelationshipObservation> { Row(0, PatternA), Row(1, "ContractTransition", ceTransition: true), Row(2, PatternA) };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        Assert.Equal(2, episodes.Count);
        Assert.False(episodes[0].AtmTransitionDuringEpisode);
        Assert.False(episodes[1].AtmTransitionDuringEpisode);
    }

    [Fact]
    public void DetectEpisodes_EpisodeStillOpenAtDayEnd_IsClosedAtTheLastRow()
    {
        var rows = new List<RelationshipObservation> { Row(0, Other), Row(1, PatternA), Row(2, PatternA) };

        var episodes = EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers);

        var episode = Assert.Single(episodes);
        Assert.Equal(2, episode.EndEventId);
    }

    [Fact]
    public void DetectEpisodes_NoMatchingRows_ReturnsEmpty()
    {
        var rows = new List<RelationshipObservation> { Row(0, Other), Row(1, Other) };

        Assert.Empty(EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers));
    }

    [Fact]
    public void DetectEpisodes_StartAndEndPricesMatchTheFirstAndLastEventOfTheEpisode()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA, futuresClose: 25000m, cePrice: 100m, pePrice: 50m), Row(1, PatternA, futuresClose: 25010m, cePrice: 95m, pePrice: 55m) };

        var episode = Assert.Single(EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers));

        Assert.Equal(25000m, episode.StartFuturesPrice);
        Assert.Equal(25010m, episode.EndFuturesPrice);
        Assert.Equal(100m, episode.StartCePrice);
        Assert.Equal(95m, episode.EndCePrice);
        Assert.Equal(50m, episode.StartPePrice);
        Assert.Equal(55m, episode.EndPePrice);
    }

    [Fact]
    public void DetectEpisodes_BeganAfterCrossover_MatchesAnExistingUnmodifiedCrossoverObservation()
    {
        var rows = new List<RelationshipObservation> { Row(0, Other), Row(1, PatternA), Row(2, PatternA) };
        var crossovers = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            new(0, Day, 0, 1, DateTimeOffset.UtcNow, 25000m, OptionType.Call, 1.0, 1.0, "Bullish", 100m,
                null, null, null, null, null, 5m, 2m, null, null,
                25010m, null, null, null, null, null, null, null, null, null, null, 5m, 2m,
                null, null, null, null, null, null, null, null, 60000, 180000),
        };

        var episode = Assert.Single(EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, crossovers));

        Assert.True(episode.BeganAfterCrossover);
        Assert.Equal("Call Bullish", episode.CrossoverType);
    }

    [Fact]
    public void DetectEpisodes_NoMatchingCrossover_BeganAfterCrossoverIsFalse()
    {
        var rows = new List<RelationshipObservation> { Row(0, PatternA) };

        var episode = Assert.Single(EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers));

        Assert.False(episode.BeganAfterCrossover);
        Assert.Null(episode.CrossoverType);
    }

    [Theory]
    [InlineData(1, "1 event")]
    [InlineData(2, "2-3 events")]
    [InlineData(3, "2-3 events")]
    [InlineData(4, "4-10 events")]
    [InlineData(10, "4-10 events")]
    [InlineData(11, ">10 events")]
    public void DurationBucket_ClassifiesEventCountDeterministically(int eventCount, string expected)
        => Assert.Equal(expected, EpisodeAnalysis.DurationBucket(eventCount));

    [Fact]
    public void DetectEpisodes_FirstEventOfEachEpisode_IsDeterministicallyTheSignalPoint()
    {
        // Section 5's own requirement: the FIRST event of the episode is the signal point, never
        // the last or the "best" one. Confirmed here: StartEventId always equals the lowest
        // EventId in that contiguous run, regardless of how the run's own prices moved.
        var rows = new List<RelationshipObservation>
        {
            Row(0, PatternA, futuresClose: 25000m), Row(1, PatternA, futuresClose: 24900m), Row(2, PatternA, futuresClose: 25100m),
        };

        var episode = Assert.Single(EpisodeAnalysis.DetectEpisodes(Day, rows, PatternA, NoCrossovers));

        Assert.Equal(0, episode.StartEventId);
        Assert.Equal(25000m, episode.StartFuturesPrice); // the FIRST event's own price, not the min/max/best one.
    }
}
