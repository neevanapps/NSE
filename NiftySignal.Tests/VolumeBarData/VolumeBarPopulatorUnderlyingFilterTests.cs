using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Audit finding F62 (2026-09-22): regression coverage for the offline follow-up to F61.
/// <see cref="VolumeBarPopulator.PopulateDayAsync"/> picked "the" future instrument for a given
/// AsOfDate via a bare <c>FirstOrDefaultAsync(i => i.InstrumentType == InstrumentType.Future)</c>
/// with no <see cref="Instrument.Underlying"/> filter and no deterministic ordering -- the exact
/// same gap <see cref="LiveVolumeBarPopulatorUnderlyingFilterTests"/> already covers for the live
/// path (F61), except this offline populator was not in F61's fix list. This fixture puts a second
/// underlying's future (deliberately inserted FIRST, with an EARLIER expiry -- the ordering an
/// unfiltered/unordered query is most likely to get wrong) alongside NIFTY's own in the same table
/// for the same AsOfDate, and proves the offline volume-bar populator still resolves to NIFTY's
/// future and NIFTY's own ticks only.
/// </summary>
public sealed class VolumeBarPopulatorUnderlyingFilterTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 16);
    const string NiftyFutureToken = "NIFTY-FUT-TOK";
    const string OtherFutureToken = "SENSEX-FUT-TOK";
    const long Threshold = 1000;
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);

    static NiftySignalDbContext NewSourceDbWithTwoUnderlyings()
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

        // Other underlying's future inserted first, with an EARLIER expiry -- if the fix's
        // Underlying filter/OrderBy(ExpiryDate) were ever removed, this would be the one picked.
        source.Instruments.Add(new Instrument
        {
            Token = OtherFutureToken,
            Exchange = Exchange.Bfo,
            TradingSymbol = "SENSEX-FUT",
            InstrumentType = InstrumentType.Future,
            ExpiryDate = AsOfDate.AddDays(1),
            Underlying = "SENSEX",
            LotSize = 10,
            TickSize = 0.05m,
            AsOfDate = AsOfDate,
        });
        source.Instruments.Add(new Instrument
        {
            Token = NiftyFutureToken,
            Exchange = Exchange.Nfo,
            TradingSymbol = "NIFTY-FUT",
            InstrumentType = InstrumentType.Future,
            ExpiryDate = AsOfDate.AddDays(7),
            Underlying = "NIFTY",
            LotSize = 65,
            TickSize = 0.05m,
            AsOfDate = AsOfDate,
        });

        // Distinct, easily-told-apart price levels per token so a wrongly-selected future would
        // produce visibly wrong bar prices rather than an accidental pass.
        for (var i = 0; i < 60; i++)
        {
            var at = DayStart + TimeSpan.FromSeconds(i * 5);
            source.Ticks.Add(new Tick { Token = NiftyFutureToken, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 24000m + i, Volume = i * 40 });
            source.Ticks.Add(new Tick { Token = OtherFutureToken, Exchange = Exchange.Bfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 81000m + i, Volume = i * 40 });
        }

        source.SaveChanges();
        return source;
    }

    [Fact]
    public async Task PopulateDayAsync_WithTwoUnderlyingsInInstrumentsTable_UsesNiftyFutureOnly()
    {
        await using var source = NewSourceDbWithTwoUnderlyings();
        await using var destination = new VolumeBarDbContext(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase($"dest-{Guid.NewGuid()}").Options);

        var result = await VolumeBarPopulator.PopulateDayAsync(source, destination, AsOfDate, Threshold, CancellationToken.None);

        Assert.Equal(VolumeBarPopulationOutcome.Populated, result.Outcome);

        var bars = await destination.VolumeBars.Where(b => b.AsOfDate == AsOfDate && b.BarVolumeThreshold == Threshold).ToListAsync();
        Assert.NotEmpty(bars);
        Assert.All(bars, b => Assert.InRange(b.ClosePrice, 24000m, 24100m));
    }
}
