using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;

namespace NiftySignal.Tests.Ingestion;

public class FlatTradeFeedStateTests
{
    static RawFeedMessage Empty(string type) => new(
        Type: type, Exchange: null, Token: null, LastPrice: null, Volume: null, OpenInterest: null, FeedTimeEpochSeconds: null,
        Bid1Price: null, Bid1Qty: null, Bid2Price: null, Bid2Qty: null, Bid3Price: null, Bid3Qty: null,
        Bid4Price: null, Bid4Qty: null, Bid5Price: null, Bid5Qty: null,
        Ask1Price: null, Ask1Qty: null, Ask2Price: null, Ask2Qty: null, Ask3Price: null, Ask3Qty: null,
        Ask4Price: null, Ask4Qty: null, Ask5Price: null, Ask5Qty: null);

    [Fact]
    public void ToTick_ReturnsNull_BeforeAnyLastPriceSeen()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tf") with { Volume = "100" });

        Assert.Null(state.ToTick(DateTimeOffset.Now));
    }

    [Fact]
    public void ApplyDelta_ThenToTick_ProducesATickOnceLastPriceArrives()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50", Volume = "1200", OpenInterest = "50000" });

        var tick = state.ToTick(DateTimeOffset.Now);

        Assert.NotNull(tick);
        Assert.Equal(185.50m, tick.LastPrice);
        Assert.Equal(1200, tick.Volume);
        Assert.Equal(50000, tick.OpenInterest);
    }

    [Fact]
    public void ApplyDelta_PartialUpdate_LeavesPreviouslySetFieldsUnchanged()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50", Volume = "1200" });

        // "tf" carries only the changed field -- Volume is absent (null), not zero.
        state.ApplyDelta(Empty("tf") with { LastPrice = "186.00" });

        var tick = state.ToTick(DateTimeOffset.Now);

        Assert.Equal(186.00m, tick!.LastPrice);
        Assert.Equal(1200, tick.Volume); // unchanged, not reset to 0
    }

    [Fact]
    public void ToTick_DepthIsNull_UntilAllFiveLevelsOnBothSidesArePresent()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50" });
        state.ApplyDelta(Empty("dk") with { Bid1Price = "185.00", Bid1Qty = "10", Ask1Price = "186.00", Ask1Qty = "12" });

        Assert.Null(state.ToTick(DateTimeOffset.Now)!.Depth);
    }

    [Fact]
    public void ToTick_DepthIsPopulated_OnceAllFiveLevelsOnBothSidesArePresent()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50" });
        state.ApplyDelta(Empty("dk") with
        {
            Bid1Price = "185.00", Bid1Qty = "10", Ask1Price = "186.00", Ask1Qty = "12",
            Bid2Price = "184.95", Bid2Qty = "20", Ask2Price = "186.05", Ask2Qty = "22",
            Bid3Price = "184.90", Bid3Qty = "30", Ask3Price = "186.10", Ask3Qty = "32",
            Bid4Price = "184.85", Bid4Qty = "40", Ask4Price = "186.15", Ask4Qty = "42",
            Bid5Price = "184.80", Bid5Qty = "50", Ask5Price = "186.20", Ask5Qty = "52",
        });

        var depth = state.ToTick(DateTimeOffset.Now)!.Depth;

        Assert.NotNull(depth);
        Assert.Equal(185.00m, depth.Bid1Price);
        Assert.Equal(186.20m, depth.Ask5Price);
        Assert.Equal(10 + 20 + 30 + 40 + 50, depth.TotalBidQty);
        Assert.Equal(12 + 22 + 32 + 42 + 52, depth.TotalAskQty);
    }

    [Fact]
    public void ApplyDelta_FeedTime_ConvertsEpochSecondsToExchangeTimestamp()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50", FeedTimeEpochSeconds = "1735689600" });

        var tick = state.ToTick(DateTimeOffset.Now);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), tick!.ExchangeTimestamp);
    }

    [Fact]
    public void ToTick_FallsBackToReceivedTime_WhenNoFeedTimeHasArrivedYet()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "26000");
        state.ApplyDelta(Empty("tk") with { LastPrice = "185.50" });
        var receivedAt = DateTimeOffset.Now;

        var tick = state.ToTick(receivedAt);

        Assert.Equal(receivedAt, tick!.ExchangeTimestamp);
    }
}
