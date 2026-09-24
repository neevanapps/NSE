using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, descriptive research layer. Coverage for
/// <see cref="UnderlyingOptionRelationshipSummary"/>'s change calculators (CE/PE relative change,
/// forward-horizon null handling, contract-transition-aware windows) and aggregation (category
/// counts, crossover-context attachment, day audit).
/// </summary>
public sealed class UnderlyingOptionRelationshipSummaryTests
{
    static readonly DateOnly Day = new(2026, 9, 8);

    static RelationshipObservation Row(
        int eventId, decimal futuresClose, string ceToken, decimal? ceLtp, bool ceTransition, bool ceMissing,
        string peToken, decimal? peLtp, bool peTransition, bool peMissing, DateTimeOffset? start = null,
        decimal? futuresChange1 = null, decimal? ceChange1 = null, decimal? peChange1 = null) => new(
        Day, eventId, start ?? DateTimeOffset.UtcNow, (start ?? DateTimeOffset.UtcNow).AddMinutes(1), 0,
        futuresClose, futuresClose, futuresClose, futuresClose, 1300, 60000,
        SpotAvailable: false, SpotMissingData: false, null, null, null, null, null, 0,
        25000m,
        ceToken, ceLtp, ceMissing, false, ceMissing ? 0 : 1, ceTransition,
        peToken, peLtp, peMissing, false, peMissing ? 0 : 1, peTransition,
        futuresChange1, null, ceChange1, peChange1,
        null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable",
        "Test");

    [Fact]
    public void ComputeCeChange_SameContractBothEndpoints_ComputesChange()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25000, "CE-A", 110m, false, false, "PE-A", 45m, false, false),
        };

        var change = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, 0, 1);

        Assert.Equal(10m, change.AbsoluteChange);
        Assert.Equal(10m, change.PercentChange);
        Assert.True(change.SameContract);
        Assert.True(change.StartAvailable);
        Assert.True(change.EndAvailable);
    }

    [Fact]
    public void ComputeCeChange_ContractTransitionInsideWindow_ReturnsNull_NeverMixesContracts()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25000, "CE-B", 60m, true, false, "PE-A", 50m, false, false), // transitioned mid-window.
            Row(2, 25000, "CE-B", 65m, false, false, "PE-A", 50m, false, false),
        };

        var change = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, 0, 2);

        Assert.Null(change.AbsoluteChange);
        Assert.False(change.SameContract);
    }

    [Fact]
    public void ComputeCeChange_EndpointTokensDiffer_NotSameContract_EvenWithoutExplicitTransitionFlag()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25000, "CE-B", 60m, false, false, "PE-A", 50m, false, false), // token differs even if flag wasn't set.
        };

        var change = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, 0, 1);

        Assert.False(change.SameContract);
        Assert.Null(change.AbsoluteChange);
    }

    [Fact]
    public void ComputeCeChange_MissingAtEitherEndpoint_ReturnsNull_NeverZero()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", null, false, true, "PE-A", 50m, false, false),
            Row(1, 25000, "CE-A", 110m, false, false, "PE-A", 45m, false, false),
        };

        var change = UnderlyingOptionRelationshipSummary.ComputeCeChange(rows, 0, 1);

        Assert.Null(change.AbsoluteChange);
        Assert.False(change.StartAvailable);
        Assert.True(change.EndAvailable);
    }

    [Fact]
    public void ComputeFuturesChange_HorizonBeyondLastBar_ReturnsNull()
    {
        var rows = new List<RelationshipObservation> { Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false) };

        var change = UnderlyingOptionRelationshipSummary.ComputeFuturesChange(rows, 0, 10);

        Assert.Null(change.AbsoluteChange);
        Assert.False(change.EndAvailable);
    }

    [Fact]
    public void ComputeSpotChange_Unavailable_ReturnsNull_NeverSubstitutesFutures()
    {
        var rows = new List<RelationshipObservation> { Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false), Row(1, 25010, "CE-A", 105m, false, false, "PE-A", 48m, false, false) };

        var change = UnderlyingOptionRelationshipSummary.ComputeSpotChange(rows, 0, 1);

        Assert.Null(change.AbsoluteChange);
        Assert.False(change.StartAvailable);
    }

    [Fact]
    public void FindPattern_MatchesExactDirectionTriple_UnderlyingUpCeDownPeUp()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25010, "CE-A", 95m, false, false, "PE-A", 55m, false, false, futuresChange1: 10m, ceChange1: -5m, peChange1: 5m), // matches: Up/Down/Up
            Row(2, 25020, "CE-A", 90m, false, false, "PE-A", 60m, false, false, futuresChange1: 10m, ceChange1: -5m, peChange1: 5m), // also matches
        };

        var matches = UnderlyingOptionRelationshipSummary.FindPattern(rows, RelationshipDirection.Up, RelationshipDirection.Down, RelationshipDirection.Up);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void CountCategories_GroupsByExactLabel()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false) with { RelationshipCategory = "UnderlyingUp_CEDown_PEUp" },
            Row(1, 25010, "CE-A", 95m, false, false, "PE-A", 55m, false, false) with { RelationshipCategory = "UnderlyingUp_CEDown_PEUp" },
            Row(2, 25020, "CE-A", 90m, false, false, "PE-A", 60m, false, false) with { RelationshipCategory = "ContractTransition" },
        };

        var counts = UnderlyingOptionRelationshipSummary.CountCategories(rows);

        Assert.Equal(2, counts.Single(c => c.Category == "UnderlyingUp_CEDown_PEUp").Count);
        Assert.Equal(1, counts.Single(c => c.Category == "ContractTransition").Count);
    }

    [Fact]
    public void AuditByDay_CountsAvailabilityAndTransitionsPerDay()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25010, "CE-A", null, false, true, "PE-A", 55m, false, false),
            Row(2, 25020, "CE-B", 90m, true, false, "PE-A", 60m, false, false),
        };

        var audit = Assert.Single(UnderlyingOptionRelationshipSummary.AuditByDay(rows));

        Assert.Equal(3, audit.EventCount);
        Assert.Equal(2, audit.CeAvailable); // event1's CE was missing.
        Assert.Equal(3, audit.PeAvailable);
        Assert.Equal(1, audit.CeContractTransitions);
        Assert.Equal(0, audit.PeContractTransitions);
    }

    [Fact]
    public void AttachCrossoverContext_JoinsByDateAndEventId_SkipsDaysNotInDataset()
    {
        var rows = new List<RelationshipObservation>
        {
            Row(0, 25000, "CE-A", 100m, false, false, "PE-A", 50m, false, false),
            Row(1, 25010, "CE-A", 110m, false, false, "PE-A", 45m, false, false),
            Row(2, 25020, "CE-A", 120m, false, false, "PE-A", 40m, false, false),
        };
        var byDay = new Dictionary<DateOnly, List<RelationshipObservation>> { [Day] = rows };

        var crossovers = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            new(0, Day, 0, 1, DateTimeOffset.UtcNow, 25000m, OptionType.Call, 1.0, 1.0, "Bullish", 110m,
                null, null, null, null, null, 5m, 2m, null, null,
                25010m, null, null, null, null, null, null, null, null, null, null, 5m, 2m,
                null, null, null, null, null, null, null, null, 60000, 180000),
        };

        var context = UnderlyingOptionRelationshipSummary.AttachCrossoverContext(crossovers, byDay);

        var row = Assert.Single(context);
        Assert.Equal(1, row.EventId);
        // Post1 futures change from event1(25010) to event2(25020).
        Assert.Equal(10m, row.PostFutures1.AbsoluteChange);
    }
}
