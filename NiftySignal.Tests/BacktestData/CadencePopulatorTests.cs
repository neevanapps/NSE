using NiftySignal.BacktestData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Tests.BacktestData;

public sealed class InstrumentPriceStateTests
{
    [Fact]
    public void FreshState_HasNoLatestPrice_AndNoIntrabarOrDayFields()
    {
        var state = new InstrumentPriceState();
        Assert.Null(state.LatestKnownPrice);
        Assert.Null(state.CadenceClose);
        Assert.Null(state.DayOpen);
    }

    [Fact]
    public void ApplyTick_SetsOpenHighLowClose_ToTheSingleTickWhenOnlyOneArrives()
    {
        var state = new InstrumentPriceState();
        state.ApplyTick(100m);

        Assert.Equal(100m, state.CadenceOpen);
        Assert.Equal(100m, state.CadenceHigh);
        Assert.Equal(100m, state.CadenceLow);
        Assert.Equal(100m, state.CadenceClose);
        Assert.Equal(1, state.TickCountThisCadence);
    }

    [Fact]
    public void ApplyTick_TracksIntrabarOpenHighLowCloseAcrossSeveralTicks()
    {
        var state = new InstrumentPriceState();
        state.ApplyTick(100m);
        state.ApplyTick(105m);
        state.ApplyTick(98m);
        state.ApplyTick(101m);

        Assert.Equal(100m, state.CadenceOpen);
        Assert.Equal(105m, state.CadenceHigh);
        Assert.Equal(98m, state.CadenceLow);
        Assert.Equal(101m, state.CadenceClose);
        Assert.Equal(4, state.TickCountThisCadence);
    }

    [Fact]
    public void ResetCadence_ClearsIntrabarFields_ButNotDayFieldsOrLatestKnownPrice()
    {
        var state = new InstrumentPriceState();
        state.ApplyTick(100m);
        state.ApplyTick(105m);

        state.ResetCadence();

        Assert.Null(state.CadenceOpen);
        Assert.Null(state.CadenceHigh);
        Assert.Null(state.CadenceLow);
        Assert.Null(state.CadenceClose);
        Assert.Equal(0, state.TickCountThisCadence);

        // Day-level and "latest known" state must survive a cadence reset -- otherwise a quiet
        // cadence (no new ticks) would wrongly blank out "change for day" and similar fields.
        Assert.Equal(105m, state.LatestKnownPrice);
        Assert.Equal(100m, state.DayOpen);
        Assert.Equal(105m, state.DayHigh);
    }

    [Fact]
    public void AQuietCadenceWithNoTicks_LeavesIntrabarFieldsNull_ButLatestKnownPriceAndDayFieldsUnchanged()
    {
        var state = new InstrumentPriceState();
        state.ApplyTick(100m);
        state.ResetCadence();

        // No ApplyTick calls this cadence -- simulates a genuine feed gap for this instrument.
        Assert.Null(state.CadenceClose);
        Assert.Equal(100m, state.LatestKnownPrice);
        Assert.Equal(100m, state.DayOpen);
    }

    [Fact]
    public void DayHighAndLow_GrowAcrossTheWholeDay_NotJustTheCurrentCadence()
    {
        var state = new InstrumentPriceState();
        state.ApplyTick(100m);
        state.ResetCadence();
        state.ApplyTick(110m); // a new intraday high, in a later cadence
        state.ResetCadence();
        state.ApplyTick(95m); // a new intraday low, in a later cadence still

        Assert.Equal(100m, state.DayOpen);
        Assert.Equal(110m, state.DayHigh);
        Assert.Equal(95m, state.DayLow);
    }
}

// FutureCvdProxyAccumulatorTests/RollingNetSumWindowTests moved to
// NiftySignal.Tests/Features/ (2026-09-17, Phase 3 unification) -- the classes themselves moved to
// NiftySignal.Features, see that project's own FutureCvdProxyAccumulator.cs/RollingNetSumWindow.cs.
//
// FutureFlowAccumulatorTests/DepthImbalanceAccumulatorTests moved to NiftySignal.Tests/Features/
// (2026-09-17, volume-bar cadence work) for the same reason -- see
// NiftySignal.Features/FutureFlowAccumulator.cs/DepthImbalanceAccumulator.cs.

