using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>S3 of the Top-5 design: depth normalizer, ordering buffer, freshness and the streaming reader. All synthetic data; nothing here touches a real database.</summary>
public sealed class Depth5ChannelTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 4, 0, 0, TimeSpan.Zero);          // 09:30 IST

    static decimal Px(int ticks) => ticks * 0.05m;

    static MarketDepth Depth(int bestBid, long[] bq, long[] aq, int gap = 2) => new(
        Px(bestBid), bq[0], Px(bestBid - 1), bq[1], Px(bestBid - 2), bq[2], Px(bestBid - 3), bq[3], Px(bestBid - 4), bq[4],
        Px(bestBid + gap), aq[0], Px(bestBid + gap + 1), aq[1], Px(bestBid + gap + 2), aq[2], Px(bestBid + gap + 3), aq[3], Px(bestBid + gap + 4), aq[4]);

    static readonly long[] Q = [10, 20, 30, 40, 50];

    static DepthRawRow Row(string token, long id, double sec, MarketDepth? depth, double? receivedSec = null) =>
        new(token, id, T0.AddSeconds(sec), T0.AddSeconds(receivedSec ?? sec), depth);

    // ------------------------------------------------------------------ decimal conversion

    [Fact]
    public void PriceConversion_IsScaleIndependent_AndEqualDecimalsGiveBitIdenticalDoubles()
    {
        decimal[] sameValue = [2000.05m, 2000.050m, 2000.0500m, 2000.05000000m, decimal.Parse("2000.0500"), new decimal(200005, 0, 0, false, 2), new decimal(2000050000, 0, 0, false, 6)];
        var first = BitConverter.DoubleToInt64Bits(Depth5Price.FromDecimal(sameValue[0]));
        foreach (var d in sameValue) Assert.Equal(first, BitConverter.DoubleToInt64Bits(Depth5Price.FromDecimal(d)));
        Assert.Equal(Depth5Price.FromDecimal(2000.05m), Depth5Price.FromDecimal(2000.0500m));
        Assert.Equal(2000.05d, Depth5Price.FromDecimal(2000.05m));                                // and equals the double the literal denotes
    }

    static decimal Scaled(long unscaled, byte scale) => new((int)(unscaled & 0xFFFFFFFFL), (int)(unscaled >> 32), 0, false, scale);

    [Fact]
    public void PriceConversion_Random4DpPrices_MatchTheCanonicalRoundTrip_AndEqualityIsExact()
    {
        var rng = new Random(5);
        for (var i = 0; i < 200_000; i++)
        {
            var units = rng.NextInt64(1, 9_999_999_9999L);                                           // decimal(18,4) stored value
            var stored = Scaled(units, 4);                                                           // as Npgsql returns it (scale 4)
            var other = Scaled(units * 10, 5);                                                       // same value, different scale
            var a = Depth5Price.FromDecimal(stored);
            Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(Depth5Price.FromDecimal(other)));
            Assert.Equal(double.Parse(stored.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture), a);
        }
    }

    [Fact]
    public void LiveAndReplayPaths_ShareTheConversion_SoTheSameStoredPriceCompareEqual()
    {
        // Both paths read the stored row through DepthRawRow -> Depth5Mapping.FromMarketDepth. Two independently built MarketDepth values with equal prices at different scales -> equal books.
        var live = Depth5Mapping.FromMarketDepth(Depth(2000, Q, Q));
        var replayDepth = Depth(2000, Q, Q) with { Bid1Price = 100.0000m, Ask1Price = 100.1000m };
        var replayDepth2 = Depth(2000, Q, Q) with { Bid1Price = 100m, Ask1Price = 100.10m };
        Assert.True(Depth5Mapping.FromMarketDepth(replayDepth).SameBook(Depth5Mapping.FromMarketDepth(replayDepth2)));
        Assert.True(live.SameBook(Depth5Mapping.FromMarketDepth(Depth(2000, Q, Q))));
    }

    // ------------------------------------------------------------------ complete-book reconstruction from partial deltas

    static RawFeedMessage Msg(Action<Dictionary<string, string>> set)
    {
        var d = new Dictionary<string, string>();
        set(d);
        string? G(string k) => d.TryGetValue(k, out var v) ? v : null;
        return new RawFeedMessage(G("t"), "NFO", "61471", G("lp"), G("v"), G("oi"), G("ft"),
            G("bp1"), G("bq1"), G("bp2"), G("bq2"), G("bp3"), G("bq3"), G("bp4"), G("bq4"), G("bp5"), G("bq5"),
            G("sp1"), G("sq1"), G("sp2"), G("sq2"), G("sp3"), G("sq3"), G("sp4"), G("sq4"), G("sp5"), G("sq5"));
    }

    [Fact]
    public void Reconstruction_FromTouchlineAndDepthPartialDeltas_BuildsTheCompleteBook_AndOnlyWhenAllTenPricesAreKnown()
    {
        var state = new FlatTradeFeedState(Exchange.Nfo, "61471");
        var at = T0;
        state.ApplyDelta(Msg(m => { m["lp"] = "2000.00"; m["bp1"] = "1999.95"; m["bq1"] = "10"; m["sp1"] = "2000.05"; m["sq1"] = "12"; }));        // touchline: L1 only
        Assert.Null(state.ToTick(at)!.Depth);                                                                                                   // not a complete book yet
        state.ApplyDelta(Msg(m => { m["bp2"] = "1999.90"; m["bq2"] = "20"; m["bp3"] = "1999.85"; m["bq3"] = "30"; m["sp2"] = "2000.10"; m["sq2"] = "22"; m["sp3"] = "2000.15"; m["sq3"] = "32"; }));
        Assert.Null(state.ToTick(at)!.Depth);                                                                                                   // levels 4-5 still unseen
        state.ApplyDelta(Msg(m => { m["bp4"] = "1999.80"; m["bq4"] = "40"; m["bp5"] = "1999.75"; m["bq5"] = "50"; m["sp4"] = "2000.20"; m["sq4"] = "42"; m["sp5"] = "2000.25"; m["sq5"] = "52"; }));
        var full = state.ToTick(at)!.Depth!;
        var book = Depth5Mapping.FromMarketDepth(full);
        Assert.True(book.IsValid);
        Assert.Equal([10, 20, 30, 40, 50], book.Bids.Select(l => l.Qty));
        Assert.Equal([12, 22, 32, 42, 52], book.Asks.Select(l => l.Qty));
        Assert.Equal(1999.95d, book.Bids[0].Price);
        Assert.Equal(2000.25d, book.Asks[4].Price);

        // A later depth-only delta changing ONE level-3 quantity keeps everything else (partial delta never zeroes the rest).
        state.ApplyDelta(Msg(m => { m["bq3"] = "35"; }));
        var next = Depth5Mapping.FromMarketDepth(state.ToTick(at)!.Depth!);
        Assert.Equal([10, 20, 35, 40, 50], next.Bids.Select(l => l.Qty));
        Assert.Equal(book.Asks.Select(l => l.Qty), next.Asks.Select(l => l.Qty));
        Assert.Equal(+5L, Depth5Metrics.Ofi5Contribution(book, next));
    }

    // ------------------------------------------------------------------ normalizer: depth-only preservation and dedup

    [Fact]
    public void DepthOnlyChange_IsPreserved_WhileTheLevelOneNormalizerStillDropsIt()
    {
        var a = Depth(2000, Q, Q);
        var b = Depth(2000, [10, 20, 31, 40, 50], Q);                                    // only level 3 bid quantity moved; Level 1 identical
        var depthNorm = new AdaptiveDepthNormalizer();
        var l1Norm = new AdaptiveIncrementalTickNormalizer();
        // A depth-only feed message advances the receive time but not the touchline: same exchange timestamp, last, volume, OI and Level 1 -> the L1 payload is identical.
        ObserverRawTick Raw(long id, double receivedSec, MarketDepth d) =>
            new(id, T0, T0.AddSeconds(receivedSec), 2000.0, (double)d.Bid1Price, (double)d.Ask1Price, d.Bid1Qty, d.Ask1Qty, 1000, 5000);

        Assert.NotNull(depthNorm.Process(Row("T", 1, 0, a)));                            // first complete book
        Assert.NotNull(l1Norm.Process("T", Raw(1, 0, a)));
        var obs = depthNorm.Process(new DepthRawRow("T", 2, T0, T0.AddSeconds(1), b));
        Assert.NotNull(obs);
        Assert.Equal(31, obs!.Book!.Bids[2].Qty);
        Assert.Null(l1Norm.Process("T", Raw(2, 1, b)));                                  // existing behaviour is unchanged: the L1 payload is the same, the row is dropped
    }

    [Fact]
    public void RepeatsAndNullDepth_AreNotSnapshots_AndALevelOneMoveIsAnExplicitSupersededMarkerNeverASnapshot()
    {
        var n = new AdaptiveDepthNormalizer();
        var a = Depth(2000, Q, Q);
        Assert.NotNull(n.Process(Row("T", 1, 0, a)));
        var marker = n.Process(Row("T", 2, 1, a with { Bid1Qty = 99 }));                 // L1-only change: held snapshot is superseded, NOT a new book
        Assert.Equal(Depth5EventKind.L1Superseded, marker!.Kind);
        Assert.Null(marker.Book);
        Assert.Null(n.Process(Row("T", 3, 2, a with { Bid1Qty = 99 })));                 // exact repeat
        Assert.Null(n.Process(Row("T", 4, 3, a with { Bid1Qty = 98 })));                 // further L1 moves while already unavailable: no new event
        // a row after an L1-only change still compares against the PREVIOUS ROW's deep levels
        Assert.Equal(Depth5EventKind.Snapshot, n.Process(Row("T", 5, 4, a with { Bid1Qty = 98, Ask3Qty = 7 }))!.Kind);
        var fresh = new AdaptiveDepthNormalizer();
        Assert.Null(fresh.Process(Row("T", 1, 0, null)));                                // first row with no depth says nothing
        Assert.Null(fresh.Process(Row("T", 2, 1, null)));
        Assert.NotNull(fresh.Process(Row("T", 3, 2, a)));                                // depth appears
        var gone = fresh.Process(Row("T", 4, 3, null));                                  // depth disappears -> an explicit "unavailable" observation
        Assert.NotNull(gone);
        Assert.Null(gone!.Book);
        Assert.Null(fresh.Process(Row("T", 5, 4, null)));
    }

    [Fact]
    public void Dedup_IsPerToken_AndRevertingToAnEarlierBookIsARealChange()
    {
        var n = new AdaptiveDepthNormalizer();
        var a = Depth(2000, Q, Q);
        var b = Depth(2000, [10, 20, 31, 40, 50], Q);
        Assert.NotNull(n.Process(Row("X", 1, 0, a)));
        Assert.NotNull(n.Process(Row("Y", 2, 0, a)));                                    // another token's identical book is its own first book
        Assert.Null(n.Process(Row("X", 3, 1, a)));
        Assert.NotNull(n.Process(Row("X", 4, 2, b)));
        Assert.NotNull(n.Process(Row("X", 5, 3, a)));                                    // A -> B -> A: both moves are transitions
        Assert.Null(n.Process(Row("Y", 6, 3, a)));
    }

    [Fact]
    public void AvailableAt_IsMaxOfReceivedAndExchange_LikeTheLevelOnePath()
    {
        var n = new AdaptiveDepthNormalizer();
        var o1 = n.Process(Row("T", 1, 10, Depth(2000, Q, Q), receivedSec: 5))!;         // exchange ahead of receive
        Assert.Equal(T0.AddSeconds(10), o1.AvailableAt);
        var o2 = n.Process(Row("T", 2, 10, Depth(2000, [10, 21, 30, 40, 50], Q), receivedSec: 12))!;
        Assert.Equal(T0.AddSeconds(12), o2.AvailableAt);
    }

    // ------------------------------------------------------------------ ordering and live-vs-batch equivalence

    static List<Depth5Observation> Stream(int seed, int rows, bool jitterIntoTheFuture)
    {
        var rng = new Random(seed);
        var n = new AdaptiveDepthNormalizer();
        var list = new List<Depth5Observation>();
        long id = 0;
        var tokens = new[] { "A", "B", "C" };
        var book = tokens.ToDictionary(t => t, t => Q.ToArray());
        for (var i = 0; i < rows; i++)
        {
            var t = tokens[rng.Next(tokens.Length)];
            id++;
            var received = i * 0.4;
            var exchange = jitterIntoTheFuture ? received + rng.NextDouble() * 1.5 - 0.5 : received - 0.2;        // up to +1.0 s ahead stays inside the 2 s stability lag
            if (rng.Next(3) != 0) book[t][rng.Next(5)] = rng.Next(1, 100);
            var depth = Depth(2000, book[t], Q);
            if (n.Process(Row(t, id, exchange, depth, received)) is { } o) list.Add(o);
        }

        return list;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void LiveChunkedRelease_EqualsTheBatchOrder_WhenNothingArrivesBeyondTheStabilityLag(int seed, bool jitter)
    {
        var observations = Stream(seed, 3000, jitter);

        var batch = new Depth5OrderingBuffer();
        foreach (var o in observations) batch.Add(o);
        var batchOrder = batch.ReleaseAll().Select(o => (o.AvailableAt, o.Id)).ToList();

        var live = new Depth5OrderingBuffer();
        var liveOrder = new List<(DateTimeOffset, long)>();
        var rng = new Random(seed + 100);
        var i = 0;
        // simulate polls: every poll appends the next few rows (all rows with Id <= cursor are known), `now` is the receive time of the last appended row.
        while (i < observations.Count)
        {
            var take = rng.Next(1, 25);
            for (var k = 0; k < take && i < observations.Count; k++, i++) live.Add(observations[i]);
            var now = observations[i - 1].AvailableAt;                                   // clock never behind the newest availability seen
            liveOrder.AddRange(live.Release(now).Select(o => (o.AvailableAt, o.Id)));
        }

        liveOrder.AddRange(live.ReleaseAll().Select(o => (o.AvailableAt, o.Id)));
        Assert.Equal(0, live.LateArrivals);
        Assert.Equal(batchOrder, liveOrder);
        Assert.Equal(batchOrder.OrderBy(x => x.AvailableAt).ThenBy(x => x.Id).ToList(), batchOrder);
    }

    [Fact]
    public void Buffer_HoldsBackWithinTheLag_AndCountsAnObservationOlderThanWhatWasAlreadyReleased()
    {
        var buffer = new Depth5OrderingBuffer();
        Depth5Observation Obs(long id, double sec) => new("T", id, T0.AddSeconds(sec), T0.AddSeconds(sec), null);
        buffer.Add(Obs(2, 10));
        buffer.Add(Obs(1, 10));                                                          // same instant: Id breaks the tie
        buffer.Add(Obs(3, 11.5));
        Assert.Empty(buffer.Release(T0.AddSeconds(11.9)));                               // 10 + 2 s lag > 11.9
        Assert.Equal([1L, 2L], buffer.Release(T0.AddSeconds(12)).Select(o => o.Id));
        Assert.Equal(1, buffer.Count);
        buffer.Add(Obs(4, 9));                                                           // older than the released watermark: late
        Assert.Equal(1, buffer.LateArrivals);
    }

    // ------------------------------------------------------------------ freshness

    [Fact]
    public void Freshness_FiveSecondLimit_IsInclusive_RepeatsDoNotRefreshIt_AndNothingBeforeTheObservationIsFresh()
    {
        var n = new AdaptiveDepthNormalizer();
        var a = Depth(2000, Q, Q);
        var observations = new List<Depth5Observation>();
        // the same stored snapshot is repeated every 1.6 s for a minute (what the 1.6 s sampled stream does when the depth is quiet)
        for (var i = 0; i < 40; i++)
            if (n.Process(Row("T", i + 1, i * 1.6, a)) is { } o) observations.Add(o);
        Assert.Single(observations);                                                     // one real observation, at t = 0

        Assert.NotNull(Depth5Freshness.BookAt(observations, T0.AddSeconds(5)));          // exactly 5 s: still fresh
        Assert.Null(Depth5Freshness.BookAt(observations, T0.AddSeconds(5).AddTicks(1))); // just beyond
        Assert.Null(Depth5Freshness.BookAt(observations, T0.AddSeconds(30)));            // the repeats at 8, 9.6, ... 62 s did NOT keep it alive
        Assert.Null(Depth5Freshness.BookAt(observations, T0.AddSeconds(-1)));            // nothing known yet
        Assert.NotNull(Depth5Freshness.BookAt(observations, T0));
    }

    [Fact]
    public void Freshness_ARealChangeRestartsTheClock_AndInvalidOrUnavailableBooksAreNeverFresh()
    {
        var n = new AdaptiveDepthNormalizer();
        var obs = new List<Depth5Observation>();
        foreach (var (id, sec, qty) in new[] { (1L, 0d, 20L), (2L, 4d, 21L), (3L, 8d, 22L) })
            if (n.Process(Row("T", id, sec, Depth(2000, [10, qty, 30, 40, 50], Q))) is { } o) obs.Add(o);
        Assert.Equal(3, obs.Count);
        Assert.NotNull(Depth5Freshness.BookAt(obs, T0.AddSeconds(12.9)));                // 4.9 s after the third observation
        Assert.Null(Depth5Freshness.BookAt(obs, T0.AddSeconds(13.1)));

        var crossed = new Depth5Observation("T", 9, T0, T0, Depth5Mapping.FromMarketDepth(Depth(2000, Q, Q, gap: -3)));
        Assert.False(crossed.Book!.IsValid);
        Assert.False(Depth5Freshness.IsFresh(crossed, T0));
        Assert.False(Depth5Freshness.IsFresh(new Depth5Observation("T", 10, T0, T0, null), T0));
    }

    // ------------------------------------------------------------------ Level-1-only updates against the depth channel

    static List<Depth5Observation> Events(params DepthRawRow[] rows)
    {
        var n = new AdaptiveDepthNormalizer();
        return rows.Select(n.Process).Where(o => o is not null).Select(o => o!).ToList();
    }

    [Fact]
    public void L1PriceMove_WithoutDeeperChange_NeverBecomesABook_EvenWhenTheMixedLadderIsNonMonotone()
    {
        var a = Depth(2000, Q, Q);
        var moved = a with { Bid1Price = Px(1998) };                                     // best bid falls below the stored level-2 bid (1999): old depth + new touchline
        Assert.Equal(Depth5Validity.NonMonotone, Depth5Mapping.FromMarketDepth(moved).Validity);   // documents the hazard: the stored row pairs incompatible observations
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, moved), Row("T", 3, 2, moved));
        Assert.Equal([Depth5EventKind.Snapshot, Depth5EventKind.L1Superseded], events.Select(e => e.Kind));
        Assert.Null(events[1].Book);
        Assert.NotNull(Depth5Freshness.BookAt(events, T0.AddSeconds(0.5)));
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(1)));                   // unavailable from the moment the touchline moved
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(2)));                   // and the repeat of the moved row does not bring it back

        // the existing Level-1 input stream is unaffected: its normalizer still passes the moved-touchline row through
        var l1 = new AdaptiveIncrementalTickNormalizer();
        ObserverRawTick Raw(long id, double sec, MarketDepth d) => new(id, T0.AddSeconds(sec), T0.AddSeconds(sec), 2000.0, (double)d.Bid1Price, (double)d.Ask1Price, d.Bid1Qty, d.Ask1Qty, 1000, 5000);
        Assert.NotNull(l1.Process("T", Raw(1, 0, a)));
        Assert.NotNull(l1.Process("T", Raw(2, 1, moved)));
    }

    [Fact]
    public void L1PriceMove_ThatLeavesAMonotoneMixedLadder_IsStillNotABook()
    {
        var a = Depth(2000, Q, Q);
        var moved = a with { Bid1Price = Px(2001), Ask1Price = Px(2002) };               // looks valid, but is old depth + new touchline: not verified
        Assert.True(Depth5Mapping.FromMarketDepth(moved).IsValid);
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, moved));
        Assert.Equal(Depth5EventKind.L1Superseded, events[1].Kind);
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(1.5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void L1QuantityMove_WithoutDeeperChange_SupersedesToo_OnEitherSide(bool bidSide)
    {
        var a = Depth(2000, Q, Q);
        var moved = bidSide ? a with { Bid1Qty = 11 } : a with { Ask1Qty = 11 };
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, moved));
        Assert.Equal(Depth5EventKind.L1Superseded, events[1].Kind);
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(1.1)));
    }

    [Fact]
    public void TouchlineBeforeDepth_BookIsUnavailableUntilTheDepthRowThenCoherentAgain()
    {
        var a = Depth(2000, Q, Q);
        var touchline = a with { Bid1Price = Px(2001), Bid1Qty = 15 };
        // the later depth message changes level 2-5 (here level-2 bid quantity); the stored row then carries the already-moved touchline plus the new depth
        var b = touchline with { Bid2Qty = 25 };
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, touchline), Row("T", 3, 2, b));
        Assert.Equal([Depth5EventKind.Snapshot, Depth5EventKind.L1Superseded, Depth5EventKind.Snapshot], events.Select(e => e.Kind));
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(1.5)));
        var book = Depth5Freshness.BookAt(events, T0.AddSeconds(2.5));
        Assert.NotNull(book);
        Assert.Equal(15, book!.Bids[0].Qty);                                             // the snapshot at the depth row is the stored row: the freshest touchline plus the fresh depth
        Assert.Equal(25, book.Bids[1].Qty);
    }

    [Fact]
    public void DepthBeforeTouchline_IsAvailableUntilTheTouchlineMoves()
    {
        var a = Depth(2000, Q, Q);
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 2.5, a with { Ask1Qty = 33 }));
        Assert.NotNull(Depth5Freshness.BookAt(events, T0.AddSeconds(2.4)));
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(2.5)));
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(4)));
    }

    [Fact]
    public void CrossedOrNonMonotoneSnapshots_AreUnavailableWithTheirReason_AndInterruptTheOfiBaseline()
    {
        var a = Depth(2000, Q, Q);
        var crossed = Depth(2000, [10, 20, 31, 40, 50], Q, gap: -3);                      // ask below bid
        var nonMono = Depth(2000, [10, 20, 32, 40, 50], Q) with { Ask2Price = Px(2002) };  // ask2 == ask1
        var b = Depth(2000, [10, 20, 33, 40, 50], Q);
        Assert.Equal(Depth5Validity.Crossed, Depth5Mapping.FromMarketDepth(crossed).Validity);
        Assert.Equal(Depth5Validity.NonMonotone, Depth5Mapping.FromMarketDepth(nonMono).Validity);

        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, crossed), Row("T", 3, 2, nonMono), Row("T", 4, 3, b));
        Assert.Equal(4, events.Count);
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(1.5)));
        Assert.Null(Depth5Freshness.BookAt(events, T0.AddSeconds(2.5)));
        Assert.NotNull(Depth5Freshness.BookAt(events, T0.AddSeconds(3.5)));
        var seg = Depth5Coverage.Segments(events, T0, T0.AddSeconds(4));
        Assert.Equal(Depth5GapReason.InvalidBook, seg[1].Reason);
        Assert.Equal(Depth5Validity.Crossed, seg[1].Validity);
        Assert.Equal(Depth5Validity.NonMonotone, seg[2].Validity);
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Flow.Ofi5(events));            // A, invalid, invalid, B: nothing is bridged across the invalid books
    }

    [Fact]
    public void CoverageGap_WhileWaitingForACoherentSnapshot_IsRecordedWithTheRightReasons()
    {
        var a = Depth(2000, Q, Q);
        var b = Depth(2000, [10, 20, 31, 40, 50], Q);
        var events = Events(Row("T", 1, 0, a), Row("T", 2, 1, a with { Bid1Qty = 12 }), Row("T", 3, 9, b));
        var end = T0.AddSeconds(20);
        var seg = Depth5Coverage.Segments(events, T0.AddSeconds(-2), end);
        Assert.Equal(
            [
                (-2d, 0d, false, Depth5GapReason.NotYetObserved),
                (0d, 1d, true, Depth5GapReason.None),
                (1d, 9d, false, Depth5GapReason.L1Superseded),        // the gap is attributed to the touchline move, not to expiry, even though 5 s elapsed inside it
                (9d, 14d, true, Depth5GapReason.None),
                (14d, 20d, false, Depth5GapReason.Stale),
            ],
            seg.Select(s => ((s.From - T0).TotalSeconds, (s.To - T0).TotalSeconds, s.Available, s.Reason)));
        Assert.Equal(end, seg[^1].To);
        // contiguous, non-overlapping cover of the whole requested range
        for (var i = 1; i < seg.Count; i++) Assert.Equal(seg[i - 1].To, seg[i].From);
        Assert.Equal(T0.AddSeconds(-2), seg[0].From);
    }

    [Fact]
    public void CoverageGap_WithNothingNewer_IsStale_NotSuperseded_AndNoDepthIsItsOwnReason()
    {
        var a = Depth(2000, Q, Q);
        var stale = Depth5Coverage.Segments(Events(Row("T", 1, 0, a)), T0, T0.AddSeconds(30));
        Assert.Equal(2, stale.Count);
        Assert.True(stale[0].Available);
        Assert.Equal(T0.AddSeconds(5), stale[0].To);
        Assert.Equal(Depth5GapReason.Stale, stale[1].Reason);

        var gone = Depth5Coverage.Segments(Events(Row("T", 1, 0, a), Row("T", 2, 1, null)), T0, T0.AddSeconds(10));
        Assert.Equal(Depth5GapReason.NoDepth, gone[^1].Reason);
        Assert.Empty(Depth5Coverage.Segments([], T0, T0).ToList());
        Assert.Equal(Depth5GapReason.NotYetObserved, Depth5Coverage.Segments([], T0, T0.AddSeconds(3)).Single().Reason);
    }

    [Fact]
    public void L1MoveWhileNoValidSnapshotIsHeld_ProducesNoMarker()
    {
        var crossed = Depth(2000, Q, Q, gap: -3);
        var events = Events(Row("T", 1, 0, crossed), Row("T", 2, 1, crossed with { Bid1Qty = 99 }));
        Assert.Single(events);                                                           // already unavailable: a marker would add nothing
    }

    [Fact]
    public void Ofi5_BridgesAcrossASupersededMarkerByDefault_AndCanBeToldNotTo_NoDepthAlwaysBreaks()
    {
        var a = Depth(2000, Q, Q);
        var b = Depth(2000, [10, 20, 31, 40, 50], Q);
        var withMarker = Events(Row("T", 1, 0, a), Row("T", 2, 1, a with { Bid1Qty = 12 }), Row("T", 3, 2, b));
        Assert.Equal(3, withMarker.Count);
        Assert.Equal(1, Depth5Flow.Ofi5(withMarker).Transitions);
        Assert.Equal(+1L, Depth5Flow.Ofi5(withMarker).Net);
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Flow.Ofi5(withMarker, breakOnL1Superseded: true));
        var withGap = Events(Row("T", 1, 0, a), Row("T", 2, 1, null), Row("T", 3, 2, b));
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Flow.Ofi5(withGap));
    }

    // ------------------------------------------------------------------ OFI5 over the channel

    [Fact]
    public void Ofi5OverObservations_IgnoresRepeats_AndEqualsTheDirectFoldOverTheDistinctBooks()
    {
        var n = new AdaptiveDepthNormalizer();
        var books = new[] { Depth(2000, Q, Q), Depth(2000, [10, 20, 31, 40, 50], Q), Depth(2000, [10, 20, 31, 40, 50], [10, 20, 30, 40, 44]) };
        var obs = new List<Depth5Observation>();
        long id = 0;
        foreach (var b in books)
            for (var rep = 0; rep < 4; rep++)
                if (n.Process(Row("T", ++id, id * 1.6, b)) is { } o) obs.Add(o);
        Assert.Equal(3, obs.Count);
        var viaChannel = Depth5Metrics.Ofi5(obs.Select(o => o.Book));
        var direct = Depth5Metrics.Ofi5(books.Select(Depth5Mapping.FromMarketDepth));
        Assert.Equal(direct, viaChannel);
        Assert.Equal(2, viaChannel.Transitions);
    }

    // ------------------------------------------------------------------ streaming reader

    static NiftySignalDbContext Db() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static Tick TickRow(string token, double sec, MarketDepth? depth) => new()
    {
        Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = T0.AddSeconds(sec), ReceivedAt = T0.AddSeconds(sec), LastPrice = 2000m, Volume = 1, Depth = depth,
    };

    [Fact]
    public async Task Reader_StreamsInIdOrder_WithTheSameTokenAndTimePredicatesAsTheExistingRawReader()
    {
        await using var db = Db();
        var day = DateOnly.FromDateTime(T0.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        db.Ticks.AddRange(
            TickRow("A", 0, Depth(2000, Q, Q)),
            TickRow("B", 1, Depth(2000, Q, Q)),                                          // another token: excluded
            TickRow("A", 2, null),
            TickRow("A", 3, Depth(2000, [10, 20, 31, 40, 50], Q)),
            TickRow("A", 100000, Depth(2000, Q, Q)));                                    // beyond throughUtc
        await db.SaveChangesAsync();
        var through = T0.AddSeconds(60);

        var streamed = new List<DepthRawRow>();
        await foreach (var r in new AdaptiveDepthTickReader().StreamSessionAsync(db, day, ["A"], through, default)) streamed.Add(r);
        var legacy = await new AdaptiveSourceTickReader().ReadRawSessionAsync(db, day, ["A"], through, default);

        Assert.Equal(legacy.Select(x => x.Tick.Id), streamed.Select(x => x.Id));
        Assert.Equal(streamed.Select(x => x.Id).Order(), streamed.Select(x => x.Id));
        Assert.Equal(3, streamed.Count);
        Assert.Null(streamed[1].Depth);
        Assert.Equal(31, streamed[2].Depth!.Bid3Qty);
        // end to end: the streamed rows through the normalizer give first book + the level-3 change; the null-depth row yields an unavailable observation, then depth re-appears
        var n = new AdaptiveDepthNormalizer();
        var observations = streamed.Select(n.Process).Where(o => o is not null).ToList();
        Assert.Equal(3, observations.Count);
        Assert.Null(observations[1]!.Book);
    }
}
