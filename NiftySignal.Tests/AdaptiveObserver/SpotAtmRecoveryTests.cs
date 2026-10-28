using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>Restart / replay continuity of the ATM contract: recovery replays a session under the rule it was frozen with, with the frozen spot identity, and reproduces the same bands.</summary>
public sealed class SpotAtmRecoveryTests
{
    static readonly DateOnly Day = new(2026, 9, 25);
    static readonly DateOnly Expiry = Day.AddDays(4);
    internal static readonly DateTimeOffset At0 = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);          // 09:30 IST

    static List<ObserverOptionInstrument> Chain()
    {
        var chain = new List<ObserverOptionInstrument>();
        for (var strike = 22800d; strike <= 23200d; strike += 50)
        {
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                chain.Add(new((side == OptionType.Call ? "C" : "P") + strike, "OPT" + strike, side, strike, Expiry, 65));
            }
        }

        return chain;
    }

    static MarketDepth Depth(decimal bid, decimal ask) => new(bid, 100, 0, 0, 0, 0, 0, 0, 0, 0, ask, 100, 0, 0, 0, 0, 0, 0, 0, 0);

    internal static NiftySignalDbContext Source(int niftyIndexes = 1, bool spotTicks = true)
    {
        var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        for (var k = 0; k < niftyIndexes; k++)
        {
            source.Instruments.Add(new Instrument { Token = k == 0 ? "NIFTYSPOT" : "OTHERSPOT" + k, Exchange = Exchange.Nse, TradingSymbol = "Nifty 50" + k, InstrumentType = InstrumentType.Index, Underlying = "NIFTY", AsOfDate = Day });
        }

        long id = 1;
        // Spot changes from 23020 to 23030 at +1 s; the future volume closes a 100-contract bar every 2 s (+0, +2, +4, +6).
        foreach (var second in new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 })
        {
            var at = At0.AddSeconds(second);
            foreach (var i in Chain())
            {
                var p = (decimal)Math.Round(AdaptiveResearchPricing.Price(i.OptionType, 23000, i.Strike, TimeToExpiry.YearsUntilExpiry(i.ExpiryDate, at), .065, .20), 2);
                source.Ticks.Add(new Tick { Id = id++, Token = i.Token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = p, Volume = 1000 + (long)second * 3, OpenInterest = 1000, Depth = Depth(p - 0.05m, p + 0.05m) });
            }

            if (spotTicks && niftyIndexes > 0)
            {
                source.Ticks.Add(new Tick { Id = id++, Token = "NIFTYSPOT", Exchange = Exchange.Nse, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = second < 1 ? 23020m : 23030m });
            }

            if (second % 2 == 0)
            {
                source.Ticks.Add(new Tick { Id = id++, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000m, Volume = 1000 + (long)(second / 2) * 100, Depth = Depth(22999m, 23001m) });
            }
        }

        source.SaveChanges();
        return source;
    }

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    internal static AdaptiveObserverSessionContext Context(string? atmRule)
    {
        var c = AdaptiveWeak2ObservationServiceTests.Context();
        c.Session.RiskFreeRate = .065;
        c.Session.AtmRuleVersion = atmRule;
        c.Session.WeeklyOptionExpiry = Expiry;
        return c with { Options = Chain(), WeeklyOptionExpiry = Expiry };
    }

    internal static AdaptiveStateRecoveryService Service() => new(new AdaptiveSourceTickReader(),
        new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
        new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance),
        NullLogger<AdaptiveStateRecoveryService>.Instance,
        sessionSupplemental: new AdaptiveSessionSupplementalService(NullLogger<AdaptiveSessionSupplementalService>.Instance));

    internal static AdaptiveStateRecoveryService ServiceWithSidecars() => new(new AdaptiveSourceTickReader(),
        new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
        new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance),
        NullLogger<AdaptiveStateRecoveryService>.Instance,
        supplemental: new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance),
        sessionSupplemental: new AdaptiveSessionSupplementalService(NullLogger<AdaptiveSessionSupplementalService>.Instance));

    internal static async Task SeedSessionAsync(AdaptiveObserverDbContext db, AdaptiveObserverSessionContext context)
    {
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = At0 });
        await db.SaveChangesAsync();
    }

    static async Task<AdaptiveObserverDbContext> SessionDbAsync(AdaptiveObserverSessionContext context)
    {
        var db = Observer();
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = At0 });
        await db.SaveChangesAsync();
        return db;
    }

    internal static async Task<List<(int Bar, double Center, bool Available)>> CentersAsync(AdaptiveObserverDbContext db) =>
        (await db.OptionBandBars.AsNoTracking().Where(x => x.Side == OptionType.Call).OrderBy(x => x.BarSeq).ToListAsync())
        .Select(x => (x.BarSeq, x.CenterStrike, x.BandAvailable)).ToList();

    [Fact]
    public async Task Recovery_OfASpotRuleSession_ReplaysTheBandsByTheFrozenSpot_AndFreezesThatSpotIdentity()
    {
        await using var source = Source();
        var context = Context(AtmCenterRules.SpotV1Version);
        await using var db = await SessionDbAsync(context);

        var recovered = await Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default);

        var centers = await CentersAsync(db);
        Assert.True(centers.Count >= 3);
        Assert.Equal(23000d, centers[0].Center);                       // bar 1 band chosen at +0 with spot 23020
        Assert.Equal(23050d, centers[1].Center);                       // bar 2 band chosen at +2 with spot 23030
        Assert.Equal(23050d, centers[2].Center);
        Assert.All(centers, c => Assert.True(c.Available));
        Assert.Equal("NIFTYSPOT", (await db.SessionSupplemental.SingleAsync()).SpotToken);
        Assert.Contains("NIFTYSPOT", recovered.Tokens);
    }

    [Fact]
    public async Task Recovery_Twice_FromTheSameSource_ReproducesTheSameBands()
    {
        await using var source = Source();
        async Task<List<(int Bar, double Center, bool Available)>> Run()
        {
            var context = Context(AtmCenterRules.SpotV1Version);
            await using var db = await SessionDbAsync(context);
            await Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default);
            return await CentersAsync(db);
        }

        var first = await Run();
        var second = await Run();
        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Recovery_AfterARestart_KeepsTheFrozenSpotToken_EvenIfTheInstrumentMasterNowHasTwo_AndVerifiesTheStoredBars()
    {
        await using var source = Source();
        var context = Context(AtmCenterRules.SpotV1Version);
        await using var db = await SessionDbAsync(context);
        await Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default);
        var before = await CentersAsync(db);

        // The instrument master later gains a second NIFTY Index: a fresh resolution would now be ambiguous, but the session keeps what it froze.
        source.Instruments.Add(new Instrument { Token = "SECONDSPOT", Exchange = Exchange.Nse, TradingSymbol = "Nifty 50 2", InstrumentType = InstrumentType.Index, Underlying = "NIFTY", AsOfDate = Day });
        await source.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var restarted = await Service().RecoverAsync(source, db, context with { SpotToken = null }, At0.AddSeconds(10), default);

        Assert.Contains("NIFTYSPOT", restarted.Tokens);
        Assert.DoesNotContain("SECONDSPOT", restarted.Tokens);
        Assert.Equal("NIFTYSPOT", (await db.SessionSupplemental.SingleAsync()).SpotToken);
        Assert.True(restarted.ReconciledBars > 0);                       // the stored bars verified against the recomputed ones: same rule, same inputs
        Assert.Equal(before, await CentersAsync(db));
    }

    [Fact]
    public async Task Recovery_OfALegacySession_KeepsTheSyntheticForwardCenter_EvenWithSpotTicksPresent()
    {
        await using var source = Source();
        var context = Context(atmRule: null);
        await using var db = await SessionDbAsync(context);
        await Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default);

        var centers = await CentersAsync(db);
        Assert.True(centers.Count >= 3);
        Assert.All(centers, c => Assert.Equal(23000d, c.Center));       // by the forward (~23000); the 23030 spot did not move any band of an unstamped session
    }

    [Fact]
    public async Task Recovery_OfASpotRuleSession_WithoutAnyNiftyIndex_HasNoBandsAndNoFallback()
    {
        await using var source = Source(niftyIndexes: 0);
        var context = Context(AtmCenterRules.SpotV1Version);
        await using var db = await SessionDbAsync(context);
        var recovered = await Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default);

        Assert.Null((await db.SessionSupplemental.SingleAsync()).SpotToken);       // frozen as unresolved
        Assert.DoesNotContain("NIFTYSPOT", recovered.Tokens);
        var bands = await db.OptionBandBars.AsNoTracking().ToListAsync();
        Assert.NotEmpty(bands);
        Assert.All(bands, b =>
        {
            Assert.False(b.BandAvailable);
            Assert.Equal(AtmCenterRules.SpotUnavailableReason, b.UnavailableReason);
        });
    }

    [Fact]
    public async Task Recovery_RefusesASessionStampedWithAnUnknownAtmRule()
    {
        await using var source = Source();
        var context = Context("spot-atm-v9");
        await using var db = await SessionDbAsync(context);
        await Assert.ThrowsAsync<NotSupportedException>(() => Service().RecoverAsync(source, db, context, At0.AddSeconds(10), default));
    }
}
