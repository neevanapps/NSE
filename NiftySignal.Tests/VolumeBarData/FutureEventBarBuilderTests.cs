using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (section 4). Coverage for
/// <see cref="FutureEventBarBuilder"/>'s two invariants the existing production
/// <c>VolumeBarBuilder</c> deliberately does NOT have: excess-volume carry into the next bar
/// (spec 4.1), and an explicitly-marked final partial bar (spec 5).
/// </summary>
public sealed class FutureEventBarBuilderTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    const string FutureToken = "NIFTY-FUT-TOK";
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    static void AddFuture(NiftySignalDbContext source) => source.Instruments.Add(new Instrument
    {
        Token = FutureToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY-FUT",
        InstrumentType = InstrumentType.Future, ExpiryDate = AsOfDate.AddDays(7), Underlying = "NIFTY",
        LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
    });

    static void AddTick(NiftySignalDbContext source, int secondsFromOpen, decimal price, long cumulativeVolume) =>
        source.Ticks.Add(new Tick
        {
            Token = FutureToken, Exchange = Exchange.Nfo,
            ExchangeTimestamp = DayStart + TimeSpan.FromSeconds(secondsFromOpen),
            ReceivedAt = DayStart + TimeSpan.FromSeconds(secondsFromOpen),
            LastPrice = price, Volume = cumulativeVolume,
        });

    [Fact]
    public async Task BuildDayAsync_ExcessVolume_IsCarriedIntoTheNextBarsAccumulator()
    {
        await using var source = NewSourceDb();
        AddFuture(source);
        // First tick establishes the baseline (delta always 0 for the very first tick, same
        // convention FutureFlowAccumulator uses). Second tick's delta (1340) crosses the 1300
        // threshold with 40 in excess.
        AddTick(source, 0, 100m, 0);
        AddTick(source, 5, 101m, 1340);
        // Third tick's delta (200) plus the 40 carried in reaches 240 -- nowhere near a second
        // threshold crossing, so this stays open as the day's final partial bar.
        AddTick(source, 10, 102m, 1540);
        await source.SaveChangesAsync();

        var bars = await FutureEventBarBuilder.BuildDayAsync(source, AsOfDate, thresholdContracts: 1300, CancellationToken.None);

        Assert.Equal(2, bars.Count);
        Assert.Equal(1340, bars[0].Volume);
        Assert.False(bars[0].IsFinalPartialBar);

        // The carried-in 40 means the second (final, partial) bar's own Volume already starts
        // from 40 before its own real ticks are added: 40 (carry) + 200 (third tick's delta) = 240.
        Assert.Equal(240, bars[1].Volume);
        Assert.True(bars[1].IsFinalPartialBar);
    }

    [Fact]
    public async Task BuildDayAsync_FinalPartialBar_IsExplicitlyMarked_NeverAThresholdBar()
    {
        await using var source = NewSourceDb();
        AddFuture(source);
        AddTick(source, 0, 100m, 0);
        AddTick(source, 5, 101m, 500); // never reaches the 1300 threshold this whole day.
        await source.SaveChangesAsync();

        var bars = await FutureEventBarBuilder.BuildDayAsync(source, AsOfDate, thresholdContracts: 1300, CancellationToken.None);

        var bar = Assert.Single(bars);
        Assert.True(bar.IsFinalPartialBar);
        Assert.Equal(500, bar.Volume);
    }

    [Fact]
    public async Task BuildDayAsync_NoTicksAtAll_ProducesNoBars_NeverFabricated()
    {
        await using var source = NewSourceDb();
        AddFuture(source);
        await source.SaveChangesAsync();

        var bars = await FutureEventBarBuilder.BuildDayAsync(source, AsOfDate, thresholdContracts: 1300, CancellationToken.None);

        Assert.Empty(bars);
    }

    [Fact]
    public async Task BuildDayAsync_Vwap_ExcludesTheCarriedInBalance_UsesOnlyThisBarsOwnRealDeltas()
    {
        await using var source = NewSourceDb();
        AddFuture(source);
        AddTick(source, 0, 100m, 0);
        AddTick(source, 5, 200m, 1340); // delta 1340 @ price 200 closes bar 0; VWAP = 200.
        AddTick(source, 10, 300m, 1540); // delta 200 @ price 300 -- bar 1's own VWAP, carry excluded.
        await source.SaveChangesAsync();

        var bars = await FutureEventBarBuilder.BuildDayAsync(source, AsOfDate, thresholdContracts: 1300, CancellationToken.None);

        Assert.Equal(200.0, bars[0].Vwap);
        Assert.Equal(300.0, bars[1].Vwap);
    }

    [Fact]
    public async Task BuildDayWithTraceAsync_ProvesTheWholeTickIsNeverSplit_OnlyTheExcessCarries()
    {
        // The user's own worked example: accumulator sits at 1270 (from earlier ticks), one more
        // tick's delta (70) crosses the 1300 threshold with 40 in excess. The spec's own
        // distinction: the WHOLE tick (1340 total, 1270+70) is attributed to the closing bar --
        // never split into "30 for this bar, 40 for the next" -- while only the numeric EXCESS
        // (40) carries forward as the next bar's own starting accumulator balance.
        await using var source = NewSourceDb();
        AddFuture(source);
        AddTick(source, 0, 100m, 0);      // baseline tick, delta always 0.
        AddTick(source, 5, 101m, 1270);   // delta 1270 -- accumulator reaches 1270, does not close yet.
        AddTick(source, 10, 102m, 1340);  // delta 70 -- crosses 1300 with 40 excess.
        await source.SaveChangesAsync();

        var (bars, trace) = await FutureEventBarBuilder.BuildDayWithTraceAsync(source, AsOfDate, thresholdContracts: 1300, CancellationToken.None);

        var crossingTick = trace.Single(t => t.Delta == 70);
        Assert.Equal(1270, crossingTick.AccumulatorBefore);
        Assert.Equal(1300, crossingTick.Threshold);
        Assert.True(crossingTick.ClosesBar);
        Assert.Equal(40, crossingTick.Excess); // the EXCESS carried forward, not a split amount.
        Assert.Equal(40, crossingTick.AccumulatorAfter); // next bar's own starting balance.

        // The closing bar's own Volume is the WHOLE 1340 -- never clipped to exactly 1300, i.e.
        // never split.
        Assert.Equal(1340, bars[0].Volume);
    }
}
