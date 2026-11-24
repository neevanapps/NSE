using System.Text.Json;
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

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>S4 of the Top-5 design: Futures-only depth5 sidecar (D10 conditional bridging, zero-duration bars, determinism, persistence, flag). Synthetic data only.</summary>
public sealed class FuturesDepth5Tests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 3, 45, 0, TimeSpan.Zero);

    static decimal Px(int ticks) => ticks * 0.05m;

    static readonly long[] Q = [10, 20, 30, 40, 50];

    static MarketDepth Depth(long[] bq, long[] aq, int bestBid = 2000, int gap = 2) => new(
        Px(bestBid), bq[0], Px(bestBid - 1), bq[1], Px(bestBid - 2), bq[2], Px(bestBid - 3), bq[3], Px(bestBid - 4), bq[4],
        Px(bestBid + gap), aq[0], Px(bestBid + gap + 1), aq[1], Px(bestBid + gap + 2), aq[2], Px(bestBid + gap + 3), aq[3], Px(bestBid + gap + 4), aq[4]);

    static readonly Depth5Book A = Depth5Mapping.FromMarketDepth(Depth(Q, Q));
    static readonly Depth5Book B = Depth5Mapping.FromMarketDepth(Depth([10, 20, 31, 40, 50], Q));          // +1 queued at bid level 3 versus A
    static readonly Depth5Book C = Depth5Mapping.FromMarketDepth(Depth([10, 20, 31, 40, 52], Q));          // +2 more at bid level 5 versus B
    static readonly Depth5Book Crossed = Depth5Mapping.FromMarketDepth(Depth(Q, Q, gap: -3));

    static Depth5Observation Snap(long id, double sec, Depth5Book book) => new("FUT", id, T0.AddSeconds(sec), T0.AddSeconds(sec), book);
    static Depth5Observation Mark(long id, double sec) => new("FUT", id, T0.AddSeconds(sec), T0.AddSeconds(sec), null, Depth5EventKind.L1Superseded);
    static Depth5Observation Gap(long id, double sec) => new("FUT", id, T0.AddSeconds(sec), T0.AddSeconds(sec), null, Depth5EventKind.FeedGap);
    static Depth5Observation NoDepth(long id, double sec) => new("FUT", id, T0.AddSeconds(sec), T0.AddSeconds(sec), null, Depth5EventKind.NoDepth);

    static FuturesDepth5Bar Run(double barStart, double barEnd, params Depth5Observation[] events)
    {
        var t = new FuturesDepth5Tracker();
        foreach (var e in events) t.Observe(e);
        return t.Complete(T0.AddSeconds(barStart), T0.AddSeconds(barEnd));
    }

    static long E(Depth5Book p, Depth5Book n) => Depth5Metrics.Ofi5Contribution(p, n)!.Value;

    // ------------------------------------------------------------------ D10 bridging (tests 1-6)

    [Fact]
    public void T1_AToBAcrossL1Superseded_Within5s_IsOneEligibleBridgedTransition()
    {
        var bar = Run(-1, 10, Snap(1, 0, A), Mark(2, 1), Snap(3, 3, B));
        Assert.Equal(1, bar.Ofi5Transitions);
        Assert.Equal(0, bar.Ofi5DirectTransitions);
        Assert.Equal(1, bar.Ofi5BridgedTransitions);
        Assert.Equal(E(A, B), bar.Ofi5);
        Assert.Equal(E(A, B), bar.Ofi5BridgedNet);
        Assert.Equal(2d, bar.Ofi5BridgedGapSeconds, 12);                                  // unavailable from the marker (1 s) to the new snapshot (3 s)
        Assert.Equal(0, bar.Ofi5BaselineResets);
        Assert.Equal(2d, bar.Depth5SupersededSeconds, 12);                                // coverage unavailable [1, 3): from the marker to the next coherent snapshot
    }

    [Fact]
    public void T1b_WithoutAMarker_TheSameTransitionIsDirect()
    {
        var bar = Run(-1, 10, Snap(1, 0, A), Snap(2, 3, B));
        Assert.Equal((1, 1, 0), (bar.Ofi5Transitions, bar.Ofi5DirectTransitions, bar.Ofi5BridgedTransitions));
        Assert.Null(bar.Ofi5BridgedNet);
        Assert.Equal(0d, bar.Ofi5BridgedGapSeconds);
    }

    [Fact]
    public void T2_AGapOverFiveSeconds_ResetsTheBaseline_AndExactlyFiveStillBridges()
    {
        var late = Run(-1, 20, Snap(1, 0, A), Mark(2, 1), Snap(3, 6.5, B));
        Assert.Equal(0, late.Ofi5Transitions);
        Assert.Null(late.Ofi5);
        Assert.Equal(1, late.Ofi5BaselineResets);

        var boundary = Run(-1, 20, Snap(1, 0, A), Mark(2, 1), Snap(3, 5, B));              // exactly 5 s between endpoints: still eligible
        Assert.Equal(1, boundary.Ofi5BridgedTransitions);
        var beyond = Run(-1, 20, Snap(1, 0, A), Snap(2, 5.001, B));
        Assert.Equal(0, beyond.Ofi5Transitions);

        // after the reset the new snapshot is the baseline: the next transition within 5 s counts
        var recovered = Run(-1, 30, Snap(1, 0, A), Snap(2, 7, B), Snap(3, 9, C));
        Assert.Equal(1, recovered.Ofi5Transitions);
        Assert.Equal(E(B, C), recovered.Ofi5);
    }

    [Fact]
    public void T3_AToInvalidDepthToB_DoesNotBridge()
    {
        var bar = Run(-1, 20, Snap(1, 0, A), Snap(2, 1, Crossed), Snap(3, 2, B));
        Assert.Equal(0, bar.Ofi5Transitions);
        Assert.Null(bar.Ofi5);
        Assert.Equal(1, bar.Ofi5BaselineResets);
        Assert.Equal(1, bar.InvalidSnapshots);
        Assert.Equal(1d, bar.Depth5InvalidSeconds, 12);

        var withMarker = Run(-1, 20, Snap(1, 0, A), Mark(2, 0.5), Snap(3, 1, Crossed), Snap(4, 2, B));
        Assert.Equal(0, withMarker.Ofi5Transitions);
        var nodepth = Run(-1, 20, Snap(1, 0, A), NoDepth(2, 1), Snap(3, 2, B));
        Assert.Equal(0, nodepth.Ofi5Transitions);
    }

    [Fact]
    public void T4_AToL1SupersededToIdenticalA_IsNoTransition_ButItRestoresCoverageFromItsOwnInstant()
    {
        var bar = Run(-1, 10, Snap(1, 0, A), Mark(2, 1), Snap(3, 3, A));
        Assert.Equal(0, bar.Ofi5Transitions);
        Assert.Null(bar.Ofi5);
        Assert.Equal(0, bar.Ofi5BridgedTransitions);
        Assert.Equal(1d, bar.Depth5UnobservedSeconds, 12);                                  // [-1, 0) before the first event
        Assert.Equal(1d + 5d, bar.Depth5UsableSeconds, 12);                                 // [0,1) and [3,8): the second book is a fresh observation after the marker
        Assert.Equal(2d, bar.Depth5SupersededSeconds, 12);                                  // [1,3)
        Assert.Equal(2d, bar.Depth5StaleSeconds, 12);                                       // [8,10)

        // the identical snapshot becomes the baseline: the next real change is a DIRECT transition from it
        var next = Run(-1, 10, Snap(1, 0, A), Mark(2, 1), Snap(3, 3, A), Snap(4, 4, B));
        Assert.Equal((1, 1, 0), (next.Ofi5Transitions, next.Ofi5DirectTransitions, next.Ofi5BridgedTransitions));
    }

    [Fact]
    public void T5_MultipleMarkersBeforeTheNextSnapshot_AreOneBridgedTransition_WithTheGapMeasuredFromTheFirst()
    {
        var bar = Run(-1, 10, Snap(1, 0, A), Mark(2, 1), Mark(3, 1.5), Mark(4, 2), Snap(5, 4, B));
        Assert.Equal((1, 0, 1), (bar.Ofi5Transitions, bar.Ofi5DirectTransitions, bar.Ofi5BridgedTransitions));
        Assert.Equal(3d, bar.Ofi5BridgedGapSeconds, 12);
        Assert.Equal(3, bar.SupersededMarkers);
        Assert.Equal(E(A, B), bar.Ofi5);
        // two bridged transitions in a row accumulate separately from a direct one
        var mixed = Run(-1, 30, Snap(1, 0, A), Mark(2, 1), Snap(3, 2, B), Snap(4, 3, C), Mark(5, 4), Snap(6, 6, A));
        Assert.Equal((3, 1, 2), (mixed.Ofi5Transitions, mixed.Ofi5DirectTransitions, mixed.Ofi5BridgedTransitions));
        Assert.Equal(E(A, B) + E(B, C) + E(C, A), mixed.Ofi5);
        Assert.Equal(E(A, B) + E(C, A), mixed.Ofi5BridgedNet);
        Assert.Equal(1d + 2d, mixed.Ofi5BridgedGapSeconds, 12);
        Assert.Equal(Math.Abs(E(A, B)) + Math.Abs(E(B, C)) + Math.Abs(E(C, A)), mixed.GrossOfi5);
    }

    [Fact]
    public void T6_AKnownFeedGap_InterruptsOfi_AndIsItsOwnAccountedTime()
    {
        var bar = Run(-1, 10, Snap(1, 0, A), Gap(2, 1), Snap(3, 2, B));
        Assert.Equal(0, bar.Ofi5Transitions);
        Assert.Equal(1, bar.Ofi5BaselineResets);
        Assert.Equal(1d, bar.Depth5NoDepthSeconds, 12);                                     // the gap occupies [1, 2)
        Assert.Equal(1d + 5d, bar.Depth5UsableSeconds, 12);
    }

    // ------------------------------------------------------------------ accounting / zero-duration (test 7)

    [Fact]
    public void EverySecondOfTheBarIsAccountedExactlyOnce()
    {
        var events = new[] { Snap(1, 0, A), Mark(2, 2), Snap(3, 4, B), Snap(4, 6, Crossed), NoDepth(5, 8), Snap(6, 9, C) };
        var bar = Run(-3, 30, events);
        var total = bar.Depth5UsableSeconds + bar.Depth5StaleSeconds + bar.Depth5InvalidSeconds + bar.Depth5SupersededSeconds + bar.Depth5NoDepthSeconds + bar.Depth5UnobservedSeconds;
        Assert.Equal(bar.BarSeconds, total, 9);
        Assert.Equal(33d, bar.BarSeconds, 12);
        Assert.Equal(3d, bar.Depth5UnobservedSeconds, 12);
    }

    [Fact]
    public void T7_ZeroDurationBars_FabricateNoTimeWeightedEvidence_AndNeverDoubleCountTransitions()
    {
        var t = new FuturesDepth5Tracker();
        t.Observe(Snap(1, 0, A)); t.Observe(Snap(2, 2, B));
        var first = t.Complete(T0.AddSeconds(-1), T0.AddSeconds(3));
        t.Observe(Snap(3, 3, C));                                                          // an event AT the shared instant, delivered before the next bars
        var z1 = t.Complete(T0.AddSeconds(3), T0.AddSeconds(3));
        var z2 = t.Complete(T0.AddSeconds(3), T0.AddSeconds(3));
        var after = t.Complete(T0.AddSeconds(3), T0.AddSeconds(6));

        foreach (var z in new[] { z1, z2 })
        {
            Assert.Equal(0d, z.BarSeconds);
            Assert.Equal(0d, z.Depth5UsableSeconds + z.Depth5StaleSeconds + z.Depth5InvalidSeconds + z.Depth5SupersededSeconds + z.Depth5NoDepthSeconds + z.Depth5UnobservedSeconds);
            Assert.Null(z.Tob5TimeWeighted); Assert.Null(z.MicroDev5TimeWeighted); Assert.Null(z.DepthImb5TimeWeighted);
            Assert.Null(z.BidNotionalTimeWeighted); Assert.Null(z.AskNotionalTimeWeighted);
            Assert.Null(z.Ofi5); Assert.Null(z.GrossOfi5);
            Assert.Equal((0, 0, 0, 0), (z.Ofi5Transitions, z.SnapshotEvents, z.SupersededMarkers, z.Ofi5BaselineResets));
        }

        // the A->B transition (at 2) belongs to the first bar, B->C (at 3 = the first bar's end) was delivered after its Complete and, being at <= start of the later bars, belongs to NO later bar:
        // exactly like the Level-1 convention (an event arriving after Complete at the same instant is the next reference state, not a transition of a (start, end] bar that starts at that instant).
        Assert.Equal(E(A, B), first.Ofi5);
        Assert.Null(after.Ofi5);
        Assert.Equal(3d, after.Depth5UsableSeconds, 12);                                    // C is usable [3, 6)
    }

    // ------------------------------------------------------------------ freshness (test 8)

    [Fact]
    public void T8_DuplicateSnapshots_NeverRenewTheFreshnessClock_OrTheOfiBaselineClock()
    {
        // 40 stored rows repeat the same complete book every 1.6 s: the feed emits ONE event, so usable time is exactly 5 s.
        var feed = new FuturesDepth5Feed();
        feed.Ingest(Enumerable.Range(0, 40).Select(i => new DepthRawRow("FUT", i + 1, T0.AddSeconds(i * 1.6), T0.AddSeconds(i * 1.6), Depth(Q, Q))), T0.AddSeconds(-10));
        var t = new FuturesDepth5Tracker();
        foreach (var o in feed.DrainThrough(T0.AddSeconds(100))) t.Observe(o);
        var bar = t.Complete(T0.AddSeconds(-1), T0.AddSeconds(70));
        Assert.Equal(1, bar.SnapshotEvents);
        Assert.Equal(5d, bar.Depth5UsableSeconds, 12);
        Assert.Equal(65d, bar.Depth5StaleSeconds, 12);                                      // bar [-1,70]: 1 s unobserved + 5 s usable + 65 s stale

        // another producer delivering the identical snapshot directly: ignored, the clock stays on the original observation, and the baseline is not renewed either
        var direct = new FuturesDepth5Tracker();
        direct.Observe(Snap(1, 0, A)); direct.Observe(Snap(2, 4, A)); direct.Observe(Snap(3, 7, B));
        var d = direct.Complete(T0.AddSeconds(-1), T0.AddSeconds(20));
        Assert.Equal(1, direct.RepeatsIgnored);
        Assert.Equal(5d + 5d, d.Depth5UsableSeconds, 12);                                   // A for 5 s, then B for 5 s; the repeat at 4 s extended nothing
        Assert.Equal(0, d.Ofi5Transitions);                                                 // 7 s after the ORIGINAL baseline: freshness expired, baseline reset
        Assert.Equal(1, d.Ofi5BaselineResets);
    }

    // ------------------------------------------------------------------ engine: causal ordering, flag parity, determinism (tests 9-11)

    static AdaptiveObserverEngine Engine(bool depth, string? spot = "SPOT", IReadOnlyList<ObserverOptionInstrument>? options = null) => new(
        new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
        new DateOnly(2026, 10, 15), .065, null, T0, options ?? [], null, spot, trackFuturesDepth5: depth);

    static List<(string Token, CleanObserverTick Tick)> Ticks(int n, bool withOptions)
    {
        var rng = new Random(7);
        var list = new List<(string, CleanObserverTick)>();
        double bid = 22995; long volume = 1000; var id = 1000L;
        for (var i = 0; i < n; i++)
        {
            bid += rng.Next(-1, 2); volume += rng.Next(0, 60);
            var at = T0.AddSeconds(i * 0.9);
            list.Add(("FUT", new CleanObserverTick(++id, at, at, bid + 0.5, bid, bid + 1, rng.Next(1, 300), rng.Next(1, 300), volume, 5000)));
            list.Add(("SPOT", new CleanObserverTick(++id, at, at, 22600 + rng.Next(-30, 30), 0, 0, 0, 0, 0, null)));      // same instant as the futures tick
            if (withOptions && i % 2 == 0)
                list.Add(("CE", new CleanObserverTick(++id, at.AddSeconds(0.3), at.AddSeconds(0.3), 100 + rng.Next(5), 99, 101, 50, 60, 100 + i, 10)));
        }

        return list;
    }

    /// <summary>Stored depth rows of the future token: a touchline row (Level-1 quantity moves) then a depth row (a deeper level moves) per 1.6 s, with an occasional crossed and a null-depth row.</summary>
    static List<DepthRawRow> DepthRows(int n, long firstId = 1)
    {
        var rng = new Random(3);
        var rows = new List<DepthRawRow>();
        var bq = Q.ToArray(); var aq = Q.ToArray();
        var id = firstId;
        for (var i = 0; i < n; i++)
        {
            var at = T0.AddSeconds(i * 0.8 + 0.1);
            if (i % 2 == 0)
            {
                bq[0] = rng.Next(1, 200);                                                // touchline-only update
                rows.Add(new DepthRawRow("FUT", id++, at, at, Depth(bq, aq)));
            }
            else
            {
                (rng.Next(2) == 0 ? bq : aq)[rng.Next(1, 5)] = rng.Next(1, 200);          // depth-only update
                var book = i % 37 == 0 ? Depth(bq, aq, gap: -3) : Depth(bq, aq);
                rows.Add(new DepthRawRow("FUT", id++, at, at, i % 53 == 0 ? null : book));
            }
        }

        return rows;
    }

    static List<AdaptiveCompletedBarPackage> Drive(AdaptiveObserverEngine engine, IEnumerable<(string Token, CleanObserverTick Tick)> ticks, FuturesDepth5Feed? feed)
    {
        var bars = new List<AdaptiveCompletedBarPackage>();
        foreach (var g in ticks.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key))
        {
            if (feed is not null) foreach (var o in feed.DrainThrough(g.Key)) engine.ObserveDepth(o);
            bars.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
        }

        return bars;
    }

    static FuturesDepth5Feed Feed(IEnumerable<DepthRawRow> rows)
    {
        var feed = new FuturesDepth5Feed();
        feed.Ingest(rows, T0);
        return feed;
    }

    /// <summary>Synthetic packages with a populated depth5 sidecar, for the PostgreSQL tests.</summary>
    internal static List<AdaptiveCompletedBarPackage> SamplePackages() => Drive(Engine(true), Ticks(300, false), Feed(DepthRows(400)));

    [Fact]
    public void T9_CausalOrdering_InterleavedFuturesOptionsAndSpot_DeliversEveryDepthEventUpToTheBarEnd_AndNoLaterOne()
    {
        var ce = new ObserverOptionInstrument("CE", "NIFTYCE", OptionType.Call, 23000, new DateOnly(2026, 10, 15), 65);
        var ticks = Ticks(400, withOptions: true);
        var rows = DepthRows(500);
        var bars = Drive(Engine(true, options: [ce]), ticks, Feed(rows));
        Assert.True(bars.Count > 3);

        // Independent oracle: events from the normalizer, in (AvailableAt, Id) order, counted per bar by the (start, end] convention with the reference rule.
        var normalizer = new AdaptiveDepthNormalizer();
        var all = rows.Select(normalizer.Process).Where(o => o is not null).Select(o => o!).OrderBy(o => o.AvailableAt).ThenBy(o => o.Id).ToList();
        foreach (var bar in bars)
        {
            var start = bar.FutureBar.StartAvailableAtUtc; var end = bar.FutureBar.EndAvailableAtUtc;
            var inside = all.Where(o => o.AvailableAt > start && o.AvailableAt <= end).ToList();
            Assert.Equal(inside.Count(o => o.Kind == Depth5EventKind.Snapshot), bar.Depth5!.SnapshotEvents);
            Assert.Equal(inside.Count(o => o.Kind == Depth5EventKind.L1Superseded), bar.Depth5.SupersededMarkers);
        }

        // an event at exactly the closing instant of a bar belongs to that bar (delivered before the group)
        var probe = bars[2].FutureBar.EndAvailableAtUtc;
        var extra = new DepthRawRow("FUT", 99999, probe, probe, Depth([10, 20, 33, 40, 50], Q));
        var withProbe = Drive(Engine(true, options: [ce]), ticks, Feed(rows.Append(extra)));
        Assert.Equal(bars[2].Depth5!.SnapshotEvents + 1, withProbe[2].Depth5!.SnapshotEvents);
    }

    [Fact]
    public void T10_CoreObserverJson_IsIdentical_WithTheDepthFlagEnabledOrDisabled()
    {
        var ticks = Ticks(600, withOptions: false);
        var off = Drive(Engine(false), ticks, null);
        var on = Drive(Engine(true), ticks, Feed(DepthRows(800)));
        Assert.NotEmpty(off);
        Assert.Equal(off.Count, on.Count);
        for (var i = 0; i < off.Count; i++)
        {
            Assert.Null(off[i].Depth5);
            Assert.NotNull(on[i].Depth5);
            Assert.Equal(JsonSerializer.Serialize(off[i]), JsonSerializer.Serialize(on[i] with { Depth5 = null }));         // every core, band, residual and other sidecar value, exact doubles
        }

        // enabled but fed NO events: still identical, and every second is "unobserved", never zero-filled
        var empty = Drive(Engine(true), ticks, Feed([]));
        Assert.All(empty, p => Assert.Equal(p.Depth5!.BarSeconds, p.Depth5.Depth5UnobservedSeconds, 9));
        Assert.Equal(JsonSerializer.Serialize(off[3]), JsonSerializer.Serialize(empty[3] with { Depth5 = null }));
    }

    [Fact]
    public void T11_LiveStylePollingAndFullReplay_ProduceIdenticalDepth5Values()
    {
        var ticks = Ticks(900, withOptions: false);
        var rows = DepthRows(1300);

        var replay = Drive(Engine(true), ticks, Feed(rows));

        // "live": rows arrive in small Id-ordered batches (each poll ingests what it read, then processes the groups that are stable)
        var feed = new FuturesDepth5Feed();
        var engine = Engine(true);
        var live = new List<AdaptiveCompletedBarPackage>();
        var rng = new Random(11);
        var next = 0;
        foreach (var g in ticks.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key))
        {
            // before processing the group at time T, the poll has read every depth row with availability <= T (stable lag), plus a random amount further ahead
            while (next < rows.Count && (rows[next].ReceivedAt <= g.Key || rng.Next(4) == 0))
            {
                var take = Math.Min(rng.Next(1, 6), rows.Count - next);
                feed.Ingest(rows.Skip(next).Take(take), T0);
                next += take;
            }

            foreach (var o in feed.DrainThrough(g.Key)) engine.ObserveDepth(o);
            live.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
        }

        Assert.Equal(replay.Count, live.Count);
        for (var i = 0; i < replay.Count; i++) Assert.Equal(JsonSerializer.Serialize(replay[i]), JsonSerializer.Serialize(live[i]));
        Assert.Equal(0, engine.LateDepthEvents);
    }

    [Fact]
    public void ZeroDurationSplitBars_AtTheEngineLevel_CarryNoTimeWeightedDepthValues()
    {
        var ticks = new List<(string Token, CleanObserverTick Tick)>
        {
            ("FUT", new CleanObserverTick(1, T0, T0, 23000, 22999, 23001, 10, 10, 1000, 5000)),
            ("FUT", new CleanObserverTick(2, T0.AddSeconds(2), T0.AddSeconds(2), 23001, 23000, 23002, 10, 10, 1020, 5000)),
            ("FUT", new CleanObserverTick(3, T0.AddSeconds(4), T0.AddSeconds(4), 23001, 23000, 23002, 10, 10, 1360, 5000)),   // +340 contracts: closes several 100-contract bars at the same instant
        };
        var rows = new[] { new DepthRawRow("FUT", 1, T0, T0, Depth(Q, Q)), new DepthRawRow("FUT", 2, T0.AddSeconds(1), T0.AddSeconds(1), Depth([10, 20, 31, 40, 50], Q)) };
        var bars = Drive(Engine(true, null), ticks, Feed(rows));
        var zero = bars.Where(b => b.FutureBar.StartAvailableAtUtc == b.FutureBar.EndAvailableAtUtc).ToList();
        Assert.NotEmpty(zero);
        foreach (var z in zero)
        {
            Assert.Equal(0d, z.Depth5!.BarSeconds);
            Assert.Null(z.Depth5.Ofi5);
            Assert.Null(z.Depth5.Tob5TimeWeighted); Assert.Null(z.Depth5.MicroDev5TimeWeighted); Assert.Null(z.Depth5.DepthImb5TimeWeighted);
            Assert.Equal(0d, z.Depth5.Depth5UsableSeconds);
            Assert.Equal(0, z.Depth5.Ofi5Transitions);
        }

        // the non-zero bars together carry the whole transition: nothing is duplicated into the zero-duration ones
        Assert.Equal(1, bars.Sum(b => b.Depth5!.Ofi5Transitions));
    }

    // ------------------------------------------------------------------ persistence (in-memory) and flag-off behaviour (tests 11, 13)

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionStateRow SessionRow() => new()
    {
        Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
        FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
        EstimatorName = "V1", CreatedAtUtc = T0,
    };

    [Fact]
    public async Task Persistence_InsertsThenVerifies_AMismatchIsMarkedAndNeverOverwritten_AndDisabledPackagesWriteNothing()
    {
        var bars = Drive(Engine(true), Ticks(300, false), Feed(DepthRows(400)));
        var svc = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        await using var db = Observer();
        db.Sessions.Add(SessionRow());
        await db.SaveChangesAsync();

        Assert.Equal(SupplementalOutcome.Inserted, await svc.PersistOrVerifyFuturesDepth5Async(db, 1, bars[0], default));
        Assert.Equal(SupplementalOutcome.Verified, await svc.PersistOrVerifyFuturesDepth5Async(db, 1, bars[0], default));      // restart replay of the same bar
        Assert.Equal(SupplementalOutcome.Skipped, await svc.PersistOrVerifyFuturesDepth5Async(db, 1, bars[1] with { Depth5 = null }, default));
        Assert.True(await svc.PersistAndVerifyAsync(db, SessionRow(), bars[1], default));
        Assert.Equal(2, await db.FuturesDepth5.CountAsync());

        // a recomputed value that differs: row untouched, durable known-invalid marker
        var stored = await db.FuturesDepth5.AsNoTracking().SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq);
        var tampered = bars[0] with { Depth5 = bars[0].Depth5! with { Ofi5BridgedGapSeconds = bars[0].Depth5!.Ofi5BridgedGapSeconds + 1 } };
        Assert.Equal(SupplementalOutcome.Mismatch, await svc.PersistOrVerifyFuturesDepth5Async(db, 1, tampered, default));
        Assert.Equal(stored.Ofi5BridgedGapSeconds, (await db.FuturesDepth5.AsNoTracking().SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq)).Ofi5BridgedGapSeconds);
        var marker = await db.ProjectionHealth.AsNoTracking().SingleAsync(x => x.Component == AdaptiveProjectionComponents.FuturesDepth5);
        Assert.Equal(FuturesDepth5Bar.MetricsVersion, marker.Version);
        Assert.Equal(1, svc.MismatchCount);

        // a package without depth (flag off) never writes a row
        await using var off = Observer();
        off.Sessions.Add(SessionRow());
        await off.SaveChangesAsync();
        foreach (var p in Drive(Engine(false), Ticks(300, false), null).Take(5)) Assert.True(await svc.PersistAndVerifyAsync(off, SessionRow(), p, default));
        Assert.Empty(await off.FuturesDepth5.ToListAsync());
    }

    [Fact]
    public async Task Persistence_AFailingContext_IsFailed_QueuedForRetry_AndDoesNotChangeTheVerificationResult()
    {
        var bars = Drive(Engine(true), Ticks(300, false), Feed(DepthRows(400)));
        var svc = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var broken = Observer();
        await broken.DisposeAsync();
        Assert.Equal(SupplementalOutcome.Failed, await svc.PersistOrVerifyFuturesDepth5Async(broken, 1, bars[0], default));
        Assert.Equal(1, svc.FailureCount);

        // PersistAndVerifyAsync on a context whose ONLY failing sidecar is depth5: the other sidecars still verify, and the depth failure is queued, not part of the returned integrity result.
        await using var db = Observer();
        db.Sessions.Add(SessionRow());
        await db.SaveChangesAsync();
        Assert.True(await svc.PersistAndVerifyAsync(db, SessionRow(), bars[0], default));
        Assert.Equal(0, svc.PendingDepth5RetryCount);
    }

    [Fact]
    public async Task T13_FlagOff_RecoveryBuildsNoDepthFeed_NoDepthEngine_AndPackagesCarryNoDepth()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        var cutoff = context.SignalStartUtc;
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = cutoff });
        await db.SaveChangesAsync();
        source.Ticks.AddRange(
            new Tick { Id = 1, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = cutoff.AddSeconds(-2), ReceivedAt = cutoff.AddSeconds(-2), LastPrice = 23000m, Volume = 1000, Depth = Depth(Q, Q) },
            new Tick { Id = 2, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = cutoff.AddSeconds(-1), ReceivedAt = cutoff.AddSeconds(-1), LastPrice = 23000m, Volume = 1010, Depth = Depth([10, 20, 31, 40, 50], Q) },
            new Tick { Id = 3, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = cutoff.AddSeconds(2), ReceivedAt = cutoff.AddSeconds(2), LastPrice = 23001m, Volume = 1150, Depth = Depth([10, 20, 32, 40, 50], Q) });
        await source.SaveChangesAsync();

        AdaptiveStateRecoveryService Service(FuturesDepth5Gateway? gateway) => new(new AdaptiveSourceTickReader(),
            new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance),
            NullLogger<AdaptiveStateRecoveryService>.Instance, depth5: gateway);

        FuturesDepth5Gateway Gateway(bool enabled) => new(Microsoft.Extensions.Options.Options.Create(new Top5MicrostructureOptions { Enabled = enabled }), new AdaptiveDepthTickReader());

        var offResult = await Service(null).RecoverAsync(source, db, context, cutoff, default);
        Assert.Null(offResult.Depth5Feed);
        Assert.False(offResult.Engine.TracksFuturesDepth5);

        var disabledGateway = await Service(Gateway(false)).RecoverAsync(source, db, context, cutoff, default);
        Assert.Null(disabledGateway.Depth5Feed);
        Assert.False(disabledGateway.Engine.TracksFuturesDepth5);
        Assert.Equal(offResult.LastFetchedRawId, disabledGateway.LastFetchedRawId);
        Assert.Equal(offResult.LastProcessedTickId, disabledGateway.LastProcessedTickId);
        Assert.Equal(JsonSerializer.Serialize(offResult.Engine.FutureBars), JsonSerializer.Serialize(disabledGateway.Engine.FutureBars));

        // enabled: identical core results and cursors; the feed carries the depth channel's own cursor and the events beyond the stable cutoff stay pending
        var onResult = await Service(Gateway(true)).RecoverAsync(source, db, context, cutoff, default);
        Assert.NotNull(onResult.Depth5Feed);
        Assert.True(onResult.Engine.TracksFuturesDepth5);
        Assert.Equal(offResult.LastFetchedRawId, onResult.LastFetchedRawId);
        Assert.Equal(offResult.LastProcessedTickId, onResult.LastProcessedTickId);
        Assert.Equal(offResult.LastProcessedAvailableAt, onResult.LastProcessedAvailableAt);
        Assert.Equal(JsonSerializer.Serialize(offResult.Engine.FutureBars), JsonSerializer.Serialize(onResult.Engine.FutureBars));
        Assert.Equal(3, onResult.Depth5Feed!.LastFetchedId);
        Assert.True(onResult.Depth5Feed.PendingCount >= 1);                                  // the row after the cutoff is not delivered yet
    }

    [Fact]
    public void T13b_TheShippedHostDefaultsTheFlagToOff()
    {
        Assert.False(new Top5MicrostructureOptions().Enabled);
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (var d = new DirectoryInfo(dir); d is not null && path is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NiftySignal.Host", "appsettings.json"))) path = Path.Combine(d.FullName, "NiftySignal.Host", "appsettings.json");
        Assert.NotNull(path);
        using var json = JsonDocument.Parse(File.ReadAllText(path!));
        Assert.False(json.RootElement.GetProperty("Adaptive").GetProperty("Top5Microstructure").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void LateDepthEvents_AreDroppedAndCounted_NeverReorderedIntoAnAlreadyCompletedBar()
    {
        var t = new FuturesDepth5Tracker();
        t.Observe(Snap(5, 5, A));
        t.Observe(Snap(4, 4, B));
        Assert.Equal(1, t.LateEvents);
        Assert.Equal(1, t.RetainedEvents);
    }
}
