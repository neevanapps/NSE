using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-24, Pattern A selectivity/frequency calibration. Coverage for
/// <see cref="PatternASelectivityCalibration"/>'s own two pure calculators -- the no-look-ahead
/// candidate-value computation and the descending-percentile threshold. The data pipeline that
/// wires these into a full date-range run lives in the CLI command
/// (vc0dte-relationship-a-selectivity-calibration) and is not separately unit-tested, consistent
/// with every other vc0dte-relationship-* research command in this project.
/// </summary>
public sealed class PatternASelectivityCalibrationTests
{
    static readonly DateOnly Day = new(2026, 9, 8);

    static RelationshipObservation Row(
        int eventId, decimal futuresClose = 25000m, decimal? cePrice = 100m, decimal? pePrice = 50m,
        bool ceTransition = false, bool peTransition = false, string ceToken = "CE-A", string peToken = "PE-A") => new(
        Day, eventId, new DateTimeOffset(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5)).AddSeconds(eventId), new DateTimeOffset(2026, 9, 8, 9, 15, 1, TimeSpan.FromHours(5.5)).AddSeconds(eventId), 0,
        futuresClose, futuresClose, futuresClose, futuresClose, 1300, 60000,
        SpotAvailable: false, SpotMissingData: false, null, null, null, null, null, 0,
        25000m,
        ceToken, cePrice, cePrice is null, false, cePrice is null ? 0 : 1, ceTransition,
        peToken, pePrice, pePrice is null, false, pePrice is null ? 0 : 1, peTransition,
        null, null, null, null,
        null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable",
        "UnderlyingUp_CEDown_PEUp");

    static PriceCrossoverEngine.Step Step(double? fast, double? slow) => new(fast, slow, null, false, false);

    [Fact]
    public void ComputeValue_UnderlyingMoveMagnitude_UsesAbsoluteOneEventFuturesPercentChange()
    {
        var rows = new List<RelationshipObservation> { Row(0, futuresClose: 25000m), Row(1, futuresClose: 24950m) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude, rows, 1, []);

        Assert.Equal(0.2m, value); // |(24950-25000)/25000*100| = 0.2%
    }

    [Fact]
    public void ComputeValue_CeReactionMagnitude_UsesAbsoluteOneEventCePercentChange()
    {
        var rows = new List<RelationshipObservation> { Row(0, cePrice: 100m), Row(1, cePrice: 90m) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.CeReactionMagnitude, rows, 1, []);

        Assert.Equal(10m, value); // |(90-100)/100*100| = 10%
    }

    [Fact]
    public void ComputeValue_PeReactionMagnitude_UsesAbsoluteOneEventPePercentChange()
    {
        var rows = new List<RelationshipObservation> { Row(0, pePrice: 50m), Row(1, pePrice: 55m) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.PeReactionMagnitude, rows, 1, []);

        Assert.Equal(10m, value); // |(55-50)/50*100| = 10%
    }

    [Fact]
    public void ComputeValue_DivergenceMagnitude_IsAbsoluteDifferenceOfCeAndPePercentChanges()
    {
        var rows = new List<RelationshipObservation> { Row(0, cePrice: 100m, pePrice: 50m), Row(1, cePrice: 90m, pePrice: 55m) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.DivergenceMagnitude, rows, 1, []);

        Assert.Equal(20m, value); // |CE%-PE%| = |-10 - 10| = 20
    }

    [Fact]
    public void ComputeValue_CrossoverDistance_UsesAbsoluteFastMinusSlowFromTheTrace()
    {
        var rows = new List<RelationshipObservation> { Row(0), Row(1) };
        var trace = new List<PriceCrossoverEngine.Step?> { null, Step(-0.4, -0.1) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.CrossoverDistance, rows, 1, trace);

        Assert.Equal(0.3m, value!.Value, 10);
    }

    [Fact]
    public void ComputeValue_CrossoverDistance_StillWarmingUp_IsNullNotFabricated()
    {
        var rows = new List<RelationshipObservation> { Row(0), Row(1) };
        var trace = new List<PriceCrossoverEngine.Step?> { null, Step(null, null) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.CrossoverDistance, rows, 1, trace);

        Assert.Null(value);
    }

    [Fact]
    public void ComputeValue_CrossoverDistance_TraceShorterThanRows_IsNullNotOutOfRange()
    {
        var rows = new List<RelationshipObservation> { Row(0), Row(1) };
        var trace = new List<PriceCrossoverEngine.Step?> { null };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.CrossoverDistance, rows, 1, trace);

        Assert.Null(value);
    }

    [Theory]
    [InlineData(PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude)]
    [InlineData(PatternASelectivityCalibration.StrengthVariable.CeReactionMagnitude)]
    [InlineData(PatternASelectivityCalibration.StrengthVariable.PeReactionMagnitude)]
    [InlineData(PatternASelectivityCalibration.StrengthVariable.DivergenceMagnitude)]
    public void ComputeValue_MissingOptionDataAtEitherEndpoint_IsNullNotFabricated(PatternASelectivityCalibration.StrengthVariable variable)
    {
        var rows = new List<RelationshipObservation> { Row(0, cePrice: null, pePrice: null), Row(1) };

        var value = PatternASelectivityCalibration.ComputeValue(variable, rows, 1, []);

        // UnderlyingMoveMagnitude never depends on option data -- confirm it stays defined while the others go null.
        if (variable == PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude)
        {
            Assert.NotNull(value);
        }
        else
        {
            Assert.Null(value);
        }
    }

    [Fact]
    public void ComputeValue_ContractTransition_IsNullNotAttributedToMovement()
    {
        var rows = new List<RelationshipObservation> { Row(0, ceToken: "CE-A"), Row(1, ceToken: "CE-B", ceTransition: true) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.CeReactionMagnitude, rows, 1, []);

        Assert.Null(value);
    }

    [Fact]
    public void ComputeValue_EventZero_HasNoPriorEvent_IsNullNotFabricated()
    {
        var rows = new List<RelationshipObservation> { Row(0) };

        var value = PatternASelectivityCalibration.ComputeValue(
            PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude, rows, 0, []);

        Assert.Null(value);
    }

    [Fact]
    public void ComputeValue_EventIdOutOfRange_IsNullNotThrow()
    {
        var rows = new List<RelationshipObservation> { Row(0) };

        Assert.Null(PatternASelectivityCalibration.ComputeValue(PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude, rows, 5, []));
        Assert.Null(PatternASelectivityCalibration.ComputeValue(PatternASelectivityCalibration.StrengthVariable.UnderlyingMoveMagnitude, rows, -1, []));
    }

    [Fact]
    public void PercentileThreshold_AllObservations_ReturnsThePopulationMinimum()
    {
        var values = new List<decimal> { 5m, 1m, 3m, 2m, 4m };

        Assert.Equal(1m, PatternASelectivityCalibration.PercentileThreshold(values, 1.00m));
    }

    [Fact]
    public void PercentileThreshold_Top20Percent_KeepsExactlyTheTopNearestRankCount()
    {
        // 10 values -> top 20% keeps 2 -> threshold is the 2nd-largest value.
        var values = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();

        var threshold = PatternASelectivityCalibration.PercentileThreshold(values, 0.20m);

        Assert.Equal(9m, threshold);
        Assert.Equal(2, values.Count(v => v >= threshold));
    }

    [Fact]
    public void PercentileThreshold_FractionalKeepCount_RoundsUpNeverDown()
    {
        // 7 values, top 30% -> ceil(0.3*7)=3 -> threshold is the 3rd-largest value, never fewer than 3 kept.
        var values = Enumerable.Range(1, 7).Select(i => (decimal)i).ToList();

        var threshold = PatternASelectivityCalibration.PercentileThreshold(values, 0.30m);

        Assert.Equal(5m, threshold);
        Assert.Equal(3, values.Count(v => v >= threshold));
    }

    [Fact]
    public void PercentileThreshold_VerySmallFraction_StillKeepsAtLeastOne()
    {
        var values = new List<decimal> { 1m, 2m, 3m };

        var threshold = PatternASelectivityCalibration.PercentileThreshold(values, 0.05m);

        Assert.Equal(3m, threshold);
    }

    [Fact]
    public void PercentileThreshold_EmptyList_ReturnsZero_NotThrow()
        => Assert.Equal(0m, PatternASelectivityCalibration.PercentileThreshold([], 0.10m));

    [Fact]
    public void PercentileThreshold_TiesAtTheCutoff_NeverAdmitFewerThanTheFraction()
    {
        // 5 values, top 20% -> keepCount=1, but the largest value is duplicated -- both must qualify
        // (threshold-based selection, not a hard rank cut that would arbitrarily pick one of the ties).
        var values = new List<decimal> { 10m, 10m, 5m, 3m, 1m };

        var threshold = PatternASelectivityCalibration.PercentileThreshold(values, 0.20m);

        Assert.Equal(10m, threshold);
        Assert.Equal(2, values.Count(v => v >= threshold));
    }
}
