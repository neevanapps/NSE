using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (sections 6, 8, 11-12). Coverage for
/// <see cref="SynchronizedOptionBarBuilder"/>: option bars must use the EXACT same interval
/// boundaries the futures bars already produced, and a strike with no real tick in an interval
/// must be marked MissingData/IsStale rather than getting a fabricated OHLC.
/// </summary>
public sealed class SynchronizedOptionBarBuilderTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 8);
    const string CallToken = "NIFTY-25000CE-TOK";
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);

    static NiftySignalDbContext NewSourceDb() =>
        new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

    static void AddCallInstrument(NiftySignalDbContext source) => source.Instruments.Add(new Instrument
    {
        Token = CallToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY-25000CE",
        InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 25000m,
        ExpiryDate = AsOfDate, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
    });

    static void AddOptionTick(NiftySignalDbContext source, DateTimeOffset at, decimal price, long cumulativeVolume) =>
        source.Ticks.Add(new Tick
        {
            Token = CallToken, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at,
            LastPrice = price, Volume = cumulativeVolume,
        });

    static List<FutureEventBar> TwoBars() =>
    [
        new(0, AsOfDate, DayStart, DayStart.AddMinutes(5), 100, 105, 99, 104, 1300, 100, null, 20, false),
        new(1, AsOfDate, DayStart.AddMinutes(5), DayStart.AddMinutes(10), 104, 106, 103, 105, 300, 105, null, 5, true),
    ];

    [Fact]
    public async Task BuildDayAsync_UsesTheExactSameBoundariesAsTheFutureBars()
    {
        await using var source = NewSourceDb();
        AddCallInstrument(source);
        AddOptionTick(source, DayStart.AddMinutes(1), 50m, 10);
        await source.SaveChangesAsync();

        var futureBars = TwoBars();
        var optionBars = await SynchronizedOptionBarBuilder.BuildDayAsync(source, AsOfDate, futureBars, CancellationToken.None);

        Assert.Equal(2, optionBars.Count);
        Assert.Equal(futureBars[0].StartTimestamp, optionBars[0].StartTimestamp);
        Assert.Equal(futureBars[0].EndTimestamp, optionBars[0].EndTimestamp);
        Assert.Equal(futureBars[1].StartTimestamp, optionBars[1].StartTimestamp);
        Assert.Equal(futureBars[1].EndTimestamp, optionBars[1].EndTimestamp);
    }

    [Fact]
    public async Task BuildDayAsync_NoTickInInterval_MarksMissingDataAndStale_NeverFabricatesOhlc()
    {
        await using var source = NewSourceDb();
        AddCallInstrument(source);
        // Only one real tick, inside bar 0's interval -- bar 1 gets no tick of its own.
        AddOptionTick(source, DayStart.AddMinutes(1), 50m, 10);
        await source.SaveChangesAsync();

        var futureBars = TwoBars();
        var optionBars = await SynchronizedOptionBarBuilder.BuildDayAsync(source, AsOfDate, futureBars, CancellationToken.None);

        var bar0 = optionBars.Single(b => b.EventId == 0);
        Assert.False(bar0.MissingData);
        Assert.False(bar0.IsStale);
        Assert.Equal(50m, bar0.Close);
        Assert.Equal(50m, bar0.AverageLtp);

        var bar1 = optionBars.Single(b => b.EventId == 1);
        Assert.True(bar1.MissingData);
        Assert.True(bar1.IsStale);
        Assert.Null(bar1.Open);
        Assert.Null(bar1.Close);
        Assert.Null(bar1.AverageLtp);
        Assert.Equal(50m, bar1.LastKnownPrice);
        Assert.NotNull(bar1.AgeMs);
    }

    [Fact]
    public async Task BuildDayAsync_MultipleTicksInOneInterval_AverageLtpIsThePlainMean()
    {
        await using var source = NewSourceDb();
        AddCallInstrument(source);
        AddOptionTick(source, DayStart.AddMinutes(1), 40m, 10);
        AddOptionTick(source, DayStart.AddMinutes(2), 60m, 20);
        await source.SaveChangesAsync();

        var futureBars = TwoBars();
        var optionBars = await SynchronizedOptionBarBuilder.BuildDayAsync(source, AsOfDate, futureBars, CancellationToken.None);

        var bar0 = optionBars.Single(b => b.EventId == 0);
        Assert.Equal(50m, bar0.AverageLtp); // (40+60)/2
        Assert.Equal(40m, bar0.Open);
        Assert.Equal(60m, bar0.Close);
        Assert.Equal(60m, bar0.High);
        Assert.Equal(40m, bar0.Low);
    }

    [Fact]
    public async Task BuildDayAsync_NoInstrumentsForDay_ProducesNoBars()
    {
        await using var source = NewSourceDb();
        await source.SaveChangesAsync();

        var optionBars = await SynchronizedOptionBarBuilder.BuildDayAsync(source, AsOfDate, TwoBars(), CancellationToken.None);

        Assert.Empty(optionBars);
    }
}