public sealed class TradingHoursRemainingTests
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    [Fact]
    public void SameDayBeforeExpiry_CountsOnlyTheRemainingSessionOverlap()
    {
        var now = new DateTimeOffset(2026, 9, 15, 14, 30, 0, IstOffset); // 1 hour before 15:30 close
        var expiryDate = new DateOnly(2026, 9, 15);
        var expiryMoment = new DateTimeOffset(2026, 9, 15, 15, 30, 0, IstOffset);

        var hours = CadencePopulator.TradingHoursRemaining(now, expiryDate, expiryMoment);

        Assert.Equal(1.0, hours, precision: 6);
    }

    [Fact]
    public void MultiDaySpan_SumsFullSessionsInBetween_PlusPartialFirstDay()
    {
        // Tuesday 09:15 to Thursday's own expiry moment (15:30): a partial Tuesday session
        // (6h15m, since 09:15-09:15 is the whole session) plus the whole Wednesday session
        // (6h15m), landing exactly at Thursday's own close.
        var now = new DateTimeOffset(2026, 9, 15, 9, 15, 0, IstOffset); // Tuesday
        var expiryDate = new DateOnly(2026, 9, 17); // Thursday
        var expiryMoment = new DateTimeOffset(2026, 9, 17, 15, 30, 0, IstOffset);

        var hours = CadencePopulator.TradingHoursRemaining(now, expiryDate, expiryMoment);

        // Tuesday full session (6.25h) + Wednesday full session (6.25h) + Thursday full session (6.25h) = 18.75h
        Assert.Equal(18.75, hours, precision: 6);
    }

    [Fact]
    public void WeekendIsExcluded_FridayToMondayCountsOnlyTheTwoTradingDays()
    {
        var now = new DateTimeOffset(2026, 9, 11, 9, 15, 0, IstOffset); // Friday, session start
        var expiryDate = new DateOnly(2026, 9, 14); // Monday
        var expiryMoment = new DateTimeOffset(2026, 9, 14, 15, 30, 0, IstOffset);

        var hours = CadencePopulator.TradingHoursRemaining(now, expiryDate, expiryMoment);

        // Friday's full session (6.25h) + Monday's full session (6.25h) = 12.5h -- Saturday and
        // Sunday contribute nothing, regardless of how many calendar hours they span.
        Assert.Equal(12.5, hours, precision: 6);
    }

    [Fact]
    public void AtOrPastExpiry_FloorsAtTheSameTwoMinuteMinimumAsCalendarTime()
    {
        var expiryMoment = new DateTimeOffset(2026, 9, 15, 15, 30, 0, IstOffset);
        var hours = CadencePopulator.TradingHoursRemaining(expiryMoment, new DateOnly(2026, 9, 15), expiryMoment);

        Assert.Equal(NiftySignal.Pricing.TimeToExpiry.Minimum.TotalHours, hours, precision: 6);
    }
}

public sealed class CvdProxyAccumulatorTests
{
    static MarketDepth Depth(decimal bid1, decimal ask1) => new(
        Bid1Price: bid1, Bid1Qty: 1, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask1, Ask1Qty: 1, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void NoTicksYet_LeavesBothNetsNull_NotZero()
    {
        var accumulator = new CvdProxyAccumulator();
        Assert.Null(accumulator.CadenceVolumeNet);
        Assert.Null(accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void PrintAtTheAsk_ClassifiesVolumeAndNotionalBuyLeaning()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 45.40m, Depth(45.00m, 45.40m), volumeDelta: 400);

        Assert.Equal(400, accumulator.CadenceVolumeNet);
        Assert.Equal(400m * 45.40m, accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void PrintThroughTheAsk_StillClassifiesBuyLeaning()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 45.50m, Depth(45.00m, 45.40m), volumeDelta: 400);

        Assert.Equal(400, accumulator.CadenceVolumeNet);
    }

    [Fact]
    public void PrintAtTheBid_ClassifiesVolumeAndNotionalSellLeaning()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 44.55m, Depth(44.55m, 44.75m), volumeDelta: 450);

