using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Hardening that survives the removal of commentary: unknown sidecar integrity is never treated as verified, the paper hand-off gate is exactly the one that existed before commentary was removed,
/// and the operational runtime cadence. (The commentary notification / outbox / replay-quarantine tests were removed together with the feature.)
/// </summary>
public sealed class AdaptiveFinalHardeningTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 30, 0, TimeSpan.Zero);

    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionStateRow Session() => new()
    {
        Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
        FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0.AddMinutes(-15),
        OpeningWindowEndUtc = T0, EstimatorName = "V1", CreatedAtUtc = T0, BaseBarVolume = 100,
    };

    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    /// <summary>The paper hand-off condition as it was written BEFORE commentary was removed (verbatim formula, with the cap computed the way <c>CommentaryCapBeforeBar</c> did).</summary>
    static bool PreviousGate(int bar, SortedSet<int> live, SortedSet<int> recovery)
    {
        int? cap = recovery.Count > 0 ? recovery.Min : null;
        if (live.Count > 0) cap = cap is { } c ? Math.Min(c, live.First()) : live.First();
        return !live.Contains(bar) && !(cap is { } k && bar >= k);
    }

    [Fact]
    public void PaperGate_IsUnchangedByTheRemovalOfCommentary_AcrossEverySidecarIntegrityScenario()
    {
        var rng = new Random(2026);
        for (var trial = 0; trial < 2_000; trial++)
        {
            var live = new SortedSet<int>(Enumerable.Range(0, rng.Next(0, 4)).Select(_ => rng.Next(1, 60)));
            var recovery = new SortedSet<int>(Enumerable.Range(0, rng.Next(0, 4)).Select(_ => rng.Next(1, 60)));
            int? cap = recovery.Count > 0 ? recovery.Min : null;
            if (live.Count > 0) cap = cap is { } c ? Math.Min(c, live.First()) : live.First();
            for (var bar = 1; bar <= 60; bar++)
                Assert.Equal(PreviousGate(bar, live, recovery), AdaptiveObserverWorker.IsBarVerifiedForPaper(bar, live.Contains(bar), cap));
        }

        Assert.True(AdaptiveObserverWorker.IsBarVerifiedForPaper(5, false, null));          // nothing unverified: every bar is handed over as verified
        Assert.False(AdaptiveObserverWorker.IsBarVerifiedForPaper(5, true, 5));             // its own sidecar unknown
        Assert.False(AdaptiveObserverWorker.IsBarVerifiedForPaper(9, false, 5));            // fail closed at and after the first unknown bar (until the next recovery)
        Assert.True(AdaptiveObserverWorker.IsBarVerifiedForPaper(4, false, 5));
    }

    [Fact]
    public async Task SidecarIntegrity_IsKnownOnlyForInsertedVerifiedSkippedOrMarkedMismatch_NotForFailure()
    {
        var supplemental = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var package = FirstPackage();
        Assert.NotNull(package.Microstructure);
        // Unusable context: a failed write/verify leaves integrity unknown => PersistAndVerifyAsync is false (the paper path must not treat the bar as verified).
        var disposed = Database(); await disposed.DisposeAsync();
        Assert.False(await supplemental.PersistAndVerifyAsync(disposed, Session(), package, default));
        // Inserted then Verified: integrity established.
        await using var db = Database();
        Assert.True(await supplemental.PersistAndVerifyAsync(db, Session(), package, default));
        Assert.True(await supplemental.PersistAndVerifyAsync(db, Session(), package, default));
        // A mismatch is durably marked (known invalid, consumers treat it as unavailable): integrity is KNOWN, so this is true as well.
        var stored = await db.FuturesSupplemental.SingleAsync(); stored.Ofi = 424242; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.True(await supplemental.PersistAndVerifyAsync(db, Session(), package, default));
        Assert.Single(await db.ProjectionHealth.ToListAsync());
        // A package without supplemental metrics has nothing to verify.
        Assert.True(await supplemental.PersistAndVerifyAsync(Database(), Session(), package with { Microstructure = null, OptionsSupplemental = null }, default));
    }

    static AdaptiveCompletedBarPackage FirstPackage()
    {
        var engine = new AdaptiveObserverEngine(new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
            new DateOnly(2026, 10, 15), .065, null, T0, [], null);
        var rng = new Random(5); double bid = 22995; long volume = 1000;
        for (var i = 0; i < 400; i++)
        {
            bid += rng.Next(-1, 2); volume += rng.Next(0, 70);
            var at = T0.AddSeconds(i * 1.3);
            var tick = new CleanObserverTick(i + 1, at, at, bid + 0.5, bid, bid + 1 + rng.Next(0, 2), rng.Next(1, 400), rng.Next(1, 400), volume, 5000);
            var packages = engine.ProcessAvailabilityGroup([("FUT", tick)]);
            if (packages.Count > 0) return packages[0];
        }

        throw new InvalidOperationException("No completed bar produced.");
    }

    // ------------------------------------------------------------------ B2

    [Fact]
    public void RuntimeRow_IsPersistedAboutOncePerSecond_NotEveryPoll_ButAlwaysOnCompletionOrClose()
    {
        var flushes = 0; DateTimeOffset? last = null;
        for (var i = 0; i < 4 * 60; i++)                                   // 60 s of empty 250 ms polls
        {
            var now = T0.AddMilliseconds(250 * i);
            if (AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, last, now)) { flushes++; last = now; }
        }

        Assert.InRange(flushes, 59, 61);                                   // ~1/s instead of 240 writes
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, null, T0));                         // first poll
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 1, T0, T0.AddMilliseconds(10)));       // a completed bar flushes immediately
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(true, 0, T0, T0.AddMilliseconds(10)));        // session close / explicit flush
        Assert.False(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, T0, T0.AddMilliseconds(999)));
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, T0, T0.AddSeconds(1)));             // source progress is eventually persisted
        Assert.Equal(TimeSpan.FromSeconds(1), AdaptiveObserverWorker.RuntimePersistInterval);
    }

    // ------------------------------------------------------------------ B3

    [Fact]
    public async Task SteadyStateRuntimeRefresh_IsASingleRuntimeReadBySessionId()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var db = new AdaptiveObserverDbContext(options))
        {
            db.Sessions.Add(Session());
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0, LastCompletedBarSeq = 12, CurrentPartialBarVolume = 345 });
            await db.SaveChangesAsync();
        }

        var service = new AdaptiveObserverDataService(new Factory(options), NullLogger<AdaptiveObserverDataService>.Instance);
        var runtime = await service.LoadRuntimeAsync(1);
        Assert.NotNull(runtime);
        Assert.Equal((12, 345L, AdaptiveRuntimeStatus.Live), (runtime!.LastCompletedBarSeq, runtime.CurrentPartialBarVolume, runtime.RuntimeStatus));
        Assert.Null(await service.LoadRuntimeAsync(99));                   // unknown session: caller falls back to a full reload
    }
}
