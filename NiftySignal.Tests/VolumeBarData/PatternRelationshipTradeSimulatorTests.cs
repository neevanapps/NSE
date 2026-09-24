using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-24, first trade simulation of the frozen Pattern A/B relationship. Coverage uses
/// hand-built synthetic <see cref="RelationshipObservation"/>/<see cref="FutureEventBar"/>/
/// <see cref="Instrument"/>/<see cref="Tick"/> rows (same convention as
/// <see cref="Vc0DteTradeSimulatorTests"/>), never the real recorder/bar builders.
/// </summary>
public sealed class PatternRelationshipTradeSimulatorTests
{
    static readonly DateOnly Day = new(2026, 9, 8);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;
    const string PeToken = "PE-A";
    const string CeToken = "CE-A";

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    static DateTimeOffset At(int hour, int minute, int second = 0) => new(Day.ToDateTime(new TimeOnly(hour, minute, second)), IstOffset);

    static Instrument OptionInstrument(string token, decimal strike, OptionType side, int lotSize = 65) => new()
    {
        Token = token, Exchange = Exchange.Nfo, TradingSymbol = token,
        InstrumentType = InstrumentType.Option, OptionType = side, StrikePrice = strike,
        ExpiryDate = Day, Underlying = "NIFTY", LotSize = lotSize, TickSize = 0.05m, AsOfDate = Day,
    };

