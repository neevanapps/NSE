using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Host;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// GrossOFI (SUM |e_i|, the denominator of the normalized OFI ratio) and the per-strike Level-1 book metrics (option-strike-v2). Both reuse the one existing tracker and its event formula;
/// nothing here changes a core adaptive output.
/// </summary>
public sealed class AdaptiveBookMetricsTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);

    static CleanObserverTick Book(long id, double seconds, double bid, double ask, long bq, long aq)
    {
        var at = T0.AddSeconds(seconds);
        return new CleanObserverTick(id, at, at, (bid + ask) / 2d, bid, ask, bq, aq, 0, 1000);
    }

    static FuturesBookState State(CleanObserverTick t) => new(t.AvailableAt, t.Id, t.Bid, t.Ask, t.BidQty, t.AskQty);

    static FuturesMicrostructureBar Run(params CleanObserverTick[] ticks)
    {
        var tracker = new FuturesMicrostructureTracker();
        foreach (var t in ticks) tracker.Observe(t);
        return tracker.Complete(T0, T0.AddSeconds(60));
    }

    [Fact]
    public void GrossOfi_IsTheSumOfAbsoluteEventContributions_AndNetOfiIsTheirSignedSum()
    {
        var a = Book(1, 1, 100, 101, 50, 40);
        var b = Book(2, 10, 100, 101, 80, 40);     // bid qty up
        var c = Book(3, 20, 100, 101, 80, 90);     // ask qty up
        var d = Book(4, 30, 99.5, 101, 30, 90);    // bid fell
        var bar = Run(a, b, c, d);

        var events = new[] { (a, b), (b, c), (c, d) }.Select(p => FuturesMicrostructureTracker.Contribution(State(p.Item1), State(p.Item2))).ToArray();
        Assert.Equal(events.Sum(), bar.Ofi);
        Assert.Equal(events.Sum(Math.Abs), bar.GrossOfi);
        Assert.Contains(events, e => e > 0);
        Assert.Contains(events, e => e < 0);
        Assert.True(bar.GrossOfi > Math.Abs(bar.Ofi!.Value));        // mixed signs: gross exceeds |net|
        Assert.Equal(3, bar.OfiTransitions);
    }

    [Fact]
    public void NoValidTransition_GivesNullOfiAndNullGross_NeverZero()
    {
        Assert.Null(Run(Book(1, 5, 100, 101, 10, 10)).GrossOfi);                   // a baseline alone is not evidence
        Assert.Null(Run(Book(1, 5, 100, 101, 10, 10)).Ofi);
        Assert.Null(Run().GrossOfi);

        // valid -> INVALID (crossed) -> valid: the invalid book is never bridged, so no transition exists at all
        var bar = Run(Book(1, 5, 100, 101, 10, 10), Book(2, 10, 102, 101, 10, 10), Book(3, 15, 100, 101, 20, 10));
        Assert.Equal(0, bar.OfiTransitions);
        Assert.Null(bar.Ofi);
        Assert.Null(bar.GrossOfi);
        Assert.Equal(1, bar.InvalidBookEvents);
    }

    [Fact]
    public void InvalidBook_RebaselinesExactlyAsBefore_GrossCountsOnlyTheValidTransitions()
    {
        var a = Book(1, 5, 100, 101, 10, 10);
        var b = Book(2, 10, 100, 101, 30, 10);
        var bad = Book(3, 15, 105, 101, 30, 10);
        var c = Book(4, 20, 100, 101, 30, 10);
        var d = Book(5, 25, 100, 101, 30, 70);
        var bar = Run(a, b, bad, c, d);

        var e1 = FuturesMicrostructureTracker.Contribution(State(a), State(b));
        var e2 = FuturesMicrostructureTracker.Contribution(State(c), State(d));      // the bridge b -> c is NOT counted
        Assert.Equal(2, bar.OfiTransitions);
        Assert.Equal(e1 + e2, bar.Ofi);
        Assert.Equal(Math.Abs(e1) + Math.Abs(e2), bar.GrossOfi);
    }

    // ---- per-strike book metrics ride the SAME per-token trackers ----

    [Fact]
    public void PerStrikeBookMetrics_EqualAnIndependentTrackerOfThatExactToken_AndAreDeterministic()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(11, 0)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc) clean.Add((r.Token, t));
        }

        var groups = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key).Select(g => g.ToArray()).ToArray();
        var optionTokens = freeze.Options.Select(o => o.Token).ToHashSet();
        var independent = new Dictionary<string, FuturesMicrostructureTracker>();
        foreach (var token in optionTokens) independent[token] = new FuturesMicrostructureTracker();
        foreach (var (token, tick) in clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id))
        {
            if (independent.TryGetValue(token, out var tracker)) tracker.Observe(tick);
        }

        var engine = new AdaptiveObserverEngine(freeze.Definition, freeze.WeeklyOptionExpiry, freeze.RiskFreeRate, freeze.StrongThreshold, freeze.SignalStartUtc, freeze.Options, freeze.ResidualAnchor, null, true);
        var packages = groups.SelectMany(g => engine.ProcessAvailabilityGroup(g)).ToList();
        Assert.True(packages.Count > 20);

        var withTob = 0;
        var withOfi = 0;
        foreach (var p in packages)
        {
            foreach (var s in p.OptionStrikes!)
            {
                var expected = independent[s.Token].Complete(p.FutureBar.StartAvailableAtUtc, p.FutureBar.EndAvailableAtUtc);
                Assert.Equal(expected.TobTimeWeighted, s.TobTimeWeighted);
                Assert.Equal(expected.MicroDevTimeWeighted, s.MicroDevTimeWeighted);
                Assert.Equal(expected.TobUsableSeconds, s.TobUsableSeconds);
                Assert.Equal(expected.Ofi, s.NetOfi);
                Assert.Equal(expected.GrossOfi, s.GrossOfi);
                Assert.Equal(expected.OfiTransitions, s.OfiTransitions);
                if (s.NetOfi is null) Assert.Null(s.GrossOfi); else Assert.True(s.GrossOfi >= Math.Abs(s.NetOfi.Value));
                if (s.TobTimeWeighted is not null) withTob++;
                if (s.NetOfi is not null) withOfi++;
            }
        }

        Assert.True(withTob > 0, "the synthetic session must exercise real option books");
        Assert.True(withOfi > 0);
        Assert.Equal("option-strike-v2", OptionStrikeBarMetrics.MetricsVersion);
        Assert.Equal("option-strike-v1", OptionStrikeBarMetrics.LegacyMetricsVersion);
    }

    static AdaptiveObserverDbContext Database() => new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    [Fact]
    public async Task FuturesV3_PersistsGrossOfi_BesideAnUntouchedV2Row_AndReplayVerifies()
    {
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var bar = Run(Book(1, 5, 100, 101, 10, 10), Book(2, 10, 100, 101, 30, 10), Book(3, 15, 100, 101, 30, 70));
        var package = MinimalPackage(bar);
        var seq = package.FutureBar.BarSeq;
        var v2 = new AdaptiveFuturesSupplementalRow { SessionId = 1, BarSeq = seq, MetricsVersion = FuturesMicrostructureBar.LegacyMetricsVersion, Ofi = bar.Ofi, MicroDevTimeWeighted = bar.MicroDevTimeWeighted };
        db.FuturesSupplemental.Add(v2);
        await db.SaveChangesAsync();

        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyFuturesAsync(db, 1, package, default));          // a v3 row is added next to the v2 row
        Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyFuturesAsync(db, 1, package, default));          // restart / replay: identical
        db.ChangeTracker.Clear();
        var rows = await db.FuturesSupplemental.AsNoTracking().Where(x => x.BarSeq == seq).OrderBy(x => x.MetricsVersion).ToListAsync();
        Assert.Equal(["futures-micro-v2", "futures-micro-v3"], rows.Select(r => r.MetricsVersion));
        Assert.Null(rows[0].GrossOfi);                                                                                              // the v2 row was never rewritten
        Assert.Equal(bar.GrossOfi, rows[1].GrossOfi);
        Assert.True(rows[1].GrossOfi > 0);
    }

    [Fact]
    public async Task StrikeV2_PersistsBookMetrics_AndAMismatchOnThemIsDetectedNotOverwritten()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(11, 0)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc) clean.Add((r.Token, t));
        }

        var engine = new AdaptiveObserverEngine(freeze.Definition, freeze.WeeklyOptionExpiry, freeze.RiskFreeRate, freeze.StrongThreshold, freeze.SignalStartUtc, freeze.Options, freeze.ResidualAnchor, null, true);
        var packages = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key)
            .SelectMany(g => engine.ProcessAvailabilityGroup(g.ToArray())).ToList();
        var package = packages.First(p => p.OptionStrikes!.Any(s => s.NetOfi is not null && s.TobTimeWeighted is not null));
        await using var db = Database();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);

        Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));
        Assert.All(db.OptionStrikeBars, r => Assert.Equal("option-strike-v2", r.MetricsVersion));
        var stored = await db.OptionStrikeBars.AsNoTracking().FirstAsync(x => x.NetOfi != null && x.TobTimeWeighted != null);
        var source = package.OptionStrikes!.Single(s => s.Token == stored.Token);
        Assert.Equal((source.TobTimeWeighted, source.MicroDevTimeWeighted, source.NetOfi, source.GrossOfi, source.OfiTransitions), (stored.TobTimeWeighted, stored.MicroDevTimeWeighted, stored.NetOfi, stored.GrossOfi, stored.OfiTransitions));
        Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));

        var tracked = await db.OptionStrikeBars.FirstAsync(x => x.Id == stored.Id);
        tracked.GrossOfi = (tracked.GrossOfi ?? 0) + 5;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(SupplementalOutcome.Mismatch, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));
    }

    static AdaptiveCompletedBarPackage MinimalPackage(FuturesMicrostructureBar micro)
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(10, 0)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var engine = new AdaptiveObserverEngine(freeze.Definition, freeze.WeeklyOptionExpiry, freeze.RiskFreeRate, freeze.StrongThreshold, freeze.SignalStartUtc, freeze.Options, freeze.ResidualAnchor);
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc) clean.Add((r.Token, t));
        }

        var first = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key)
            .SelectMany(g => engine.ProcessAvailabilityGroup(g.ToArray())).First();
        return first with { Microstructure = micro };
    }
}
