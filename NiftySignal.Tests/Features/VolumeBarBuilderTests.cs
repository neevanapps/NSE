using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class VolumeBarBuilderTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 17, 9, 15, 0, TimeSpan.Zero);

    static MarketDepth Depth(decimal bid1, long bidQty, decimal ask1, long askQty) => new(
        Bid1Price: bid1, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask1, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void BelowThreshold_ReturnsNull_StillAccumulating()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        Assert.Null(builder.ApplyTick(T0, 23000m, cumulativeVolume: 100, depth: null, openInterest: null));
        Assert.Null(builder.ApplyTick(T0.AddSeconds(5), 23010m, cumulativeVolume: 500, depth: null, openInterest: null));
    }

    [Fact]
    public void CrossingThreshold_EmitsABar_WithTheEntireTriggeringTicksDeltaIncluded()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null); // baseline tick, zero delta
        Assert.Null(builder.ApplyTick(T0.AddSeconds(1), 23010m, cumulativeVolume: 800, depth: null, openInterest: null)); // +800, still under

        // +500 pushes cumulative to 1300 -- past the 1000 threshold. The WHOLE 500 delta belongs
        // to this bar (never split), so bar volume is 1300, not capped at 1000.
        var bar = builder.ApplyTick(T0.AddSeconds(2), 23020m, cumulativeVolume: 1300, depth: null, openInterest: null);

        Assert.NotNull(bar);
        Assert.Equal(1300, bar!.Volume);
        Assert.Equal(T0, bar.StartTimestamp);
        Assert.Equal(T0.AddSeconds(2), bar.EndTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(2), bar.Duration);
    }

    [Fact]
    public void CompletedBar_ReportsOhlcCorrectly()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        builder.ApplyTick(T0.AddSeconds(1), 23050m, cumulativeVolume: 400, depth: null, openInterest: null); // high
        builder.ApplyTick(T0.AddSeconds(2), 22980m, cumulativeVolume: 700, depth: null, openInterest: null); // low
        var bar = builder.ApplyTick(T0.AddSeconds(3), 23010m, cumulativeVolume: 1100, depth: null, openInterest: null); // close

        Assert.NotNull(bar);
        Assert.Equal(23000m, bar!.OpenPrice);
        Assert.Equal(23050m, bar.HighPrice);
        Assert.Equal(22980m, bar.LowPrice);
        Assert.Equal(23010m, bar.ClosePrice);
    }

    [Fact]
    public void AfterABarCompletes_TheNextBarStartsFreshFromTheNextTick()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        builder.ApplyTick(T0.AddSeconds(1), 23050m, cumulativeVolume: 1100, depth: null, openInterest: null); // bar 1 closes here

        Assert.Null(builder.ApplyTick(T0.AddSeconds(2), 23100m, cumulativeVolume: 1200, depth: null, openInterest: null)); // +100 into bar 2, still under
        var bar2 = builder.ApplyTick(T0.AddSeconds(3), 23200m, cumulativeVolume: 2300, depth: null, openInterest: null); // bar 2 closes

        Assert.NotNull(bar2);
        Assert.Equal(T0.AddSeconds(2), bar2!.StartTimestamp); // NOT T0 -- bar 2 starts at the first tick after bar 1 closed
        Assert.Equal(1200, bar2.Volume); // 100 (1200-1100) + 1100 (2300-1200) = 1200
        Assert.Equal(23100m, bar2.OpenPrice); // NOT 23000m (bar 1's open) -- OHLC state must reset
    }

    [Fact]
    public void OpenInterest_ForwardFillsTheLatestKnownValue()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: 500_000);
        builder.ApplyTick(T0.AddSeconds(1), 23010m, cumulativeVolume: 400, depth: null, openInterest: null); // no OI update this tick
        var bar = builder.ApplyTick(T0.AddSeconds(2), 23020m, cumulativeVolume: 1100, depth: null, openInterest: null);

        Assert.Equal(500_000, bar!.OpenInterestAtClose); // still the last real value seen, not null
    }

    [Fact]
    public void DepthBearingTicks_FeedBothCvdAndDepthImbalance()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 500);

        // LastPrice at/above the bid-ask midpoint (100.5) -> buy-leaning, matches
        // FutureCvdProxyAccumulator's own classification rule exactly.
        builder.ApplyTick(T0, 100m, cumulativeVolume: 0, Depth(bid1: 100m, bidQty: 900, ask1: 101m, askQty: 300), openInterest: null);
        var bar = builder.ApplyTick(T0.AddSeconds(1), 101m, cumulativeVolume: 600, Depth(bid1: 100m, bidQty: 900, ask1: 101m, askQty: 300), openInterest: null);

        Assert.NotNull(bar);
        Assert.Equal(600, bar!.FutureCvdNet); // whole 600 delta classified buy-leaning (101 >= midpoint 100.5)
        Assert.Equal((900.0 - 300.0) / (900.0 + 300.0), bar.DepthImbalance);
    }

    [Fact]
    public void NoDepthOnAnyTick_LeavesCvdAndDepthImbalanceNull_NotZero()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 500);

        builder.ApplyTick(T0, 100m, cumulativeVolume: 0, depth: null, openInterest: null);
        var bar = builder.ApplyTick(T0.AddSeconds(1), 101m, cumulativeVolume: 600, depth: null, openInterest: null);

        Assert.NotNull(bar);
        Assert.Null(bar!.FutureCvdNet);
        Assert.Null(bar.DepthImbalance);
        Assert.Null(bar.OrderFlowImbalance);
    }

    [Fact]
    public void DepthBearingTicks_AlsoFeedOrderFlowImbalance()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 500);

        builder.ApplyTick(T0, 100m, cumulativeVolume: 0, Depth(bid1: 100m, bidQty: 50, ask1: 101m, askQty: 30), openInterest: null);
        // Bid qty grows 50->80 at the same price -- OFI contribution +30, ask unchanged -> +0.
        var bar = builder.ApplyTick(T0.AddSeconds(1), 101m, cumulativeVolume: 600, Depth(bid1: 100m, bidQty: 80, ask1: 101m, askQty: 30), openInterest: null);

        Assert.NotNull(bar);
        Assert.Equal(30.0, bar!.OrderFlowImbalance);
    }

    [Fact]
    public void TopOfBookImbalance_ReadsOnlyLevel1_DivergingFromDepthImbalanceWhenDeeperLevelsDisagree()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 500);

        // Touch is bid-heavy (520 vs 65), but levels 2-5 are offer-heavy enough that the summed
        // 5-level DepthImbalance flips negative -- same divergence as the 2026-09-17 live
        // snapshot that motivated this metric. TopOfBookImbalance must ignore the deeper levels.
        var depth = new MarketDepth(
            Bid1Price: 23398.10m, Bid1Qty: 520, Bid2Price: 0, Bid2Qty: 30_000, Bid3Price: 0, Bid3Qty: 30_000, Bid4Price: 0, Bid4Qty: 30_000, Bid5Price: 0, Bid5Qty: 30_000,
            Ask1Price: 23398.80m, Ask1Qty: 65, Ask2Price: 0, Ask2Qty: 50_000, Ask3Price: 0, Ask3Qty: 50_000, Ask4Price: 0, Ask4Qty: 50_000, Ask5Price: 0, Ask5Qty: 50_000);

        builder.ApplyTick(T0, 23398m, cumulativeVolume: 0, depth, openInterest: null);
        var bar = builder.ApplyTick(T0.AddSeconds(1), 23398m, cumulativeVolume: 600, depth, openInterest: null);

        Assert.NotNull(bar);
        Assert.Equal((520.0 - 65.0) / (520.0 + 65.0), bar!.TopOfBookImbalance);
        Assert.True(bar.DepthImbalance < 0, "5-level DepthImbalance should read offer-heavy while TopOfBookImbalance reads bid-heavy");
    }

    [Fact]
    public void VwapAtClose_IsCumulativeForTheSession_NotResetPerBar()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 500);

        builder.ApplyTick(T0, 100m, cumulativeVolume: 0, depth: null, openInterest: null);
        var bar1 = builder.ApplyTick(T0.AddSeconds(1), 100m, cumulativeVolume: 500, depth: null, openInterest: null); // all 500 @ 100

        builder.ApplyTick(T0.AddSeconds(2), 110m, cumulativeVolume: 600, depth: null, openInterest: null); // +100 @ 110, still under
        var bar2 = builder.ApplyTick(T0.AddSeconds(3), 110m, cumulativeVolume: 1000, depth: null, openInterest: null); // +400 @ 110

        Assert.Equal(100.0, bar1!.VwapAtClose);
        // Session VWAP after bar 2 = (500*100 + 500*110) / 1000 = 105 -- reflects EVERY tick since
        // session open, not just bar 2's own ticks.
        Assert.Equal(105.0, bar2!.VwapAtClose);
    }

    [Fact]
    public void FlushPartial_EmitsTheLeftoverBar_EvenThoughItNeverReachedThreshold()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        builder.ApplyTick(T0.AddSeconds(1), 23010m, cumulativeVolume: 300, depth: null, openInterest: null); // day ends here, well under threshold

        var partial = builder.FlushPartial(T0.AddSeconds(1));

        Assert.NotNull(partial);
        Assert.Equal(300, partial!.Volume);
        Assert.Equal(23010m, partial.ClosePrice);
    }

    [Fact]
    public void FlushPartial_WithNothingAccumulatedSinceTheLastBar_ReturnsNull()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        builder.ApplyTick(T0.AddSeconds(1), 23010m, cumulativeVolume: 1000, depth: null, openInterest: null); // closes exactly on the boundary

        Assert.Null(builder.FlushPartial(T0.AddSeconds(1)));
    }

    [Fact]
    public void TickCount_CountsEveryApplyTickCall_RegardlessOfDepthOrVolumeDelta()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        // Four ApplyTick calls feed this bar -- including one zero-delta baseline tick and one
        // depth-only tick with no price/volume change -- TickCount must count the CALL, not the
        // delta or the depth presence (see VolumeBar.TickCount's own "feed message, not trade" doc comment).
        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        builder.ApplyTick(T0.AddSeconds(1), 23000m, cumulativeVolume: 0, Depth(bid1: 23000m, bidQty: 10, ask1: 23001m, askQty: 10), openInterest: null);
        builder.ApplyTick(T0.AddSeconds(2), 23010m, cumulativeVolume: 400, depth: null, openInterest: null);
        var bar = builder.ApplyTick(T0.AddSeconds(3), 23020m, cumulativeVolume: 1100, depth: null, openInterest: null);

        Assert.NotNull(bar);
        Assert.Equal(4, bar!.TickCount);
    }

    [Fact]
    public void TickCount_ResetsToZero_ForTheNextBar()
    {
        var builder = new VolumeBarBuilder(barVolumeThreshold: 1000);

        builder.ApplyTick(T0, 23000m, cumulativeVolume: 0, depth: null, openInterest: null);
        var bar1 = builder.ApplyTick(T0.AddSeconds(1), 23050m, cumulativeVolume: 1100, depth: null, openInterest: null);

        var bar2 = builder.ApplyTick(T0.AddSeconds(2), 23100m, cumulativeVolume: 2200, depth: null, openInterest: null);

        Assert.Equal(2, bar1!.TickCount);
        Assert.Equal(1, bar2!.TickCount); // fresh count for bar 2, not carried over from bar 1
    }
}
