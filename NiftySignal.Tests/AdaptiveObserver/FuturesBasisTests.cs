using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan Slice 2B: causal spot plumbing and basis, with proof that core outputs do not change.</summary>
public sealed class FuturesBasisTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    static CleanObserverTick Px(long id, double seconds, double last) =>
        new(id, T0.AddSeconds(seconds), T0.AddSeconds(seconds), last, last - 0.5, last + 0.5, 1, 1, 0, null);

    // ---- pure basis tracker ----

    [Fact]
    public void Basis_IsFutureMinusLatestSpotAtOrBeforeEachInstant_NeverALaterSpot()
    {
        var t = new FuturesBasisTracker();
        t.ObserveSpot(Px(1, 0, 22600));
        t.ObserveFuture(Px(2, 0, 22700));          // basis +100 from t=0
        t.ObserveSpot(Px(3, 4, 22610));            // basis +90 from t=4
        t.ObserveSpot(Px(4, 12, 22000));           // after the bar: must not leak into it
        var bar = t.Complete(T0, T0.AddSeconds(9));                // the 4 s spot is exactly 5 s old at the 9 s end: still usable

        Assert.Equal(100d, bar.BasisStart!.Value, 12);
        Assert.Equal(90d, bar.BasisEnd!.Value, 12);
        Assert.Equal(-10d, bar.DeltaBasis!.Value, 12);
        Assert.Equal((100d * 4 + 90d * 5) / 9d, bar.BasisTimeWeighted!.Value, 12);
        Assert.Equal(1, bar.BasisStateChanges);
        Assert.Equal(9d, bar.CoveredSeconds, 12);
        Assert.Equal(0d, bar.UncoveredSeconds, 12);
    }

    [Fact]
    public void Basis_UpdatesWhenEitherSideChanges_NotOnlyOnFuturesTicks()
    {
        var t = new FuturesBasisTracker();
        t.ObserveSpot(Px(1, 0, 22600)); t.ObserveFuture(Px(2, 0, 22700));
        t.ObserveSpot(Px(3, 2, 22605)); t.ObserveSpot(Px(4, 3, 22610)); t.ObserveFuture(Px(5, 5, 22720));
        var bar = t.Complete(T0, T0.AddSeconds(8));
        Assert.Equal(3, bar.BasisStateChanges);     // spot at 2s, spot at 3s, future at 5s
        Assert.Equal(110d, bar.BasisEnd!.Value, 12); // 22720 - 22610
    }

    [Fact]
    public void SpotFreshness_IsExactlyFiveSecondsInclusive_AndStaleBoundariesAreUnavailableNeverZero()
    {
        var t = new FuturesBasisTracker();
        t.ObserveSpot(Px(1, 0, 22600)); t.ObserveFuture(Px(2, 1, 22700));
        t.ObserveFuture(Px(3, 40, 22701));           // 40 s since the last spot update
        var bar = t.Complete(T0.AddSeconds(2), T0.AddSeconds(50));
        Assert.Equal(2d, bar.SpotAgeStartSeconds!.Value, 12);
        Assert.Equal(50d, bar.SpotAgeEndSeconds!.Value, 12);
        Assert.Equal(50d, bar.SpotAgeMaxSeconds!.Value, 12);
        Assert.Equal(100d, bar.BasisStart!.Value, 12);            // start boundary: spot age 2 s => usable
        Assert.Null(bar.BasisEnd);                                // end boundary: spot age 50 s => stale => unavailable (not zero, not carried)
        Assert.Null(bar.DeltaBasis);
        Assert.Equal(FuturesBasisBar.StatusStaleEnd, bar.Status);
        // Only the fresh part of the interval is covered: the state formed at 1 s is usable until the spot (at 0 s) is 5 s old.
        Assert.Equal(3d, bar.CoveredSeconds, 12);
        Assert.Equal(45d, bar.UncoveredSeconds, 12);
        Assert.Equal(100d, bar.BasisTimeWeighted!.Value, 12);

        // A boundary exactly 5.000 s old is usable; 5.001 s is not.
        var edge = new FuturesBasisTracker();
        edge.ObserveSpot(Px(1, 0, 22600)); edge.ObserveFuture(Px(2, 0, 22700));
        Assert.Equal(100d, edge.Complete(T0, T0.AddSeconds(5)).BasisEnd!.Value, 12);
        Assert.Null(edge.Complete(T0, T0.AddSeconds(5.001)).BasisEnd);
        Assert.Equal(FuturesBasisBar.StatusStaleEnd, edge.Complete(T0, T0.AddSeconds(5.001)).Status);
    }

    [Fact]
    public void DeltaBasis_RequiresBothBoundariesFresh_AndHasNoLateSessionException()
    {
        // The same strict rule applies at 15:20 IST as at any other time (no relaxation).
        var late = new DateTimeOffset(2026, 10, 8, 9, 50, 0, TimeSpan.Zero);            // 15:20 IST
        CleanObserverTick At(long id, double s, double px) => new(id, late.AddSeconds(s), late.AddSeconds(s), px, px, px, 1, 1, 0, null);
        var t = new FuturesBasisTracker();
        t.ObserveSpot(At(1, -20, 22600)); t.ObserveFuture(At(2, -19, 22700));          // spot is 20 s old at the bar start => stale start
        t.ObserveSpot(At(3, 4, 22610)); t.ObserveFuture(At(4, 5, 22720));
        var bar = t.Complete(late, late.AddSeconds(8));
        Assert.Null(bar.BasisStart); Assert.NotNull(bar.BasisEnd); Assert.Null(bar.DeltaBasis);
        Assert.Equal(FuturesBasisBar.StatusStaleStart, bar.Status);

        var next = t.Complete(late.AddSeconds(8), late.AddSeconds(9));                 // both boundaries within 5 s of the 4 s spot tick
        Assert.Equal(110d, next.BasisStart!.Value, 12); Assert.Equal(110d, next.BasisEnd!.Value, 12); Assert.Equal(0d, next.DeltaBasis!.Value, 12);
        Assert.Equal(FuturesBasisBar.StatusUsable, next.Status);
        var stale = t.Complete(late.AddSeconds(12), late.AddSeconds(30));
        Assert.Equal(FuturesBasisBar.StatusStaleBoth, stale.Status);
        Assert.Null(stale.DeltaBasis);
        Assert.Equal(0d, stale.CoveredSeconds, 12);                                    // the state was fresh only until the 4 s spot turned 5 s old (9 s)
        Assert.Equal(18d, stale.UncoveredSeconds, 12);
    }

    [Fact]
    public void NoSpotYet_IsUncoveredNotZero_AndLateSpotIsIgnoredAndCounted()
    {
        var t = new FuturesBasisTracker();
        t.ObserveFuture(Px(1, 0, 22700));
        var bar = t.Complete(T0, T0.AddSeconds(10));
        Assert.Null(bar.BasisStart); Assert.Null(bar.BasisEnd); Assert.Null(bar.DeltaBasis); Assert.Null(bar.BasisTimeWeighted);
        Assert.Equal(10d, bar.UncoveredSeconds, 12);
        Assert.Equal(FuturesBasisBar.StatusNoState, bar.Status);

        t.ObserveSpot(Px(5, 20, 22600));
        t.ObserveSpot(Px(4, 19, 22000));             // older than one already seen
        Assert.Equal(1, t.LateSpotTicks);
        Assert.Equal(100d, t.Complete(T0.AddSeconds(10), T0.AddSeconds(22)).BasisEnd!.Value, 12);
    }

    // ---- engine isolation: spot cannot change any core output ----

    static AdaptiveObserverEngine Engine(string? spotToken) => new(
        new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
        new DateOnly(2026, 10, 15), .065, null, T0, [], null, spotToken);

    static List<(string Token, CleanObserverTick Tick)> FuturesStream(int n)
    {
        var rng = new Random(5);
        var list = new List<(string, CleanObserverTick)>();
        double bid = 22995; long volume = 1000;
        for (var i = 0; i < n; i++)
        {
            bid += rng.Next(-1, 2); volume += rng.Next(0, 60);
            var at = T0.AddSeconds(i * 0.9);
            list.Add(("FUT", new CleanObserverTick(10 * (i + 1), at, at, bid + 0.5, bid, bid + 1, rng.Next(1, 300), rng.Next(1, 300), volume, 5000)));
        }

        return list;
    }

    [Fact]
    public void AddingSpotInput_LeavesEveryCoreOutputValueEquivalent()
    {
        var futures = FuturesStream(500);
        var rng = new Random(9);
        var withSpot = new List<(string Token, CleanObserverTick Tick)>(futures);
        for (var i = 0; i < 700; i++)
        {
            // Spot ticks at their own times AND at exactly the same instants as futures ticks (applied first, never reordering core ids).
            var at = i % 3 == 0 ? futures[Math.Min(futures.Count - 1, i / 2)].Tick.AvailableAt : T0.AddSeconds(i * 0.55);
            withSpot.Add(("SPOT", new CleanObserverTick(10 * (i + 1) + 5, at, at, 22600 + rng.Next(-30, 30), 0, 0, 0, 0, 0, null)));
        }

        List<AdaptiveCompletedBarPackage> Run(string? spot, List<(string Token, CleanObserverTick Tick)> input)
        {
            var engine = Engine(spot); var bars = new List<AdaptiveCompletedBarPackage>();
            foreach (var g in input.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key)) bars.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
            return bars;
        }

        var without = Run(null, futures);
        var with = Run("SPOT", withSpot);
        Assert.NotEmpty(without);
        Assert.Equal(without.Count, with.Count);
        for (var i = 0; i < without.Count; i++)
        {
            // Everything except the new supplemental Basis member must be identical (JSON of the whole package, exact doubles).
            Assert.Null(without[i].Basis);
            Assert.NotNull(with[i].Basis);
            Assert.Equal(JsonSerializer.Serialize(without[i]), JsonSerializer.Serialize(with[i] with { Basis = null }));
        }
    }

    [Fact]
    public void SpotOnlyAndLateSpotGroups_NeverThrowAndNeverTouchCoreOrdering()
    {
        var engine = Engine("SPOT");
        engine.ProcessAvailabilityGroup([("FUT", new CleanObserverTick(10, T0, T0, 23000, 22999, 23001, 1, 1, 1000, null))]);
        engine.ProcessAvailabilityGroup([("SPOT", Px(11, 5, 22600))]);
        engine.ProcessAvailabilityGroup([("FUT", new CleanObserverTick(12, T0.AddSeconds(6), T0.AddSeconds(6), 23000, 22999, 23001, 1, 1, 1050, null))]);

        // A spot group older than everything the core has processed: would throw for any other token, is just counted for spot.
        var late = engine.ProcessAvailabilityGroup([("SPOT", Px(13, 2, 22000))]);
        Assert.Empty(late);
        Assert.Equal(1, engine.LateSpotTicks);

        // Core ordering strictness is unchanged: a late FUTURES group still demands deterministic recovery.
        Assert.Throws<InvalidOperationException>(() =>
            engine.ProcessAvailabilityGroup([("FUT", new CleanObserverTick(14, T0.AddSeconds(3), T0.AddSeconds(3), 23000, 22999, 23001, 1, 1, 1060, null))]));
    }

    // ---- frozen supplemental identity ----

    sealed class SourceFactory
    {
        public DbContextOptions<NiftySignalDbContext> Options { get; } = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
    }

    static Instrument Index(string token, string symbol, string underlying = "NIFTY", DateOnly? day = null) => new()
    { Token = token, TradingSymbol = symbol, Underlying = underlying, InstrumentType = InstrumentType.Index, Exchange = Exchange.Nse, AsOfDate = day ?? new DateOnly(2026, 10, 8) };

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static AdaptiveSessionStateRow Session() => new()
    {
        Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
        FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
        EstimatorName = "V1", CreatedAtUtc = T0,
    };

    [Fact]
    public async Task SpotIdentity_IsResolvedOnce_AndNeverReResolvedOnRestart()
    {
        var factory = new SourceFactory();
        await using var source = new NiftySignalDbContext(factory.Options);
        source.Instruments.AddRange(Index("26000", "Nifty 50"), Index("26009", "Nifty Bank", "BANKNIFTY"));
        await source.SaveChangesAsync();
        await using var observer = Observer();
        var service = new AdaptiveSessionSupplementalService(NullLogger<AdaptiveSessionSupplementalService>.Instance);

        Assert.Equal("26000", await service.GetOrFreezeSpotTokenAsync(source, observer, Session(), default));
        var row = await observer.SessionSupplemental.AsNoTracking().SingleAsync();
        Assert.Equal("Nifty 50", row.SpotSymbol);

        // The instrument master later changes (a second NIFTY index appears): the frozen identity is reused unchanged.
        source.Instruments.Add(Index("99999", "Nifty something else"));
        await source.SaveChangesAsync();
        Assert.Equal("26000", await service.GetOrFreezeSpotTokenAsync(source, observer, Session(), default));
        Assert.Equal(1, await observer.SessionSupplemental.CountAsync());
    }

    [Fact]
    public async Task AmbiguousOrMissingSpot_FreezesAsUnresolved_SoBasisStaysUnavailableAndNeverSwitchesLater()
    {
        var factory = new SourceFactory();
        await using var source = new NiftySignalDbContext(factory.Options);
        source.Instruments.AddRange(Index("1", "A"), Index("2", "B"));
        await source.SaveChangesAsync();
        await using var observer = Observer();
        var service = new AdaptiveSessionSupplementalService(NullLogger<AdaptiveSessionSupplementalService>.Instance);

        Assert.Null(await service.GetOrFreezeSpotTokenAsync(source, observer, Session(), default));
        var row = await observer.SessionSupplemental.AsNoTracking().SingleAsync();
        Assert.Null(row.SpotToken); Assert.Contains("2 NIFTY Index", row.ResolutionProvenance);

        source.Instruments.RemoveRange(source.Instruments.Where(x => x.Token == "2"));
        await source.SaveChangesAsync();                                     // now exactly one candidate...
        Assert.Null(await service.GetOrFreezeSpotTokenAsync(source, observer, Session(), default)); // ...but the frozen outcome stands
    }

    // ---- basis sidecar persistence ----

    [Fact]
    public async Task BasisSidecar_InsertsVerifiesAndNeverOverwrites()
    {
        var engine = Engine("SPOT"); var bars = new List<AdaptiveCompletedBarPackage>();
        var rng = new Random(2);
        var futures = FuturesStream(400);
        foreach (var f in futures)
        {
            var spot = ("SPOT", new CleanObserverTick(f.Tick.Id + 5, f.Tick.AvailableAt, f.Tick.AvailableAt, 22600 + rng.Next(-20, 20), 0, 0, 0, 0, 0, null));
            bars.AddRange(engine.ProcessAvailabilityGroup([f, spot]));
        }

        Assert.True(bars.Count >= 5);
        await using var db = Observer();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        foreach (var p in bars) Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyBasisAsync(db, 1, p, default));
        db.ChangeTracker.Clear();
        foreach (var p in bars) Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyBasisAsync(db, 1, p, default));

        var first = await db.BasisSupplemental.SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq);
        first.DeltaBasis = (first.DeltaBasis ?? 0) + 5; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyBasisAsync(db, 1, bars[0], default));
        Assert.Equal(first.DeltaBasis, (await db.BasisSupplemental.AsNoTracking().SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq)).DeltaBasis);

        var noSpot = bars[0] with { Basis = null };
        Assert.Equal(SupplementalOutcome.Skipped, await service.PersistOrVerifyBasisAsync(db, 1, noSpot, default));
    }

    // ---- integrity + commentary wiring ----

    [Fact]
    public async Task BasisMismatch_IsMarkedDurably_AndCommentaryFrameTreatsItAsUnavailable()
    {
        var engine = Engine("SPOT"); var bars = new List<AdaptiveCompletedBarPackage>();
        foreach (var f in FuturesStream(400))
            bars.AddRange(engine.ProcessAvailabilityGroup([f, ("SPOT", new CleanObserverTick(f.Tick.Id + 5, f.Tick.AvailableAt, f.Tick.AvailableAt, 22600, 0, 0, 0, 0, 0, null))]));
        var package = bars.First(b => b.Basis?.DeltaBasis is not null);
        await using var db = Observer();
        var persistence = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        Assert.Equal(SupplementalOutcome.Inserted, await persistence.PersistOrVerifyBasisAsync(db, 1, package, default));
        var stored = await db.BasisSupplemental.SingleAsync(); stored.DeltaBasis = 777; await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        Assert.Equal(SupplementalOutcome.Mismatch, await persistence.PersistOrVerifyBasisAsync(db, 1, package, default));
        var marker = await db.ProjectionHealth.AsNoTracking().SingleAsync();
        Assert.Equal((AdaptiveProjectionComponents.BasisSupplemental, FuturesBasisBar.MetricsVersion, package.FutureBar.BarSeq), (marker.Component, marker.Version, marker.BarSeq));
        Assert.Equal(777, (await db.BasisSupplemental.AsNoTracking().SingleAsync()).DeltaBasis);          // never overwritten

        var session = Session();
        db.Sessions.Add(session); db.FutureBars.Add(new AdaptiveFutureBarRow
        {
            SessionId = 1, BarSeq = package.FutureBar.BarSeq, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0.AddMinutes(1), Volume = 1, TradeUpdates = 1, Open = 1, High = 1, Low = 1, Close = 1, DurationSeconds = 60,
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var frame = await new AdaptiveCommentaryFrameLoader().LoadAsync(db, session, package.FutureBar.BarSeq, default);
        Assert.Null(frame!.DeltaBasis);                                                                    // known-invalid => unavailable evidence, not 777
    }

    [Fact]
    public async Task ValidBasisSidecar_FeedsTheCommentaryBasisFamily_AndStaleOrMissingBasisIsUnavailable()
    {
        await using var db = Observer();
        var session = Session(); db.Sessions.Add(session);
        for (var seq = 1; seq <= 3; seq++)
            db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0.AddMinutes(seq), Volume = 1, TradeUpdates = 1, Open = 1, High = 1, Low = 1, Close = 1, DurationSeconds = 60 });
        db.BasisSupplemental.AddRange(
            new AdaptiveBasisSupplementalRow { SessionId = 1, BarSeq = 1, MetricsVersion = FuturesBasisBar.MetricsVersion, DeltaBasis = 3.25, BasisStart = 100, BasisEnd = 103.25, Status = FuturesBasisBar.StatusUsable },
            new AdaptiveBasisSupplementalRow { SessionId = 1, BarSeq = 2, MetricsVersion = FuturesBasisBar.MetricsVersion, DeltaBasis = null, Status = FuturesBasisBar.StatusStaleEnd },
            new AdaptiveBasisSupplementalRow { SessionId = 1, BarSeq = 3, MetricsVersion = "futures-basis-v1", DeltaBasis = 9, Status = "x" });   // another contract: never read
        await db.SaveChangesAsync();
        var loader = new AdaptiveCommentaryFrameLoader();
        Assert.Equal(3.25, (await loader.LoadAsync(db, session, 1, default))!.DeltaBasis);
        Assert.Null((await loader.LoadAsync(db, session, 2, default))!.DeltaBasis);       // stale: neither support nor contradiction
        Assert.Null((await loader.LoadAsync(db, session, 3, default))!.DeltaBasis);       // other version / missing row
    }

    [Fact]
    public async Task UnknownBasisIntegrity_MakesPersistAndVerifyFalse()
    {
        var engine = Engine("SPOT"); AdaptiveCompletedBarPackage? package = null;
        foreach (var f in FuturesStream(400))
        {
            var r = engine.ProcessAvailabilityGroup([f, ("SPOT", new CleanObserverTick(f.Tick.Id + 5, f.Tick.AvailableAt, f.Tick.AvailableAt, 22600, 0, 0, 0, 0, 0, null))]);
            if (r.Count > 0) { package = r[0]; break; }
        }

        var disposed = Observer(); await disposed.DisposeAsync();
        var persistence = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        Assert.False(await persistence.PersistAndVerifyAsync(disposed, Session(), package!, default));
        await using var db = Observer();
        Assert.True(await persistence.PersistAndVerifyAsync(db, Session(), package!, default));
        Assert.Single(await db.BasisSupplemental.ToListAsync());
    }
}
