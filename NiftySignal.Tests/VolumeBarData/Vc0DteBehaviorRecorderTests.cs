using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, multi-day behaviour validation addendum. Coverage for the NEW, additive fields on
/// <see cref="Vc0DteBehaviorRecorder.ObservationRow"/> -- futures-vs-option comparison (point 5),
/// pre-signal lead/lag (point 6), event-bars-to-extreme (point 3), and window duration (point 9).
/// The original 17 fields' own formulas are unchanged and already covered by real-data runs in
/// docs/VolumeCandle_0DTE_Findings.md -- this file only exercises what's new.
/// </summary>
public sealed class Vc0DteBehaviorRecorderTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);
    const string Call25000 = "NIFTY-25000CE-TOK";

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    [Fact]
    public async Task RecordAsync_NewAdditiveFields_ComputeCorrectly()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(new Instrument
        {
            Token = Call25000, Exchange = Exchange.Nfo, TradingSymbol = Call25000,
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 25000m,
            ExpiryDate = AsOfDate, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
        });

        var barLength = TimeSpan.FromMinutes(1);
        // Futures steadily +10 per bar; option flat at 100 for 3 bars (warms fastBars=1/slowBars=3
        // with diff=0, no crossing), then jumps to 200 at event3 (crossUp), then 210 at event4.
        var futureCloses = new decimal[] { 25000, 25010, 25020, 25030, 25040 };
        var futureBars = Enumerable.Range(0, 5).Select(n => new FutureEventBar(
            n, AsOfDate, DayStart + (barLength * n), DayStart + (barLength * (n + 1)),
            futureCloses[n], futureCloses[n], futureCloses[n], futureCloses[n], 1300, (double?)futureCloses[n], null, 10, IsFinalPartialBar: n == 4))
            .ToList();

        var optionLtps = new decimal?[] { 100, 100, 100, 200, 210 };
        var optionBars = Enumerable.Range(0, 5).Select(n => new SynchronizedOptionEventBar(
            n, AsOfDate, futureBars[n].StartTimestamp, futureBars[n].EndTimestamp, 25000m, OptionType.Call, 0, Call25000,
            optionLtps[n], optionLtps[n], optionLtps[n], optionLtps[n], optionLtps[n], optionLtps[n] is { } a ? (double)a : null,
            1, 0, futureBars[n].StartTimestamp, futureBars[n].EndTimestamp, MissingData: false, IsStale: false, LastKnownPrice: null, AgeMs: null))
            .ToList();

        // MFE tick (peak 250) and MAE tick (trough 150) both land inside event4's own interval
        // (bar3.End .. bar3.End+60s) -- one event bar after the eventId=3 signal.
        source.Ticks.Add(new Tick { Token = Call25000, Exchange = Exchange.Nfo, ExchangeTimestamp = futureBars[3].EndTimestamp.AddSeconds(10), ReceivedAt = futureBars[3].EndTimestamp.AddSeconds(10), LastPrice = 250m, Volume = 0 });
        source.Ticks.Add(new Tick { Token = Call25000, Exchange = Exchange.Nfo, ExchangeTimestamp = futureBars[3].EndTimestamp.AddSeconds(50), ReceivedAt = futureBars[3].EndTimestamp.AddSeconds(50), LastPrice = 150m, Volume = 0 });
        await source.SaveChangesAsync();

        var chain = new List<Instrument> { source.Instruments.Single() };
        var rows = await Vc0DteBehaviorRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None, fastBars: 1, slowBars: 3);

        var signal = Assert.Single(rows); // only one real crossing: the flat-to-200 jump at event3.
        Assert.Equal(3, signal.EventId);
        Assert.Equal(200m, signal.PriceAtSignal);

        // Point 5: futures-vs-option comparison.
        Assert.Equal(25030m, signal.FutureCloseAtSignal);
        Assert.Equal((25040m - 25030m) / 25030m * 100m, signal.FutureForwardReturnPercent1);

        // Point 4: absolute (Rs) alongside percent, same pinned contract.
        Assert.Equal(5m, signal.ForwardReturn1); // (210-200)/200*100
        Assert.Equal(10m, signal.AbsoluteForwardReturn1); // 210-200, not a percent.

        // Point 6: pre-signal lead/lag (event3 minus 3 = event0).
        Assert.Equal(100m, signal.PreSignalOptionMovePercent3); // (200-100)/100*100
        Assert.True(signal.PreSignalFutureMovePercent3 is { } preFut && preFut > 0); // futures steadily rising into the signal too.

        // Point 3: event-bars to MFE/MAE -- both extremes land in event4, one bar after the signal.
        Assert.Equal(1, signal.EventBarsToMaximumFavorable);
        Assert.Equal(1, signal.EventBarsToMaximumAdverse);
        Assert.Equal(50m, signal.MaximumFavorableMoveAbs); // 250-200
        Assert.Equal(50m, signal.MaximumAdverseMoveAbs); // 200-150

        // Point 9: window duration -- fastBars=1 spans exactly one bar (60s); slowBars=3 spans
        // three contiguous bars (180s), both ending at the signal bar's own close.
        Assert.Equal(60000, signal.FastWindowDurationMs);
        Assert.Equal(180000, signal.SlowWindowDurationMs);
    }

    [Fact]
    public async Task RecordAsync_ForwardHorizonPastTheLastBar_IsNullNeverFabricated()
    {
        await using var source = NewSourceDb();
        source.Instruments.Add(new Instrument
        {
            Token = Call25000, Exchange = Exchange.Nfo, TradingSymbol = Call25000,
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 25000m,
            ExpiryDate = AsOfDate, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
        });

        var barLength = TimeSpan.FromMinutes(1);
        var futureBars = Enumerable.Range(0, 3).Select(n => new FutureEventBar(
            n, AsOfDate, DayStart + (barLength * n), DayStart + (barLength * (n + 1)),
            25000m, 25000m, 25000m, 25000m, 1300, 25000.0, null, 10, IsFinalPartialBar: n == 2))
            .ToList();
        var optionLtps = new decimal?[] { 100, 100, 200 };
        var optionBars = Enumerable.Range(0, 3).Select(n => new SynchronizedOptionEventBar(
            n, AsOfDate, futureBars[n].StartTimestamp, futureBars[n].EndTimestamp, 25000m, OptionType.Call, 0, Call25000,
            optionLtps[n], optionLtps[n], optionLtps[n], optionLtps[n], optionLtps[n], (double)optionLtps[n]!,
            1, 0, futureBars[n].StartTimestamp, futureBars[n].EndTimestamp, MissingData: false, IsStale: false, LastKnownPrice: null, AgeMs: null))
            .ToList();
        await source.SaveChangesAsync();

        var chain = new List<Instrument> { source.Instruments.Single() };
        var rows = await Vc0DteBehaviorRecorder.RecordAsync(source, AsOfDate, chain, futureBars, optionBars, CancellationToken.None, fastBars: 1, slowBars: 2);

        var signal = Assert.Single(rows);
        Assert.Null(signal.FutureForwardReturnPercent1); // event3 doesn't exist (only 3 bars, 0..2).
        Assert.Null(signal.AbsoluteForwardReturn1);
        Assert.Null(signal.PreSignalOptionMovePercent5); // eventId-5 < 0.
        Assert.Null(signal.PreSignalFutureMovePercent5);
    }
}
