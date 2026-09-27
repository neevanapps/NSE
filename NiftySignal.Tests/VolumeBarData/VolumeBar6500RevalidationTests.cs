using NiftySignal.Domain.ValueObjects;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500RevalidationTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void BuildBars_WholeFeedUpdateNoCarry_DoesNotPropagateOvershoot()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 1_000, depth),
            Tick(2, 1, 101m, 5_000, depth),
            Tick(3, 2, 102m, 9_000, depth),
            Tick(4, 3, 103m, 13_000, depth),
            Tick(5, 4, 104m, 17_000, depth),
        };

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);

        Assert.Equal(2, bars.Count);
        Assert.All(bars, b => Assert.False(b.IsFinalPartialBar));

        var first = bars[0];
        Assert.Equal(0, first.BarIndex);
        Assert.Equal(100m, first.Open);
        Assert.Equal(102m, first.Close);
        Assert.Equal(3, first.FeedUpdateCount);
        Assert.Equal(8_000L, first.ObservedVolume);
        Assert.Equal(1_500L, first.OvershootVolume!.Value);
        Assert.Equal(1L, first.FirstUpdateId);
        Assert.Equal(3L, first.LastUpdateId);

        var second = bars[1];
        Assert.Equal(103m, second.Open);
        Assert.Equal(104m, second.Close);
        Assert.Equal(2, second.FeedUpdateCount);
        Assert.Equal(8_000L, second.ObservedVolume);
        Assert.Equal(1_500L, second.OvershootVolume!.Value);
        Assert.Equal(4L, second.FirstUpdateId);
        Assert.Equal(5L, second.LastUpdateId);

        Assert.NotEqual(first.LastUpdateId, second.FirstUpdateId);
    }

    [Fact]
    public void BuildBars_LargeSampledVolumeJump_ClosesOnlyOneBar()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 0, depth),
            Tick(2, 1, 101m, 30_000, depth),
            Tick(3, 2, 102m, 31_000, depth),
            Tick(4, 3, 103m, 36_500, depth),
        };

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);

        Assert.Equal(2, bars.Count);
        Assert.False(bars[0].IsFinalPartialBar);
        Assert.False(bars[1].IsFinalPartialBar);

        Assert.Equal(30_000L, bars[0].ObservedVolume);
        Assert.Equal(23_500L, bars[0].OvershootVolume!.Value);
        Assert.Equal(2, bars[0].FeedUpdateCount);

        Assert.Equal(6_500L, bars[1].ObservedVolume);
        Assert.Equal(0L, bars[1].OvershootVolume!.Value);
        Assert.Equal(2, bars[1].FeedUpdateCount);
        Assert.Equal(3L, bars[1].FirstUpdateId);
    }

    [Fact]
    public void BuildBars_ComputesDepthTobAndOfiFromRawFeedUpdateSequence()
    {
        var firstDepth = Depth(bid1Qty: 10, ask1Qty: 5, deeperBidQty: 10, deeperAskQty: 5);
        var secondDepth = Depth(bid1Qty: 10, ask1Qty: 20, deeperBidQty: 100, deeperAskQty: 1);

        var ticks = new[]
        {
            Tick(1, 0, 100m, 0, firstDepth),
            Tick(2, 1, 101m, 6_500, secondDepth),
        };

        var bar = Assert.Single(VolumeBar6500Revalidation.BuildBars(ticks));

        var firstDepthRatio = (50.0 - 25.0) / (50.0 + 25.0);
        var secondDepthRatio = (410.0 - 24.0) / (410.0 + 24.0);
        var expectedDepth = (firstDepthRatio + secondDepthRatio) / 2.0;

        var firstTob = (10.0 - 5.0) / (10.0 + 5.0);
        var secondTob = (10.0 - 20.0) / (10.0 + 20.0);
        var expectedTob = (firstTob + secondTob) / 2.0;

        Assert.Equal(expectedDepth, bar.DepthImbalance!.Value, 10);
        Assert.Equal(expectedTob, bar.TopOfBookImbalance!.Value, 10);
        Assert.Equal(expectedDepth - expectedTob, bar.TobDepthDivergence!.Value, 10);

        Assert.Equal(-15.0, bar.OrderFlowImbalance!.Value, 10);
        Assert.Equal(2, bar.DepthUpdateCount);
    }

    [Fact]
    public void BuildBars_MatchesExistingVolumeBarBuilder6500()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 0, depth),
            Tick(2, 1, 100.5m, 3_500, depth),
            Tick(3, 2, 101m, 7_000, depth),
            Tick(4, 3, 100.75m, 9_000, depth),
            Tick(5, 4, 101.25m, 13_750, depth),
            Tick(6, 5, 101.5m, 15_000, depth),
        };

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);
        var parity = VolumeBar6500Revalidation.CompareWithExistingVolumeBarBuilder(ticks, bars);

        Assert.Equal(0, parity.MismatchCount);
        Assert.Null(parity.FirstMismatch);
        Assert.Equal(bars.Count, parity.ComparedBarCount);
    }

    [Fact]
    public void BuildObservations_UsesLaterCompletedBarsAndReportsActualForwardObservedVolume()
    {
        var depth = Depth();
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 0, depth),
        };

        for (var i = 1; i <= 5; i++)
        {
            ticks.Add(Tick(i + 1, i, 100m + i, 6_500L * i, depth));
        }

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);
        var observations = VolumeBar6500Revalidation.BuildObservations(new DateOnly(2026, 9, 8), 0, bars);

        Assert.Equal(5, observations.Count);

        var first = observations[0];
        Assert.Equal(101m, first.FuturesClose);
        Assert.Equal(1.0, first.Forward1Points!.Value);
        Assert.Equal(2.0, first.Forward2Points!.Value);
        Assert.Equal(4.0, first.Forward4Points!.Value);
        Assert.Equal(6_500L, first.Forward1ObservedVolume!.Value);
        Assert.Equal(13_000L, first.Forward2ObservedVolume!.Value);
        Assert.Equal(26_000L, first.Forward4ObservedVolume!.Value);
        Assert.Equal(4.0, first.MaxUp4Points!.Value);
        Assert.Equal(0.0, first.MaxDown4Points!.Value);

        var last = observations[^1];
        Assert.Null(last.Forward1Points);
        Assert.Null(last.Forward2Points);
        Assert.Null(last.Forward4Points);
        Assert.Null(last.Forward1ObservedVolume);
    }

    [Fact]
    public void BuildBars_NegativeCumulativeVolumeDelta_FailsInsteadOfClamping()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 10_000, depth),
            Tick(2, 1, 101m, 9_000, depth),
        };

        var ex = Assert.Throws<InvalidDataException>(() => VolumeBar6500Revalidation.BuildBars(ticks));
        Assert.Contains("Negative cumulative-volume delta", ex.Message);
    }

    [Fact]
    public void BuildBars_FinalPartialBar_IsExplicitAndExcludedFromForwardSeries()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 0, depth),
            Tick(2, 1, 101m, 6_500, depth),
            Tick(3, 2, 102m, 9_000, depth),
        };

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);

        Assert.Equal(2, bars.Count);
        Assert.False(bars[0].IsFinalPartialBar);
        Assert.True(bars[1].IsFinalPartialBar);
        Assert.Equal(2_500L, bars[1].ObservedVolume);
        Assert.Null(bars[1].OvershootVolume);

        var observations = VolumeBar6500Revalidation.BuildObservations(new DateOnly(2026, 9, 8), 0, bars);
        Assert.Single(observations);
        Assert.Null(observations[0].Forward1Points);
    }

    [Fact]
    public void PrimaryResearchDates_ExcludesIncompleteSeptember4BeforeMetricInspection()
    {
        Assert.DoesNotContain(new DateOnly(2026, 9, 4), VolumeBar6500Revalidation.PrimaryResearchDates);
        Assert.Equal(9, VolumeBar6500Revalidation.PrimaryResearchDates.Length);
    }

    [Fact]
    public void SummarizeOvershoot_UsesCompletedBarsOnly()
    {
        var depth = Depth();
        var ticks = new[]
        {
            Tick(1, 0, 100m, 0, depth),
            Tick(2, 1, 101m, 7_000, depth),
            Tick(3, 2, 102m, 17_000, depth),
            Tick(4, 3, 103m, 30_000, depth),
            Tick(5, 4, 104m, 31_000, depth),
        };

        var bars = VolumeBar6500Revalidation.BuildBars(ticks);
        var summary = VolumeBar6500Revalidation.SummarizeOvershoot(bars);

        Assert.Equal(3, summary.FullBarCount);
        Assert.Equal(3_500L, summary.MedianOvershoot);
        Assert.Equal(6_500L, summary.MaxOvershoot);
        Assert.Equal(13_000L, summary.MaxObservedVolume);
        Assert.Equal(2, summary.BarsAtLeast7500);
        Assert.Equal(2, summary.BarsAtLeast10000);
        Assert.Equal(1, summary.BarsAtLeast13000);
    }

    static OptionTickV2 Tick(
        long id,
        int seconds,
        decimal price,
        long cumulativeVolume,
        MarketDepth depth)
    {
        var exchange = Start.AddSeconds(seconds);
        return new OptionTickV2(
            id,
            exchange,
            exchange.AddMilliseconds(10),
            price,
            depth,
            cumulativeVolume,
            1_000_000 + id);
    }

    static MarketDepth Depth(
        long bid1Qty = 10,
        long ask1Qty = 10,
        long deeperBidQty = 10,
        long deeperAskQty = 10) =>
        new(
            100m, bid1Qty,
            99.95m, deeperBidQty,
            99.90m, deeperBidQty,
            99.85m, deeperBidQty,
            99.80m, deeperBidQty,
            100.05m, ask1Qty,
            100.10m, deeperAskQty,
            100.15m, deeperAskQty,
            100.20m, deeperAskQty,
            100.25m, deeperAskQty);
}
