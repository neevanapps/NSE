using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Npt1M1;
using NiftySignal.Npt1M1.Data;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>npt1-m1-v2 session builder: the spot instrument is resolved from the day's instrument master (exactly one NIFTY Index), read through the tick reader, frozen and persisted.</summary>
public class SpotAtmNpt1BuilderTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly DateOnly Weekly = Day.AddDays(3);
    private static DateTimeOffset At(int h, int m, int s = 0) => M1Time.IstTime(Day, h, m, s);

    private static NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Npt1M1DbContext Store() => new(new DbContextOptionsBuilder<Npt1M1DbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<NiftySignalDbContext> SeedAsync(int indexInstruments, bool spotTicks)
    {
        var db = Source();
        db.Instruments.Add(new Instrument { Token = "FUT1", Exchange = Exchange.Nfo, TradingSymbol = "NIFTYFUT", InstrumentType = InstrumentType.Future, Underlying = "NIFTY", ExpiryDate = Day.AddDays(14), LotSize = 75, TickSize = 0.05m, AsOfDate = Day });
        db.Instruments.Add(new Instrument { Token = "C24000", Exchange = Exchange.Nfo, TradingSymbol = "NIFTYC24000", InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 24000m, ExpiryDate = Weekly, Underlying = "NIFTY", LotSize = 75, TickSize = 0.05m, AsOfDate = Day });
        for (var k = 0; k < indexInstruments; k++)
        {
            db.Instruments.Add(new Instrument { Token = k == 0 ? "NIFTYSPOT" : "OTHERSPOT" + k, Exchange = Exchange.Nse, TradingSymbol = "Nifty 50" + (k == 0 ? "" : k), InstrumentType = InstrumentType.Index, Underlying = "NIFTY", AsOfDate = Day });
        }

        long volume = 0;
        for (var s = 0; s <= 15 * 60 + 40; s += 20)
        {
            volume += 500;
            var at = At(9, 15).AddSeconds(s);
            db.Ticks.Add(new Tick { Token = "FUT1", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 24000m, Volume = volume });
            if (spotTicks && indexInstruments > 0)
            {
                db.Ticks.Add(new Tick { Token = "NIFTYSPOT", Exchange = Exchange.Nse, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 24030m });
            }
        }

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task TheBuilder_FreezesTheUniqueNiftyIndexAsTheSpotToken_AndReadsItsRows()
    {
        await using var source = await SeedAsync(indexInstruments: 1, spotTicks: true);
        await using var store = Store();
        var attempt = await new M1SessionBuilder(new Npt1TickReader()).TryBuildAsync(source, store, Day, At(9, 31), 0.05m, default);

        var build = attempt.Build;
        Assert.NotNull(build);
        Assert.Equal("NIFTYSPOT", build!.Freeze.Freeze.SpotToken);
        Assert.Equal("spot-atm-v1", build.Freeze.Freeze.AtmRuleVersion);
        Assert.Equal("NIFTYSPOT", build.Freeze.Report.SpotToken);
        Assert.Contains(build.FreezeRows, r => r.Token == "NIFTYSPOT");                          // the reader was given the spot token as well as the future and options
        Assert.Contains(build.FreezeRows, r => r.Token == "FUT1");
        Assert.Equal(M1Identity.ModelVersion, build.Freeze.Freeze.ModelVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task TheBuilder_RefusesTheSession_LoudlyWhenThereIsNotExactlyOneNiftyIndex(int indexInstruments)
    {
        await using var source = await SeedAsync(indexInstruments, spotTicks: false);
        await using var store = Store();
        var attempt = await new M1SessionBuilder(new Npt1TickReader()).TryBuildAsync(source, store, Day, At(9, 31), 0.05m, default);

        Assert.Null(attempt.Build);
        Assert.False(attempt.Wait);                                                        // a final refusal for the day (reported), not a silent wait
        Assert.Contains("exactly one NIFTY Index", attempt.Reason);
    }

    [Fact]
    public async Task ThePersistedSession_RecordsTheV2IdentityAndTheSpotFreeze_AndARestartWithADifferentSpotIsRefused()
    {
        await using var source = await SeedAsync(indexInstruments: 1, spotTicks: true);
        await using var store = Store();
        var builder = new M1SessionBuilder(new Npt1TickReader());
        var build = (await builder.TryBuildAsync(source, store, Day, At(9, 31), 0.05m, default)).Build!;

        Assert.Null(await builder.PersistOrVerifyAsync(store, build, At(9, 31), default));
        var row = await store.Sessions.SingleAsync();
        Assert.Equal("npt1-m1-v2", row.ModelVersion);
        Assert.Equal("npt1-m1-v2:2026-10-12", row.SessionId);
        Assert.Contains("NIFTYSPOT", row.ReportJson);                                      // the spot identity is part of the stored report...
        Assert.Contains("spot-atm-v1", row.ReportJson);

        Assert.Null(await builder.PersistOrVerifyAsync(store, build, At(9, 32), default));  // ...a restart rebuilding the same freeze verifies

        // A restart that would resolve a DIFFERENT spot instrument has a different fingerprint and is refused instead of running on another freeze.
        var other = build with { Freeze = build.Freeze with { Report = build.Freeze.Report with { Fingerprint = "0000", SpotToken = "OTHERSPOT" } } };
        var problem = await builder.PersistOrVerifyAsync(store, other, At(9, 33), default);
        Assert.NotNull(problem);
        Assert.Contains("refuses to run on a different freeze", problem);
    }
}
