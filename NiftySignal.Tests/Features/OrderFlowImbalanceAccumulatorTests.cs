using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class OrderFlowImbalanceAccumulatorTests
{
    static MarketDepth Depth(decimal bid1, long bidQty, decimal ask1, long askQty) => new(
        Bid1Price: bid1, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask1, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void NoTicksThisCadence_LeavesNetNull_NotZero()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        Assert.Null(ofi.CadenceNet);
    }

    [Fact]
    public void FirstTickEver_ContributesZero_NoPriorQuoteToDiffAgainst()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(100m, 50, 101m, 30));

        Assert.Equal(0.0, ofi.CadenceNet);
    }

    [Fact]
    public void BidSizeGrows_AtTheSamePrice_ContributesThePositiveDelta()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(100m, 50, 101m, 30));
        ofi.ApplyTick(Depth(100m, 60, 101m, 30)); // bid qty +10, ask unchanged

        Assert.Equal(10.0, ofi.CadenceNet); // bidContribution=10, askContribution=0 -> OFI=10
    }

    [Fact]
    public void BidPriceMovesUp_ContributesTheEntireNewBidQty_NotJustTheDelta()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(100m, 50, 102m, 30));
        ofi.ApplyTick(Depth(101m, 40, 102m, 30)); // bid price moved up to a fresh level

        Assert.Equal(40.0, ofi.CadenceNet); // bidContribution=40 (whole new level), askContribution=0
    }

    [Fact]
    public void BidPriceMovesDown_ContributesTheNegativeOfThePreviousBidQty()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(100m, 50, 102m, 30));
        ofi.ApplyTick(Depth(99m, 20, 102m, 30)); // bid price stepped down -- old level's demand gone

        Assert.Equal(-50.0, ofi.CadenceNet); // bidContribution=-50 (the OLD level's qty, not the new one)
    }

    [Fact]
    public void AskPulledAway_IsBullish_PositiveOfi()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(101m, 40, 101m, 30)); // (prices touching is fine -- the formula doesn't validate a real spread)
        ofi.ApplyTick(Depth(101m, 40, 102m, 25)); // ask price moved AWAY (up) -- selling pressure retreated

        // bidContribution: price unchanged (101==101) -> 40-40=0
        // askContribution: Ask1Price(102) < prevAskPrice(101) is false -> catch-all -prevAskQty = -30
        // OFI = 0 - (-30) = 30 (ask retreating is bullish)
        Assert.Equal(30.0, ofi.CadenceNet);
    }

    [Fact]
    public void ResetCadence_ClearsOnlyTheCadenceSum_NotThePreviousQuoteBaseline()
    {
        var ofi = new OrderFlowImbalanceAccumulator();
        ofi.ApplyTick(Depth(100m, 50, 101m, 30));
        ofi.ApplyTick(Depth(100m, 60, 101m, 30)); // +10 this cadence
        ofi.ResetCadence();

        Assert.Null(ofi.CadenceNet); // cadence sum cleared

        // The very next tick must still diff against the LAST tick before the reset (qty 60), not
        // treat this as a fresh "no prior quote" first tick -- otherwise every bar boundary would
        // silently drop one tick's worth of real flow.
        ofi.ApplyTick(Depth(100m, 65, 101m, 30)); // +5 vs the pre-reset baseline of 60
        Assert.Equal(5.0, ofi.CadenceNet);
    }
}
