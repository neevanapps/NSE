using NiftySignal.Domain.ValueObjects;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class VolumeBar6500RevalidationTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void BuildBars_CarriesThresholdExcessWithoutDuplicatingClosingTick()
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
        Assert.Equal(3, first.TickCount);
        Assert.Equal(8_000, first.RealAssignedVolume);
        Assert.Equal(0, first.ThresholdCarryIn);
        Assert.Equal(8_000, first.ThresholdBalanceAtClose);
        Assert.Equal(1_500L, first.ThresholdCarryOut!.Value);
        Assert.Equal(1L, first.FirstTickId);
        Assert.Equal(3L, first.LastTickId);

        var second = bars[1];
        Assert.Equal(103m, second.Open);
        Assert.Equal(104m, second.Close);
        Assert.Equal(2, second.TickCount);
        Assert.Equal(8_000, second.RealAssignedVolume);
        Assert.Equal(1_500, second.ThresholdCarryIn);
        Assert.Equal(9_500, second.ThresholdBalanceAtClose);
        Assert.Equal(3_000L, second.ThresholdCarryOut!.Value);
        Assert.Equal(4L, second.FirstTickId);
        Assert.Equal(5L, second.LastTickId);

        Assert.NotEqual(first.LastTickId, second.FirstTickId);
    }

    [Fact]
    public void BuildBars_ComputesDepthTobAndOfiFromRawTickSequence()
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
        Assert.Equal(2, bar.DepthTickCount);
    }

    [Fact]
    public void BuildObservations_UsesOnlySubsequentCompletedBarsForForwardOutcomes()
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
        Assert.Equal(4.0, first.MaxUp4Points!.Value);
        Assert.Equal(0.0, first.MaxDown4Points!.Value);

        var last = observations[^1];
        Assert.Null(last.Forward1Points);
        Assert.Null(last.Forward2Points);
        Assert.Null(last.Forward4Points);
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
        Assert.Equal(2_500, bars[1].ThresholdBalanceAtClose);

        var observations = VolumeBar6500Revalidation.BuildObservations(new DateOnly(2026, 9, 8), 0, bars);
        Assert.Single(observations);
        Assert.Null(observations[0].Forward1Points);
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