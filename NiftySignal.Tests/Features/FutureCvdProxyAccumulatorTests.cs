using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class FutureCvdProxyAccumulatorTests
{
    static MarketDepth Depth(decimal bid1, decimal ask1) => new(
        Bid1Price: bid1, Bid1Qty: 1, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask1, Ask1Qty: 1, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void NoTicksYet_LeavesBothNetsNull_NotZero()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        Assert.Null(accumulator.CadenceNet);
        Assert.Null(accumulator.CumulativeNet);
    }

    [Fact]
    public void PrintAtOrAboveMidpoint_ClassifiesTheWholeDeltaBuyLeaning()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        // Matches the real worked example: LTP printed exactly at the ask, bid1=23409.70,
        // ask1=23420.00, midpoint=23414.85, volume delta +4290.
        accumulator.ApplyTick(lastPrice: 23420.00m, Depth(23409.70m, 23420.00m), volumeDelta: 4290);

        Assert.Equal(4290, accumulator.CadenceNet);
        Assert.Equal(4290, accumulator.CumulativeNet);
    }

    [Fact]
    public void PrintBelowMidpoint_ClassifiesTheWholeDeltaSellLeaning()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        // Matches the real worked example: LTP printed below even the bid, bid1=23427.70,
        // ask1=23440.60, midpoint=23434.15, volume delta +1040.
        accumulator.ApplyTick(lastPrice: 23427.30m, Depth(23427.70m, 23440.60m), volumeDelta: 1040);

        Assert.Equal(-1040, accumulator.CadenceNet);
        Assert.Equal(-1040, accumulator.CumulativeNet);
    }

    [Fact]
    public void MissingTwoSidedQuote_ContributesNothing_NeitherBuyNorSell()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 23420.00m, Depth(0m, 23420.00m), volumeDelta: 500); // no real bid

        Assert.Null(accumulator.CadenceNet);
    }

    [Fact]
    public void ZeroVolumeDelta_ContributesNothing_APureQuoteUpdateIsNotATrade()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 23420.00m, Depth(23409.70m, 23420.00m), volumeDelta: 0);

        Assert.Null(accumulator.CadenceNet);
    }

    [Fact]
    public void ResetCadence_ClearsOnlyTheCadenceNet_NotTheDayLongCumulative()
    {
        var accumulator = new FutureCvdProxyAccumulator();
        accumulator.ApplyTick(23420.00m, Depth(23409.70m, 23420.00m), 4290); // buy-leaning
        accumulator.ResetCadence();

        Assert.Null(accumulator.CadenceNet);
        Assert.Equal(4290, accumulator.CumulativeNet); // day-long total survives the reset

        accumulator.ApplyTick(23427.30m, Depth(23427.70m, 23440.60m), 1040); // sell-leaning
        Assert.Equal(-1040, accumulator.CadenceNet);
        Assert.Equal(4290 - 1040, accumulator.CumulativeNet);
    }
}
