using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class MarkToMarketDiagnosticsTests
{
    [Theory]
    [InlineData(30, "Group1 (<1 min)")]
    [InlineData(59, "Group1 (<1 min)")]
    [InlineData(60, "Group2 (1-2 min)")]
    [InlineData(119, "Group2 (1-2 min)")]
    [InlineData(120, "Group3 (2-5 min)")]
    [InlineData(299, "Group3 (2-5 min)")]
    [InlineData(300, "Group4 (>5 min)")]
    [InlineData(3600, "Group4 (>5 min)")]
    public void BucketByHoldingTimeCoarse_MatchesTheTasksOwnFourGroups(int seconds, string expected)
        => Assert.Equal(expected, MarkToMarketDiagnostics.BucketByHoldingTimeCoarse(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(-5, "MFE<0%")]
    [InlineData(0, "0-1%")]
    [InlineData(1, "0-1%")]
    [InlineData(1.5, "1-2%")]
    [InlineData(2, "1-2%")]
    [InlineData(3, "2-5%")]
    [InlineData(5, "2-5%")]
    [InlineData(7, "5-10%")]
    [InlineData(10, "5-10%")]
    [InlineData(15, ">10%")]
    public void BucketByMfePercent_MatchesTheTasksOwnSixBuckets(double mfePercent, string expected)
        => Assert.Equal(expected, MarkToMarketDiagnostics.BucketByMfePercent((decimal)mfePercent));

    [Theory]
    [InlineData(25000, 25000, "ATM/closest strike")]
    [InlineData(25050, 25000, "1 step away")]
    [InlineData(24900, 25000, "2 steps away")]
    [InlineData(25150, 25000, "3 steps away")]
    [InlineData(25300, 25000, "4+ steps away")]
    public void BucketByStrikeSteps_UsesTheGivenStepSize_NotAHardcodedConstant(decimal strike, decimal atm, string expected)
        => Assert.Equal(expected, MarkToMarketDiagnostics.BucketByStrikeSteps(strike, atm, strikeStepSize: 50m));

    [Fact]
    public void BucketByStrikeSteps_UnavailableStepSize_ReturnsUnavailable()
        => Assert.Equal("Unavailable", MarkToMarketDiagnostics.BucketByStrikeSteps(25100, 25000, strikeStepSize: 0m));

    [Fact]
    public void ComputeExcursionTiming_FindsFirstOccurrenceOfMaxAndMin()
    {
        var entry = new DateTimeOffset(2026, 9, 8, 9, 20, 0, TimeSpan.FromHours(5.5));
        var path = new List<(DateTimeOffset, decimal)>
        {
            (entry.AddSeconds(10), 105m),  // dips first
            (entry.AddSeconds(20), 95m),   // MAE here.
            (entry.AddSeconds(30), 120m),  // MFE here.
            (entry.AddSeconds(40), 110m),
        };

        var (timeToMfe, timeToMae) = MarkToMarketDiagnostics.ComputeExcursionTiming(entry, path);

        Assert.Equal(TimeSpan.FromSeconds(30), timeToMfe);
        Assert.Equal(TimeSpan.FromSeconds(20), timeToMae);
    }

    [Fact]
    public void ComputeExcursionTiming_EmptyPath_ReturnsNulls()
    {
        var entry = DateTimeOffset.UtcNow;
        var (timeToMfe, timeToMae) = MarkToMarketDiagnostics.ComputeExcursionTiming(entry, []);
        Assert.Null(timeToMfe);
        Assert.Null(timeToMae);
    }

    [Theory]
    [InlineData(500, 300, 200)]   // hypothetical better than actual -> positive opportunity cost.
    [InlineData(300, 500, -200)]  // hypothetical worse than actual -> negative opportunity cost.
    [InlineData(300, 300, 0)]
    public void ComputeOpportunityCost_IsHypotheticalMinusActual(decimal hypothetical, decimal actual, decimal expected)
        => Assert.Equal(expected, MarkToMarketDiagnostics.ComputeOpportunityCost(hypothetical, actual));

    [Fact]
    public void InferStrikeStepSize_ReturnsTheMinimumGapBetweenSortedStrikes()
        => Assert.Equal(50m, MarkToMarketDiagnostics.InferStrikeStepSize([24900m, 24950m, 25000m, 25100m]));

    [Fact]
    public void InferStrikeStepSize_FewerThanTwoStrikes_ReturnsZero()
        => Assert.Equal(0m, MarkToMarketDiagnostics.InferStrikeStepSize([25000m]));
}
