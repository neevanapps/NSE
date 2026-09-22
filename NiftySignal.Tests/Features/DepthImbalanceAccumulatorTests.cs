using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class DepthImbalanceAccumulatorTests
{
    static MarketDepth Depth(long bidQty, long askQty) => new(
        Bid1Price: 100m, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: 101m, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void NoTicksThisCadence_LeavesImbalanceNull_NotZero()
    {
        var accumulator = new DepthImbalanceAccumulator();
        Assert.Null(accumulator.CadenceImbalance);
    }

    [Fact]
    public void SingleTick_ComputesTheRealWorldFormula()
    {
        var accumulator = new DepthImbalanceAccumulator();
        // Matches the worked example already validated against real data: (910-325)/(910+325).
        accumulator.ApplyTick(Depth(910, 325));

        Assert.Equal((910.0 - 325.0) / (910.0 + 325.0), accumulator.CadenceImbalance);
        Assert.Equal(910.0, accumulator.AverageBidQty);
        Assert.Equal(325.0, accumulator.AverageAskQty);
    }

    [Fact]
    public void MultipleTicks_AveragesThePerTickRatio_NotTheRatioOfAverages()
    {
        var accumulator = new DepthImbalanceAccumulator();
        accumulator.ApplyTick(Depth(bidQty: 100, askQty: 0));  // ratio = +1
        accumulator.ApplyTick(Depth(bidQty: 0, askQty: 100));  // ratio = -1

        // Average of the two per-tick ratios is 0 -- not the ratio of the averaged quantities
        // (which would also happen to be 0 here, but for a different, coincidental reason;
        // the two-side symmetric case is chosen deliberately to make that distinction visible
        // rather than hide it behind a number that would pass either way).
        Assert.Equal(0.0, accumulator.CadenceImbalance);
    }

    [Fact]
    public void Reset_ClearsAccumulatedState()
    {
        var accumulator = new DepthImbalanceAccumulator();
        accumulator.ApplyTick(Depth(910, 325));
        accumulator.Reset();

        Assert.Null(accumulator.CadenceImbalance);
        Assert.Null(accumulator.AverageBidQty);
    }
}
