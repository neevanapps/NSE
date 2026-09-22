using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class TopOfBookImbalanceAccumulatorTests
{
    static MarketDepth Depth(long bid1Qty, long ask1Qty, long bidRestQty = 0, long askRestQty = 0) => new(
        Bid1Price: 100m, Bid1Qty: bid1Qty, Bid2Price: 0, Bid2Qty: bidRestQty, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: 101m, Ask1Qty: ask1Qty, Ask2Price: 0, Ask2Qty: askRestQty, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void NoTicksThisCadence_LeavesImbalanceNull_NotZero()
    {
        var accumulator = new TopOfBookImbalanceAccumulator();
        Assert.Null(accumulator.CadenceImbalance);
    }

    [Fact]
    public void SingleTick_ComputesTheRealWorldFormula()
    {
        var accumulator = new TopOfBookImbalanceAccumulator();
        // Matches the 2026-09-17 live-snapshot worked example: (520-65)/(520+65).
        accumulator.ApplyTick(Depth(520, 65));

        Assert.Equal((520.0 - 65.0) / (520.0 + 65.0), accumulator.CadenceImbalance);
        Assert.Equal(520.0, accumulator.AverageBidQty);
        Assert.Equal(65.0, accumulator.AverageAskQty);
    }

    [Fact]
    public void MultipleTicks_AveragesThePerTickRatio_NotTheRatioOfAverages()
    {
        var accumulator = new TopOfBookImbalanceAccumulator();
        accumulator.ApplyTick(Depth(bid1Qty: 100, ask1Qty: 0));  // ratio = +1
        accumulator.ApplyTick(Depth(bid1Qty: 0, ask1Qty: 100));  // ratio = -1

        Assert.Equal(0.0, accumulator.CadenceImbalance);
    }

    [Fact]
    public void IgnoresLevels2Through5_UnlikeDepthImbalanceAccumulator()
    {
        // Same worked example as the live snapshot: touch is bid-heavy (520 vs 65) but the
        // deeper book (levels 2-5) is offer-heavy enough that the TOTAL 5-level picture flips
        // negative. TopOfBookImbalanceAccumulator must read only the touch, ignoring that.
        var accumulator = new TopOfBookImbalanceAccumulator();
        accumulator.ApplyTick(Depth(bid1Qty: 520, ask1Qty: 65, bidRestQty: 175_435, askRestQty: 246_480));

        Assert.Equal((520.0 - 65.0) / (520.0 + 65.0), accumulator.CadenceImbalance);
    }

    [Fact]
    public void Reset_ClearsAccumulatedState()
    {
        var accumulator = new TopOfBookImbalanceAccumulator();
        accumulator.ApplyTick(Depth(520, 65));
        accumulator.Reset();

        Assert.Null(accumulator.CadenceImbalance);
        Assert.Null(accumulator.AverageBidQty);
    }
}