        Assert.Equal(-450, accumulator.CadenceVolumeNet);
        Assert.Equal(-450m * 44.55m, accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void PrintThroughTheBid_StillClassifiesSellLeaning()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 44.50m, Depth(44.55m, 44.75m), volumeDelta: 450);

        Assert.Equal(-450, accumulator.CadenceVolumeNet);
    }

    [Fact]
    public void PrintStrictlyInsideTheSpread_ContributesNothing_NotForcedOntoASide()
    {
        // 2026-09-12, per a friend's review: on a wide option spread, a print between the bid
        // and ask is genuinely ambiguous -- the original mid-point rule would have forced this
        // onto whichever side of the mid it sat nearer, which is exactly the low-signal guess
        // being corrected here. Bid=45.00, Ask=45.40, mid=45.20 -- this print sits exactly at the
        // old mid, which used to classify as buy-leaning; it must now classify as nothing.
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(lastPrice: 45.20m, Depth(45.00m, 45.40m), volumeDelta: 400);

        Assert.Null(accumulator.CadenceVolumeNet);
        Assert.Null(accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void MultipleTicksThisCadence_NetBothVolumeAndNotionalAcrossThem_SkippingTheOneInsideTheSpread()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(45.40m, Depth(45.00m, 45.40m), 400);  // at ask -- buy: +400 / +18,160
        accumulator.ApplyTick(45.20m, Depth(45.10m, 45.30m), 250);  // strictly inside (45.10-45.30 brackets 45.20) -- excluded
        accumulator.ApplyTick(44.55m, Depth(44.55m, 44.75m), 450);  // at bid -- sell: -450 / -20,047.50

        Assert.Equal(-50, accumulator.CadenceVolumeNet); // 400 - 450, the middle tick excluded entirely
        Assert.Equal(400m * 45.40m - 450m * 44.55m, accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void MissingTwoSidedQuote_ContributesNothing()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(45.40m, Depth(0m, 45.40m), 400);

        Assert.Null(accumulator.CadenceVolumeNet);
        Assert.Null(accumulator.CadenceNotionalNet);
    }

    [Fact]
    public void ZeroVolumeDelta_ContributesNothing()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(45.40m, Depth(45.00m, 45.40m), 0);

        Assert.Null(accumulator.CadenceVolumeNet);
    }

    [Fact]
    public void ResetCadence_ClearsBothNets()
    {
        var accumulator = new CvdProxyAccumulator();
        accumulator.ApplyTick(45.40m, Depth(45.00m, 45.40m), 400);
        accumulator.ResetCadence();

        Assert.Null(accumulator.CadenceVolumeNet);
        Assert.Null(accumulator.CadenceNotionalNet);
    }
}

