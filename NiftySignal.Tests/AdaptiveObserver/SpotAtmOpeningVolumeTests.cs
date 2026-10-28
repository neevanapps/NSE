using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>The opening-volume (adaptive-v1) creation path: every NEW session of either clock is frozen under spot-atm-v1, with the 09:30 anchor centered on the spot in force at the cutoff.</summary>
public sealed class SpotAtmOpeningVolumeTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly DateOnly Expiry = Day.AddDays(4);
    static readonly DateTimeOffset Open = new(2026, 10, 6, 3, 45, 0, TimeSpan.Zero);          // 09:15 IST
    static readonly DateTimeOffset Cutoff = Open.AddMinutes(15);                              // 09:30 IST

    static MarketDepth Depth(decimal bid, decimal ask) => new(bid, 100, 0, 0, 0, 0, 0, 0, 0, 0, ask, 100, 0, 0, 0, 0, 0, 0, 0, 0);

    static async Task<NiftySignalDbContext> SeedAsync(int niftyIndexes, double? spotPrice, double spotAgeSeconds)
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        source.Instruments.Add(new Instrument { Token = "FUT", TradingSymbol = "NIFTYFUT", Exchange = Exchange.Nfo, Underlying = "NIFTY", InstrumentType = InstrumentType.Future, AsOfDate = Day, ExpiryDate = Day.AddDays(20), LotSize = 65 });
        for (var strike = 22800m; strike <= 23200m; strike += 50)
        {
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                source.Instruments.Add(new Instrument
                {
                    Token = (side == OptionType.Call ? "C" : "P") + strike, TradingSymbol = "OPT" + strike + side, Exchange = Exchange.Nfo, Underlying = "NIFTY",
                    InstrumentType = InstrumentType.Option, OptionType = side, AsOfDate = Day, ExpiryDate = Expiry, StrikePrice = strike, LotSize = 65,
                });
            }
        }

        for (var k = 0; k < niftyIndexes; k++)
        {
            source.Instruments.Add(new Instrument { Token = k == 0 ? "NIFTYSPOT" : "OTHERSPOT" + k, TradingSymbol = "Nifty 50" + k, Exchange = Exchange.Nse, Underlying = "NIFTY", InstrumentType = InstrumentType.Index, AsOfDate = Day });
        }

        for (var minute = 0; minute <= 15; minute++)
        {
            var at = Open.AddMinutes(minute);
            source.Ticks.Add(new Tick { Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000m, Volume = 1000 + minute * 100L });
        }

        // option quotes at 09:29:58 (priced at 23000), a future tick at 09:29:59 so the stream crosses 09:30 fresh
        var quoteAt = Cutoff.AddSeconds(-2);
        for (var strike = 22800d; strike <= 23200d; strike += 50)
        {
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                var p = (decimal)Math.Round(AdaptiveResearchPricing.Price(side, 23000, strike, TimeToExpiry.YearsUntilExpiry(Expiry, quoteAt), .065, .20), 2);
                source.Ticks.Add(new Tick
                {
                    Token = (side == OptionType.Call ? "C" : "P") + (decimal)strike, Exchange = Exchange.Nfo, ExchangeTimestamp = quoteAt, ReceivedAt = quoteAt, LastPrice = p, Volume = 1000, OpenInterest = 1000, Depth = Depth(p - 0.05m, p + 0.05m),
                });
            }
        }

        source.Ticks.Add(new Tick { Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = Cutoff.AddSeconds(-1), ReceivedAt = Cutoff.AddSeconds(-1), LastPrice = 23000m, Volume = 2501 });
        if (spotPrice is { } sp && niftyIndexes > 0)
        {
            var at = Cutoff.AddSeconds(-spotAgeSeconds);
            source.Ticks.Add(new Tick { Token = "NIFTYSPOT", Exchange = Exchange.Nse, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = (decimal)sp });
        }

        await source.SaveChangesAsync();
        return source;
    }

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionCoordinator Coordinator() => new(new(), new Monitor(), NullLogger<AdaptiveSessionCoordinator>.Instance);

    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }

    [Fact]
    public async Task OpeningVolumeSession_IsStampedSpotAtmV1_AndItsAnchorCentersOnTheSpotAtTheCutoff()
    {
        await using var source = await SeedAsync(niftyIndexes: 1, spotPrice: 23080, spotAgeSeconds: 1);
        await using var db = Observer();
        var context = (await Coordinator().TryGetOrCreateAsync(source, db, Day, Cutoff.AddSeconds(5), default))!;

        Assert.Equal(OpeningVolumeProjectionV1.ModelVersion, context.Session.ModelVersion);        // the model identity (history keys) is untouched...
        Assert.Equal(AtmCenterRules.SpotV1Version, context.Session.AtmRuleVersion);                // ...the ATM contract is stamped beside it
        Assert.NotNull(context.ResidualAnchor);
        Assert.Equal(23100d, context.ResidualAnchor!.CenterStrike);                                // spot 23080 -> 23100; the forward (~23000) would have said 23000
        Assert.Equal(23100d, context.Session.ResidualCenterStrike);
        Assert.Equal(23100d, Assert.Single(await db.ResidualAnchorComponents.Where(x => x.IsCenterStrike && x.Side == OptionType.Call).ToListAsync()).Strike);

        var reloaded = (await Coordinator().TryGetOrCreateAsync(source, Observer2(db), Day, Cutoff.AddHours(1), default))!;
        Assert.Equal(AtmCenterRules.SpotV1Version, reloaded.Session.AtmRuleVersion);              // a restart reloads the frozen stamp
        Assert.Equal(23100d, reloaded.ResidualAnchor!.CenterStrike);
    }

    // The same in-memory database seen through a second context (a restart).
    static AdaptiveObserverDbContext Observer2(AdaptiveObserverDbContext db) { db.ChangeTracker.Clear(); return db; }

    [Theory]
    [InlineData(0, 1.0)]            // no NIFTY Index instrument in the master
    [InlineData(2, 1.0)]            // two: ambiguous, never guessed
    [InlineData(1, 6.0)]            // the only spot tick is 6 s old at the cutoff: stale
    public async Task OpeningVolumeSession_WithoutAFreshUniqueSpot_HasNoAnchor_ButIsStillStampedAndNeverFallsBack(int niftyIndexes, double spotAgeSeconds)
    {
        await using var source = await SeedAsync(niftyIndexes, spotPrice: 23080, spotAgeSeconds);
        await using var db = Observer();
        var context = (await Coordinator().TryGetOrCreateAsync(source, db, Day, Cutoff.AddSeconds(5), default))!;

        Assert.Equal(AtmCenterRules.SpotV1Version, context.Session.AtmRuleVersion);
        Assert.Null(context.ResidualAnchor);                                                       // not the synthetic-forward 23000 anchor
        Assert.Null(context.Session.ResidualCenterStrike);
        Assert.Empty(db.ResidualAnchorComponents);
    }

    [Fact]
    public async Task OpeningVolumeSession_WithExactlyFiveSecondsOldSpot_StillGetsItsAnchor()
    {
        await using var source = await SeedAsync(niftyIndexes: 1, spotPrice: 23080, spotAgeSeconds: 5);
        await using var db = Observer();
        var context = (await Coordinator().TryGetOrCreateAsync(source, db, Day, Cutoff.AddSeconds(5), default))!;
        Assert.Equal(23100d, context.ResidualAnchor!.CenterStrike);
    }

    [Fact]
    public async Task AnExistingUnstampedOpeningVolumeSession_IsNeverRelabelled_AndStaysOnTheLegacyRule()
    {
        await using var source = await SeedAsync(niftyIndexes: 1, spotPrice: 23080, spotAgeSeconds: 1);
        await using var db = Observer();
        var row = AdaptiveWeak2ObservationServiceTests.Context().Session;
        row.Id = 0; row.TradeDate = Day; row.IsHistoricalSeed = false; row.OpeningVolume = 150_000; row.BaseBarVolume = 16_250; row.WeeklyOptionExpiry = Expiry;
        row.OptionUniverseJson = System.Text.Json.JsonSerializer.Serialize(new[] { new ObserverOptionInstrument("CE", "NIFTYCE", OptionType.Call, 23000, Expiry, 65) });
        db.Sessions.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var context = (await Coordinator().TryGetOrCreateAsync(source, db, Day, Cutoff.AddHours(1), default))!;
        Assert.Null(context.Session.AtmRuleVersion);
        Assert.Equal(AtmCenterRule.SyntheticForwardLegacy, AtmCenterRules.FromVersion(context.Session.AtmRuleVersion));
        Assert.Single(await db.Sessions.ToListAsync());
    }
}
