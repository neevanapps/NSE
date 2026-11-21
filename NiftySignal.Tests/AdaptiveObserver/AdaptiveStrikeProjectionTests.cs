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
        var marker = await db.ProjectionHealth.SingleAsync();
        Assert.Equal(AdaptiveProjectionComponents.OptionStrikeBars, marker.Component);
        Assert.Equal(OptionStrikeBarMetrics.MetricsVersion, marker.Version);
        Assert.Equal(stored.BarSeq, marker.BarSeq);
        Assert.StartsWith($"tokens={stored.Token};", marker.Detail);

        var unverified = await UnverifiedStrikeBars.LoadAsync(db, 1, 100, default);
        Assert.True(unverified.IsUnverified(stored.BarSeq, stored.Token));
        Assert.False(unverified.IsUnverified(stored.BarSeq, "OTHER"));
        Assert.False(unverified.IsUnverified(stored.BarSeq + 1, stored.Token));

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
}