public sealed class OptionInstrumentStateTests
{
    static MarketDepth Depth(decimal bid1, decimal ask1) => new(
        Bid1Price: bid1, Bid1Qty: 1, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask1, Ask1Qty: 1, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    static Tick MakeTick(string token, decimal lastPrice, long volume, long? oi = null, MarketDepth? depth = null) => new()
    {
        Token = token,
        Exchange = Exchange.Nse,
        ExchangeTimestamp = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow,
        LastPrice = lastPrice,
        Volume = volume,
        OpenInterest = oi,
        Depth = depth,
    };

    [Fact]
    public void FreshState_HasNotTickedYet_AndNoBaselines()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));

        Assert.False(state.HasEverTicked);
        Assert.False(state.HasVolumeBaseline);
        Assert.False(state.CadenceHasOiBaseline);
        Assert.Null(state.CadenceMarkPrice);
    }

    [Fact]
    public void FirstTick_EstablishesBaselines_ButVolumeDeltaIsNullNotZero_NoPriorToDiffAgainst()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, oi: 500_000, depth: Depth(45.00m, 45.40m)));

        Assert.True(state.HasEverTicked);
        Assert.False(state.HasVolumeBaseline); // no prior cadence to diff against yet
        Assert.Null(state.CadenceVolumeDelta); // matches production's StrikeSnapshot.VolumeDelta convention: null on the first cadence after startup
        Assert.Equal("TOKEN1", state.Token);
        Assert.Equal(45.20m, state.CadenceMarkPrice);
    }

    [Fact]
    public void SecondTickSameFirstCadence_StillReadsNull_TheWholeFirstCadenceHasNoBaseline()
    {
        // Two ticks within the SAME (first-ever) cadence -- even though a 400-lot delta between
        // them is technically computable, the whole first cadence reads null, consistent with how
        // OpenInterestDelta treats its own first cadence (no partial exception either way).
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, depth: Depth(45.00m, 45.40m)));
        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 125_400, depth: Depth(44.90m, 45.10m)));

        Assert.False(state.HasVolumeBaseline);
        Assert.Null(state.CadenceVolumeDelta);
    }

    [Fact]
    public void SecondCadence_VolumeDeltaIsARealConfirmedNumber_AgainstThePreviousCadencesEndingVolume()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, depth: Depth(45.00m, 45.40m)));
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 125_400, depth: Depth(44.90m, 45.10m)));

        Assert.True(state.HasVolumeBaseline);
        Assert.Equal(400, state.CadenceVolumeDelta);
    }

    [Fact]
    public void AQuietCadenceAfterABaselineExists_ReadsAConfirmedZero_NotNull()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, depth: Depth(45.00m, 45.40m)));
        state.ResetCadence();
        state.ResetCadence(); // a whole cadence passes with zero ticks

        Assert.True(state.HasVolumeBaseline);
        Assert.Equal(0, state.CadenceVolumeDelta);
    }

    [Fact]
    public void NotionalDelta_NullOnFirstCadence_SameBaselineAsVolumeDelta()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000));

        Assert.Null(state.CadenceNotionalDelta);
    }

    [Fact]
    public void NotionalDelta_ValuesEachTicksOwnVolumeAtThatTicksOwnLastPrice_NotOneClosingPrice()
    {
        // Two ticks in the same (second) cadence at different prices -- if this were computed as
        // VolumeDelta x one closing MarkPrice, it would misvalue the first tick's own slice.
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000));
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_400)); // +400 @ 45.20 = 18,080
        state.ApplyTick(MakeTick("TOKEN1", 46.00m, volume: 125_650)); // +250 @ 46.00 = 11,500

        Assert.Equal(650, state.CadenceVolumeDelta); // 400 + 250, across both ticks this cadence
        Assert.Equal(400m * 45.20m + 250m * 46.00m, state.CadenceNotionalDelta);
    }

    [Fact]
    public void NotionalDelta_DoesNotRequireADepthQuote_UnlikeCvdProxy()
    {
        // CvdProxy needs a two-sided quote to classify buy/sell; the blind notional total does
        // not -- it only needs LastPrice and volume, both always present on a real tick.
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000)); // no depth
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_400)); // no depth

        Assert.Equal(400m * 45.20m, state.CadenceNotionalDelta);
        Assert.Null(state.CvdProxy.CadenceNotionalNet); // CVD stays null -- no depth to classify against
    }

    [Fact]
    public void OiDelta_NullUntilASecondObservationExists_ThenRealZeroOrNonZeroDelta()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 100, oi: 500_000));
        Assert.False(state.CadenceHasOiBaseline);

        state.ResetCadence();
        state.ApplyTick(MakeTick("TOKEN1", 45.30m, volume: 150, oi: 500_800));

        Assert.True(state.CadenceHasOiBaseline);
        Assert.Equal(800, state.CadenceOpenInterestDelta);
    }

    [Fact]
    public void MarkPriceDelta_ComparesAgainstLastKnownMark_CarriedThroughAQuietCadence()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 100, depth: Depth(45.00m, 45.40m)));
        state.ResetCadence(); // PriorKnownMarkPrice should now be 45.20

        Assert.Equal(45.20m, state.PriorKnownMarkPrice);

        // A quiet cadence: no tick arrives at all.
        state.ResetCadence();
        Assert.Equal(45.20m, state.PriorKnownMarkPrice); // still carried forward, not lost

        state.ApplyTick(MakeTick("TOKEN1", 46.00m, volume: 120, depth: Depth(45.80m, 46.20m)));
        Assert.Equal(46.00m, state.CadenceMarkPrice);
        Assert.Equal(45.20m, state.PriorKnownMarkPrice); // this cadence's own comparison baseline
    }

    [Fact]
    public void QuietCadence_MarkPriceAndBidAskGoNull_NotHeldOver()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 100, depth: Depth(45.00m, 45.40m)));
        state.ResetCadence();

        Assert.Null(state.CadenceMarkPrice);
        Assert.Null(state.CadenceBidPrice);
        Assert.Null(state.CadenceAskPrice);
    }

    [Fact]
    public void OiNotional_NullBeforeOneMinuteOfPriceHistory_EvenWithOiKnown()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        var t0 = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 100, oi: 500_000, depth: Depth(44.90m, 45.10m)));

        var (oiNotional, oiChangeNotional) = state.RecordCadenceAndComputeOiNotional(t0);
        Assert.Null(oiNotional);
        Assert.Null(oiChangeNotional);
    }

    [Fact]
    public void OiNotional_NullIfOiUnknown_EvenAfterOneMinuteOfPriceHistory()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        var t0 = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);

        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 100, depth: Depth(44.90m, 45.10m))); // no OI
        state.RecordCadenceAndComputeOiNotional(t0);
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 46.00m, volume: 100, depth: Depth(45.90m, 46.10m))); // still no OI

        var (oiNotional, oiChangeNotional) = state.RecordCadenceAndComputeOiNotional(t1);
        Assert.Null(oiNotional);
        Assert.Null(oiChangeNotional);
    }

    [Fact]
    public void OiNotional_ValuesOiAtThe1MinuteAveragedPrice_NotTheSpikedInstantaneousPrice()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        var t0 = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);

        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 100, oi: 500_000, depth: Depth(44.90m, 45.10m)));
        state.RecordCadenceAndComputeOiNotional(t0);
        state.ResetCadence();

        // OI itself doesn't refresh this cadence (matches the feed's real ~60s OI refresh rate),
        // but price spikes to 47.00 -- OiNotional should reflect the averaged 46.00, not 47.00.
        state.ApplyTick(MakeTick("TOKEN1", 47.00m, volume: 100, depth: Depth(46.90m, 47.10m)));
        var (oiNotional, _) = state.RecordCadenceAndComputeOiNotional(t1);

        Assert.Equal(500_000m * 46.00m, oiNotional);
    }

    [Fact]
    public void OiChangeNotional_NullOnFirstOiObservation_EvenWhenOiNotionalItselfIsAlreadyReal()
    {
        // OiNotional only needs the OI *level* (LatestOpenInterest), so it can be real the very
        // first cadence OI is ever seen. OiChangeNotional needs a prior baseline to diff against
        // (CadenceHasOiBaseline), which doesn't exist yet on that same first observation -- this
        // isolates that distinction from the window-warmup null case tested above.
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        var t0 = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);

        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 100, depth: Depth(44.90m, 45.10m))); // no OI yet
        state.RecordCadenceAndComputeOiNotional(t0);
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 46.00m, volume: 100, oi: 500_000, depth: Depth(45.90m, 46.10m))); // OI seen for the first time
        var (oiNotional, oiChangeNotional) = state.RecordCadenceAndComputeOiNotional(t1);

        Assert.Equal(500_000m * 45.50m, oiNotional); // real -- only needs the level, averaged price = (45.00+46.00)/2
        Assert.Null(oiChangeNotional); // still null -- no prior OI baseline to diff against yet
    }

    [Fact]
    public void OiChangeNotional_ValuesTheOiDeltaAtTheSame1MinuteAveragedPrice_AsOiNotional()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        var t0 = new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(1);

        state.ApplyTick(MakeTick("TOKEN1", 45.00m, volume: 100, oi: 500_000, depth: Depth(44.90m, 45.10m)));
        state.RecordCadenceAndComputeOiNotional(t0);
        state.ResetCadence();

        state.ApplyTick(MakeTick("TOKEN1", 47.00m, volume: 100, oi: 500_800, depth: Depth(46.90m, 47.10m))); // +800 OI
        var (oiNotional, oiChangeNotional) = state.RecordCadenceAndComputeOiNotional(t1);

        // Averaged price = (45.00 + 47.00) / 2 = 46.00, shared by both values.
        Assert.Equal(500_800m * 46.00m, oiNotional);
        Assert.Equal(800m * 46.00m, oiChangeNotional);
    }

    [Fact]
    public void ApplyTick_FeedsTheSameClassifiedDeltaIntoCvdProxy()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.40m, volume: 125_000, depth: Depth(45.00m, 45.40m)));
        state.ApplyTick(MakeTick("TOKEN1", 45.40m, volume: 125_400, depth: Depth(45.00m, 45.40m))); // +400, at the ask -- buy-leaning

        Assert.Equal(400, state.CvdProxy.CadenceVolumeNet);
    }

    static MarketDepth DepthWithQty(long bidQty, long askQty) => new(
        Bid1Price: 45.00m, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: 45.40m, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    [Fact]
    public void ApplyTick_FeedsTheSameDepthIntoDepthImbalance_RegardlessOfWhetherVolumeTraded()
    {
        // Resting liquidity is sampled every tick with a two-sided quote -- unlike CvdProxy, this
        // does not require any volume to have traded (zero volumeDelta below).
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, depth: DepthWithQty(910, 325)));

        Assert.Equal((910.0 - 325.0) / (910.0 + 325.0), state.DepthImbalance.CadenceImbalance);
        Assert.Equal(910.0, state.DepthImbalance.AverageBidQty);
        Assert.Equal(325.0, state.DepthImbalance.AverageAskQty);
    }

    [Fact]
    public void ResetCadence_ClearsDepthImbalanceToo()
    {
        var state = new OptionInstrumentState(23800m, OptionType.Call, new DateOnly(2026, 9, 15));
        state.ApplyTick(MakeTick("TOKEN1", 45.20m, volume: 125_000, depth: DepthWithQty(910, 325)));
        state.ResetCadence();

        Assert.Null(state.DepthImbalance.CadenceImbalance);
    }
}

