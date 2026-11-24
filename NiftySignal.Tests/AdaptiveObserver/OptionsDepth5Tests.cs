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

/// <summary>S5 of the Top-5 design: per-contract options depth sidecar (independent per-token D10 state, persistence + own attestation, flag parity). Synthetic data only.</summary>
public sealed class OptionsDepth5Tests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 3, 45, 0, TimeSpan.Zero);
    static readonly DateOnly Day = new(2026, 10, 8);
    static readonly DateOnly Weekly = new(2026, 10, 15);
    static decimal Px(int ticks) => ticks * 0.05m;
    static readonly long[] Q = [10, 20, 30, 40, 50];

    static MarketDepth Depth(long[] bq, long[] aq, int bestBid = 2000, int gap = 2) => new(
        Px(bestBid), bq[0], Px(bestBid - 1), bq[1], Px(bestBid - 2), bq[2], Px(bestBid - 3), bq[3], Px(bestBid - 4), bq[4],
        Px(bestBid + gap), aq[0], Px(bestBid + gap + 1), aq[1], Px(bestBid + gap + 2), aq[2], Px(bestBid + gap + 3), aq[3], Px(bestBid + gap + 4), aq[4]);

    static readonly Depth5Book A = Depth5Mapping.FromMarketDepth(Depth(Q, Q));
    static readonly Depth5Book B = Depth5Mapping.FromMarketDepth(Depth([10, 20, 31, 40, 50], Q));
    static readonly Depth5Book C = Depth5Mapping.FromMarketDepth(Depth([10, 20, 31, 40, 52], Q));
    static readonly Depth5Book Crossed = Depth5Mapping.FromMarketDepth(Depth(Q, Q, gap: -3));

    static Depth5Observation Snap(string token, long id, double sec, Depth5Book book) => new(token, id, T0.AddSeconds(sec), T0.AddSeconds(sec), book);
    static Depth5Observation Mark(string token, long id, double sec) => new(token, id, T0.AddSeconds(sec), T0.AddSeconds(sec), null, Depth5EventKind.L1Superseded);
    static Depth5Observation Gap(string token, long id, double sec) => new(token, id, T0.AddSeconds(sec), T0.AddSeconds(sec), null, Depth5EventKind.FeedGap);
    static long E(Depth5Book p, Depth5Book n) => Depth5Metrics.Ofi5Contribution(p, n)!.Value;

    // ---- universe: 20 strikes x CE/PE = 40 contracts ("C0..C19" ascending strikes, "P0..P19"); the ATM region is index 9/10 of 22900..23100 step 10
    static readonly IReadOnlyList<ObserverOptionInstrument> Universe =
        [.. Enumerable.Range(0, 20).Select(i => new ObserverOptionInstrument("C" + i, $"NIFTY{22900 + i * 10}CE", OptionType.Call, 22900 + i * 10, Weekly, 65)),
         .. Enumerable.Range(0, 20).Select(i => new ObserverOptionInstrument("P" + i, $"NIFTY{22900 + i * 10}PE", OptionType.Put, 22900 + i * 10, Weekly, 65))];

    static AdaptiveObserverEngine Engine(bool futures, bool options, IReadOnlyList<ObserverOptionInstrument>? universe = null) => new(
        new AdaptiveSessionDefinition(Day, "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
        Weekly, .065, null, T0, universe ?? Universe, null, null, projectStrikes: true, trackFuturesDepth5: futures, trackOptionsDepth5: options);

    static List<(string Token, CleanObserverTick Tick)> Ticks(int n)
    {
        var rng = new Random(7);
        var list = new List<(string, CleanObserverTick)>();
        double bid = 23000; long volume = 1000; var id = 1000L;
        for (var i = 0; i < n; i++)
        {
            bid += rng.Next(-1, 2); volume += rng.Next(0, 60);
            var at = T0.AddSeconds(i * 0.9);
            list.Add(("FUT", new CleanObserverTick(++id, at, at, bid + 0.5, bid, bid + 1, rng.Next(1, 300), rng.Next(1, 300), volume, 5000)));
            foreach (var o in Universe)
                if ((i + o.Token.Length + (int)o.Strike / 10) % 3 == 0)
                    list.Add((o.Token, new CleanObserverTick(++id, at.AddSeconds(0.3), at.AddSeconds(0.3), 100 + rng.Next(5), 99, 101, 50, 60, 100 + i, 10)));
        }

        return list;
    }

    /// <summary>Stored depth rows for one token: touchline-only rows (L1 qty moves) alternate with depth-only rows; period seconds controls liquidity; optional invalid injections.</summary>
    static List<DepthRawRow> Rows(string token, ref long id, int n, double period, int seed, bool invalid = false)
    {
        var rng = new Random(seed);
        var rows = new List<DepthRawRow>();
        var bq = Q.ToArray(); var aq = Q.ToArray();
        for (var i = 0; i < n; i++)
        {
            var at = T0.AddSeconds(i * period + 0.1);
            if (i % 2 == 0)
            {
                bq[0] = rng.Next(1, 200);
                rows.Add(new DepthRawRow(token, id++, at, at, Depth(bq, aq)));
            }
            else
            {
                (rng.Next(2) == 0 ? bq : aq)[rng.Next(1, 5)] = rng.Next(1, 200);
                var book = invalid && i % 9 == 1 ? Depth(bq, aq, gap: -3) : Depth(bq, aq);
                rows.Add(new DepthRawRow(token, id++, at, at, invalid && i % 13 == 1 ? null : book));
            }
        }

        return rows;
    }

    static List<DepthRawRow> UniverseRows(int n)
    {
        long id = 1;
        var all = new List<DepthRawRow>();
        for (var k = 0; k < Universe.Count; k++)
            all.AddRange(Rows(Universe[k].Token, ref id, n, 0.7 + (k % 5) * 0.4, 100 + k, invalid: k % 7 == 3));
        // Id order must follow arrival order across tokens, as in the stored table
        var ordered = all.OrderBy(r => r.ReceivedAt).ThenBy(r => r.Token, StringComparer.Ordinal).Select((r, i) => r with { Id = i + 1 }).ToList();
        return ordered;
    }

    static FuturesDepth5Feed Feed(IEnumerable<DepthRawRow> rows)
    {
        var feed = new FuturesDepth5Feed();
        feed.Ingest(rows, T0);
        return feed;
    }

    static List<AdaptiveCompletedBarPackage> Drive(AdaptiveObserverEngine engine, IEnumerable<(string Token, CleanObserverTick Tick)> ticks, FuturesDepth5Feed? options, FuturesDepth5Feed? futures = null)
    {
        var bars = new List<AdaptiveCompletedBarPackage>();
        foreach (var g in ticks.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key))
        {
            if (futures is not null) foreach (var o in futures.DrainThrough(g.Key)) engine.ObserveDepth(o);
            if (options is not null) foreach (var o in options.DrainThrough(g.Key)) engine.ObserveDepth(o);
            bars.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
        }

        return bars;
    }

    static string Core(AdaptiveCompletedBarPackage p) => JsonSerializer.Serialize(p with { Depth5 = null, OptionsDepth5 = null });

    static FuturesDepth5Bar Solo(string token, double start, double end, params Depth5Observation[] events)
    {
        var t = new OptionsDepth5Tracker([Universe.First(x => x.Token == token)]);
        foreach (var e in events.Where(x => x.Token == token)) t.Observe(e);
        return t.Complete(T0.AddSeconds(start), T0.AddSeconds(end)).Single().Depth;
    }

    // ------------------------------------------------------------------ 1, 9: full universe, contract identity

    [Fact]
    public void T1_T9_FullFortyContractUniverse_EveryBarCarriesEveryContract_WithItsIdentity()
    {
        Assert.Equal(40, Universe.Count);
        var bars = Drive(Engine(false, true), Ticks(500), Feed(UniverseRows(600)));
        Assert.True(bars.Count > 3);
        foreach (var p in bars)
        {
            Assert.NotNull(p.OptionsDepth5);
            Assert.Equal(40, p.OptionsDepth5!.Count);
            Assert.Equal(p.OptionsDepth5.Select(x => x.Token).OrderBy(x => x, StringComparer.Ordinal), p.OptionsDepth5.Select(x => x.Token));
            foreach (var row in p.OptionsDepth5)
            {
                var u = Universe.Single(x => x.Token == row.Token);
                Assert.Equal((u.ExpiryDate, u.Strike, u.OptionType, u.TradingSymbol), (row.Expiry, row.Strike, row.Side, row.TradingSymbol));
                Assert.Equal(p.FutureBar.DurationSeconds, row.Depth.BarSeconds, 9);
            }
        }

        Assert.Contains(bars.SelectMany(p => p.OptionsDepth5!), r => r.Depth.SnapshotEvents > 0);
        // the identity survives the mapping to the stored row, with the side as the enum (never a swapped CE/PE)
        var mapped = AdaptiveSupplementalPersistence.MapOptionsDepth5(1, 1, bars[0].OptionsDepth5!.First(x => x.Token == "P3"));
        Assert.Equal((OptionType.Put, 22930d, Weekly, "P3"), (mapped.Side, mapped.Strike, mapped.Expiry, mapped.Token));
    }

    // ------------------------------------------------------------------ 2: independent per-token state, ordering, freshness

    [Fact]
    public void T2_TokensAreIndependent_AJointRunEqualsEachTokensSoloRun()
    {
        var events = new[]
        {
            Snap("C1", 1, 0, A), Mark("C1", 2, 1), Snap("C1", 3, 3, B),
            Snap("C2", 4, 0.5, A), Snap("C2", 5, 7, C),                // C2: more than 5 s between endpoints -> reset
            Snap("P1", 6, 1, Crossed), Snap("P1", 7, 2, A), Snap("P1", 8, 2.5, B),
            Gap("C3", 9, 2), Snap("C3", 10, 3, A),
        };
        var joint = new OptionsDepth5Tracker(Universe);
        foreach (var e in events.OrderBy(x => x.AvailableAt).ThenBy(x => x.Id)) joint.Observe(e);
        var all = joint.Complete(T0.AddSeconds(-1), T0.AddSeconds(20));
        foreach (var token in new[] { "C1", "C2", "P1", "C3", "C9" })
            Assert.Equal(JsonSerializer.Serialize(Solo(token, -1, 20, events)), JsonSerializer.Serialize(all.Single(x => x.Token == token).Depth));

        Assert.Equal(E(A, B), all.Single(x => x.Token == "C1").Depth.Ofi5);
        Assert.Null(all.Single(x => x.Token == "C2").Depth.Ofi5);
        Assert.Equal(E(A, B), all.Single(x => x.Token == "P1").Depth.Ofi5);                 // only after the invalid snapshot broke the baseline
        Assert.Equal(0, joint.LateEvents);
    }

    [Fact]
    public void T2b_LateEventsAreCountedPerToken_AnotherTokensClockNeverMakesAnEventLate()
    {
        var t = new OptionsDepth5Tracker(Universe);
        t.Observe(Snap("C1", 5, 10, A));
        t.Observe(Snap("C2", 6, 2, A));                                  // earlier than C1's event, but a DIFFERENT token: not late
        Assert.Equal(0, t.LateEvents);
        t.Observe(Snap("C1", 7, 4, B));                                  // late for C1
        Assert.Equal(1, t.LateEvents);
        t.Observe(Snap("NOT_IN_UNIVERSE", 8, 1, A));
        Assert.Equal(1, t.UnknownTokenEvents);
        Assert.Equal(2, t.RetainedEvents);
    }

    [Fact]
    public void T2c_FreshnessIsPerToken_OneTokenGoesStaleWhileAnotherStaysUsable()
    {
        var t = new OptionsDepth5Tracker(Universe);
        t.Observe(Snap("C1", 1, 0, A));
        t.Observe(Snap("C2", 2, 0, A)); t.Observe(Snap("C2", 3, 4, B)); t.Observe(Snap("C2", 4, 8, C));
        var bar = t.Complete(T0.AddSeconds(-1), T0.AddSeconds(10));
        Assert.Equal(5d, bar.Single(x => x.Token == "C1").Depth.Depth5UsableSeconds, 12);
        Assert.Equal(5d, bar.Single(x => x.Token == "C1").Depth.Depth5StaleSeconds, 12);
        Assert.Equal(10d, bar.Single(x => x.Token == "C2").Depth.Depth5UsableSeconds, 12);       // 0-4, 4-8, 8-10 each fresh; the first second is unobserved
    }

    // ------------------------------------------------------------------ 3, 16: strike coverage and unavailability

    [Fact]
    public void T3_T16_CoverageDiffersByLiquidity_AndAContractWithNoEventsIsUnobserved_NeverZero()
    {
        var bars = Drive(Engine(false, true), Ticks(500), Feed(UniverseRows(600).Where(r => r.Token != "C0" && r.Token != "P19")));   // far-OTM ends have no depth rows at all
        var totals = Universe.ToDictionary(u => u.Token, _ => (usable: 0d, seconds: 0d));
        foreach (var p in bars)
            foreach (var r in p.OptionsDepth5!)
                totals[r.Token] = (totals[r.Token].usable + r.Depth.Depth5UsableSeconds, totals[r.Token].seconds + r.Depth.BarSeconds);

        Assert.Equal(0d, totals["C0"].usable);
        Assert.All(bars.SelectMany(p => p.OptionsDepth5!.Where(r => r.Token is "C0" or "P19")), r =>
        {
            Assert.Equal(r.Depth.BarSeconds, r.Depth.Depth5UnobservedSeconds, 9);                // the whole bar is "not observed", never a fabricated book
            Assert.Null(r.Depth.Ofi5); Assert.Null(r.Depth.GrossOfi5);
            Assert.Null(r.Depth.Tob5TimeWeighted); Assert.Null(r.Depth.MicroDev5TimeWeighted); Assert.Null(r.Depth.DepthImb5TimeWeighted);
            Assert.Equal(0, r.Depth.Ofi5Transitions);
        });
        // busy contracts (period 0.7 s: k % 5 == 0) cover far more of the session than sparse ones (period 2.3 s: k % 5 == 4), but a sparse contract is still observed
        double Cov(string t) => totals[t].usable / totals[t].seconds;
        Assert.InRange(Cov("C5"), 0.3, 1.0);          // the synthetic book alternates touchline-only and depth rows 0.7 s apart, so roughly half the time is superseded by design
        Assert.InRange(Cov("C9"), 0.01, 1.0);
    }

    // ------------------------------------------------------------------ 4: missing / padded / crossed / non-monotone books

    [Fact]
    public void T4_MissingPaddedCrossedAndNonMonotoneBooks_AreInvalid_NeverBridged_AndNeverZeroFilled()
    {
        var padded = Depth5Mapping.FromMarketDepth(new MarketDepth(Px(2000), 10, Px(1999), 20, Px(1998), 30, 0m, 0, 0m, 0, Px(2002), 10, Px(2003), 20, Px(2004), 30, 0m, 0, 0m, 0));
        var nonMonotone = Depth5Mapping.FromMarketDepth(new MarketDepth(Px(2000), 10, Px(1999), 20, Px(2001), 30, Px(1997), 40, Px(1996), 50, Px(2002), 10, Px(2003), 20, Px(2004), 30, Px(2005), 40, Px(2006), 50));
        var emptyTop = Depth5Mapping.FromMarketDepth(new MarketDepth(0m, 0, Px(1999), 20, Px(1998), 30, Px(1997), 40, Px(1996), 50, Px(2002), 10, Px(2003), 20, Px(2004), 30, Px(2005), 40, Px(2006), 50));
        Assert.False(Crossed!.IsValid);
        Assert.False(nonMonotone!.IsValid);
        Assert.False(emptyTop!.IsValid);                         // padding of the TOP of the book is invalid ...
        Assert.True(padded!.IsValid);                            // ... empty DEEP levels of an otherwise coherent book are a valid, thinner book (the S2 contract)
        foreach (var bad in new Depth5Book?[] { Crossed, emptyTop, nonMonotone })
        {
            var t = new OptionsDepth5Tracker(Universe);
            t.Observe(Snap("C1", 1, 0, A)); t.Observe(Snap("C1", 2, 1, bad!)); t.Observe(Snap("C1", 3, 2, B));
            var d = t.Complete(T0.AddSeconds(-1), T0.AddSeconds(10)).Single(x => x.Token == "C1").Depth;
            Assert.Equal(0, d.Ofi5Transitions);
            Assert.Equal(1, d.Ofi5BaselineResets);
            Assert.Equal(1, d.InvalidSnapshots);
            Assert.Equal(1d, d.Depth5InvalidSeconds, 12);
        }

        var none = new OptionsDepth5Tracker(Universe);                                            // a stored row with no depth at all
        none.Observe(Snap("C1", 1, 0, A));
        none.Observe(new Depth5Observation("C1", 2, T0.AddSeconds(1), T0.AddSeconds(1), null, Depth5EventKind.NoDepth));
        none.Observe(Snap("C1", 3, 2, B));
        var n = none.Complete(T0.AddSeconds(-1), T0.AddSeconds(10)).Single(x => x.Token == "C1").Depth;
        Assert.Equal((0, 1d), (n.Ofi5Transitions, n.Depth5NoDepthSeconds));
    }

    // ------------------------------------------------------------------ 5, 6, 7: L1Superseded, accounting, expiry

    [Fact]
    public void T5_T6_L1SupersededThenCoherentDepth_IsBridged_PerToken_AndDirectBridgedAccountingAdds()
    {
        var t = new OptionsDepth5Tracker(Universe);
        foreach (var e in new[]
                 {
                     Snap("C1", 1, 0, A), Mark("C1", 2, 1), Snap("C1", 3, 2, B), Snap("C1", 4, 3, C), Mark("C1", 5, 4), Mark("C1", 6, 4.5), Snap("C1", 7, 6, A),
                     Snap("P2", 8, 0, A), Snap("P2", 9, 1, B),                                           // another token: direct only
                 }.OrderBy(x => x.AvailableAt).ThenBy(x => x.Id))
            t.Observe(e);
        var all = t.Complete(T0.AddSeconds(-1), T0.AddSeconds(20));
        var c1 = all.Single(x => x.Token == "C1").Depth;
        Assert.Equal((3, 1, 2), (c1.Ofi5Transitions, c1.Ofi5DirectTransitions, c1.Ofi5BridgedTransitions));
        Assert.Equal(E(A, B) + E(B, C) + E(C, A), c1.Ofi5);
        Assert.Equal(E(A, B) + E(C, A), c1.Ofi5BridgedNet);
        Assert.Equal(1d + 2d, c1.Ofi5BridgedGapSeconds, 12);
        Assert.Equal(3, c1.SupersededMarkers);
        var p2 = all.Single(x => x.Token == "P2").Depth;
        Assert.Equal((1, 1, 0), (p2.Ofi5Transitions, p2.Ofi5DirectTransitions, p2.Ofi5BridgedTransitions));
        Assert.Null(p2.Ofi5BridgedNet);
        // a marker followed by an IDENTICAL snapshot is no transition, and a marker with nothing after it is only unavailable time
        var again = Solo("C1", -1, 10, Snap("C1", 1, 0, A), Mark("C1", 2, 1), Snap("C1", 3, 2, A));
        Assert.Equal(0, again.Ofi5Transitions);
        var dangling = Solo("C1", -1, 10, Snap("C1", 1, 0, A), Mark("C1", 2, 1));
        Assert.Equal((0, 9d), (dangling.Ofi5Transitions, dangling.Depth5SupersededSeconds));
    }

    [Fact]
    public void T7_FiveSecondExpiryAndBaselineReset_AndTheClockIsNeverRenewedByRepeats()
    {
        Assert.Equal(1, Solo("C1", -1, 30, Snap("C1", 1, 0, A), Snap("C1", 2, 5, B)).Ofi5Transitions);          // exactly 5 s is still eligible
        var late = Solo("C1", -1, 30, Snap("C1", 1, 0, A), Mark("C1", 2, 1), Snap("C1", 3, 6.5, B));
        Assert.Equal((0, 1), (late.Ofi5Transitions, late.Ofi5BaselineResets));
        var next = Solo("C1", -1, 30, Snap("C1", 1, 0, A), Snap("C1", 2, 7, B), Snap("C1", 3, 9, C));
        Assert.Equal(E(B, C), next.Ofi5);
        var repeats = Solo("C1", -1, 30, Snap("C1", 1, 0, A), Snap("C1", 2, 4, A), Snap("C1", 3, 7, B));
        Assert.Equal((10d, 0), (repeats.Depth5UsableSeconds, repeats.Ofi5Transitions));
    }

    // ------------------------------------------------------------------ 8: zero-duration bars

    [Fact]
    public void T8_ZeroDurationSplitBars_CarryNoOptionsDepth()
    {
        var ticks = new List<(string Token, CleanObserverTick Tick)>
        {
            ("FUT", new CleanObserverTick(1, T0, T0, 23000, 22999, 23001, 10, 10, 1000, 5000)),
            ("FUT", new CleanObserverTick(2, T0.AddSeconds(2), T0.AddSeconds(2), 23001, 23000, 23002, 10, 10, 1020, 5000)),
            ("FUT", new CleanObserverTick(3, T0.AddSeconds(4), T0.AddSeconds(4), 23001, 23000, 23002, 10, 10, 1360, 5000)),
        };
        var rows = new[] { new DepthRawRow("C1", 1, T0, T0, Depth(Q, Q)), new DepthRawRow("C1", 2, T0.AddSeconds(1), T0.AddSeconds(1), Depth([10, 20, 31, 40, 50], Q)) };
        var bars = Drive(Engine(false, true), ticks, Feed(rows));
        var zero = bars.Where(b => b.FutureBar.StartAvailableAtUtc == b.FutureBar.EndAvailableAtUtc).ToList();
        Assert.NotEmpty(zero);
        foreach (var z in zero)
            Assert.All(z.OptionsDepth5!, r =>
            {
                Assert.Equal(0d, r.Depth.BarSeconds);
                Assert.Null(r.Depth.Ofi5); Assert.Null(r.Depth.Tob5TimeWeighted); Assert.Null(r.Depth.MicroDev5TimeWeighted); Assert.Null(r.Depth.DepthImb5TimeWeighted);
                Assert.Equal(0d, r.Depth.Depth5UsableSeconds + r.Depth.Depth5StaleSeconds + r.Depth.Depth5InvalidSeconds + r.Depth.Depth5SupersededSeconds + r.Depth.Depth5NoDepthSeconds + r.Depth.Depth5UnobservedSeconds);
            });
        Assert.Equal(1, bars.SelectMany(b => b.OptionsDepth5!).Where(r => r.Token == "C1").Sum(r => r.Depth.Ofi5Transitions));
    }

    // ------------------------------------------------------------------ 10, 11, 12: restart / multi-token replay, flag parity, futures sidecar parity

    [Fact]
    public void T11_T12_CoreAndFuturesSidecar_AreIdentical_WithOptionsDepthOnOrOff_IncludingStrikeProjection()
    {
        var ticks = Ticks(600);
        var futureRows = Rows("FUT", ref SharedId, 800, 0.8, 5);
        var optRows = UniverseRows(700);
        var off = Drive(Engine(true, false), ticks, null, Feed(futureRows));
        var on = Drive(Engine(true, true), ticks, Feed(optRows), Feed(futureRows));
        var bare = Drive(Engine(false, false), ticks, null);
        Assert.Equal(off.Count, on.Count);
        for (var i = 0; i < off.Count; i++)
        {
            Assert.Null(off[i].OptionsDepth5); Assert.NotNull(on[i].OptionsDepth5);
            Assert.Equal(JsonSerializer.Serialize(off[i]), JsonSerializer.Serialize(on[i] with { OptionsDepth5 = null }));       // core, strike projection (option-strike-v3), futures depth5: byte-identical
            Assert.Equal(Core(bare[i]), Core(on[i]));                                                                            // and identical to a Host with both flags off
            Assert.Equal(JsonSerializer.Serialize(bare[i].OptionStrikes), JsonSerializer.Serialize(on[i].OptionStrikes));
        }
    }

    static long SharedId = 5_000_000;

    [Fact]
    public void T10_FullReplay_MatchesLiveStylePolling_ForAllTokens()
    {
        var ticks = Ticks(700);
        var rows = UniverseRows(800);
        var replay = Drive(Engine(false, true), ticks, Feed(rows));

        var feed = new FuturesDepth5Feed();
        var engine = Engine(false, true);
        var live = new List<AdaptiveCompletedBarPackage>();
        var rng = new Random(11);
        var next = 0;
        foreach (var g in ticks.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key))
        {
            while (next < rows.Count && (rows[next].ReceivedAt <= g.Key || rng.Next(4) == 0))
            {
                var take = Math.Min(rng.Next(1, 40), rows.Count - next);
                feed.Ingest(rows.Skip(next).Take(take), T0);
                next += take;
            }

            foreach (var o in feed.DrainThrough(g.Key)) engine.ObserveDepth(o);
            live.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
        }

        Assert.Equal(replay.Count, live.Count);
        for (var i = 0; i < replay.Count; i++) Assert.Equal(JsonSerializer.Serialize(replay[i]), JsonSerializer.Serialize(live[i]));
        Assert.Equal(0, engine.LateOptionDepthEvents);
    }

    [Fact]
    public async Task T10b_TheBoundedMemoryLazySource_DeliversExactlyTheEventsOfAWholeDayIngest_AndKeepsRetainedStateBounded()
    {
        var ticks = Ticks(700);
        var rows = UniverseRows(800);
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        source.Ticks.AddRange(rows.Select(r => new Tick { Id = r.Id, Token = r.Token, Exchange = Exchange.Nfo, ExchangeTimestamp = r.ExchangeTimestamp, ReceivedAt = r.ReceivedAt, LastPrice = 100m, Volume = 1, Depth = r.Depth }));
        await source.SaveChangesAsync();

        var whole = Drive(Engine(false, true), ticks, Feed(rows));

        var lazy = new OptionsDepth5Source(new AdaptiveDepthTickReader(), source, Universe.Select(x => x.Token).ToArray(), Day, T0);
        var engine = Engine(false, true);
        var bars = new List<AdaptiveCompletedBarPackage>();
        var peakPending = 0; var peakRetained = 0;
        foreach (var g in ticks.GroupBy(x => x.Tick.AvailableAt).OrderBy(g => g.Key))
        {
            await lazy.AdvanceToAsync(g.Key, default);
            foreach (var o in lazy.Feed.DrainThrough(g.Key)) engine.ObserveDepth(o);
            bars.AddRange(engine.ProcessAvailabilityGroup(g.ToArray()));
            peakPending = Math.Max(peakPending, lazy.Feed.PendingCount);
            peakRetained = Math.Max(peakRetained, engine.RetainedOptionDepthEvents);
        }

        await lazy.FinishAsync(default);
        Assert.Equal(whole.Count, bars.Count);
        for (var i = 0; i < whole.Count; i++) Assert.Equal(JsonSerializer.Serialize(whole[i]), JsonSerializer.Serialize(bars[i]));
        Assert.Equal(rows.Max(r => r.Id), lazy.Feed.LastFetchedId);                                  // the cursor equals the last stored row
        Assert.Equal(rows.Count, lazy.RowsRead);
        Assert.True(peakPending < rows.Count / 10, $"pending events stayed small: {peakPending} of {rows.Count}");
        Assert.True(peakRetained < rows.Count / 5, $"retained tracker events stayed bounded: {peakRetained} of {rows.Count}");
        Assert.Equal(0, engine.LateOptionDepthEvents);
    }

    /// <summary>Recovery paging with ReceivedAt NOT monotonic in Id (bounded disorder, same and different tokens): every stored row is read exactly once (no loss, no duplicate) and each token's events equal a whole-day Id-order ingest.</summary>
    [Theory]
    [InlineData(3)]     // within the 5 s lookahead margin: delivery order must ALSO be exactly (AvailableAt, Id)
    [InlineData(30)]
    [InlineData(100)]   // below the 2-minute read-ahead window
    public async Task T10c_RecoveryPaging_WithNonMonotonicReceivedAt_LosesAndDuplicatesNothing_AndMatchesWholeDayIngest(int disorderSeconds)
    {
        var rng = new Random(disorderSeconds);
        // shift ReceivedAt by a random amount within +-disorder (Ids unchanged): across window boundaries and between tokens
        var stored = UniverseRows(1500).Select(r => r with { ReceivedAt = r.ReceivedAt + TimeSpan.FromSeconds((rng.NextDouble() * 2 - 1) * disorderSeconds) }).ToList();
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        source.Ticks.AddRange(stored.Select(r => new Tick { Id = r.Id, Token = r.Token, Exchange = Exchange.Nfo, ExchangeTimestamp = r.ExchangeTimestamp, ReceivedAt = r.ReceivedAt, LastPrice = 100m, Volume = 1, Depth = r.Depth }));
        await source.SaveChangesAsync();
        Assert.Contains(stored.OrderBy(r => r.Id).Zip(stored.OrderBy(r => r.Id).Skip(1)), p => p.Second.ReceivedAt < p.First.ReceivedAt);   // the disorder is real

        // The reader's day predicate (ReceivedAt >= market open - 1 min) applies to the live poll and the whole-day ingest alike: rows shifted below it are outside the day by definition, and are accounted for explicitly.
        var lower = T0.AddMinutes(-1);
        var inDay = stored.Where(r => r.ReceivedAt >= lower).ToList();
        Assert.All(stored.Except(inDay), r => Assert.True(r.ReceivedAt < lower));
        var whole = Feed(inDay.OrderBy(r => r.Id));
        var lazy = new OptionsDepth5Source(new AdaptiveDepthTickReader(), source, Universe.Select(x => x.Token).ToArray(), Day, T0);
        var drained = new List<Depth5Observation>();
        var instants = stored.Select(r => r.ReceivedAt).Order().ToArray();
        for (var i = 0; i < instants.Length; i += 37)
        {
            await lazy.AdvanceToAsync(instants[i], default);
            drained.AddRange(lazy.Feed.DrainThrough(instants[i]));
        }

        await lazy.FinishAsync(default);
        drained.AddRange(lazy.Feed.DrainThrough(DateTimeOffset.MaxValue));
        var expected = whole.DrainThrough(DateTimeOffset.MaxValue).ToList();

        Assert.Equal(inDay.Count, lazy.RowsRead);
        Assert.Equal(0, lazy.OutOfOrderRows);
        Assert.Equal(inDay.Max(r => r.Id), lazy.Feed.LastFetchedId);
        Assert.Equal(expected.Count, drained.Count);
        // No event lost, duplicated or altered: compare each token's events in canonical (AvailableAt, Id) order.
        static string Canon(IEnumerable<Depth5Observation> e) => JsonSerializer.Serialize(e.OrderBy(x => x.AvailableAt).ThenBy(x => x.Id));
        foreach (var token in expected.Select(e => e.Token).Distinct())
            Assert.Equal(Canon(expected.Where(e => e.Token == token)), Canon(drained.Where(e => e.Token == token)));
        // Delivery order: events handed to the engine are late when an event with a later AvailableAt was delivered before them. Within the lookahead margin there must be none.
        var late = 0; var max = DateTimeOffset.MinValue;
        foreach (var e in drained) { if (e.AvailableAt < max) late++; else max = e.AvailableAt; }
        if (disorderSeconds <= 3) Assert.Equal(0, late);
    }

    [Fact]
    public async Task T11b_Recovery_WithOptionsDepthOnOrOff_ReturnsTheSameCoreAndCursors_AndFlagOffBuildsNothing()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        var cutoff = context.SignalStartUtc;
        var optionTokens = new[] { "OC1", "OP1" };
        var withOptions = context with { Options = optionTokens.Select((t, i) => new ObserverOptionInstrument(t, t, i == 0 ? OptionType.Call : OptionType.Put, 23000, context.WeeklyOptionExpiry, 65)).ToArray() };
        long id = 0;
        for (var i = 0; i < 12; i++)
        {
            var at = cutoff.AddSeconds(-30 + i * 2.5);
            source.Ticks.Add(new Tick { Id = ++id, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000m + i, Volume = 1000 + i * 30, Depth = Depth(Q, Q) });
            foreach (var t in optionTokens)
                source.Ticks.Add(new Tick { Id = ++id, Token = t, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 120m, Volume = 10 + i, Depth = Depth([10 + i, 20, 30, 40, 50], Q) });
        }

        await source.SaveChangesAsync();

        async Task<AdaptiveRecoveryResult> Recover(FuturesDepth5Gateway? gateway)
        {
            await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            db.Sessions.Add(withOptions.Session);
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = withOptions.Session.Id, LastHeartbeatUtc = cutoff });
            await db.SaveChangesAsync();
            return await new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
                new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), NullLogger<AdaptiveStateRecoveryService>.Instance, depth5: gateway)
                .RecoverAsync(source, db, withOptions, cutoff, default);
        }

        static FuturesDepth5Gateway Gateway(bool futures, bool options) => new(Microsoft.Extensions.Options.Options.Create(new Top5MicrostructureOptions { Enabled = futures, OptionsEnabled = options }), new AdaptiveDepthTickReader());

        var off = await Recover(null);
        Assert.Null(off.OptionsDepth5Feed);
        Assert.False(off.Engine.TracksOptionsDepth5);
        var futuresOnly = await Recover(Gateway(true, false));
        Assert.Null(futuresOnly.OptionsDepth5Feed);                                                    // the S4 flag alone enables nothing of S5
        var on = await Recover(Gateway(false, true));
        Assert.NotNull(on.OptionsDepth5Feed);
        Assert.True(on.Engine.TracksOptionsDepth5);
        Assert.Null(on.Depth5Feed);                                                                    // and the S5 flag alone enables nothing of S4
        Assert.Equal(optionTokens, on.OptionDepthTokens);
        foreach (var r in new[] { futuresOnly, on })
        {
            Assert.Equal(off.LastFetchedRawId, r.LastFetchedRawId);
            Assert.Equal(off.LastProcessedTickId, r.LastProcessedTickId);
            Assert.Equal(JsonSerializer.Serialize(off.Engine.FutureBars), JsonSerializer.Serialize(r.Engine.FutureBars));
        }

        Assert.Equal(source.Ticks.Where(t => optionTokens.Contains(t.Token)).Max(t => t.Id), on.OptionsDepth5Feed!.LastFetchedId);
    }

    [Fact]
    public void T13b_TheShippedHostDefaultsBothFlagsToOff()
    {
        Assert.False(new Top5MicrostructureOptions().OptionsEnabled);
        string? path = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && path is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "NiftySignal.Host", "appsettings.json"))) path = Path.Combine(d.FullName, "NiftySignal.Host", "appsettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path!));
        var section = json.RootElement.GetProperty("Adaptive").GetProperty("Top5Microstructure");
        Assert.False(section.GetProperty("Enabled").GetBoolean());
        Assert.False(section.GetProperty("OptionsEnabled").GetBoolean());
    }

    // ------------------------------------------------------------------ 13, 14, 16: persistence, attestation, withholding

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionStateRow SessionRow() => new()
    {
        Id = 1, TradeDate = Day, ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
        FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
        EstimatorName = "V1", CreatedAtUtc = T0,
    };

    internal static List<AdaptiveCompletedBarPackage> SamplePackages() => Drive(Engine(true, true), Ticks(400), Feed(UniverseRows(500)), Feed(Rows("FUT", ref SharedId, 500, 0.8, 5)));

    static AdaptiveSupplementalPersistence Svc() => new(NullLogger<AdaptiveSupplementalPersistence>.Instance);

    [Fact]
    public async Task T14_Attestation_InsertsVerifiesAndMarksAMismatch_NeverOverwrites_AndWithholdsTheBar()
    {
        var bars = SamplePackages();
        var svc = Svc();
        await using var db = Observer();
        db.Sessions.Add(SessionRow());
        await db.SaveChangesAsync();

        Assert.Equal(SupplementalOutcome.Inserted, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[0], default));
        Assert.Equal(40, await db.OptionsDepth5.CountAsync());
        var att = await db.OptionsDepth5Attestations.AsNoTracking().SingleAsync();
        Assert.Equal((40, OptionDepth5Bar.MetricsVersion), (att.TokenCount, att.MetricsVersion));
        Assert.Equal(SupplementalOutcome.Verified, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[0], default));          // restart replay
        Assert.Equal(SupplementalOutcome.Inserted, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[1], default));
        Assert.Equal(SupplementalOutcome.Skipped, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[2] with { OptionsDepth5 = null }, default));

        // served rows: attested only, in (bar, token) order
        var served = await VerifiedOptionsDepth5.LoadServedRowsAsync(db, 1, 0, 1000, default);
        Assert.Equal(80, served.Count);

        // a recomputed value that differs: stored row untouched, whole bar marked and its attestation revoked in one step
        var tampered = bars[0] with { OptionsDepth5 = bars[0].OptionsDepth5!.Select(x => x.Token == "C7" ? x with { Depth = x.Depth with { Ofi5BridgedGapSeconds = x.Depth.Ofi5BridgedGapSeconds + 1 } } : x).ToList() };
        var storedBefore = await db.OptionsDepth5.AsNoTracking().SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq && x.Token == "C7");
        Assert.Equal(SupplementalOutcome.Mismatch, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, tampered, default));
        Assert.Equal(storedBefore.Ofi5BridgedGapSeconds, (await db.OptionsDepth5.AsNoTracking().SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq && x.Token == "C7")).Ofi5BridgedGapSeconds);
        Assert.DoesNotContain(await db.OptionsDepth5Attestations.AsNoTracking().ToListAsync(), a => a.BarSeq == bars[0].FutureBar.BarSeq);
        Assert.Single(await db.ProjectionHealth.AsNoTracking().Where(x => x.Component == AdaptiveProjectionComponents.OptionsDepth5).ToListAsync());
        var after = await VerifiedOptionsDepth5.LoadServedRowsAsync(db, 1, 0, 1000, default);
        Assert.Equal(40, after.Count);                                                                                              // the other bar is still served, the mismatching one is withheld entirely
        Assert.DoesNotContain(after, r => r.BarSeq == bars[0].FutureBar.BarSeq);
        // a second mismatch is idempotent
        Assert.Equal(SupplementalOutcome.Mismatch, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, tampered, default));
        Assert.Equal(2, svc.MismatchCount);
        // and the honest replay does not silently "heal" it: the bar stays withheld (no attestation is re-issued over a marked bar)
        Assert.Equal(SupplementalOutcome.Verified, await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[0], default));
        Assert.DoesNotContain(await VerifiedOptionsDepth5.LoadServedRowsAsync(db, 1, 0, 1000, default), r => r.BarSeq == bars[0].FutureBar.BarSeq);
    }

    [Fact]
    public async Task T16_MissingUnattestedAndEditedRows_AreUnavailable_NotZero()
    {
        var bars = SamplePackages();
        await using var db = Observer();
        db.Sessions.Add(SessionRow());
        await db.SaveChangesAsync();
        var svc = Svc();
        await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[0], default);
        await svc.PersistOrVerifyOptionsDepth5Async(db, 1, bars[1], default);

        // no attestation row: withheld (rows written before/without proof are never served)
        db.OptionsDepth5Attestations.RemoveRange(db.OptionsDepth5Attestations.Where(a => a.BarSeq == bars[1].FutureBar.BarSeq));
        // an edited row under a valid attestation: withheld by digest
        var edit = await db.OptionsDepth5.SingleAsync(x => x.BarSeq == bars[0].FutureBar.BarSeq && x.Token == "P4");
        edit.SnapshotEvents += 1;
        await db.SaveChangesAsync();
        var served = await VerifiedOptionsDepth5.LoadServedRowsAsync(db, 1, 0, 1000, default);
        Assert.DoesNotContain(served, r => r.BarSeq == bars[1].FutureBar.BarSeq);
        Assert.DoesNotContain(served, r => r.BarSeq == bars[0].FutureBar.BarSeq && r.Token == "P4");
        Assert.Equal(39, served.Count);
        // a bar that was never persisted simply has no rows; a contract with no events is stored with NULL metrics (unobserved), not zero
        var unobserved = new OptionsDepth5Tracker(Universe).Complete(T0, T0.AddSeconds(30)).First();
        var row = AdaptiveSupplementalPersistence.MapOptionsDepth5(1, 1, unobserved);
        Assert.Null(row.Ofi5); Assert.Null(row.Tob5TimeWeighted); Assert.Null(row.MicroDev5TimeWeighted);
        Assert.True(row.Depth5UnobservedSeconds > 0);
        Assert.True(VerifiedOptionsDepth5.None.IsUnverified(row));                                                                  // fail closed placeholder
    }

    [Fact]
    public async Task T13_StrikeV3RowsAndTheirAttestation_AreIdenticalWithOptionsDepthOnOrOff_AndAnOptionsDepthMismatchNeverTouchesThem()
    {
        var ticks = Ticks(400);
        var rows = UniverseRows(500);
        var off = Drive(Engine(false, false), ticks, null);
        var on = Drive(Engine(false, true), ticks, Feed(rows));
        var svc = Svc();

        async Task<(string Strikes, string Attestations)> Persist(List<AdaptiveCompletedBarPackage> packages)
        {
            await using var db = Observer();
            db.Sessions.Add(SessionRow());
            await db.SaveChangesAsync();
            foreach (var p in packages) Assert.True(await svc.PersistAndVerifyAsync(db, SessionRow(), p, default));
            var strikes = await db.OptionStrikeBars.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.Token).ToListAsync();
            var atts = await db.StrikeAttestations.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
            Assert.NotEmpty(strikes);
            return (string.Join("\n", strikes.Select(s => s.BarSeq + s.Token + StrikeAttestation.Digest(s))), string.Join("\n", atts.Select(a => a.BarSeq + ":" + a.TokenCount + ":" + a.Digests)));
        }

        Assert.Equal(await Persist(off), await Persist(on));

        // an options depth mismatch leaves the strike rows, their digests, and their attestation untouched, and the paper-verification result true
        await using var db2 = Observer();
        db2.Sessions.Add(SessionRow());
        await db2.SaveChangesAsync();
        foreach (var p in on.Take(4)) Assert.True(await svc.PersistAndVerifyAsync(db2, SessionRow(), p, default));
        var beforeStrikes = (await db2.OptionStrikeBars.AsNoTracking().ToListAsync()).Select(StrikeAttestation.Digest).OrderBy(x => x).ToList();
        var beforeAtt = (await db2.StrikeAttestations.AsNoTracking().ToListAsync()).Select(a => a.Digests).OrderBy(x => x).ToList();
        var tampered = on[1] with { OptionsDepth5 = on[1].OptionsDepth5!.Select(x => x with { Depth = x.Depth with { SnapshotEvents = x.Depth.SnapshotEvents + 1 } }).ToList() };
        Assert.True(await svc.PersistAndVerifyAsync(db2, SessionRow(), tampered, default));                                          // paper verification result unaffected
        Assert.Equal(beforeStrikes, (await db2.OptionStrikeBars.AsNoTracking().ToListAsync()).Select(StrikeAttestation.Digest).OrderBy(x => x).ToList());
        Assert.Equal(beforeAtt, (await db2.StrikeAttestations.AsNoTracking().ToListAsync()).Select(a => a.Digests).OrderBy(x => x).ToList());
        Assert.DoesNotContain(await db2.ProjectionHealth.AsNoTracking().ToListAsync(), h => h.Component == AdaptiveProjectionComponents.OptionStrikeBars);
        Assert.Contains(await db2.ProjectionHealth.AsNoTracking().ToListAsync(), h => h.Component == AdaptiveProjectionComponents.OptionsDepth5);
    }

    [Fact]
    public async Task OptionsDepthWriteFailure_IsQueued_AndNeverChangesThePaperVerificationResult()
    {
        var bars = SamplePackages();
        var svc = Svc();
        var broken = Observer();
        await broken.DisposeAsync();
        Assert.Equal(SupplementalOutcome.Failed, await svc.PersistOrVerifyOptionsDepth5Async(broken, 1, bars[0], default));
        Assert.Equal(1, svc.FailureCount);
        await using var db = Observer();
        db.Sessions.Add(SessionRow());
        await db.SaveChangesAsync();
        Assert.True(await svc.PersistAndVerifyAsync(db, SessionRow(), bars[0], default));
        Assert.Equal(0, svc.PendingOptionsDepth5RetryCount);
    }
}