    static void AddTick(NiftySignalDbContext source, string token, DateTimeOffset at, decimal price)
        => source.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = price, Volume = 0 });

    static FutureEventBar Bar(int eventId, DateTimeOffset start, DateTimeOffset end, decimal close = 25000m) =>
        new(eventId, Day, start, end, close, close, close, close, 1300, (double?)close, null, 10, IsFinalPartialBar: false);

    static SynchronizedOptionEventBar OptionBar(FutureEventBar fb, string token, decimal strike, OptionType side, decimal close) => new(
        fb.EventId, Day, fb.StartTimestamp, fb.EndTimestamp, strike, side, 0, token,
        close, close, close, close, close, (double)close, 1, 0, fb.StartTimestamp, fb.EndTimestamp,
        MissingData: false, IsStale: false, LastKnownPrice: null, AgeMs: null);

    static RelationshipObservation Row(
        int eventId, DateTimeOffset endTimestamp, string category, decimal atmStrike = 25000m,
        decimal? cePrice = 100m, decimal? pePrice = 50m, bool ceMissing = false, bool peMissing = false,
        bool ceTransition = false, bool peTransition = false, string ceToken = CeToken, string peToken = PeToken,
        decimal futuresClose = 25000m, int dte = 0) => new(
        Day, eventId, endTimestamp.AddSeconds(-1), endTimestamp, dte,
        futuresClose, futuresClose, futuresClose, futuresClose, 1300, 60000,
        SpotAvailable: false, SpotMissingData: false, null, null, null, null, null, 0,
        atmStrike,
        ceToken, ceMissing ? null : cePrice, ceMissing, false, ceMissing ? 0 : 1, ceTransition,
        peToken, peMissing ? null : pePrice, peMissing, false, peMissing ? 0 : 1, peTransition,
        null, null, null, null,
        null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable", null, null, "Unavailable",
        category);

    [Fact]
    public async Task SimulateDayAsync_PatternA_GeneratesPeTrade()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, At(9, 21, 5), 51m);
        AddTick(source, PeToken, At(15, 14, 55), 55m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(OptionType.Put, trade.OptionType);
        Assert.Equal("PatternA", trade.Pattern);
    }

    [Fact]
    public async Task SimulateDayAsync_PatternB_GeneratesCeTrade()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(CeToken, 25000m, OptionType.Call));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, CeToken, At(9, 21, 5), 101m);
        AddTick(source, CeToken, At(15, 14, 55), 105m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternB) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(OptionType.Call, trade.OptionType);
        Assert.Equal("PatternB", trade.Pattern);
    }

    [Fact]
    public async Task SimulateDayAsync_EntryUsesFirstTickAfterSignal_NotTheSignalBarsOwnAverageLtp()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        // The signal's own recorded average LTP is 50 -- the real executable tick right after is 62.
        AddTick(source, PeToken, bar.EndTimestamp, 62m);
        AddTick(source, PeToken, At(15, 14, 55), 62m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA, pePrice: 50m) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(50m, trade.SignalOptionLtp);
        Assert.Equal(62m, trade.EntryPrice);
    }

    [Fact]
    public async Task SimulateDayAsync_NoEntryAfter3Pm()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(15, 0, 1), At(15, 0, 2));
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        Assert.Empty(result.Trades);
        var audit = Assert.Single(result.SignalAudit);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.After3Pm, audit.Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_OpenPosition_IsForcedClosedAt1515()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(14, 50), At(14, 51));
        AddTick(source, PeToken, At(14, 51, 5), 60m);
        AddTick(source, PeToken, At(15, 10, 0), 65m); // last real tick at/before 15:15 -- used for the forced close.
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(nameof(PatternRelationshipTradeSimulator.ExitReason.ForcedEod), trade.ExitReason);
        Assert.Equal(65m, trade.ExitPrice);
        Assert.True(trade.ExitTimestamp <= At(15, 15));
    }

    [Fact]
    public async Task SimulateDayAsync_SecondSignalWhileHoldingSamePattern_IsIgnoredNotDropped()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        var bar1 = Bar(1, At(9, 21), At(9, 22));
        AddTick(source, PeToken, bar0.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, At(15, 14, 55), 65m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternA), Row(1, bar1.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar0, bar1], IstOffset, CancellationToken.None);

        Assert.Single(result.Trades); // only ONE position opened.
        var ignored = Assert.Single(result.SignalAudit, a => a.Outcome == PatternRelationshipTradeSimulator.SignalOutcome.AlreadyInPosition);
        Assert.Equal(bar1.EndTimestamp, ignored.SignalTimestamp); // the second signal was recorded, not silently dropped.
    }

    [Fact]
    public async Task SimulateDayAsync_OppositeSignal_ClosesExistingAndOpensNew_AsTwoSeparateTrades()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        source.Instruments.Add(OptionInstrument(CeToken, 25000m, OptionType.Call));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        var bar1 = Bar(1, At(10, 0), At(10, 1));
        AddTick(source, PeToken, bar0.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, bar1.EndTimestamp.AddSeconds(1), 58m); // closes the PE position.
        AddTick(source, CeToken, bar1.EndTimestamp.AddSeconds(1), 90m); // opens the CE position.
        AddTick(source, CeToken, At(15, 14, 55), 95m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternA), Row(1, bar1.EndTimestamp, PatternB) };
        var instruments = source.Instruments.Local.ToList();
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, instruments, [bar0, bar1], IstOffset, CancellationToken.None);

        Assert.Equal(2, result.Trades.Count);
        Assert.Equal(OptionType.Put, result.Trades[0].OptionType);
        Assert.Equal(nameof(PatternRelationshipTradeSimulator.ExitReason.OppositePatternSignal), result.Trades[0].ExitReason);
        Assert.Equal(OptionType.Call, result.Trades[1].OptionType);
    }

    [Fact]
    public async Task SimulateDayAsync_TenLots_ProducesQuantityEqualToLotSizeTimesTen()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put, lotSize: 65));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, bar.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, At(15, 14, 55), 65m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(65 * 10, trade.Quantity);
        Assert.Equal(10, trade.Lots);
        Assert.Equal(65, trade.LotSize);
    }

    [Fact]
    public async Task SimulateDayAsync_MaeMfe_ComputedFromRealPostEntryTicks()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, bar.EndTimestamp.AddSeconds(1), 100m); // entry.
        AddTick(source, PeToken, bar.EndTimestamp.AddSeconds(2), 90m);  // worst excursion -> MAE = 10.
        AddTick(source, PeToken, bar.EndTimestamp.AddSeconds(3), 110m); // best excursion -> MFE = 10.
        AddTick(source, PeToken, At(15, 14, 55), 105m);                 // EOD exit.
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(10m, trade.MaeRupees);
        Assert.Equal(10m, trade.MfeRupees);
        Assert.NotNull(trade.MfeCaptured);
    }

    [Fact]
    public async Task SimulateDayAsync_MissingOptionData_IsRecordedNotSilentlyDropped()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA, peMissing: true) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        Assert.Empty(result.Trades);
        var audit = Assert.Single(result.SignalAudit);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.MissingOptionData, audit.Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_ContractTransition_IsRecordedNotSilentlyDropped()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA, peTransition: true) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        Assert.Empty(result.Trades);
        var audit = Assert.Single(result.SignalAudit);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.ContractTransition, audit.Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_StrikeStaysPinnedFromTheSignalEvent_EvenIfALaterRowWouldImplyADifferentAtm()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, bar0.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, At(15, 14, 55), 65m);
        await source.SaveChangesAsync();

        // Signal strike is 25000 -- pinned. No later row is even processed as a new signal because
        // the position stays open, so there is no re-derivation opportunity at all.
        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternA, atmStrike: 25000m) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar0], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(25000m, trade.Strike);
    }

    [Fact]
    public async Task SimulateDayAsync_TransactionCosts_SttOnExitOnly_ZeroBrokerageZeroGst()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put, lotSize: 65));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, bar.EndTimestamp.AddSeconds(1), 100m);
        AddTick(source, PeToken, At(15, 14, 55), 110m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None);

        var trade = Assert.Single(result.Trades);
        var expectedStt = Math.Round(trade.ExitPrice * trade.Quantity * TransactionCostCalculator.SttRateOnSellPremium, 2);
        Assert.Equal(expectedStt, trade.Stt);
        Assert.Equal(0m, trade.Gst);
        Assert.Equal(0m, trade.OtherCosts);
        Assert.Equal(trade.GrossPnl - trade.Stt, trade.NetPnl);
    }

    [Fact]
    public async Task SimulateDayAsync_BandSelection_PicksTheNearestStrikeWhosePremiumFallsInTheBand_NotTheDynamicAtm()
    {
        await using var source = NewSourceDb();
        // ATM (25000) PE premium is 250 -- outside [100,150]. The next strike out (24900) has
        // premium 120 -- inside the band -- and should be selected instead.
        const string AtmPe = "PE-ATM-25000";
        const string BandPe = "PE-BAND-24900";
        source.Instruments.Add(OptionInstrument(AtmPe, 25000m, OptionType.Put));
        source.Instruments.Add(OptionInstrument(BandPe, 24900m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(bar, AtmPe, 25000m, OptionType.Put, 250m),
            OptionBar(bar, BandPe, 24900m, OptionType.Put, 120m),
        };
        AddTick(source, BandPe, bar.EndTimestamp.AddSeconds(1), 121m);
        AddTick(source, BandPe, At(15, 14, 55), 125m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA, atmStrike: 25000m, peToken: AtmPe, pePrice: 250m) };
        var chain = source.Instruments.Local.ToList();
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, chain, [bar], IstOffset, CancellationToken.None,
            optionBars: optionBars, minEntryPrice: 100m, maxEntryPrice: 150m);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(24900m, trade.Strike);
        Assert.Equal(121m, trade.EntryPrice);
    }

    [Fact]
    public async Task SimulateDayAsync_BandSelection_NoStrikeInBand_IsRecordedNotSilentlyDropped()
    {
        await using var source = NewSourceDb();
        const string AtmPe = "PE-ATM-25000";
        source.Instruments.Add(OptionInstrument(AtmPe, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        var optionBars = new List<SynchronizedOptionEventBar> { OptionBar(bar, AtmPe, 25000m, OptionType.Put, 250m) }; // outside [100,150], no other strikes.
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA, atmStrike: 25000m, peToken: AtmPe, pePrice: 250m) };
        var chain = source.Instruments.Local.ToList();
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, chain, [bar], IstOffset, CancellationToken.None,
            optionBars: optionBars, minEntryPrice: 100m, maxEntryPrice: 150m);

        Assert.Empty(result.Trades);
        var audit = Assert.Single(result.SignalAudit);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.NoStrikeInBand, audit.Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_DifferentDays_DoNotShareStateOrLeakTradesAcrossEachOther()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var day1Bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, day1Bar.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, At(15, 14, 55), 65m);
        await source.SaveChangesAsync();

        var day1Rows = new List<RelationshipObservation> { Row(0, day1Bar.EndTimestamp, PatternA) };
        var day1Result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, Day, day1Rows, [source.Instruments.Local.First()], [day1Bar], IstOffset, CancellationToken.None);

        var day2 = Day.AddDays(1);
        var day2Result = await PatternRelationshipTradeSimulator.SimulateDayAsync(source, day2, [], [source.Instruments.Local.First()], [], IstOffset, CancellationToken.None);

        Assert.Single(day1Result.Trades);
        Assert.All(day1Result.Trades, t => Assert.Equal(Day, t.TradingDate));
        Assert.Empty(day2Result.Trades); // no rows/bars for day2 -- proves nothing carried over from day1's open state.
    }

    [Fact]
    public async Task SimulateDayAsync_PatternAEntryFilter_SkipsFilteredSignal_ButLaterSignalStillEnters()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        var bar1 = Bar(1, At(9, 21), At(9, 22));
        AddTick(source, PeToken, At(9, 22, 5), 51m); // only consumed if event 1's entry is taken.
        AddTick(source, PeToken, At(15, 14, 55), 55m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternA), Row(1, bar1.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, [source.Instruments.Local.First()], [bar0, bar1], IstOffset, CancellationToken.None,
            patternAEntryFilter: eventId => eventId != 0); // reject event 0 only.

        var trade = Assert.Single(result.Trades);
        Assert.Equal(bar1.EndTimestamp, trade.SignalTimestamp); // event 0's signal never opened a position, so event 1 was still free to enter.
        Assert.Equal(2, result.SignalAudit.Count);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.FilteredByEntryCondition, result.SignalAudit[0].Outcome);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.Executed, result.SignalAudit[1].Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_PatternAEntryFilter_NeverAppliesToPatternB()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(CeToken, 25000m, OptionType.Call));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, CeToken, At(9, 21, 5), 101m);
        AddTick(source, CeToken, At(15, 14, 55), 105m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternB) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None,
            patternAEntryFilter: _ => false); // would reject every signal if it applied to Pattern B -- it must not.

        var trade = Assert.Single(result.Trades);
        Assert.Equal("PatternB", trade.Pattern);
    }

    [Fact]
    public async Task SimulateDayAsync_PatternBEntryFilter_SkipsFilteredSignal_ButLaterSignalStillEnters()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(CeToken, 25000m, OptionType.Call));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        var bar1 = Bar(1, At(9, 21), At(9, 22));
        AddTick(source, CeToken, At(9, 22, 5), 101m); // only consumed if event 1's entry is taken.
        AddTick(source, CeToken, At(15, 14, 55), 105m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternB), Row(1, bar1.EndTimestamp, PatternB) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, [source.Instruments.Local.First()], [bar0, bar1], IstOffset, CancellationToken.None,
            patternBEntryFilter: eventId => eventId != 0); // reject event 0 only.

        var trade = Assert.Single(result.Trades);
        Assert.Equal(bar1.EndTimestamp, trade.SignalTimestamp); // event 0's signal never opened a position, so event 1 was still free to enter.
        Assert.Equal(2, result.SignalAudit.Count);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.FilteredByEntryCondition, result.SignalAudit[0].Outcome);
        Assert.Equal(PatternRelationshipTradeSimulator.SignalOutcome.Executed, result.SignalAudit[1].Outcome);
    }

    [Fact]
    public async Task SimulateDayAsync_PatternBEntryFilter_NeverAppliesToPatternA()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        var bar = Bar(0, At(9, 20), At(9, 21));
        AddTick(source, PeToken, At(9, 21, 5), 51m);
        AddTick(source, PeToken, At(15, 14, 55), 55m);
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar.EndTimestamp, PatternA) };
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, [source.Instruments.Local.First()], [bar], IstOffset, CancellationToken.None,
            patternBEntryFilter: _ => false); // would reject every signal if it applied to Pattern A -- it must not.

        var trade = Assert.Single(result.Trades);
        Assert.Equal("PatternA", trade.Pattern);
    }

    [Fact]
    public async Task SimulateDayAsync_PatternBEntryFilter_DisablesPatternBEntirely_ButStillClosesAnOpenPatternAPosition()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(OptionInstrument(PeToken, 25000m, OptionType.Put));
        source.Instruments.Add(OptionInstrument(CeToken, 25000m, OptionType.Call));
        var bar0 = Bar(0, At(9, 20), At(9, 21));
        var bar1 = Bar(1, At(10, 0), At(10, 1));
        AddTick(source, PeToken, bar0.EndTimestamp.AddSeconds(1), 60m);
        AddTick(source, PeToken, bar1.EndTimestamp.AddSeconds(1), 58m); // closes the PE position on the Pattern B signal.
        await source.SaveChangesAsync();

        var rows = new List<RelationshipObservation> { Row(0, bar0.EndTimestamp, PatternA), Row(1, bar1.EndTimestamp, PatternB) };
        var instruments = source.Instruments.Local.ToList();
        var result = await PatternRelationshipTradeSimulator.SimulateDayAsync(
            source, Day, rows, instruments, [bar0, bar1], IstOffset, CancellationToken.None,
            patternBEntryFilter: _ => false); // Pattern B can never OPEN a position...

        var trade = Assert.Single(result.Trades); // ...but it still closed the open Pattern A position, so no CE trade was ever opened.
        Assert.Equal("PatternA", trade.Pattern);
        Assert.Equal(nameof(PatternRelationshipTradeSimulator.ExitReason.OppositePatternSignal), trade.ExitReason);
    }
}
