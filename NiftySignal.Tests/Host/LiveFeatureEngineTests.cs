using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;

namespace NiftySignal.Tests.Host;

public class LiveFeatureEngineTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 4);
    static readonly DateOnly NearestExpiry = new(2026, 9, 8);
    static readonly DateTimeOffset Start = new(2026, 9, 4, 9, 30, 0, TimeSpan.FromHours(5.5));

    const string SpotToken = "26000";
    const string FutureToken = "68407";
    const string CallToken = "42637"; // strike 23950
    const string PutToken = "42638"; // strike 23950

    static Instrument Spot() => new()
    {
        Token = SpotToken,
        Exchange = Exchange.Nse,
        TradingSymbol = "Nifty 50",
        InstrumentType = InstrumentType.Index,
        Underlying = "NIFTY",
        LotSize = 1,
        TickSize = 0.05m,
        AsOfDate = AsOfDate,
    };

    static Instrument Future() => new()
    {
        Token = FutureToken,
        Exchange = Exchange.Nfo,
        TradingSymbol = "NIFTY08SEP26F",
        InstrumentType = InstrumentType.Future,
        ExpiryDate = NearestExpiry,
        Underlying = "NIFTY",
        LotSize = 65,
        TickSize = 0.05m,
        AsOfDate = AsOfDate,
    };

    static Instrument Option(string token, OptionType type, decimal strike) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        TradingSymbol = $"NIFTY08SEP26{(type == OptionType.Call ? "C" : "P")}{strike:0}",
        InstrumentType = InstrumentType.Option,
        OptionType = type,
        StrikePrice = strike,
        ExpiryDate = NearestExpiry,
        Underlying = "NIFTY",
        LotSize = 65,
        TickSize = 0.05m,
        AsOfDate = AsOfDate,
    };

    static List<Instrument> BaseUniverse() =>
    [
        Spot(),
        Future(),
        Option(CallToken, OptionType.Call, 23950m),
        Option(PutToken, OptionType.Put, 23950m),
    ];

    static Tick MakeTick(string token, decimal lastPrice, DateTimeOffset at, long? oi = null, MarketDepth? depth = null) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        ExchangeTimestamp = at,
        ReceivedAt = at,
        LastPrice = lastPrice,
        OpenInterest = oi,
        Depth = depth,
    };

    static MarketDepth Depth(long bidQty, long askQty, decimal bid = 100m, decimal ask = 101m) => new(
        Bid1Price: bid, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void ComputeCadence_ReturnsNull_BeforeSpotAndFutureHaveTicks()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());

        Assert.Null(engine.ComputeCadence(Start));
    }

    [Fact]
    public void ComputeCadence_ReturnsNull_WhenOnlySpotHasTicked()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));

        Assert.Null(engine.ComputeCadence(Start));
    }

    [Fact]
    public void ComputeCadence_ComputesFuturesBasis_AsFutureMinusSpot()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24028.35m, Start));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.Equal(128.35, snapshot!.FuturesBasisRaw!.Value, precision: 2);
    }

    [Fact]
    public void ComputeCadence_ComputesPcr_AsPutOiOverCallOi()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, oi: 1_000_000));
        engine.OnTick(MakeTick(PutToken, 80m, Start, oi: 870_000));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.Equal(0.87, snapshot!.PcrRaw!.Value, precision: 2);
    }

    [Fact]
    public void ComputeCadence_ComputesDepthImbalance_FromNearestTwoStrikesOnly_ExcludingFartherOnes()
    {
        const string MediumCallToken = "99000";
        const string FarCallToken = "99001";
        var universe = BaseUniverse();
        // Three distinct strikes total (23950/24450/25000) so "nearest 2" genuinely
        // excludes the third (25000) instead of trivially including everything there is.
        universe.Add(Option(MediumCallToken, OptionType.Call, 24450m));
        universe.Add(Option(FarCallToken, OptionType.Call, 25000m));

        var engine = new LiveFeatureEngine(universe);
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, depth: Depth(bidQty: 500, askQty: 400)));
        engine.OnTick(MakeTick(PutToken, 80m, Start, depth: Depth(bidQty: 300, askQty: 600)));
        engine.OnTick(MakeTick(MediumCallToken, 40m, Start, depth: Depth(bidQty: 200, askQty: 200)));
        engine.OnTick(MakeTick(FarCallToken, 5m, Start, depth: Depth(bidQty: 9_000, askQty: 1)));

        var snapshot = engine.ComputeCadence(Start);

        // (500+300+200) bid / (400+600+200) ask = 1000/1200 -- the far strike's 9000/1
        // depth would swamp this if it weren't excluded.
        Assert.NotNull(snapshot);
        Assert.Equal(1000.0 / 1200.0, snapshot!.DepthImbalanceRaw!.Value, precision: 3);
    }

    [Fact]
    public void ComputeCadence_CompositeScoreStaysNull_UntilAllSixMetricsWarmUp()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, oi: 1_000_000, depth: Depth(500, 400)));
        engine.OnTick(MakeTick(PutToken, 80m, Start, oi: 870_000, depth: Depth(300, 600)));

        // A single observation can never satisfy any window's real-elapsed-time warm-up
        // requirement (shortest is 5 minutes), regardless of how many cadence ticks fire
        // at the same instant.
        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsWarmedUp);
        Assert.Null(snapshot.CompositeScore);
    }

    [Fact]
    public void NearestExpiry_IsTheSmallestOptionExpiryInTheUniverse()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());

        Assert.Equal(NearestExpiry, engine.NearestExpiry);
    }

    [Fact]
    public void BuildStrikeCandidates_ReturnsOnlyTheRequestedSide()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, oi: 1_000_000, depth: Depth(500, 400, bid: 99.9m, ask: 100.1m)));
        engine.OnTick(MakeTick(PutToken, 80m, Start, oi: 870_000, depth: Depth(300, 600, bid: 79.9m, ask: 80.1m)));

        var calls = engine.BuildStrikeCandidates(OptionType.Call, Start);

        var candidate = Assert.Single(calls);
        Assert.Equal(CallToken, candidate.Token);
        Assert.Equal(OptionType.Call, candidate.OptionType);
        Assert.Equal(1_000_000, candidate.OpenInterest);
    }

    [Fact]
    public void TryGetLatestQuote_PrefersBidOverLtp()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(CallToken, 100m, Start, depth: Depth(500, 400, bid: 98.5m, ask: 101m)));

        var found = engine.TryGetLatestQuote(CallToken, out var ltp, out var bid);

        Assert.True(found);
        Assert.Equal(100m, ltp);
        Assert.Equal(98.5m, bid);
    }

    [Fact]
    public void TryGetLatestQuote_FallsBackToNullBid_WhenNoDepthSnapshotYet()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(CallToken, 100m, Start));

        var found = engine.TryGetLatestQuote(CallToken, out var ltp, out var bid);

        Assert.True(found);
        Assert.Equal(100m, ltp);
        Assert.Null(bid);
    }

    [Fact]
    public void SeedHistory_RestoresWarmUp_WithoutNeedingLiveTicksToAccumulate()
    {
        // Depth imbalance's window is 5 minutes -- replay one point per minute across that
        // span, as if this were the ScoreSnapshot history a restart would find in Postgres.
        // Values vary (not a constant) so the window has real spread -- an all-identical
        // series has zero StdDev, which ComputeZScore correctly reports as "no z-score" on
        // its own merits, and that would be indistinguishable from a warm-up failure here.
        var history = new[] { 0.80, 0.85, 0.95, 0.90, 1.05, 0.92 }
            .Select((value, i) => new ScoreSnapshot
            {
                ComputedAt = Start.AddMinutes(i),
                DepthImbalanceRaw = value,
                WeightSetVersion = "v1",
            })
            .ToList();

        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.SeedHistory(history);

        // A single fresh observation right after seeding should already read as warmed up
        // -- if the seed hadn't taken effect, this would need another 5 real minutes.
        var now = Start.AddMinutes(5).AddSeconds(1);
        engine.OnTick(MakeTick(SpotToken, 23900m, now));
        engine.OnTick(MakeTick(FutureToken, 24000m, now));
        engine.OnTick(MakeTick(CallToken, 100m, now, depth: Depth(bidQty: 90, askQty: 100)));
        engine.OnTick(MakeTick(PutToken, 80m, now, depth: Depth(bidQty: 90, askQty: 100)));

        var snapshot = engine.ComputeCadence(now);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.DepthImbalanceZ);
    }

    [Fact]
    public void SeedHistory_IgnoresNullRawValues_InsteadOfWarmingUpOnGaps()
    {
        var history = new List<ScoreSnapshot>
        {
            new() { ComputedAt = Start, DepthImbalanceRaw = null, WeightSetVersion = "v1" },
        };

        var engine = new LiveFeatureEngine(BaseUniverse());

        // Must not throw on a null raw value (e.g. a cadence tick where depth hadn't
        // arrived yet) -- SeedHistory should skip it, not feed a fabricated 0 into the window.
        var exception = Record.Exception(() => engine.SeedHistory(history));
        Assert.Null(exception);
    }
}