public sealed class OffsetInBandTests
{
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(-2, false)]
    [InlineData(2, false)]
    public void Strike3_IsAtmPlusMinusOne_SameForBothOptionTypes(int offset, bool expected)
    {
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike3", OptionType.Call, offset));
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike3", OptionType.Put, offset));
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(2, true)]
    [InlineData(-3, false)]
    [InlineData(3, false)]
    public void Strike5_IsAtmPlusMinusTwo_SameForBothOptionTypes(int offset, bool expected)
    {
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike5", OptionType.Call, offset));
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike5", OptionType.Put, offset));
    }

    [Theory]
    [InlineData(-3, true)]
    [InlineData(3, true)]
    [InlineData(-4, false)]
    [InlineData(4, false)]
    public void Strike7_IsAtmPlusMinusThree_SameForBothOptionTypes(int offset, bool expected)
    {
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike7", OptionType.Call, offset));
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Strike7", OptionType.Put, offset));
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void Itm2Atm1_ForCalls_IsAtmAndTheTwoStrikesBelow_ItmSideForCalls(int offset, bool expected)
    {
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Itm2Atm1", OptionType.Call, offset));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(-1, false)]
    [InlineData(-2, false)]
    public void Itm2Atm1_ForPuts_IsAtmAndTheTwoStrikesAbove_ItmSideForPuts_MirroredFromCalls(int offset, bool expected)
    {
        Assert.Equal(expected, CadencePopulator.OffsetInBand("Itm2Atm1", OptionType.Put, offset));
    }
}
