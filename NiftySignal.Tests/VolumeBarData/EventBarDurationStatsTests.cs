using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-23, forensic-verification addendum. Coverage for <see cref="EventBarDurationStats"/>'s percentile/histogram arithmetic and its exclusion of the day's final partial bar.</summary>
public sealed class EventBarDurationStatsTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    static readonly DateTimeOffset T0 = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));

    static FutureEventBar Bar(int id, double durationMs, bool isFinalPartial = false) => new(
        id, AsOfDate, T0, T0 + TimeSpan.FromMilliseconds(durationMs), 100, 100, 100, 100, 1300, null, null, 1, isFinalPartial);

    [Fact]
    public void Compute_NoBars_ReturnsZeroedResult()
    {
        var result = EventBarDurationStats.Compute([]);
        Assert.Equal(0, result.BarCount);
        Assert.Empty(result.Histogram);
    }

    [Fact]
    public void Compute_ExcludesTheFinalPartialBar()
    {
        var bars = new List<FutureEventBar> { Bar(0, 1000), Bar(1, 2000), Bar(2, 999999, isFinalPartial: true) };

        var result = EventBarDurationStats.Compute(bars);

        Assert.Equal(2, result.BarCount);
        Assert.Equal(2000, result.MaxMs);
    }

    [Fact]
    public void Compute_MinAndMax_MatchTheActualExtremes()
    {
        var bars = new List<FutureEventBar> { Bar(0, 500), Bar(1, 15000), Bar(2, 3000) };

        var result = EventBarDurationStats.Compute(bars);

        Assert.Equal(500, result.MinMs);
        Assert.Equal(15000, result.MaxMs);
    }

    [Fact]
    public void Compute_Median_OfTenEvenlySpacedValues_IsAReasonableMidpoint()
    {
        var bars = Enumerable.Range(1, 10).Select(n => Bar(n, n * 1000)).ToList(); // 1000..10000 ms

        var result = EventBarDurationStats.Compute(bars);

        Assert.InRange(result.MedianMs, 5000, 6000);
    }

    [Fact]
    public void Compute_Histogram_BucketsSumToTotalBarCount()
    {
        var bars = new List<FutureEventBar>
        {
            Bar(0, 500), Bar(1, 1500), Bar(2, 3000), Bar(3, 8000), Bar(4, 20000),
            Bar(5, 45000), Bar(6, 90000), Bar(7, 200000), Bar(8, 400000),
        };

        var result = EventBarDurationStats.Compute(bars);

        Assert.Equal(bars.Count, result.Histogram.Sum(h => h.Count));
        Assert.Equal(1, result.Histogram.Single(h => h.Label == "<1 sec").Count);
        Assert.Equal(1, result.Histogram.Single(h => h.Label == ">5 min").Count);
    }
}
