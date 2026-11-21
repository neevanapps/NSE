using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// The per-strike observational projection (option-strike-v1): it reads the SAME session-long per-instrument flow state as the ATM±2 band metrics, between the SAME bar boundaries, and changes nothing
/// that adaptive-v1 computes. Its sums over the band's contracts must therefore reproduce the band's own figures exactly.
/// </summary>
public sealed class AdaptiveStrikeProjectionTests
{
    private static (SessionFreeze Freeze, IReadOnlyList<(string Token, CleanObserverTick Tick)>[] Groups) Session()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(11, 0)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc)
            {
                clean.Add((r.Token, t));
            }
        }

        var groups = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<(string Token, CleanObserverTick Tick)>)g.ToList()).ToArray();
        return (freeze, groups);
    }

    private static AdaptiveObserverEngine NewEngine(SessionFreeze f, bool project)
        => new(f.Definition, f.WeeklyOptionExpiry, f.RiskFreeRate, f.StrongThreshold, f.SignalStartUtc, f.Options, f.ResidualAnchor, null, project);

    private static List<AdaptiveCompletedBarPackage> Run(AdaptiveObserverEngine engine, IEnumerable<IReadOnlyList<(string Token, CleanObserverTick Tick)>> groups)
    {
        var packages = new List<AdaptiveCompletedBarPackage>();
        foreach (var g in groups)
        {
            packages.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
        }

        return packages;
    }

    [Fact]
    public void TheProjection_ChangesNoExistingAdaptiveOutput()
    {
        var (freeze, groups) = Session();
        var without = Run(NewEngine(freeze, false), groups);
        var with = Run(NewEngine(freeze, true), groups);

        Assert.True(without.Count > 20);
        Assert.Equal(without.Count, with.Count);
        Assert.All(without, p => Assert.Null(p.OptionStrikes));
        Assert.All(with, p => Assert.NotNull(p.OptionStrikes));
        for (var i = 0; i < without.Count; i++)
        {
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(without[i] with { OptionStrikes = null }), System.Text.Json.JsonSerializer.Serialize(with[i] with { OptionStrikes = null }));                // every core, band, residual, sidecar field is identical
        }
    }

    [Fact]
    public void ThePerStrikeFlow_SumsToTheBandMetrics_ForTheBandsOwnContracts()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        var checkedBars = 0;
        foreach (var p in packages.Where(p => p.OptionBand.Selection is not null && p.OptionBand.Call is not null && p.OptionBand.Put is not null))
        {
            var sel = p.OptionBand.Selection!;
            var calls = sel.Calls.Select(c => c.Token).ToHashSet();
            var puts = sel.Puts.Select(c => c.Token).ToHashSet();
            var strikes = p.OptionStrikes!;
            long Sum(HashSet<string> tokens, Func<OptionStrikeBarMetrics, long> f) => strikes.Where(s => tokens.Contains(s.Token)).Sum(f);

            Assert.Equal(p.OptionBand.Call!.ContractStrictBuy, Sum(calls, s => s.StrictBuy));
            Assert.Equal(p.OptionBand.Call.ContractStrictSell, Sum(calls, s => s.StrictSell));
            Assert.Equal(p.OptionBand.Call.ContractStrictUnknown, Sum(calls, s => s.StrictUnknown));
            Assert.Equal(p.OptionBand.Call.ContractEnrichedDelta, Sum(calls, s => s.EnrichedDelta));
            Assert.Equal(p.OptionBand.Put!.ContractStrictDelta, Sum(puts, s => s.StrictDelta));
            Assert.Equal(p.OptionBand.Put.ContractTotalQuantity, Sum(puts, s => s.TotalQuantity));
            checkedBars++;
        }

        Assert.True(checkedBars > 15);
    }

    [Fact]
    public void EveryRow_IsOneExactContract_OfTheFrozenUniverse_AndMissingValuesAreNullNeverZero()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        var universe = freeze.Options.ToDictionary(o => o.Token);
        foreach (var p in packages)
        {
            Assert.Equal(freeze.Options.Count, p.OptionStrikes!.Count);                       // one row per contract per bar
            Assert.Equal(p.OptionStrikes.Count, p.OptionStrikes.Select(s => s.Token).Distinct().Count());
            foreach (var s in p.OptionStrikes)
            {
                var o = universe[s.Token];
                Assert.Equal((o.ExpiryDate, o.Strike, o.OptionType, o.TradingSymbol), (s.Expiry, s.Strike, s.Side, s.TradingSymbol));
                Assert.Equal(s.StrictBuy - s.StrictSell, s.StrictDelta);
                Assert.Equal(s.PriceStart is { } a && s.PriceEnd is { } b ? b - a : (double?)null, s.PriceChange);
                Assert.Equal(s.OiStart is { } c && s.OiEnd is { } d ? d - c : (long?)null, s.OiDelta);
            }
        }

        // a token that never ticked has no price and no OI (null), and zero flow
        var quiet = Run(NewEngine(freeze, true), groups.Take(3)).Concat(Run(NewEngine(freeze, true), groups)).SelectMany(p => p.OptionStrikes!).Where(s => s.PriceEnd is null).ToList();
        Assert.All(quiet, s => Assert.Null(s.PriceChange));
    }

    [Fact]
    public void TheProjection_IsDeterministic_ARestartReplayGivesIdenticalRows()
    {
        var (freeze, groups) = Session();
        var a = Run(NewEngine(freeze, true), groups).SelectMany(p => p.OptionStrikes!.Select(s => (p.FutureBar.BarSeq, s))).ToList();
        var b = Run(NewEngine(freeze, true), groups).SelectMany(p => p.OptionStrikes!.Select(s => (p.FutureBar.BarSeq, s))).ToList();
        Assert.Equal(a, b);
    }

    private static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    [Fact]
    public async Task Persistence_InsertsOnce_VerifiesOnRestart_BackfillsMissingBars_AndNeverOverwritesAMismatch()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);

        foreach (var p in packages.Take(5))
        {
            Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyStrikesAsync(db, 1, p, default));
        }

        Assert.Equal(5 * freeze.Options.Count, await db.OptionStrikeBars.CountAsync());

        db.ChangeTracker.Clear();
        var replay = Run(NewEngine(freeze, true), groups);                                    // a restart replays the same ticks
        for (var i = 0; i < replay.Count; i++)
        {
            var outcome = await service.PersistOrVerifyStrikesAsync(db, 1, replay[i], default);
            Assert.Equal(i < 5 ? SupplementalOutcome.Verified : SupplementalOutcome.Inserted, outcome);
        }

        Assert.Equal(replay.Count * freeze.Options.Count, await db.OptionStrikeBars.CountAsync());
        Assert.Equal(0, service.MismatchCount);

        var stored = await db.OptionStrikeBars.OrderBy(x => x.BarSeq).FirstAsync();
        stored.StrictBuy += 7;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, replay[0], default));
        Assert.Equal(stored.StrictBuy, (await db.OptionStrikeBars.AsNoTracking().SingleAsync(x => x.Id == stored.Id)).StrictBuy);        // left as stored
    }

    [Fact]
    public async Task APackageWithoutTheProjection_IsSkipped_AndTheCoreVerificationResultIsUnaffected()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, false), groups);
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);

        Assert.Equal(SupplementalOutcome.Skipped, await service.PersistOrVerifyStrikesAsync(db, 1, packages[0], default));
        Assert.Empty(db.OptionStrikeBars);
        var session = new AdaptiveSessionStateRow
        {
            TradeDate = new DateOnly(2026, 9, 22), ModelVersion = "m", SourceBranch = "b", SourceCommitSha = "s", BuildUtc = DateTimeOffset.UtcNow, FutureToken = "F", FutureSymbol = "F", FutureExpiry = new DateOnly(2026, 9, 29),
            EstimatorName = "e", CreatedAtUtc = DateTimeOffset.UtcNow, OpeningWindowStartUtc = DateTimeOffset.UtcNow, OpeningWindowEndUtc = DateTimeOffset.UtcNow,
        };
        Assert.True(await service.PersistAndVerifyAsync(db, session, packages[0], default));
    }

    [Fact]
    public async Task A4_AMismatch_IsDurablyMarkedUnverified_PerToken_AndReadersWithholdIt()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in packages.Take(3)) await service.PersistOrVerifyStrikesAsync(db, 1, p, default);
        db.ChangeTracker.Clear();
        var stored = await db.OptionStrikeBars.OrderBy(x => x.BarSeq).ThenBy(x => x.Token).FirstAsync();
        stored.StrictBuy += 7;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var replay = Run(NewEngine(freeze, true), groups);

        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, replay[stored.BarSeq - 1], default));
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, replay[stored.BarSeq - 1], default));   // second restart: idempotent, still one marker
        var marker = await db.ProjectionHealth.Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).SingleAsync();
        Assert.Equal(AdaptiveProjectionComponents.OptionStrikeBars, marker.Component);
        Assert.Equal(OptionStrikeBarMetrics.MetricsVersion, marker.Version);
        Assert.Equal(stored.BarSeq, marker.BarSeq);
        Assert.StartsWith($"tokens={stored.Token};", marker.Detail);

        var unverified = await UnverifiedStrikeBars.LoadAsync(db, 1, 100, default);
        Assert.True(unverified.IsMarked(stored.BarSeq, stored.Token));
        Assert.False(unverified.IsMarked(stored.BarSeq, "OTHER"));
        Assert.False(unverified.IsMarked(stored.BarSeq + 1, stored.Token));

        // The fixed-strike reader withholds exactly that cell (null, flagged), never serves it, and the neighbours stay normal.
        var all = await db.OptionStrikeBars.AsNoTracking().ToListAsync();
        var catalog = new NiftySignal.Dashboard.Services.FixedStrikeCatalog(stored.Expiry, [new NiftySignal.Dashboard.Services.FixedStrikeContract(stored.Strike, stored.Side == OptionType.Call ? stored.Token : null, "CE", stored.Side == OptionType.Put ? stored.Token : null, "PE")]);
        var seqs = all.Select(x => x.BarSeq).Distinct().OrderBy(x => x).Select(x => (x, DateTimeOffset.UtcNow)).ToList();
        var h = NiftySignal.Dashboard.Services.FixedStrikeProjection.Assemble(catalog, catalog.Strikes[0], seqs, all, 0, unverified);
        var row = h.Rows.Single(r => r.BarSeq == stored.BarSeq);
        var cell = stored.Side == OptionType.Call ? row.Ce : row.Pe;
        Assert.Null(cell);
        Assert.True(stored.Side == OptionType.Call ? row.CeUnverified : row.PeUnverified);
        Assert.DoesNotContain(h.Rows.Where(r => r.BarSeq != stored.BarSeq), r => r.CeUnverified || r.PeUnverified);
    }

    [Fact]
    public void A4_MarkerDetail_RoundTrips_AndFallsBackToWholeBar()
    {
        Assert.Equal(new[] { "A1", "B2" }, UnverifiedStrikeBars.ParseTokens(UnverifiedStrikeBars.FormatDetail(["B2", "A1"], "x vs y"))!.OrderBy(x => x).ToArray());
        Assert.Null(UnverifiedStrikeBars.ParseTokens(UnverifiedStrikeBars.FormatDetail([], "row count 3 vs 4")));      // count-only mismatch: every strike of the bar
        Assert.Null(UnverifiedStrikeBars.ParseTokens("garbage"));                                                      // unreadable: conservative
        Assert.True(UnverifiedStrikeBars.FormatDetail(Enumerable.Range(0, 200).Select(i => $"NFO{i:00000}").ToList(), "d").Length <= 512);
    }

    sealed class FailMarkerWrites : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fail;
        /// <summary>When set, only writes of this projection-health component fail (e.g. the strike marker) - other health rows (a fence) still commit.</summary>
        public string? OnlyComponent;
        /// <summary>When set, any write (add/modify/delete) of an entity this predicate accepts fails (e.g. attestation or close-ATM rows); replaces the projection-health rule.</summary>
        public Func<object, bool>? Match;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail && Match is { } match && eventData.Context!.ChangeTracker.Entries().Any(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && match(x.Entity)))
                throw new DbUpdateException("injected write failure");
            if (Fail && Match is null && eventData.Context!.ChangeTracker.Entries<AdaptiveProjectionHealthRow>().Any(x => x.State is EntityState.Added or EntityState.Modified && (OnlyComponent is null || x.Entity.Component == OnlyComponent)))
                throw new DbUpdateException("injected marker write failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static AdaptiveObserverDbContext Shared(string name, FailMarkerWrites? failer = null)
    {
        var b = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
        if (failer is not null) b.AddInterceptors(failer);
        return new AdaptiveObserverDbContext(b.Options);
    }

    private static AdaptiveSessionStateRow SessionRow() => new()
    {
        Id = 1, TradeDate = new DateOnly(2026, 9, 22), ModelVersion = "m", SourceBranch = "b", SourceCommitSha = "s", BuildUtc = DateTimeOffset.UtcNow, FutureToken = "F", FutureSymbol = "F", FutureExpiry = new DateOnly(2026, 9, 29),
        EstimatorName = "e", CreatedAtUtc = DateTimeOffset.UtcNow, OpeningWindowStartUtc = DateTimeOffset.UtcNow, OpeningWindowEndUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task A4_MarkerWriteFailure_IsNotReportedAsMismatch_IsRetried_AndSurvivesARestart()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        var name = Guid.NewGuid().ToString();
        var failer = new FailMarkerWrites();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        await using var db = Shared(name, failer);
        foreach (var p in packages.Take(3)) await service.PersistOrVerifyStrikesAsync(db, 1, p, default);
        db.ChangeTracker.Clear();
        var stored = await db.OptionStrikeBars.OrderBy(x => x.BarSeq).ThenBy(x => x.Token).FirstAsync();
        var original = stored.StrictBuy;
        stored.StrictBuy += 7;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var bad = packages[stored.BarSeq - 1];

        failer.Fail = true;
        Assert.Equal(SupplementalOutcome.Failed, await service.PersistOrVerifyStrikesAsync(db, 1, bad, default));          // integrity unknown, NOT "Mismatch (handled)"
        Assert.Empty(await db.ProjectionHealth.AsNoTracking().ToListAsync());
        Assert.Equal(original + 7, (await db.OptionStrikeBars.AsNoTracking().SingleAsync(x => x.Id == stored.Id)).StrictBuy);  // original row preserved

        // The paper path's verification answer is unchanged, but the bar is queued for retry.
        Assert.True(await service.PersistAndVerifyAsync(db, SessionRow(), bad, default));
        Assert.Equal(1, service.PendingStrikeRetryCount);

        failer.Fail = false;                                                                                                // the next bar's call retries it, and the marker is now written
        Assert.True(await service.PersistAndVerifyAsync(db, SessionRow(), packages[stored.BarSeq], default));
        Assert.Equal(0, service.PendingStrikeRetryCount);
        Assert.True((await UnverifiedStrikeBars.LoadAsync(db, 1, 100, default)).IsMarked(stored.BarSeq, stored.Token, OptionStrikeBarMetrics.MetricsVersion));

        // Restart variant: marker write failed and the process died - a fresh service over the same database re-detects the mismatch on replay and marks it.
        var name2 = Guid.NewGuid().ToString();
        await using (var d1 = Shared(name2, failer))
        {
            var s1 = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
            foreach (var p in packages.Take(3)) await s1.PersistOrVerifyStrikesAsync(d1, 1, p, default);
            d1.ChangeTracker.Clear();
            var row = await d1.OptionStrikeBars.OrderBy(x => x.BarSeq).ThenBy(x => x.Token).FirstAsync();
            row.StrictBuy += 7;
            await d1.SaveChangesAsync();
            d1.ChangeTracker.Clear();
            failer.Fail = true;
            Assert.Equal(SupplementalOutcome.Failed, await s1.PersistOrVerifyStrikesAsync(d1, 1, packages[row.BarSeq - 1], default));
        }

        failer.Fail = false;
        await using var d2 = Shared(name2, failer);
        var s2 = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in Run(NewEngine(freeze, true), groups).Take(3)) await s2.PersistAndVerifyAsync(d2, SessionRow(), p, default);
        Assert.Single(await d2.ProjectionHealth.AsNoTracking().Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).ToListAsync());
    }

    [Fact]
    public async Task A4_RepeatedMismatches_WithDifferentTokens_UnionIntoOneMarker_AndAWholeBarMarkerAbsorbs()
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in packages.Take(2)) await service.PersistOrVerifyStrikesAsync(db, 1, p, default);
        db.ChangeTracker.Clear();
        var rows = await db.OptionStrikeBars.Where(x => x.BarSeq == 1).OrderBy(x => x.Token).Take(2).ToListAsync();
        var bar = rows[0].BarSeq;
        rows[0].StrictBuy += 7;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, packages[bar - 1], default));
        Assert.Equal([rows[0].Token], UnverifiedStrikeBars.ParseTokens((await db.ProjectionHealth.Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).SingleAsync()).Detail)!.ToArray());

        db.ChangeTracker.Clear();
        var second = await db.OptionStrikeBars.SingleAsync(x => x.Id == rows[1].Id);
        second.StrictBuy += 9;                                                                // a LATER mismatch of a different token on the same bar
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, packages[bar - 1], default));
        var marker = await db.ProjectionHealth.Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).SingleAsync();                                 // still ONE row, now covering both
        Assert.Equal(new[] { rows[0].Token, rows[1].Token }.OrderBy(x => x), UnverifiedStrikeBars.ParseTokens(marker.Detail)!.OrderBy(x => x));
        var u = await UnverifiedStrikeBars.LoadAsync(db, 1, 100, default);
        Assert.True(u.IsMarked(bar, rows[0].Token, OptionStrikeBarMetrics.MetricsVersion));
        Assert.True(u.IsMarked(bar, rows[1].Token, OptionStrikeBarMetrics.MetricsVersion));

        Assert.True(await service.RecordUnverifiedStrikesAsync(db, 1, OptionStrikeBarMetrics.MetricsVersion, bar, [], "count", default));   // whole bar absorbs
        Assert.Null(UnverifiedStrikeBars.ParseTokens((await db.ProjectionHealth.Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).SingleAsync()).Detail));
        Assert.True(await service.RecordUnverifiedStrikesAsync(db, 1, OptionStrikeBarMetrics.MetricsVersion, bar, ["X"], "d", default));    // and is never narrowed again
        Assert.Null(UnverifiedStrikeBars.ParseTokens((await db.ProjectionHealth.Where(x => x.Component == AdaptiveProjectionComponents.OptionStrikeBars).SingleAsync()).Detail));
    }

    [Fact]
    public async Task A4_Markers_AreVersionAware_ANewerVersionMarkerNeverWithholdsAnOlderVersionRow()
    {
        await using var db = Database();
        db.ProjectionHealth.Add(new AdaptiveProjectionHealthRow { SessionId = 1, Component = AdaptiveProjectionComponents.OptionStrikeBars, Version = "option-strike-v3", BarSeq = 4, Detail = "tokens=T1; x", DetectedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var u = await UnverifiedStrikeBars.LoadAsync(db, 1, 100, default);
        Assert.True(u.IsMarked(4, "T1", "option-strike-v3"));
        Assert.False(u.IsMarked(4, "T1", "option-strike-v2"));                            // a legacy-version row of the same bar/token is a different observation
        Assert.False(u.IsMarked(4, "T2", "option-strike-v3"));
    }

    sealed class SharedFactory(string name) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => Shared(name);
    }

    /// <summary>Seeds 3 bars of strike rows, corrupts the first row, and returns what the Dashboard needs to read it back.</summary>
    private static async Task<(List<AdaptiveCompletedBarPackage> Packages, AdaptiveOptionStrikeBarRow Bad, NiftySignal.Dashboard.Services.FixedStrikeCatalog Catalog)> SeedWrongRowAsync(string name)
    {
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        await using var host = Shared(name);
        var seeder = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in packages.Take(3)) await seeder.PersistOrVerifyStrikesAsync(host, 1, p, default);
        host.ChangeTracker.Clear();
        var bad = await host.OptionStrikeBars.OrderBy(x => x.BarSeq).ThenBy(x => x.Token).FirstAsync();
        bad.StrictBuy += 7;
        await host.SaveChangesAsync();
        var catalog = new NiftySignal.Dashboard.Services.FixedStrikeCatalog(bad.Expiry, [new NiftySignal.Dashboard.Services.FixedStrikeContract(bad.Strike, bad.Side == OptionType.Call ? bad.Token : null, "CE", bad.Side == OptionType.Put ? bad.Token : null, "PE")]);
        return (packages, bad, catalog);
    }

    private static async Task<NiftySignal.Dashboard.Services.FixedStrikeHistory?> DashboardReadAsync(string name, NiftySignal.Dashboard.Services.FixedStrikeCatalog catalog, double strike)
    {
        var service = new NiftySignal.Dashboard.Services.AdaptiveObserverDataService(new SharedFactory(name), NullLogger<NiftySignal.Dashboard.Services.AdaptiveObserverDataService>.Instance);
        var bars = Enumerable.Range(1, 3).Select(i => (i, DateTimeOffset.UtcNow)).ToList();
        return await service.LoadFixedStrikeHistoryAsync(1, catalog, strike, bars);
    }

    static object? Cell(NiftySignal.Dashboard.Services.FixedStrikeHistory h, AdaptiveOptionStrikeBarRow bad, int bar) =>
        bad.Side == OptionType.Call ? h.Rows.Single(r => r.BarSeq == bar).Ce : h.Rows.Single(r => r.BarSeq == bar).Pe;

    static async Task<int> ServedCellsAsync(string name, NiftySignal.Dashboard.Services.FixedStrikeCatalog catalog, AdaptiveOptionStrikeBarRow bad)
    {
        var h = await DashboardReadAsync(name, catalog, bad.Strike);
        return Enumerable.Range(1, 3).Count(b => Cell(h!, bad, b) is not null);
    }

    [Fact]
    public async Task A4_BeforeAnyRestartComparison_ARowChangedAfterAttestation_IsWithheld_WithoutAnyHostWrite()
    {
        var name = Guid.NewGuid().ToString();
        var (_, bad, catalog) = await SeedWrongRowAsync(name);                                   // rows + attestations written, then ONE row corrupted; the Host has not looked at it again
        var h = await DashboardReadAsync(name, catalog, bad.Strike);
        Assert.Null(Cell(h!, bad, bad.BarSeq));                                                   // digest differs from the attested one: withheld with no marker and no fence
        Assert.Equal(2, await ServedCellsAsync(name, catalog, bad));                             // the neighbouring bars stay served
        await using var reader = Shared(name);
        Assert.Empty(await reader.ProjectionHealth.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A4_FailedMarkerAndFailedRevocation_StillNeverServesTheBadRow_AndRecoversWhenWritesReturn()
    {
        var name = Guid.NewGuid().ToString();
        var (packages, bad, catalog) = await SeedWrongRowAsync(name);
        var failer = new FailMarkerWrites { Match = e => e is AdaptiveProjectionHealthRow or AdaptiveStrikeAttestationRow, Fail = true };
        await using var host = Shared(name, failer);                                              // restarted Host: re-verifies, finds the mismatch, but neither the marker nor the revocation can be written
        var restarted = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in packages.Take(3)) await restarted.PersistAndVerifyAsync(host, SessionRow(), p, default);
        Assert.Empty(await host.ProjectionHealth.AsNoTracking().ToListAsync());
        Assert.True(restarted.PendingStrikeRetryCount > 0);

        Assert.Null(Cell((await DashboardReadAsync(name, catalog, bad.Strike))!, bad, bad.BarSeq));   // still withheld: its values no longer match what was attested
        Assert.Equal(2, await ServedCellsAsync(name, catalog, bad));

        failer.Fail = false;                                                                      // writes return: the next bar retries, the marker lands and the attestation is revoked atomically
        await restarted.PersistAndVerifyAsync(host, SessionRow(), packages[3], default);
        Assert.Equal(0, restarted.PendingStrikeRetryCount);
        await using var reader = Shared(name);
        Assert.True((await UnverifiedStrikeBars.LoadAsync(reader, 1, 100, default)).IsMarked(bad.BarSeq, bad.Token, OptionStrikeBarMetrics.MetricsVersion));
        Assert.DoesNotContain(await reader.StrikeAttestations.AsNoTracking().ToListAsync(), a => a.BarSeq == bad.BarSeq);
        Assert.Equal(2, await ServedCellsAsync(name, catalog, bad));
    }

    [Fact]
    public async Task A4_UnattestedHistoricalRows_AreWithheld_PartialAttestationCoversOnlyItsTokens_AndReplayAttests()
    {
        var name = Guid.NewGuid().ToString();
        var (packages, bad, catalog) = await SeedWrongRowAsync(name);
        await using (var host = Shared(name))
        {
            host.StrikeAttestations.RemoveRange(await host.StrikeAttestations.ToListAsync());     // rows written before attestations existed
            var row = await host.OptionStrikeBars.SingleAsync(x => x.Id == bad.Id);
            row.StrictBuy -= 7;                                                                    // undo the corruption: the rows are now correct but unattested
            await host.SaveChangesAsync();
        }

        Assert.Equal(0, await ServedCellsAsync(name, catalog, bad));                              // never silently trusted

        await using (var host = Shared(name))                                                     // incomplete attestation: only bar 1 attested, and only without the contract under test
        {
            var bar1 = await host.OptionStrikeBars.Where(x => x.BarSeq == 1 && x.Token != bad.Token).ToListAsync();
            host.StrikeAttestations.Add(new AdaptiveStrikeAttestationRow { SessionId = 1, BarSeq = 1, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, TokenCount = bar1.Count, Digests = StrikeAttestation.Format(bar1), VerifiedAtUtc = DateTimeOffset.UtcNow });
            await host.SaveChangesAsync();
        }

        Assert.Equal(0, await ServedCellsAsync(name, catalog, bad));

        await using (var host = Shared(name))                                                     // successful replay
        {
            var s = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
            foreach (var p in Run(NewEngine(Session().Item1, true), Session().Item2).Take(3)) await s.PersistAndVerifyAsync(host, SessionRow(), p, default);
            Assert.Equal(0, s.PendingStrikeRetryCount);
        }

        Assert.Equal(3, await ServedCellsAsync(name, catalog, bad));
    }

    [Fact]
    public async Task A4_HealthyRestart_NeverWithholdsAttestedRows_AtAnyPoint()
    {
        var name = Guid.NewGuid().ToString();
        var (packages, bad, catalog) = await SeedWrongRowAsync(name);
        await using (var fix = Shared(name))
        {
            var row = await fix.OptionStrikeBars.SingleAsync(x => x.Id == bad.Id);
            row.StrictBuy -= 7;
            await fix.SaveChangesAsync();                                                          // healthy: stored rows equal what was attested
        }

        Assert.Equal(3, await ServedCellsAsync(name, catalog, bad));
        await using var host = Shared(name);
        var restarted = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var replay = Run(NewEngine(Session().Item1, true), Session().Item2);
        foreach (var p in replay.Take(3))
        {
            await restarted.PersistAndVerifyAsync(host, SessionRow(), p, default);
            Assert.Equal(3, await ServedCellsAsync(name, catalog, bad));                          // during replay too: nothing is fenced
        }

        Assert.Equal(0, restarted.PendingStrikeRetryCount);
        Assert.Empty(await host.ProjectionHealth.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A4_QueueOverflow_LeavesUnattestedRowsWithheld_UntilARestartReplayAttests()
    {
        var name = Guid.NewGuid().ToString();
        var (packages, bad, catalog) = await SeedWrongRowAsync(name);
        await using (var host = Shared(name))
        {
            host.StrikeAttestations.RemoveRange(await host.StrikeAttestations.ToListAsync());
            var row = await host.OptionStrikeBars.SingleAsync(x => x.Id == bad.Id);
            row.StrictBuy -= 7;
            await host.SaveChangesAsync();
        }

        var failer = new FailMarkerWrites { Match = e => e is AdaptiveStrikeAttestationRow, Fail = true };
        await using var h1 = Shared(name, failer);
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance) { StrikeRetryCapacity = 1 };
        foreach (var p in packages.Take(3)) await service.PersistAndVerifyAsync(h1, SessionRow(), p, default);
        Assert.Equal(1, service.PendingStrikeRetryCount);                                         // capacity 1: two of the three bars were dropped from the queue
        Assert.Equal(0, await ServedCellsAsync(name, catalog, bad));                              // dropped or not, nothing unattested is served

        await using var h2 = Shared(name);                                                        // restart replay with writes working
        var s2 = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in Run(NewEngine(Session().Item1, true), Session().Item2).Take(3)) await s2.PersistAndVerifyAsync(h2, SessionRow(), p, default);
        Assert.Equal(3, await ServedCellsAsync(name, catalog, bad));
    }

    [Fact]
    public async Task A4_CloseAtmWriteFailure_DoesNotWithholdVerifiedStrikes_AndHasItsOwnQueue()
    {
        var name = Guid.NewGuid().ToString();
        var failer = new FailMarkerWrites { Match = e => e is AdaptiveBarCloseAtmRow, Fail = true };   // the close-ATM table is missing / unwritable
        var (freeze, groups) = Session();
        var packages = Run(NewEngine(freeze, true), groups);
        await using var host = Shared(name, failer);
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in packages.Take(3)) await service.PersistAndVerifyAsync(host, SessionRow(), p, default);
        foreach (var p in packages.Take(3)) await service.PersistAndVerifyAsync(host, SessionRow(), p, default);   // a restart-style second pass: strikes already exist and are re-verified
        Assert.Equal(0, service.PendingStrikeRetryCount);
        Assert.True(service.PendingCloseAtmRetryCount > 0);

        var bad = await host.OptionStrikeBars.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.Token).FirstAsync();
        var catalog = new NiftySignal.Dashboard.Services.FixedStrikeCatalog(bad.Expiry, [new NiftySignal.Dashboard.Services.FixedStrikeContract(bad.Strike, bad.Side == OptionType.Call ? bad.Token : null, "CE", bad.Side == OptionType.Put ? bad.Token : null, "PE")]);
        Assert.Equal(3, await ServedCellsAsync(name, catalog, bad));                              // verified strikes are served although close ATM cannot be persisted
    }
}
