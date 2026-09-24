using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-23, forensic-verification addendum. Coverage for <see cref="Vc0DteBehaviorSummary"/>'s per-(OptionType,SignalDirection) aggregation -- must never merge CE CrossDown with PE CrossUp, and must average forward returns over non-null readings only.</summary>
public sealed class Vc0DteBehaviorSummaryTests
{
    static Vc0DteBehaviorRecorder.ObservationRow Row(
        OptionType side, string direction, decimal? fwd1, decimal mfe = 5m, decimal mae = 2m,
        DateOnly? tradingDate = null, DateTimeOffset? signalTimestamp = null,
        decimal? futureFwd1 = null, decimal? futureFwd3 = null,
        decimal? preSignalOptionMove3 = null, decimal? preSignalFutureMove3 = null,
        int? eventBarsToMfe = null, int? eventBarsToMae = null,
        double fastWindowDurationMs = 3000, double slowWindowDurationMs = 10000) => new(
        0, tradingDate ?? new DateOnly(2026, 9, 8), 0, 0, signalTimestamp ?? DateTimeOffset.UtcNow, 25000m, side, 1.0, 1.0, direction, 100m,
        fwd1, null, null, null, null, mfe, mae, null, null,
        25000m,
        futureFwd1, null, futureFwd3, null, null,
        null, null, null, null, null,
        mfe, mae,
        preSignalOptionMove3, null, null,
        preSignalFutureMove3, null, null,
        eventBarsToMfe, eventBarsToMae,
        fastWindowDurationMs, slowWindowDurationMs);

    [Fact]
    public void Summarize_KeepsAllFourCombinationsSeparate_NeverMerges()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            Row(OptionType.Call, "Bullish", 1m),
            Row(OptionType.Call, "Bearish", 2m),
            Row(OptionType.Put, "Bullish", 3m),
            Row(OptionType.Put, "Bearish", 4m),
        };

        var summary = Vc0DteBehaviorSummary.Summarize(rows);

        Assert.Equal(4, summary.Count);
        Assert.All(summary, s => Assert.Equal(1, s.Count));
    }

    [Fact]
    public void Summarize_AveragesOnlyOverNonNullForwardReturns_NeverTreatsMissingAsZero()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            Row(OptionType.Call, "Bullish", 10m),
            Row(OptionType.Call, "Bullish", null), // forward window ran past the session -- must be excluded, not averaged as 0.
            Row(OptionType.Call, "Bullish", 20m),
        };

        var summary = Vc0DteBehaviorSummary.Summarize(rows);

        var ceBullish = Assert.Single(summary);
        Assert.Equal(3, ceBullish.Count);
        Assert.Equal(15m, ceBullish.AvgForwardReturn1); // (10+20)/2, NOT (10+0+20)/3.
    }

    [Fact]
    public void Summarize_AllNullForHorizon_ReturnsNullAverage_NeverZero()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow> { Row(OptionType.Put, "Bearish", null) };

        var summary = Vc0DteBehaviorSummary.Summarize(rows);

        Assert.Null(Assert.Single(summary).AvgForwardReturn1);
    }

    [Fact]
    public void Summarize_MedianIsTheTrueMedian_NotJustTheMean()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            Row(OptionType.Call, "Bullish", 1m), Row(OptionType.Call, "Bullish", 2m),
            Row(OptionType.Call, "Bullish", 3m), Row(OptionType.Call, "Bullish", 100m), // outlier
        };

        var summary = Assert.Single(Vc0DteBehaviorSummary.Summarize(rows));

        Assert.Equal(26.5m, summary.AvgForwardReturn1); // (1+2+3+100)/4
        Assert.Equal(2.5m, summary.MedianForwardReturn1); // (2+3)/2 -- the outlier doesn't dominate the median.
    }

    [Fact]
    public void Summarize_DayCount_CountsDistinctDaysNotObservations()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            Row(OptionType.Call, "Bullish", 1m, tradingDate: new DateOnly(2026, 9, 8)),
            Row(OptionType.Call, "Bullish", 2m, tradingDate: new DateOnly(2026, 9, 8)),
            Row(OptionType.Call, "Bullish", 3m, tradingDate: new DateOnly(2026, 9, 15)),
        };

        var summary = Assert.Single(Vc0DteBehaviorSummary.Summarize(rows));

        Assert.Equal(3, summary.Count);
        Assert.Equal(2, summary.DayCount);
    }

    [Fact]
    public void SummarizeByDay_ProducesOneRowPerDatePerSignalType()
    {
        var rows = new List<Vc0DteBehaviorRecorder.ObservationRow>
        {
            Row(OptionType.Call, "Bullish", 1m, tradingDate: new DateOnly(2026, 9, 8)),
            Row(OptionType.Call, "Bullish", 2m, tradingDate: new DateOnly(2026, 9, 15)),
        };

        var byDay = Vc0DteBehaviorSummary.SummarizeByDay(rows);

        Assert.Equal(2, byDay.Count);
        Assert.All(byDay, r => Assert.Equal(1, r.Count));
    }

    [Theory]
    [InlineData(9, 30, "09:15-10:00")]
    [InlineData(10, 30, "10:00-11:00")]
    [InlineData(14, 59, "14:00-15:00")]
    [InlineData(15, 5, "15:00-15:30")]
    public void SessionBucket_AssignsTheCorrectBucket(int hour, int minute, string expected)
    {
        var istOffset = TimeSpan.FromHours(5.5);
        var signalTimestamp = new DateTimeOffset(new DateTime(2026, 9, 8, hour, minute, 0), istOffset).ToUniversalTime();

        Assert.Equal(expected, Vc0DteBehaviorSummary.SessionBucket(signalTimestamp, istOffset));
    }

    [Fact]
    public void SpeedBucket_UsesTheDatasetsOwnQuartiles_NotAnInventedThreshold()
    {
        Assert.Equal("Fast (bottom quartile)", Vc0DteBehaviorSummary.SpeedBucket(1000, q1Ms: 2000, q3Ms: 8000));
        Assert.Equal("Normal (middle 50%)", Vc0DteBehaviorSummary.SpeedBucket(5000, q1Ms: 2000, q3Ms: 8000));
        Assert.Equal("Slow (top quartile)", Vc0DteBehaviorSummary.SpeedBucket(9000, q1Ms: 2000, q3Ms: 8000));
    }

    [Fact]
    public void ComputeSlowWindowQuartiles_MatchesNearestRankPercentiles()
    {
        var rows = Enumerable.Range(1, 10).Select(n => Row(OptionType.Call, "Bullish", 1m, slowWindowDurationMs: n * 1000)).ToList();

        var (q1, q3) = Vc0DteBehaviorSummary.ComputeSlowWindowQuartiles(rows);

        Assert.Equal(3000, q1);
        Assert.Equal(8000, q3);
    }
}
