using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.VolumeBarData;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Audit finding F62 (2026-09-22): regression coverage for the offline follow-up to F61.
/// <see cref="OptionMaxPainPopulator.PopulateDayAsync"/> picked its whole option chain via a bare
/// <c>Where(i => i.InstrumentType == InstrumentType.Option &amp;&amp; i.ExpiryDate != null)</c> with no
/// <see cref="Instrument.Underlying"/> filter, then took the chain's nearest expiry via
/// <c>allOptions.Select(o => o.ExpiryDate!.Value).Min()</c>. This fixture inserts a second
/// underlying's option chain (deliberately with an EARLIER expiry and a strike range far from
/// NIFTY's own -- the case an unfiltered Min() is most likely to get wrong) alongside NIFTY's own
/// chain for the same AsOfDate, and proves the offline max-pain populator still resolves to
/// NIFTY's own nearest-expiry chain and strike range only.
/// </summary>
public sealed class OptionMaxPainPopulatorUnderlyingFilterTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 16);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly DateTimeOffset DayStart = new(AsOfDate.ToDateTime(new TimeOnly(9, 15)), IstOffset);
    static readonly DateTimeOffset DayEnd = new(AsOfDate.ToDateTime(new TimeOnly(15, 30)), IstOffset);
    const long Threshold = 1000;

    // NIFTY's own nearest-expiry chain: two strikes, both sides.
    const string NiftyCallToken = "NIFTY-24000-CE";
    const string NiftyPutToken = "NIFTY-24000-PE";
    const decimal NiftyStrike = 24000m;

    // Other underlying's chain: EARLIER expiry, strike range nowhere near NIFTY's -- if the
    // Underlying filter were ever removed, Min() would pick this expiry and the whole chain
    // resolution would go to SENSEX's 81000 strike instead of NIFTY's 24000 strike.
    const string OtherCallToken = "SENSEX-81000-CE";
    const string OtherPutToken = "SENSEX-81000-PE";
    const decimal OtherStrike = 81000m;

    static NiftySignalDbContext NewSourceDbWithTwoUnderlyings()
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase($"source-{Guid.NewGuid()}").Options);

        source.Instruments.AddRange(
            new Instrument
            {
                Token = OtherCallToken,
                Exchange = Exchange.Bfo,
                TradingSymbol = "SENSEX-81000-CE",
                InstrumentType = InstrumentType.Option,
                OptionType = OptionType.Call,
                StrikePrice = OtherStrike,
                ExpiryDate = AsOfDate.AddDays(1),
                Underlying = "SENSEX",
                LotSize = 10,
                TickSize = 0.05m,
                AsOfDate = AsOfDate,
            },
            new Instrument
            {
                Token = OtherPutToken,
                Exchange = Exchange.Bfo,
                TradingSymbol = "SENSEX-81000-PE",
                InstrumentType = InstrumentType.Option,
                OptionType = OptionType.Put,
                StrikePrice = OtherStrike,
                ExpiryDate = AsOfDate.AddDays(1),
                Underlying = "SENSEX",
                LotSize = 10,
                TickSize = 0.05m,
                AsOfDate = AsOfDate,
            },
            new Instrument
            {
                Token = NiftyCallToken,
                Exchange = Exchange.Nfo,
                TradingSymbol = "NIFTY-24000-CE",
                InstrumentType = InstrumentType.Option,
                OptionType = OptionType.Call,
                StrikePrice = NiftyStrike,
                ExpiryDate = AsOfDate.AddDays(7),
                Underlying = "NIFTY",
                LotSize = 65,
                TickSize = 0.05m,
                AsOfDate = AsOfDate,
            },
            new Instrument
            {
                Token = NiftyPutToken,
                Exchange = Exchange.Nfo,
                TradingSymbol = "NIFTY-24000-PE",
                InstrumentType = InstrumentType.Option,
                OptionType = OptionType.Put,
                StrikePrice = NiftyStrike,
                ExpiryDate = AsOfDate.AddDays(7),
                Underlying = "NIFTY",
                LotSize = 65,
                TickSize = 0.05m,
                AsOfDate = AsOfDate,
            });

        // NIFTY's put carries the higher OI, so max pain / highest-OI strike is unambiguously
        // NIFTY's own 24000 -- never SENSEX's 81000 -- once resolution is correct.
        void AddOiTick(string token, Exchange exchange, long openInterest) =>
            source.Ticks.Add(new Tick
            {
                Token = token,
                Exchange = exchange,
                ExchangeTimestamp = DayStart + TimeSpan.FromMinutes(1),
                ReceivedAt = DayStart + TimeSpan.FromMinutes(1),
                LastPrice = 100m,
                Volume = 100,
                OpenInterest = openInterest,
            });

        AddOiTick(NiftyCallToken, Exchange.Nfo, 5000);
        AddOiTick(NiftyPutToken, Exchange.Nfo, 9000);
        AddOiTick(OtherCallToken, Exchange.Bfo, 5000);
        AddOiTick(OtherPutToken, Exchange.Bfo, 9000);

        source.SaveChanges();
        return source;
    }

    [Fact]
    public async Task PopulateDayAsync_WithTwoUnderlyingsInInstrumentsTable_ResolvesNiftyChainOnly()
    {
        await using var source = NewSourceDbWithTwoUnderlyings();
        await using var destination = new VolumeBarDbContext(new DbContextOptionsBuilder<VolumeBarDbContext>().UseInMemoryDatabase($"dest-{Guid.NewGuid()}").Options);

        destination.VolumeBars.Add(new VolumeBarRow
        {
            AsOfDate = AsOfDate,
            BarIndex = 0,
            BarVolumeThreshold = Threshold,
            StartTimestamp = DayStart,
            EndTimestamp = DayEnd,
            OpenPrice = 24000m,
            HighPrice = 24010m,
            LowPrice = 23990m,
            ClosePrice = 24005m,
            Volume = Threshold,
        });
        await destination.SaveChangesAsync();

        var result = await OptionMaxPainPopulator.PopulateDayAsync(source, destination, AsOfDate, Threshold, CancellationToken.None);

        Assert.Equal(OptionMaxPainPopulationOutcome.Populated, result.Outcome);

        var row = await destination.OptionMaxPainBars.SingleAsync(b => b.AsOfDate == AsOfDate && b.BarVolumeThreshold == Threshold);

        // Pre-fix (unfiltered Min() over both underlyings' chains), the earlier-expiry SENSEX
        // chain would have won and every strike here would be 81000, not 24000.
        Assert.Equal(NiftyStrike, row.MaxPainStrike);
        Assert.Equal(NiftyStrike, row.HighestOiStrike);
    }
}
