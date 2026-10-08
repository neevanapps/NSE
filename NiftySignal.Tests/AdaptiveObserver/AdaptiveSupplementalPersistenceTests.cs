using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan section 71: sidecar insert-if-missing / verify-if-present, versioning and core-row isolation.</summary>
public sealed class AdaptiveSupplementalPersistenceTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);
    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);
    static AdaptiveSupplementalPersistence Service() => new(NullLogger<AdaptiveSupplementalPersistence>.Instance);

    static AdaptiveObserverEngine Engine() => new(
        new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
        new DateOnly(2026, 10, 15), .065, null, T0, [], null);

    /// <summary>Deterministic stream producing several completed bars with book changes.</summary>
    static List<AdaptiveCompletedBarPackage> Packages(int ticks = 300)
    {
        var engine = Engine();
        var rng = new Random(11);
        var result = new List<AdaptiveCompletedBarPackage>();
        double bid = 22995; long volume = 1000;
        for (var i = 0; i < ticks; i++)
        {
            bid += rng.Next(-1, 2);
            volume += rng.Next(0, 70);
            var at = T0.AddSeconds(i * 1.3);
            var tick = new CleanObserverTick(i + 1, at, at, bid + 0.5, bid, bid + 1 + rng.Next(0, 2), rng.Next(1, 400), rng.Next(1, 400), volume, 5000);
            result.AddRange(engine.ProcessAvailabilityGroup([("FUT", tick)]));
        }

        Assert.True(result.Count >= 5);
        return result;
    }

    [Fact]
    public async Task MissingRow_IsInserted_ThenVerified_AndNothingIsDuplicated()
    {
        await using var db = Database(); var service = Service(); var packages = Packages();
        foreach (var p in packages) Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyFuturesAsync(db, 1, p, default));
        Assert.Equal(packages.Count, await db.FuturesSupplemental.CountAsync());

        // Restart: the same deterministic replay verifies every stored row without writing.
        db.ChangeTracker.Clear();
        foreach (var p in Packages()) Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyFuturesAsync(db, 1, p, default));
        Assert.Equal(packages.Count, await db.FuturesSupplemental.CountAsync());
        Assert.Equal(0, service.MismatchCount);
    }

    [Fact]
    public async Task MidSessionDeployment_BackfillsOnlyTheMissingBars()
    {
        await using var db = Database(); var service = Service(); var packages = Packages();
        foreach (var p in packages.Take(3)) await service.PersistOrVerifyFuturesAsync(db, 1, p, default);   // rows written before the "deployment"
        db.ChangeTracker.Clear();

        var outcomes = new List<SupplementalOutcome>();
        foreach (var p in packages) outcomes.Add(await service.PersistOrVerifyFuturesAsync(db, 1, p, default));
        Assert.All(outcomes.Take(3), o => Assert.Equal(SupplementalOutcome.Verified, o));
        Assert.All(outcomes.Skip(3), o => Assert.Equal(SupplementalOutcome.Inserted, o));
        Assert.Equal(packages.Count, await db.FuturesSupplemental.CountAsync());
    }

    [Fact]
    public async Task MismatchedStoredRow_IsReportedAndNeverOverwritten_AndDoesNotThrow()
    {
        await using var db = Database(); var service = Service(); var package = Packages()[0];
        await service.PersistOrVerifyFuturesAsync(db, 1, package, default);
        var stored = await db.FuturesSupplemental.SingleAsync();
        stored.Ofi = (stored.Ofi ?? 0) + 12345;                        // tamper
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyFuturesAsync(db, 1, package, default));
        Assert.Equal(1, service.MismatchCount);
        var after = await db.FuturesSupplemental.AsNoTracking().SingleAsync();
        Assert.Equal(stored.Ofi, after.Ofi);                           // left exactly as stored
    }

    [Fact]
    public async Task Mismatch_IsRecordedDurably_SurvivesARestart_AndIsDistinctFromMissing()
    {
        await using var db = Database(); var service = Service(); var packages = Packages();
        foreach (var p in packages.Take(3)) await service.PersistOrVerifyFuturesAsync(db, 1, p, default);
        var stored = await db.FuturesSupplemental.OrderBy(x => x.BarSeq).FirstAsync();
        stored.Ofi = (stored.Ofi ?? 0) + 99; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var badBar = stored.BarSeq;

        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyFuturesAsync(db, 1, packages[0], default));
        var marker = await db.ProjectionHealth.AsNoTracking().SingleAsync();
        Assert.Equal((AdaptiveProjectionComponents.FuturesSupplemental, FuturesMicrostructureBar.MetricsVersion, badBar), (marker.Component, marker.Version, marker.BarSeq));
        Assert.Contains("Ofi", marker.Detail);

        // "Restart": a new service instance with no memory sees the same mismatch again; the durable marker is not duplicated and the row is untouched.
        db.ChangeTracker.Clear();
        var restarted = Service();
        Assert.Equal(SupplementalOutcome.Mismatch, await restarted.PersistOrVerifyFuturesAsync(db, 1, packages[0], default));
        Assert.Single(await db.ProjectionHealth.ToListAsync());
        Assert.Equal(stored.Ofi, (await db.FuturesSupplemental.AsNoTracking().SingleAsync(x => x.BarSeq == badBar)).Ofi);

        // Other bars still verify (the degradation is per bar, core is unaffected) and a bar never written is simply missing: no marker for it.
        Assert.Equal(SupplementalOutcome.Verified, await restarted.PersistOrVerifyFuturesAsync(db, 1, packages[1], default));
        Assert.Equal(SupplementalOutcome.Inserted, await restarted.PersistOrVerifyFuturesAsync(db, 1, packages[3], default));
        Assert.Equal(1, await db.ProjectionHealth.CountAsync());
    }

    [Fact]
    public async Task OptionsMismatch_AlsoRecordsADurableMarker()
    {
        await using var db = Database(); var service = Service();
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
            FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
            EstimatorName = "V1", CreatedAtUtc = T0, OptionUniverseJson = "[]",
        };
        var bar = new OptionsSupplementalBar(
            CenterStrike: 23000, UnavailableReason: null,
            CeOiStart: 1, CeOiEnd: 2, CeOiDelta: 1, PeOiStart: 1, PeOiEnd: 2, PeOiDelta: 1,
            CeMidStart: null, CeMidEnd: null, CeMidDelta: null, PeMidStart: null, PeMidEnd: null, PeMidDelta: null,
            CePosition: null, PePosition: null, CeIvStart: null, CeIvEnd: null, CeDeltaIv: null, PeIvStart: null, PeIvEnd: null, PeDeltaIv: null,
            IvSkewStart: null, IvSkewEnd: null, DeltaSkew: null, BarCeQuantity: null, BarPeQuantity: null, VolPcr: null,
            RollCeQuantity: null, RollPeQuantity: null, RollVolPcr: null, DayCeVolume: 0, DayPeVolume: 0, UniverseTokenCount: 0, TokensObserved: 0,
            CeMicroDevTimeWeighted: null, CeOfi: null, PeMicroDevTimeWeighted: null, PeOfi: null, CeActivityPerSecond: null, PeActivityPerSecond: null,
            StraddleMidStart: null, StraddleMidEnd: null, StraddleDelta: null);
        var package = Packages()[0] with { OptionsSupplemental = bar };
        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyOptionsAsync(db, session, package, default));
        var stored = await db.OptionsSupplemental.SingleAsync(); stored.CeOiDelta = 777; await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyOptionsAsync(db, session, package, default));
        var marker = await db.ProjectionHealth.AsNoTracking().SingleAsync();
        Assert.Equal((AdaptiveProjectionComponents.OptionsSupplemental, OptionsSupplementalBar.MetricsVersion), (marker.Component, marker.Version));
        Assert.Equal(777, (await db.OptionsSupplemental.AsNoTracking().SingleAsync()).CeOiDelta);
    }

    [Fact]
    public async Task RowFromAnotherMetricsVersion_IsNotOverwritten_AndTheCurrentVersionIsInsertedAlongside()
    {
        await using var db = Database(); var service = Service(); var package = Packages()[0];
        db.FuturesSupplemental.Add(new() { SessionId = 1, BarSeq = package.FutureBar.BarSeq, MetricsVersion = "futures-micro-v0", Ofi = 777 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyFuturesAsync(db, 1, package, default));
        var rows = await db.FuturesSupplemental.AsNoTracking().OrderBy(x => x.MetricsVersion).ToListAsync();
        Assert.Equal(["futures-micro-v0", FuturesMicrostructureBar.MetricsVersion], rows.Select(x => x.MetricsVersion));
        Assert.Equal(777, rows[0].Ofi);
    }

    [Fact]
    public async Task PackageWithoutMicrostructure_IsSkipped()
    {
        await using var db = Database();
        var bare = Packages()[0] with { Microstructure = null };
        Assert.Equal(SupplementalOutcome.Skipped, await Service().PersistOrVerifyFuturesAsync(db, 1, bare, default));
        Assert.Empty(db.FuturesSupplemental);
    }

    [Fact]
    public async Task UnusableContext_ReturnsFailed_AndNeverThrowsIntoTheCoreObserver()
    {
        var db = Database(); var package = Packages()[0];
        await db.DisposeAsync();
        var service = Service();
        Assert.Equal(SupplementalOutcome.Failed, await service.PersistOrVerifyFuturesAsync(db, 1, package, default));
        Assert.Equal(1, service.FailureCount);
    }

    [Fact]
    public async Task Sidecar_NeverChangesTheCoreRows_OrTheirParityVerification()
    {
        // Core rows are written/verified by the unchanged AdaptiveObserverPersistence; the sidecar lives in its own table.
        await using var db = Database();
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
            FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
            EstimatorName = "V1", CreatedAtUtc = T0, OptionUniverseJson = "[]",
        };
        db.Sessions.Add(session);
        db.Runtime.Add(new() { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0 });
        await db.SaveChangesAsync();

        var core = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var sidecar = Service();
        var packages = Packages();
        foreach (var p in packages)
        {
            Assert.True((await core.PersistOrVerifyAsync(db, session, p, T0, default)).Inserted);
            await sidecar.PersistOrVerifyFuturesAsync(db, session.Id, p, default);
        }

        // A full replay against the same database verifies the core rows exactly as before (the sidecar is invisible to VerifyRow).
        db.ChangeTracker.Clear();
        foreach (var p in Packages())
        {
            Assert.True((await core.PersistOrVerifyAsync(db, session, p, T0, default)).VerifiedExisting);
            Assert.Equal(SupplementalOutcome.Verified, await sidecar.PersistOrVerifyFuturesAsync(db, session.Id, p, default));
        }

        Assert.Equal(packages.Count, await db.FutureBars.CountAsync());
        Assert.Equal(packages.Count, await db.FuturesSupplemental.CountAsync());
    }
}
