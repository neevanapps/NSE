using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (sections 24-39), plus the user's own three
/// explicit rules for this pass. Coverage focuses on those three rules plus the basic
/// entry/exit/forced-close state machine, using hand-built bars/ticks rather than the real bar
/// builders (those have their own dedicated test files).
/// </summary>
public sealed class Vc0DteTradeSimulatorTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);
    const string Call25000 = "NIFTY-25000CE-TOK";
    const string Call25100 = "NIFTY-25100CE-TOK";
    const string Put25000 = "NIFTY-25000PE-TOK";

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    static Instrument CallInstrument(string token, decimal strike) => OptionInstrument(token, strike, OptionType.Call);

    static Instrument OptionInstrument(string token, decimal strike, OptionType side) => new()
    {
        Token = token, Exchange = Exchange.Nfo, TradingSymbol = token,
        InstrumentType = InstrumentType.Option, OptionType = side, StrikePrice = strike,
        ExpiryDate = AsOfDate, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
    };

    static void AddTick(NiftySignalDbContext source, string token, DateTimeOffset at, decimal price) =>
        source.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = price, Volume = 0 });

    static List<FutureEventBar> Bars(int count, TimeSpan barLength, decimal close = 25000m) =>
        Enumerable.Range(0, count).Select(n => new FutureEventBar(
            n, AsOfDate, DayStart + (barLength * n), DayStart + (barLength * (n + 1)),
            close, close, close, close, 1300, (double?)close, null, 10, IsFinalPartialBar: n == count - 1))
        .ToList();

    static SynchronizedOptionEventBar OptionBar(FutureEventBar fb, string token, decimal strike, decimal? avgLtp, decimal? close)
        => OptionBar(fb, token, strike, OptionType.Call, avgLtp, close);

    static SynchronizedOptionEventBar OptionBar(FutureEventBar fb, string token, decimal strike, OptionType side, decimal? avgLtp, decimal? close) => new(
        fb.EventId, AsOfDate, fb.StartTimestamp, fb.EndTimestamp, strike, side, 0, token,
        avgLtp, avgLtp, avgLtp, close ?? avgLtp, avgLtp, avgLtp is { } a ? (double)a : null,
        avgLtp is null ? 0 : 1, 0, fb.StartTimestamp, fb.EndTimestamp,
        MissingData: avgLtp is null, IsStale: avgLtp is null, LastKnownPrice: null, AgeMs: null);

    [Fact]
    public async Task SimulateDayAsync_FullCycle_EntersOnCrossUp_ExitsOnCrossDown()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(CallInstrument(Call25000, 25000m));

        var barLength = TimeSpan.FromMinutes(5);
        var futureBars = Bars(4, barLength);
        // fastBars=1/slowBars=2: bar0/1 warm the window flat at 100 (diff=0), bar2 jumps to 150
        // (crosses up), bar3 drops to 90 (crosses down) -- same hand-computed shape
        // PriceCrossoverEngineTests already establishes for this engine.
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[2], Call25000, 25000m, 150m, 130m), // 130 is the "current premium" used for the [100,150] filter -- inside the band.
            OptionBar(futureBars[3], Call25000, 25000m, 90m, 90m),
        };

        // Real ticks for the actual entry/exit fills (no MarketDepth -- exercises the LTP-fallback
        // execution model, spec section 29).
        AddTick(source, Call25000, futureBars[2].EndTimestamp.AddSeconds(10), 131m);
        AddTick(source, Call25000, futureBars[3].EndTimestamp.AddSeconds(10), 89m);
        await source.SaveChangesAsync();

        var result = await Vc0DteTradeSimulator.SimulateDayAsync(
            source, AsOfDate, source.Instruments.ToList(), futureBars, optionBars, CancellationToken.None,
            fastBars: 1, slowBars: 2);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(25000m, trade.Strike);
        Assert.Equal(OptionType.Call, trade.OptionType);
        Assert.Equal("CrossoverReversed", trade.ExitReason);
        Assert.Equal(Vc0DteTradeSimulator.ExecutionModel.LtpFallback, trade.EntryExecutionModel);
        Assert.Equal(65, trade.Quantity);
        Assert.True(trade.NetPnl < 0, "Price dropped from entry to exit -- net P&L must be negative.");
        Assert.Empty(result.RejectedSignals);

        // Signal-funnel accounting (forensic-verification addendum): exactly one CE up-cross and
        // one CE down-cross were observed, both accounted for as the single trade's entry/exit.
        Assert.Equal(1, result.Funnel.CeCrossUp);
        Assert.Equal(1, result.Funnel.CeCrossDown);
        Assert.Equal(0, result.Funnel.PeCrossUp + result.Funnel.PeCrossDown);
        Assert.Equal(0, result.Funnel.SignalsWhileWarmingUp); // structurally always 0.
        Assert.Equal(1, result.Funnel.TradesOpened);
        Assert.Equal(1, result.Funnel.CeTrades);
        Assert.Equal(1, result.Funnel.ReversalExits);
        Assert.Equal(0, result.Funnel.EntrySignalsIgnoredPositionAlreadyOpen);
    }

    [Fact]
    public async Task SimulateDayAsync_SignalOnOtherSideWhilePositionOpen_IsCountedAsIgnored_NeverOpensASecondPosition()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(CallInstrument(Call25000, 25000m));
        source.Instruments.Add(OptionInstrument(Put25000, 25000m, OptionType.Put));

        var barLength = TimeSpan.FromMinutes(5);
        var futureBars = Bars(4, barLength);
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[2], Call25000, 25000m, 150m, 130m), // CE crosses up -> opens a Call position.
            OptionBar(futureBars[3], Call25000, 25000m, 150m, 130m), // CE stays flat/positive -- no exit.

            OptionBar(futureBars[0], Put25000, 25000m, OptionType.Put, 100m, 100m),
            OptionBar(futureBars[1], Put25000, 25000m, OptionType.Put, 100m, 100m),
            OptionBar(futureBars[2], Put25000, 25000m, OptionType.Put, 100m, 100m),
            OptionBar(futureBars[3], Put25000, 25000m, OptionType.Put, 150m, 130m), // PE crosses up WHILE the Call position is still open.
        };

        AddTick(source, Call25000, futureBars[2].EndTimestamp.AddSeconds(10), 131m);
        await source.SaveChangesAsync();

        var result = await Vc0DteTradeSimulator.SimulateDayAsync(
            source, AsOfDate, source.Instruments.ToList(), futureBars, optionBars, CancellationToken.None,
            fastBars: 1, slowBars: 2);

        Assert.Equal(1, result.Funnel.PeCrossUp); // the PE crossing was real and observed...
        Assert.Equal(1, result.Funnel.EntrySignalsIgnoredPositionAlreadyOpen); // ...but ignored, not silently invisible.
        Assert.Equal(0, result.Funnel.PeTrades); // and never became a second, pyramided position.
        Assert.Equal(1, result.Funnel.CeTrades); // the original Call position is untouched.
    }

    [Fact]
    public async Task SimulateDayAsync_StrikeSelection_SkipsAtmWhenOutOfBand_PicksNearestInBandStrike()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(CallInstrument(Call25000, 25000m)); // ATM, premium out of [100,150] band.
        source.Instruments.Add(CallInstrument(Call25100, 25100m)); // next-nearest, premium in band.

        var barLength = TimeSpan.FromMinutes(5);
        var futureBars = Bars(3, barLength, close: 25000m); // futures close pins ATM to the 25000 strike.
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, 100m, 300m),
            OptionBar(futureBars[1], Call25000, 25000m, 100m, 300m),
            OptionBar(futureBars[2], Call25000, 25000m, 150m, 300m), // ATM's own premium (300) stays OUT of [100,150] throughout.
        };
        optionBars.Add(OptionBar(futureBars[0], Call25100, 25100m, 50m, 120m));
        optionBars.Add(OptionBar(futureBars[1], Call25100, 25100m, 50m, 120m));
        optionBars.Add(OptionBar(futureBars[2], Call25100, 25100m, 50m, 120m)); // in-band the whole time.

        AddTick(source, Call25100, futureBars[2].EndTimestamp.AddSeconds(10), 121m);
        await source.SaveChangesAsync();

        var result = await Vc0DteTradeSimulator.SimulateDayAsync(
            source, AsOfDate, source.Instruments.ToList(), futureBars, optionBars, CancellationToken.None,
            fastBars: 1, slowBars: 2);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(25100m, trade.Strike); // NOT the ATM strike -- the in-band one instead.
    }

    [Fact]
    public async Task SimulateDayAsync_SignalAfter1500Ist_IsRejectedAsOutsideSession_NeverOpensATrade()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(CallInstrument(Call25000, 25000m));

        var barLength = TimeSpan.FromMinutes(5);
        // Anchor the warm-up/crossing bars right at 15:00-15:10 IST so the crossing bar (event 2) lands after the cutoff.
        var lateStart = new DateTimeOffset(AsOfDate.ToDateTime(new TimeOnly(14, 55)), IstOffset);
        var futureBars = Enumerable.Range(0, 3).Select(n => new FutureEventBar(
            n, AsOfDate, lateStart + (barLength * n), lateStart + (barLength * (n + 1)),
            25000m, 25000m, 25000m, 25000m, 1300, 25000.0, null, 10, n == 2)).ToList();
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[2], Call25000, 25000m, 150m, 130m), // crosses up at 15:05-15:10 IST, after the 15:00 cutoff.
        };
        await source.SaveChangesAsync();

        var result = await Vc0DteTradeSimulator.SimulateDayAsync(
            source, AsOfDate, source.Instruments.ToList(), futureBars, optionBars, CancellationToken.None,
            fastBars: 1, slowBars: 2);

        Assert.Empty(result.Trades);
        var rejection = Assert.Single(result.RejectedSignals);
        Assert.Equal(Vc0DteTradeSimulator.RejectReason.OutsideSession, rejection.ReasonRejected);
    }

    [Fact]
    public async Task SimulateDayAsync_PositionStillOpenAt1515Ist_IsForcedClosed()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(CallInstrument(Call25000, 25000m));

        var barLength = TimeSpan.FromMinutes(5);
        var futureBars = Bars(3, barLength); // event2 (the crossing bar) ends at DayStart+15min = 09:30 IST -- well before 15:00, so entry succeeds.
        var optionBars = new List<SynchronizedOptionEventBar>
        {
            OptionBar(futureBars[0], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[1], Call25000, 25000m, 100m, 100m),
            OptionBar(futureBars[2], Call25000, 25000m, 150m, 130m), // crosses up, opens a position; never crosses back down.
        };

        AddTick(source, Call25000, futureBars[2].EndTimestamp.AddSeconds(10), 131m); // entry fill
        // Last real tick, well before the 15:15 forced-close cutoff -- the forced close must use
        // THIS tick, never a manufactured 15:30 print.
        var lastRealTickAt = new DateTimeOffset(AsOfDate.ToDateTime(new TimeOnly(10, 0)), IstOffset);
        AddTick(source, Call25000, lastRealTickAt, 140m);
        await source.SaveChangesAsync();

        var result = await Vc0DteTradeSimulator.SimulateDayAsync(
            source, AsOfDate, source.Instruments.ToList(), futureBars, optionBars, CancellationToken.None,
            fastBars: 1, slowBars: 2);

        var trade = Assert.Single(result.Trades);
        Assert.Equal("ForcedEod", trade.ExitReason);
        Assert.Equal(140m, trade.ExitLtp);
    }
}
