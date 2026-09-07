using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Host;
using NiftySignal.Pricing;
using NiftySignal.Scoring;

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

    static Tick MakeTick(string token, decimal lastPrice, DateTimeOffset at, long? oi = null, MarketDepth? depth = null, long volume = 0) => new()
    {
        Token = token,
        Exchange = Exchange.Nfo,
        ExchangeTimestamp = at,
        ReceivedAt = at,
        LastPrice = lastPrice,
        Volume = volume,
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
    public void ComputeCadence_UsesFutureMidPrice_NotBouncingLastPrice_ForBasisAndMomentum()
    {
        // Regression for a live-caught bug (2026-09-07): the future's last-traded price was
        // observed alternating between two levels several points apart within seconds -- not
        // genuine price discovery. Mid (bid+ask)/2 is stable against that kind of bounce.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 23905m, Start, depth: Depth(bidQty: 10, askQty: 10, bid: 24000m, ask: 24002m)));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        // Mid = (24000+24002)/2 = 24001, basis = 24001-23900 = 101 -- not 23905-23900 = 5,
        // which is what using the bouncing LastPrice directly would have produced.
        Assert.Equal(101.0, snapshot!.FuturesBasisRaw!.Value, precision: 2);
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

        // bid=500+300+200=1000, ask=400+600+200=1200 -- normalized (bid-ask)/(bid+ask) =
        // -200/2200 = -0.0909. The far strike's 9000/1 depth would swamp this (and flip its
        // sign) if it weren't excluded.
        Assert.NotNull(snapshot);
        Assert.Equal(-200.0 / 2200.0, snapshot!.DepthImbalanceRaw!.Value, precision: 3);
    }

    [Fact]
    public void ComputeCadence_SkipsOiBuildupNet_WhenTheGapSinceTheLastCadenceExceedsNormalSpacing()
    {
        // Regression for a live-caught bug (2026-09-07): after a feed reconnect, "the last
        // cadence" could be 60-90+ seconds old instead of the normal ~15s, so the OI delta
        // would really represent several cadences' worth of change bunched into one -- which
        // read as a spurious extreme z-score against a window calibrated for normal 15s
        // deltas. The cadence right after a gap like that must report no OiBuildupNet reading
        // at all rather than a misleading one.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, oi: 1_000_000));
        engine.OnTick(MakeTick(PutToken, 80m, Start, oi: 900_000));
        var first = engine.ComputeCadence(Start);
        Assert.NotNull(first);

        // A big, real OI change -- but arriving 90 seconds later, well past the normal ~15s
        // cadence spacing (simulating a reconnect gap).
        var afterGap = Start.AddSeconds(90);
        engine.OnTick(MakeTick(SpotToken, 23900m, afterGap));
        engine.OnTick(MakeTick(FutureToken, 24000m, afterGap));
        engine.OnTick(MakeTick(CallToken, 100m, afterGap, oi: 1_500_000));
        engine.OnTick(MakeTick(PutToken, 80m, afterGap, oi: 1_200_000));
        var afterReconnect = engine.ComputeCadence(afterGap);

        Assert.NotNull(afterReconnect);
        Assert.Null(afterReconnect!.OiBuildupNetRaw);

        // The next cadence, back to normal ~15s spacing, must compute normally again.
        var normalNext = afterGap.AddSeconds(15);
        engine.OnTick(MakeTick(SpotToken, 23900m, normalNext));
        engine.OnTick(MakeTick(FutureToken, 24000m, normalNext));
        engine.OnTick(MakeTick(CallToken, 100m, normalNext, oi: 1_500_000));
        engine.OnTick(MakeTick(PutToken, 80m, normalNext, oi: 1_200_000));
        var resumed = engine.ComputeCadence(normalNext);

        Assert.NotNull(resumed);
        Assert.NotNull(resumed!.OiBuildupNetRaw);
    }

    [Fact]
    public void ComputeCadence_SmoothsCompositeRaw_AcrossTheLastSeveralCadences()
    {
        // Regression for the live-caught "score isn't tradable" problem (2026-09-07): a
        // composite recomputed from scratch every cadence, with no memory of its own recent
        // behavior, let a single noisy cadence swing the tradable score just as much as a
        // genuinely sustained move -- which kept resetting the entry rules' sustain timer even
        // on days with a real, persistent directional move. Warm up a baseline with small,
        // realistic jitter (StdDev must be non-zero or every z-score stays null -- see
        // WelfordRollingWindow.ComputeZScore), then inject one drastic single-cadence OI
        // change and confirm the smoothed CompositeScoreRaw moves far less than the
        // instantaneous one did.
        var engine = new LiveFeatureEngine(BaseUniverse());
        var at = Start;
        ScoreSnapshot? snapshot = null;
        var random = new Random(42);

        // ~31 minutes at 15s cadence -- long enough to warm up the 30-minute windows
        // (Pcr, OiBuildupNet, FuturesBasis), the longest of the six required. Every jittered
        // quantity is independently randomized -- spot and future must NOT share the same
        // offset (that cancels out in the basis calc), and depth quantities need their own
        // variance too (imbalance is otherwise perfectly balanced, hence zero, every cadence).
        for (var elapsed = TimeSpan.Zero; elapsed <= TimeSpan.FromMinutes(31); elapsed += TimeSpan.FromSeconds(15))
        {
            at = Start + elapsed;
            engine.OnTick(MakeTick(SpotToken, 23900m + random.Next(-2, 3), at));
            engine.OnTick(MakeTick(FutureToken, 24000m + random.Next(-2, 3), at));
            // OiBuildupNet's classifier needs the option's OWN price to move too (it classifies
            // off price-change x OI-change together) -- a constant price here is why oiRaw
            // stayed exactly 0 every cadence before this fix.
            engine.OnTick(MakeTick(CallToken, 100m + random.Next(-1, 2), at,
                oi: 1_000_000 + random.Next(-3_000, 3_001),
                depth: Depth(400 + random.Next(0, 200), 400 + random.Next(0, 200), bid: 99.5m, ask: 100.5m)));
            engine.OnTick(MakeTick(PutToken, 80m + random.Next(-1, 2), at,
                oi: 900_000 + random.Next(-3_000, 3_001),
                depth: Depth(400 + random.Next(0, 200), 400 + random.Next(0, 200), bid: 79.5m, ask: 80.5m)));
            snapshot = engine.ComputeCadence(at);
        }

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsWarmedUp);

        // One drastic, single-cadence OI spike -- call OI roughly 4-5x its baseline, versus
        // the +/-2,000 jitter the baseline warmed up on.
        var spikeAt = at + TimeSpan.FromSeconds(15);
        engine.OnTick(MakeTick(SpotToken, 23900m, spikeAt));
        engine.OnTick(MakeTick(FutureToken, 24000m, spikeAt));
        engine.OnTick(MakeTick(CallToken, 100m, spikeAt, oi: 5_000_000, depth: Depth(500, 500, bid: 99.5m, ask: 100.5m)));
        engine.OnTick(MakeTick(PutToken, 80m, spikeAt, oi: 900_000, depth: Depth(500, 500, bid: 79.5m, ask: 80.5m)));
        var spiked = engine.ComputeCadence(spikeAt);

        Assert.NotNull(spiked);
        Assert.NotNull(spiked!.CompositeScoreRawInstant);
        Assert.NotNull(spiked.CompositeScoreRaw);

        // The smoothed value must move far less than the instantaneous one -- averaged
        // against ~20 calm prior readings, not replaced by the spike outright.
        Assert.True(Math.Abs(spiked.CompositeScoreRaw!.Value) < Math.Abs(spiked.CompositeScoreRawInstant!.Value) / 2);
    }

    [Fact]
    public void BuildStrikeSnapshots_ReportsVolumeTradedThisCadence_NotTheCumulativeDayTotal()
    {
        // The feed reports cumulative day volume, so a naive persist would record an
        // ever-growing number instead of "how much traded in these 15 seconds".
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, volume: 5_000));

        // First pass has no prior observation to diff against -- null, not a fabricated zero.
        var first = engine.BuildStrikeSnapshots(Start);
        Assert.Null(Assert.Single(first, s => s.Token == CallToken).VolumeDelta);

        engine.OnTick(MakeTick(CallToken, 101m, Start.AddSeconds(15), volume: 5_350));

        var second = engine.BuildStrikeSnapshots(Start.AddSeconds(15));

        // 5,350 cumulative minus 5,000 at the previous cadence = 350 traded in between.
        Assert.Equal(350, Assert.Single(second, s => s.Token == CallToken).VolumeDelta);
    }

    [Fact]
    public void BuildStrikeSnapshots_ClampsVolumeDeltaToZero_WhenTheCumulativeCounterGoesBackwards()
    {
        // A feed reset mid-session would otherwise yield a negative "traded volume", which is
        // meaningless -- better to record no activity than a nonsense negative.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, volume: 9_000));
        engine.BuildStrikeSnapshots(Start);

        engine.OnTick(MakeTick(CallToken, 100m, Start.AddSeconds(15), volume: 120));
        var afterReset = engine.BuildStrikeSnapshots(Start.AddSeconds(15));

        Assert.Equal(0, Assert.Single(afterReset, s => s.Token == CallToken).VolumeDelta);
    }

    [Fact]
    public void BuildStrikeSnapshots_RecordsSpreadAndGreeks_ForTheNearAtmBand()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, oi: 1_000_000, depth: Depth(500, 400, bid: 99.5m, ask: 100.5m)));
        // Spread is sampled by Sample() (called every ~3s in production, and once more right at
        // the cadence boundary by ComputeCadence) rather than read instantaneously -- one sample
        // in means the average is trivially that one value.
        engine.Sample(Start);

        var call = Assert.Single(engine.BuildStrikeSnapshots(Start), s => s.Token == CallToken);

        Assert.Equal(99.5m, call.BidPrice);
        Assert.Equal(100.5m, call.AskPrice);
        Assert.Equal(1.0m, call.SpreadAbs);
        Assert.Equal(1_000_000, call.OpenInterest);
        Assert.Equal(NearestExpiry, call.ExpiryDate);

        // Greeks only exist when the IV solve succeeded -- they must travel together, never a
        // partially-populated row.
        Assert.NotNull(call.ImpliedVolatility);
        Assert.NotNull(call.Delta);
        Assert.NotNull(call.Gamma);
        Assert.NotNull(call.Vega);
    }

    [Fact]
    public void BuildStrikeSnapshots_AveragesSpreadAcrossSamples_RatherThanReadingTheLatestOnly()
    {
        // Two samples straddling a brief wide print: (1.0 + 3.0) / 2 = 2.0. A point-in-time
        // read at the cadence boundary would have reported 3.0 (or 1.0, depending on which
        // arrived last) and hidden that the strike was tight for most of the window.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, depth: Depth(500, 400, bid: 99.5m, ask: 100.5m)));
        engine.Sample(Start);

        engine.OnTick(MakeTick(CallToken, 100m, Start.AddSeconds(3), depth: Depth(500, 400, bid: 98.5m, ask: 101.5m)));
        engine.Sample(Start.AddSeconds(3));

        var call = Assert.Single(engine.BuildStrikeSnapshots(Start.AddSeconds(15)), s => s.Token == CallToken);

        Assert.Equal(2.0m, call.SpreadAbs);
        // The bid/ask fields themselves stay instantaneous -- they reflect whichever tick
        // arrived last, not an average of unrelated price levels.
        Assert.Equal(98.5m, call.BidPrice);
        Assert.Equal(101.5m, call.AskPrice);
    }

    [Fact]
    public void BuildStrikeSnapshots_ResetsSpreadSamples_SoTheyDoNotBleedIntoTheNextCadence()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 100m, Start, depth: Depth(500, 400, bid: 90m, ask: 110m))); // spread 20
        engine.Sample(Start);
        engine.BuildStrikeSnapshots(Start);

        // A single new sample in the next window -- if the previous window's spread=20 sample
        // survived, this would average to (20 + 1) / 2 = 10.5 instead of 1.
        engine.OnTick(MakeTick(CallToken, 100m, Start.AddSeconds(15), depth: Depth(500, 400, bid: 99.5m, ask: 100.5m)));
        engine.Sample(Start.AddSeconds(15));

        var call = Assert.Single(engine.BuildStrikeSnapshots(Start.AddSeconds(15)), s => s.Token == CallToken);

        Assert.Equal(1.0m, call.SpreadAbs);
    }

    [Fact]
    public void BuildStrikeSnapshots_TheoreticalPriceAtTheAtmStrike_ReproducesItsOwnMarkPrice()
    {
        // BaseUniverse's one strike (23950) sits exactly at spot, so it IS the ATM strike --
        // the reference vol is solved from its own quote, so pricing it back at that same vol
        // must reproduce the price it was solved from. Same sanity check already verified live
        // for the dashboard's identical methodology (2026-09-05).
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 150m, Start, depth: Depth(500, 400, bid: 149.5m, ask: 150.5m)));

        var call = Assert.Single(engine.BuildStrikeSnapshots(Start), s => s.Token == CallToken);

        Assert.Equal(150m, call.MarkPrice);
        Assert.NotNull(call.TheoreticalPrice);
        Assert.Equal((double)call.MarkPrice!.Value, call.TheoreticalPrice!.Value, precision: 6);
        Assert.NotNull(call.PriceVsTheoretical);
        Assert.Equal(0.0, call.PriceVsTheoretical!.Value, precision: 6);
    }

    [Fact]
    public void BuildStrikeSnapshots_PricesEveryStrikeAtTheSharedAtmReferenceVol_NotItsOwnImpliedVol()
    {
        // A second, farther strike must be priced at the ATM leg's vol, not its own -- proving
        // the vol is genuinely shared rather than each row solving (and so trivially
        // reproducing) its own price.
        const string FarCallToken = "99001";
        var universe = BaseUniverse();
        universe.Add(Option(FarCallToken, OptionType.Call, 24450m));

        var engine = new LiveFeatureEngine(universe);
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(CallToken, 150m, Start, depth: Depth(500, 400, bid: 149.5m, ask: 150.5m)));
        engine.OnTick(MakeTick(FarCallToken, 40m, Start, depth: Depth(200, 200, bid: 39.5m, ask: 40.5m)));

        var snapshots = engine.BuildStrikeSnapshots(Start);
        var far = Assert.Single(snapshots, s => s.Token == FarCallToken);

        var t = TimeToExpiry.YearsUntilExpiry(NearestExpiry, Start);
        var atmVol = ImpliedVolatilitySolver.Solve(OptionType.Call, 150.0, 23950.0, 23950.0, t, 0.065)!.Value;
        var expectedTheoretical = BlackScholes.Calculate(OptionType.Call, 23950.0, 24450.0, t, 0.065, atmVol).Price;

        Assert.NotNull(far.TheoreticalPrice);
        Assert.Equal(expectedTheoretical, far.TheoreticalPrice!.Value, precision: 6);
        // Its own market price (40) is nowhere near the shared-vol theoretical for a strike
        // this far out of the money -- if this failed, it'd mean the row silently fell back to
        // solving its own IV instead of using the shared one.
        Assert.NotEqual(40.0, far.TheoreticalPrice!.Value, precision: 1);
    }

    [Fact]
    public void BuildStrikeSnapshots_LeavesTheoreticalPriceNull_WhenNeitherAtmLegHasAUsableQuote()
    {
        // Both ATM legs present but with no depth and no LTP (price 0) -- there's nothing to
        // solve a reference vol from, so every row's theoretical price must stay null rather
        // than falling back to a fabricated vol.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
        engine.OnTick(MakeTick(CallToken, 0m, Start));
        engine.OnTick(MakeTick(PutToken, 0m, Start));

        var snapshots = engine.BuildStrikeSnapshots(Start);

        Assert.All(snapshots, s => Assert.Null(s.TheoreticalPrice));
        Assert.All(snapshots, s => Assert.Null(s.PriceVsTheoretical));
    }

    [Fact]
    public void BuildStrikeSnapshots_ReturnsEmpty_BeforeSpotHasTicked()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(CallToken, 100m, Start));

        Assert.Empty(engine.BuildStrikeSnapshots(Start));
    }

    [Fact]
    public void ComputeCadence_RecordsSpotPrice_ForChartingAndReversalAnalysis()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23942.35m, Start));
        engine.OnTick(MakeTick(FutureToken, 24068.90m, Start));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.Equal(23942.35, snapshot!.SpotPrice!.Value, precision: 2);
    }

    [Fact]
    public void ComputeCadence_ComputesVixChange_AsNegatedDeltaOverTheLookbackWindow()
    {
        const string VixToken = "26017";
        var universe = BaseUniverse();
        universe.Add(new Instrument
        {
            Token = VixToken,
            Exchange = Exchange.Nse,
            TradingSymbol = "India VIX",
            InstrumentType = InstrumentType.Vix,
            Underlying = "NIFTY",
            LotSize = 1,
            TickSize = 0.01m,
            AsOfDate = AsOfDate,
        });

        var engine = new LiveFeatureEngine(universe);
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));
        engine.OnTick(MakeTick(VixToken, 12.00m, Start));

        var first = engine.ComputeCadence(Start);
        Assert.NotNull(first);
        Assert.Equal(0.0, first!.VixChangeRaw!.Value, precision: 6); // first observation compares to itself

        var later = Start.AddMinutes(10);
        engine.OnTick(MakeTick(SpotToken, 23900m, later));
        engine.OnTick(MakeTick(FutureToken, 24000m, later));
        engine.OnTick(MakeTick(VixToken, 13.50m, later)); // VIX rose 1.50

        var second = engine.ComputeCadence(later);

        Assert.NotNull(second);
        // Negated: VIX rose (12.00 -> 13.50), so raw must be negative (bearish contribution).
        Assert.Equal(-1.50, second!.VixChangeRaw!.Value, precision: 6);
    }

    [Fact]
    public void ComputeCadence_VixChangeZStaysNull_UntilTheZScoreWindowElapses_NotJustTheRawLookbackWindow()
    {
        // Regression for a live-caught bug (2026-09-07): the raw VIX change is a 30-minute
        // lookback delta, and z-scoring it against a window of that SAME 30-minute length
        // means each new 15s sample's lookback overlaps the last one almost entirely -- during
        // any stretch where VIX drifts smoothly, that series has almost no internal variance,
        // so the window's StdDev collapses and an ordinary later move slams into the +/-3 clip.
        // The z-score window is now several times longer than the raw lookback, confirmed here
        // by checking VixChangeZ is still null just past the OLD window's length -- a genuine
        // fix must make the window longer, not just rename the same duration.
        const string VixToken = "26017";
        var universe = BaseUniverse();
        universe.Add(new Instrument
        {
            Token = VixToken,
            Exchange = Exchange.Nse,
            TradingSymbol = "India VIX",
            InstrumentType = InstrumentType.Vix,
            Underlying = "NIFTY",
            LotSize = 1,
            TickSize = 0.01m,
            AsOfDate = AsOfDate,
        });

        // Sample every 5 minutes (well inside the 30-minute raw lookback, so each reading is a
        // genuine non-zero comparison rather than degenerating to "compares to itself") with
        // VIX drifting up a little each time -- exactly the "smoothly drifting" shape that
        // triggered the live bug. Runs past both the old 30-minute window and the new one so
        // the warm-up transition is directly observable.
        var engine = new LiveFeatureEngine(universe);
        ScoreSnapshot? last = null;
        var vix = 12.00m;
        for (var elapsed = TimeSpan.Zero; elapsed <= FeatureWindowLengths.VixChangeZScoreWindow + TimeSpan.FromMinutes(10); elapsed += TimeSpan.FromMinutes(5))
        {
            var at = Start + elapsed;
            vix += 0.01m;
            engine.OnTick(MakeTick(SpotToken, 23900m, at));
            engine.OnTick(MakeTick(FutureToken, 24000m, at));
            engine.OnTick(MakeTick(VixToken, vix, at));
            last = engine.ComputeCadence(at);

            // Just past the OLD 30-minute window -- under the pre-fix window length this would
            // already be warmed up (and prone to clipping); it must not be now.
            if (elapsed == FeatureWindowLengths.VixChange + TimeSpan.FromMinutes(5))
            {
                Assert.Null(last!.VixChangeZ);
            }
        }

        // Past the new (longer) window, it should finally be able to warm up.
        Assert.NotNull(last);
        Assert.NotNull(last!.VixChangeZ);
    }

    [Fact]
    public void ComputeCadence_LeavesVixChangeNull_WhenUniverseDoesNotTrackVix()
    {
        // BaseUniverse() has no Vix-type instrument -- ComputeCadence must not throw, and
        // VixChangeRaw/Z simply stay null (optional by design, see CompositeScoreCalculator).
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.VixChangeRaw);
        Assert.Null(snapshot.VixChangeZ);
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
    public void ComputeCadence_AveragesSamplesSinceLastCadence_ForTheFiveSmoothedMetrics()
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(FutureToken, 24000m, Start));

        // Two mid-cadence samples (basis = future - spot) before any explicit ComputeCadence
        // call: 100 then 200. A third, implicit sample happens inside ComputeCadence itself
        // using whatever spot is current at that point (50) -- average of 100/200/50 = 350/3.
        engine.OnTick(MakeTick(SpotToken, 23900m, Start.AddSeconds(3))); // basis = 100
        engine.Sample(Start.AddSeconds(3));
        engine.OnTick(MakeTick(SpotToken, 23800m, Start.AddSeconds(6))); // basis = 200
        engine.Sample(Start.AddSeconds(6));
        engine.OnTick(MakeTick(SpotToken, 23950m, Start.AddSeconds(9))); // basis = 50

        var snapshot = engine.ComputeCadence(Start.AddSeconds(9));

        Assert.NotNull(snapshot);
        Assert.Equal((100.0 + 200.0 + 50.0) / 3.0, snapshot!.FuturesBasisRaw!.Value, precision: 6);
    }

    [Fact]
    public void ComputeCadence_SingleSample_MatchesTheUnsampledValue_WhenNoMidCadenceSamplesWereTaken()
    {
        // Guards against a regression where averaging changes behavior even for callers (all
        // existing tests) that never call Sample() themselves -- a single implicit sample
        // inside ComputeCadence should equal the old "just read the instant" behavior.
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 23900m, Start));
        engine.OnTick(MakeTick(FutureToken, 24028.35m, Start));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.Equal(128.35, snapshot!.FuturesBasisRaw!.Value, precision: 2);
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
        // IV/Delta price against spot, not the future (2026-09-04 fix) -- spot must have
        // ticked for BuildStrikeCandidates to return anything.
        engine.OnTick(MakeTick(SpotToken, 23950m, Start));
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
    public void BuildStrikeCandidates_PricesIvAgainstSpot_NotTheMismatchedTrackedFuture()
    {
        // Regression test for the 2026-09-04 live-caught bug ("all put IVs read >10, call
        // IVs around 5"): IV/Delta were solved against the tracked future, but NSE only
        // lists monthly futures -- mismatched against a weekly option's own much nearer
        // expiry, overstating the underlying by the month-vs-week cost-of-carry gap.
        // Deliberately push spot and future ~150 points apart (comparable to the real gap
        // observed live) so a formula that used the wrong one would recover a badly wrong
        // IV instead of the true one.
        var engine = new LiveFeatureEngine(BaseUniverse());
        const decimal spot = 23950m; // == CallToken's own strike, i.e. exactly ATM against spot
        const decimal future = 24100m;
        const double trueIv = 0.15;

        var years = TimeToExpiry.YearsUntilExpiry(NearestExpiry, Start);
        var fairPrice = (decimal)BlackScholes.Calculate(OptionType.Call, (double)spot, 23950, years, 0.065, trueIv).Price;

        engine.OnTick(MakeTick(SpotToken, spot, Start));
        engine.OnTick(MakeTick(FutureToken, future, Start));
        engine.OnTick(MakeTick(CallToken, fairPrice, Start, depth: Depth(bidQty: 100, askQty: 100, bid: fairPrice - 0.05m, ask: fairPrice + 0.05m)));

        var candidate = Assert.Single(engine.BuildStrikeCandidates(OptionType.Call, Start));

        Assert.NotNull(candidate.ImpliedVolatility);
        Assert.Equal(trueIv, candidate.ImpliedVolatility!.Value, 1e-3);
    }

    [Fact]
    public void ComputeCadence_ComputesIvSkew_AgainstSpot_NotTheMismatchedTrackedFuture()
    {
        // Same fix, different call site: IvSkew is one of the six required score components,
        // so this bug wasn't just a display issue -- it was feeding a biased raw value into
        // the composite score itself.
        var engine = new LiveFeatureEngine(BaseUniverse());
        const decimal spot = 23950m;
        const decimal future = 24100m;
        const double callTrueIv = 0.12;
        const double putTrueIv = 0.18;

        var years = TimeToExpiry.YearsUntilExpiry(NearestExpiry, Start);
        // CallToken/PutToken are both strike 23950 in BaseUniverse() -- the +/-200 offset's
        // nearest match trivially lands on this same strike for both sides, which is fine:
        // this test only cares whether the solved IVs recover the true values, proving spot
        // (not future) was used as the underlying.
        var callPrice = (decimal)BlackScholes.Calculate(OptionType.Call, (double)spot, 23950, years, 0.065, callTrueIv).Price;
        var putPrice = (decimal)BlackScholes.Calculate(OptionType.Put, (double)spot, 23950, years, 0.065, putTrueIv).Price;

        engine.OnTick(MakeTick(SpotToken, spot, Start));
        engine.OnTick(MakeTick(FutureToken, future, Start));
        engine.OnTick(MakeTick(CallToken, callPrice, Start, depth: Depth(bidQty: 100, askQty: 100, bid: callPrice - 0.05m, ask: callPrice + 0.05m)));
        engine.OnTick(MakeTick(PutToken, putPrice, Start, depth: Depth(bidQty: 100, askQty: 100, bid: putPrice - 0.05m, ask: putPrice + 0.05m)));

        var snapshot = engine.ComputeCadence(Start);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot!.IvSkewRaw);
        Assert.Equal(putTrueIv - callTrueIv, snapshot.IvSkewRaw!.Value, 1e-3);
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
    public void ComputeCadence_FallsBackToDefaultK_WhenCompositeRawWindowIsNotYetWarm()
    {
        // All six per-metric windows warmed (longest is 30 minutes), but CompositeScoreRaw
        // never seeded -- simulates a composite-raw window that hasn't accumulated 30 real
        // minutes yet (e.g. right after this feature was deployed). k must fall back to
        // DefaultK, not to some degenerate/near-zero value from an unwarmed window.
        const int SeedMinutes = 30;
        var history = BuildSeedHistory(SeedMinutes, includeCompositeRaw: false);
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.SeedHistory(history);

        var now = Start.AddMinutes(SeedMinutes).AddSeconds(1);
        FeedFinalTick(engine, now);
        var snapshot = engine.ComputeCadence(now);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsWarmedUp);
        Assert.NotNull(snapshot.CompositeScore);

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: snapshot.OiBuildupNetZ, PcrZ: snapshot.PcrZ, FuturesBasisZ: snapshot.FuturesBasisZ,
            IvSkewZ: snapshot.IvSkewZ, PriceMomentumZ: snapshot.PriceMomentumZ, DepthImbalanceZ: snapshot.DepthImbalanceZ);
        var expectedScore = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, now, CompositeScoreCalculator.DefaultK).Score;

        Assert.Equal(expectedScore!.Value, snapshot.CompositeScore!.Value, precision: 9);
    }

    [Fact]
    public void ComputeCadence_UsesCompositeRawWindowsStdDev_AsK_OnceWarmedUp()
    {
        const int SeedMinutes = 30;
        var history = BuildSeedHistory(SeedMinutes, includeCompositeRaw: true);
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.SeedHistory(history);

        var now = Start.AddMinutes(SeedMinutes).AddSeconds(1);
        FeedFinalTick(engine, now);
        var snapshot = engine.ComputeCadence(now);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsWarmedUp);
        Assert.NotNull(snapshot.CompositeScore);
        Assert.NotNull(snapshot.CompositeScoreRaw);

        // Oracle: feed the same CompositeScoreRaw sequence (seed history + this final point)
        // into a fresh window and read its own StdDev independently. WelfordRollingWindow's
        // arithmetic has its own dedicated tests -- reusing it here only verifies that
        // ComputeCadence wires the result through as k, not that the math itself is right.
        var oracle = new WelfordRollingWindow(TimeSpan.FromMinutes(30));
        foreach (var s in history)
        {
            oracle.Add(s.ComputedAt, s.CompositeScoreRaw!.Value);
        }
        oracle.Add(now, snapshot.CompositeScoreRaw!.Value);

        Assert.True(oracle.IsWarmedUp);
        var expectedK = oracle.StdDev;
        Assert.NotEqual(CompositeScoreCalculator.DefaultK, expectedK); // sanity: the seeded spread isn't coincidentally 1.0

        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: snapshot.OiBuildupNetZ, PcrZ: snapshot.PcrZ, FuturesBasisZ: snapshot.FuturesBasisZ,
            IvSkewZ: snapshot.IvSkewZ, PriceMomentumZ: snapshot.PriceMomentumZ, DepthImbalanceZ: snapshot.DepthImbalanceZ);
        var expectedScore = CompositeScoreCalculator.Calculate(inputs, ScoreWeights.Default, now, expectedK).Score;

        Assert.Equal(expectedScore!.Value, snapshot.CompositeScore!.Value, precision: 6);
    }

    static double Wave(double amplitude, int i) => amplitude * Math.Sin(i * 0.7);

    static List<ScoreSnapshot> BuildSeedHistory(int minutes, bool includeCompositeRaw)
    {
        var history = new List<ScoreSnapshot>();
        for (var i = 0; i < minutes; i++)
        {
            history.Add(new ScoreSnapshot
            {
                ComputedAt = Start.AddMinutes(i),
                OiBuildupNetRaw = Wave(1.0, i),
                PcrRaw = 0.8 + Wave(0.1, i),
                FuturesBasisRaw = 100 + Wave(20, i),
                IvSkewRaw = Wave(0.05, i),
                PriceMomentumRaw = Wave(10, i),
                DepthImbalanceRaw = Wave(0.3, i),
                CompositeScoreRaw = includeCompositeRaw ? Wave(0.6, i) : null,
                WeightSetVersion = "v1",
            });
        }

        return history;
    }

    static void FeedFinalTick(LiveFeatureEngine engine, DateTimeOffset at)
    {
        engine.OnTick(MakeTick(SpotToken, 23900m, at));
        engine.OnTick(MakeTick(FutureToken, 24000m, at));
        engine.OnTick(MakeTick(CallToken, 100m, at, oi: 1_000_000, depth: Depth(bidQty: 90, askQty: 100)));
        engine.OnTick(MakeTick(PutToken, 80m, at, oi: 870_000, depth: Depth(bidQty: 90, askQty: 100)));
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
