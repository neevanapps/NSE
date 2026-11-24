using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// S2 of the Top-5 microstructure design: pure OFI5 / MicroDev5Proxy / notional DepthN5 calculations. Snapshots only; no feed protocol, reader, tracker or database is involved.
/// Prices are built as <c>ticks * 0.05</c> so equal ticks give bit-identical doubles (the production reader will have to build prices deterministically the same way).
/// </summary>
public sealed class Depth5MetricsTests
{
    const double Tick = 0.05;

    static double P(int ticks) => ticks * Tick;

    /// <summary>Book from (tick, qty) pairs, best first; padded to five slots with (0, 0).</summary>
    static Depth5Book Book((int Tick, long Qty)[] bids, (int Tick, long Qty)[] asks) => new(Pad(bids), Pad(asks));

    static Depth5Level[] Pad((int Tick, long Qty)[] side) =>
        side.Select(l => new Depth5Level(P(l.Tick), l.Qty)).Concat(Enumerable.Repeat(new Depth5Level(0d, 0), 5 - side.Length)).ToArray();

    // Reference book used by several tests: bids 2000..1996 ticks, asks 2002..2006 ticks.
    static Depth5Book Std(long[] bq, long[] aq, int bestBid = 2000, int gap = 2) =>
        Book(bq.Select((q, i) => (bestBid - i, q)).ToArray(), aq.Select((q, i) => (bestBid + gap + i, q)).ToArray());

    // ------------------------------------------------------------------ validity

    [Fact]
    public void Validity_AcceptsFullAndPaddedBooks_AndALockedBook()
    {
        Assert.Equal(Depth5Validity.Valid, Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]).Validity);
        Assert.Equal(Depth5Validity.Valid, Book([(2000, 5)], [(2002, 7)]).Validity);                       // padded tail on both sides
        Assert.Equal(Depth5Validity.Valid, Book([(2000, 5), (1999, 3)], [(2000, 7)]).Validity);            // locked (ask == bid) is allowed, like Ask >= Bid at Level 1
        var tailWithPriceButNoQty = new Depth5Book(
            Pad([(2000, 5)]).Take(1).Concat([new Depth5Level(P(1999), 0), new(0, 0), new(0, 0), new(0, 0)]),
            Pad([(2002, 5)]));
        Assert.Equal(Depth5Validity.Valid, tailWithPriceButNoQty.Validity);                                // price>0 with qty 0 is an empty level, fine in the tail
    }

    [Fact]
    public void Validity_RejectsWrongCountBadNumbersEmptyTopCrossedHolesAndNonMonotone()
    {
        Assert.Equal(Depth5Validity.WrongLevelCount, new Depth5Book(Pad([(2000, 5)]).Take(4), Pad([(2002, 5)])).Validity);
        Assert.Equal(Depth5Validity.InvalidPrice, new Depth5Book(Pad([(2000, 5)]).Select((l, i) => i == 2 ? l with { Price = double.NaN } : l), Pad([(2002, 5)])).Validity);
        Assert.Equal(Depth5Validity.InvalidPrice, new Depth5Book(Pad([(2000, 5)]), Pad([(2002, 5)]).Select((l, i) => i == 0 ? l with { Price = -1d } : l)).Validity);
        Assert.Equal(Depth5Validity.NegativeQuantity, Book([(2000, -1)], [(2002, 5)]).Validity);
        Assert.Equal(Depth5Validity.EmptyTopOfBook, Book([(2000, 0)], [(2002, 5)]).Validity);              // zero best-bid quantity: no best bid
        Assert.Equal(Depth5Validity.EmptyTopOfBook, Book([], [(2002, 5)]).Validity);                       // empty side
        Assert.Equal(Depth5Validity.Crossed, Book([(2003, 5)], [(2002, 5)]).Validity);
        Assert.Equal(Depth5Validity.NonContiguous, Book([(2000, 5), (0, 0), (1998, 4)], [(2002, 5)]).Validity);
        Assert.Equal(Depth5Validity.NonMonotone, Book([(2000, 5), (2000, 4)], [(2002, 5)]).Validity);      // equal bid prices
        Assert.Equal(Depth5Validity.NonMonotone, Book([(2000, 5), (2001, 4)], [(2002, 5)]).Validity);      // bid above the better bid
        Assert.Equal(Depth5Validity.NonMonotone, Book([(2000, 5)], [(2002, 5), (2002, 3)]).Validity);
        Assert.Equal(Depth5Validity.NonMonotone, Book([(2000, 5)], [(2002, 5), (2001, 3)]).Validity);
    }

    [Fact]
    public void InvalidOrMissingBooks_GiveNullEverywhere_NeverZero()
    {
        var crossed = Book([(2003, 5)], [(2002, 5)]);
        var ok = Std([10, 10, 10, 10, 10], [10, 10, 10, 10, 10]);
        Assert.Null(Depth5Metrics.MicroDev5Proxy(crossed));
        Assert.Null(Depth5Metrics.MicroDev5Proxy(null));
        Assert.Null(Depth5Metrics.NotionalImbalance(crossed));
        Assert.Null(Depth5Metrics.NotionalImbalance(null));
        Assert.Null(Depth5Metrics.TotalBidQuantity(crossed));
        Assert.Null(Depth5Metrics.Ofi5Contribution(crossed, ok));
        Assert.Null(Depth5Metrics.Ofi5Contribution(ok, crossed));
        Assert.Null(Depth5Metrics.Ofi5Contribution(null, ok));
        Assert.Null(Depth5Metrics.Ofi5Contribution(ok, ok, levels: 0));
        Assert.Null(Depth5Metrics.Ofi5Contribution(ok, ok, levels: 6));
    }

    // ------------------------------------------------------------------ MicroDev5Proxy

    [Fact]
    public void MicroDev5_BalancedBook_IsExactlyZero()
    {
        Assert.Equal(0d, Depth5Metrics.MicroDev5Proxy(Std([10, 20, 30, 40, 50], [50, 40, 30, 20, 10]))!.Value);   // Qb == Qa = 150 although the shapes differ
    }

    [Fact]
    public void MicroDev5_FormulaIdentityAndSign()
    {
        var bidHeavy = Std([100, 20, 30, 40, 50], [10, 20, 30, 40, 50]);       // Qb 240, Qa 150
        var askHeavy = Std([10, 20, 30, 40, 50], [100, 20, 30, 40, 50]);       // mirror
        var halfSpread = (P(2002) - P(2000)) / 2d;
        Assert.Equal(halfSpread * (240d - 150d) / (240d + 150d), Depth5Metrics.MicroDev5Proxy(bidHeavy)!.Value, 15);
        Assert.Equal(halfSpread * (150d - 240d) / (240d + 150d), Depth5Metrics.MicroDev5Proxy(askHeavy)!.Value, 15);
        Assert.True(Depth5Metrics.MicroDev5Proxy(bidHeavy) > 0d);               // raw signed: bid-heavy positive
        Assert.True(Depth5Metrics.MicroDev5Proxy(askHeavy) < 0d);
        Assert.Equal(-Depth5Metrics.MicroDev5Proxy(bidHeavy)!.Value, Depth5Metrics.MicroDev5Proxy(askHeavy)!.Value, 15);
    }

    [Fact]
    public void MicroDev5_UsesTouchSpreadNotDepthWidth_AndAggregateQuantityNotRankWeights()
    {
        // Same Qb/Qa and same touch; deep levels spread far apart on the ask side. Formula A does not care about the deep prices.
        var tight = Book([(2000, 100), (1999, 20), (1998, 20), (1997, 20), (1996, 20)], [(2002, 50), (2003, 50), (2004, 50), (2005, 50), (2006, 50)]);
        var wideDeep = Book([(2000, 100), (1999, 20), (1998, 20), (1997, 20), (1996, 20)], [(2002, 50), (2010, 50), (2020, 50), (2030, 50), (2040, 50)]);
        Assert.Equal(Depth5Metrics.MicroDev5Proxy(tight)!.Value, Depth5Metrics.MicroDev5Proxy(wideDeep)!.Value);
        // A book whose L1 says bid-heavy but whose aggregate says ask-heavy takes the AGGREGATE sign (no rank decay).
        var disagree = Book([(2000, 1000), (1999, 20), (1998, 20), (1997, 20), (1996, 20)], [(2002, 100), (2003, 400), (2004, 400), (2005, 400), (2006, 400)]);
        Assert.True(Depth5Metrics.MicroDev5Proxy(disagree) < 0d);
    }

    [Fact]
    public void MicroDev5_AtOneLevelEqualsTheExistingLevelOneMicroDev()
    {
        var rng = new Random(11);
        for (var i = 0; i < 2000; i++)
        {
            var bid = rng.Next(1000, 3000); var ask = bid + rng.Next(0, 8);
            long bq = rng.Next(1, 500), aq = rng.Next(1, 500);
            var five = Book([(bid, bq)], [(ask, aq)]);
            var l1 = new FuturesBookState(DateTimeOffset.UnixEpoch, 1, P(bid), P(ask), bq, aq);
            // The two formulas are algebraically identical but evaluated differently (the L1 one subtracts two prices of ~100 after a weighted average): agreement to ~1e-10 absolute.
            Assert.Equal(l1.MicroDev!.Value, Depth5Metrics.MicroDev5Proxy(five)!.Value, 10);
        }
    }

    [Fact]
    public void MicroDev5_QuantityChangeWithoutPriceChange_MovesTheValue_PriceChangeWithoutQuantityChange_OnlyTheSpreadFactor()
    {
        var a = Std([10, 10, 10, 10, 10], [10, 10, 10, 10, 10]);
        var b = Std([10, 10, 10, 10, 30], [10, 10, 10, 10, 10]);               // only the 5th bid quantity grows
        Assert.True(Depth5Metrics.MicroDev5Proxy(b) > Depth5Metrics.MicroDev5Proxy(a));
        var wider = Std([30, 10, 10, 10, 10], [10, 10, 10, 10, 10], gap: 4);
        var narrower = Std([30, 10, 10, 10, 10], [10, 10, 10, 10, 10], gap: 2);
        Assert.Equal(Depth5Metrics.MicroDev5Proxy(narrower)!.Value * 2d, Depth5Metrics.MicroDev5Proxy(wider)!.Value, 12);   // same imbalance, double the half-spread (doubles: 1e-12 absolute)
    }

    // ------------------------------------------------------------------ notional DepthN5

    [Fact]
    public void DepthN5_BalancedBidHeavyAskHeavy_AndBounds()
    {
        // Equal notionals => 0. Use prices so that price * qty balances: bid 100.00 x 50 vs ask 100.00 x 50 at a locked book.
        var balanced = new Depth5Book(Pad([(2000, 50)]), Pad([(2000, 50)]));
        Assert.Equal(0d, Depth5Metrics.NotionalImbalance(balanced)!.Value);
        Assert.True(Depth5Metrics.NotionalImbalance(Std([100, 100, 100, 100, 100], [1, 1, 1, 1, 1])) > 0.9d);
        Assert.True(Depth5Metrics.NotionalImbalance(Std([1, 1, 1, 1, 1], [100, 100, 100, 100, 100])) < -0.9d);
        // one side thin but valid: bounded strictly inside (-1, 1) because both best levels are non-empty
        var v = Depth5Metrics.NotionalImbalance(Book([(2000, 1000)], [(2002, 1)]))!.Value;
        Assert.InRange(v, -1d, 1d);
    }

    [Fact]
    public void DepthN5_NormalizationMatchesDecimalReference_AndIsPriceWeighted()
    {
        var book = Book([(2000, 10), (1990, 20), (1980, 30), (1970, 40), (1960, 50)], [(2002, 11), (2010, 22), (2020, 33), (2030, 44), (2040, 55)]);
        decimal bidN = 0m, askN = 0m;
        for (var i = 0; i < 5; i++)
        {
            bidN += (decimal)book.Bids[i].Price * book.Bids[i].Qty;
            askN += (decimal)book.Asks[i].Price * book.Asks[i].Qty;
        }

        var expected = (double)((bidN - askN) / (bidN + askN));
        Assert.Equal(expected, Depth5Metrics.NotionalImbalance(book)!.Value, 12);
        Assert.Equal((double)bidN, Depth5Metrics.BidNotional(book)!.Value, 8);
        // identical quantities, different prices => different notionals => a price-weighted (not quantity-only) imbalance
        var qtyOnly = Std([10, 10, 10, 10, 10], [10, 10, 10, 10, 10]);
        var farAsks = Book([(2000, 10), (1999, 10), (1998, 10), (1997, 10), (1996, 10)], [(2002, 10), (2100, 10), (2200, 10), (2300, 10), (2400, 10)]);
        Assert.True(Depth5Metrics.NotionalImbalance(farAsks) < Depth5Metrics.NotionalImbalance(qtyOnly));
    }

    [Fact]
    public void DepthN5_UnavailableInputs_AreNull_NotZero()
    {
        Assert.Null(Depth5Metrics.NotionalImbalance(Book([(2000, 0)], [(2002, 0)])));                      // empty top of book => invalid
        Assert.Null(Depth5Metrics.NotionalImbalance(null));
    }

    [Fact]
    public void DepthN5_RandomBooks_AreAlwaysWithinMinusOneAndOne_AndMirrorSymmetric()
    {
        var rng = new Random(23);
        for (var i = 0; i < 20_000; i++)
        {
            var book = RandomBook(rng);
            var v = Depth5Metrics.NotionalImbalance(book)!.Value;
            Assert.InRange(v, -1d, 1d);
            Assert.Equal(Depth5Metrics.NotionalImbalance(book), Depth5Metrics.NotionalImbalance(book));      // deterministic
        }
    }

    // ------------------------------------------------------------------ OFI5 hand-computed cases

    [Fact]
    public void Ofi5_UnchangedAndIdenticalSnapshots_ContributeExactlyZero()
    {
        // NOTE: in this test the three objects are three separately built books with IDENTICAL values (A, A, A), so there is no transition.
        // The A -> B -> A case with genuinely different books is Ofi5_ABA_* below.
        var a1 = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var a2 = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var a3 = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        Assert.Equal(0L, Depth5Metrics.Ofi5Contribution(a1, a2));
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a1, a2, a3]));                      // identical complete books are repeats, not transitions
    }

    [Fact]
    public void Ofi5_ABA_WithGenuinelyDifferentValidBooks_IsTwoTransitions_WhoseSignedContributionsCancel()
    {
        var a = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var b = Std([17, 20, 30, 40, 50], [11, 21, 31, 41, 51]);                                            // a real change: +7 queued at the best bid
        var e = Depth5Metrics.Ofi5Contribution(a, b)!.Value;
        Assert.Equal(+7L, e);
        Assert.Equal(-e, Depth5Metrics.Ofi5Contribution(b, a));                                             // the contribution is exactly antisymmetric
        var totals = Depth5Metrics.Ofi5([a, b, a]);
        Assert.Equal(new Ofi5Totals(0, 14, 2), totals);                                                     // net cancels to 0; gross (14) and the transition count (2) are still reported
        // A, A, B, B, A: repeats collapse, the two real changes remain
        Assert.Equal(new Ofi5Totals(0, 14, 2), Depth5Metrics.Ofi5([a, a, b, b, a]));
        // different-looking books that are value-identical are NOT A -> B: same ladder built twice
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]), a]));
    }

    [Fact]
    public void Ofi5_InvalidSnapshotsInterruptTheBaseline_SoNothingBridgesAcrossThem()
    {
        var a = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var b = Std([17, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var crossed = Book([(2003, 5)], [(2002, 5)]);
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, crossed, b]));                   // a ... b is not differenced across the invalid book
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, null, b]));
        Assert.Equal(new Ofi5Totals(7, 7, 1), Depth5Metrics.Ofi5([a, crossed, a, b]));                      // baseline restarts at the first valid book after the gap
        Assert.Equal(new Ofi5Totals(7, 7, 1), Depth5Metrics.Ofi5([a, b, crossed, b, b]));                   // the repeat after the gap is only a new baseline, not a transition
    }

    [Fact]
    public void Ofi5_IdenticalRepeatsAreSkipped_ButARealZeroNetChangeStillCounts()
    {
        var a = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var aRepeat = Std([10, 20, 30, 40, 50], [11, 21, 31, 41, 51]);
        var b = Std([17, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        // a, a, a, b, b: exactly one transition (a -> b); the repeats around it add nothing.
        Assert.Equal(new Ofi5Totals(Depth5Metrics.Ofi5Contribution(a, b), Math.Abs(Depth5Metrics.Ofi5Contribution(a, b)!.Value), 1), Depth5Metrics.Ofi5([a, aRepeat, a, b, b]));
        // a real change whose net is 0 (+5 on a bid, +5 on an ask) is a transition with value 0.
        var zeroNet = Std([15, 20, 30, 40, 50], [16, 21, 31, 41, 51]);
        Assert.Equal(new Ofi5Totals(0, 0, 1), Depth5Metrics.Ofi5([a, zeroNet]));
        // A repeat does not hide a gap: invalid in between re-baselines, so a, invalid, a has no transition either way.
        var crossed = Book([(2003, 5)], [(2002, 5)]);
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, crossed, a]));
        Assert.True(a.SameBook(aRepeat));
        Assert.False(a.SameBook(b));
        Assert.False(a.SameBook(null));
    }

    // ------------------------------------------------------------------ Level-1 validity contract vs Depth5

    [Fact]
    public void ZeroQuantityBestLevel_IsValidForTheExistingLevelOneContract_ButEmptyTopOfBookForDepth5()
    {
        var l1 = new FuturesBookState(DateTimeOffset.UnixEpoch, 1, P(2000), P(2002), 0, 10);      // existing contract: quantities need only be >= 0
        Assert.True(l1.IsValid);
        Assert.True(l1.HasQuantity);                                                               // valid, and still has a total-quantity denominator
        var depth = Book([(2000, 0)], [(2002, 10)]);
        Assert.False(depth.IsValid);
        Assert.Equal(Depth5Validity.EmptyTopOfBook, depth.Validity);
        Assert.Null(Depth5Metrics.MicroDev5Proxy(depth));
        Assert.Null(Depth5Metrics.Ofi5Contribution(depth, Std([10, 10, 10, 10, 10], [10, 10, 10, 10, 10])));

        var l1BothZero = new FuturesBookState(DateTimeOffset.UnixEpoch, 1, P(2000), P(2002), 0, 0);
        Assert.True(l1BothZero.IsValid);
        Assert.Equal(Depth5Validity.EmptyTopOfBook, Book([(2000, 0)], [(2002, 0)]).Validity);
    }

    [Fact]
    public void Ofi5_QueueAdditionsAndRemovals_AtAnyLevel_HaveTheRightSign()
    {
        var baseBook = Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        Assert.Equal(+7L, Depth5Metrics.Ofi5Contribution(baseBook, Std([17, 20, 30, 40, 50], [10, 20, 30, 40, 50])));    // bid add at best
        Assert.Equal(-4L, Depth5Metrics.Ofi5Contribution(baseBook, Std([10, 20, 30, 40, 46], [10, 20, 30, 40, 50])));    // bid removal at level 5
        Assert.Equal(-9L, Depth5Metrics.Ofi5Contribution(baseBook, Std([10, 20, 30, 40, 50], [10, 29, 30, 40, 50])));    // ask add at level 2
        Assert.Equal(+3L, Depth5Metrics.Ofi5Contribution(baseBook, Std([10, 20, 30, 40, 50], [10, 20, 27, 40, 50])));    // ask removal at level 3
        Assert.Equal(+7L - 9L, Depth5Metrics.Ofi5Contribution(baseBook, Std([17, 20, 30, 40, 50], [10, 29, 30, 40, 50])));// both sides in one transition
        Assert.True(Depth5Metrics.Ofi5Contribution(baseBook, Std([17, 20, 30, 40, 50], [10, 20, 30, 40, 50])) > 0);
        Assert.True(Depth5Metrics.Ofi5Contribution(baseBook, Std([10, 20, 30, 40, 46], [10, 20, 30, 40, 50])) < 0);
    }

    [Fact]
    public void Ofi5_BestBidRises_ShiftsRanksButOnlyTheNewLevelQuantityIsFlow()
    {
        // previous bids 2000:10 1999:20 1998:30 1997:40 1996:50 ; next bids 2001:5 2000:10 1999:20 1998:30 1997:40 (the old levels slid down one rank, untouched)
        var previous = Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        var next = Book([(2001, 5), (2000, 10), (1999, 20), (1998, 30), (1997, 40)], [(2003, 10), (2004, 20), (2005, 30), (2006, 40), (2007, 50)]);
        // bid floor = max(1996, 1997) = 1997: next 105 - previous 100 = +5.  Asks are the same quantities, one tick higher: ceiling = min(2006, 2007) = 2006 => next(2003..2006) 100 - previous 150... see below.
        var bidSide = 5L;
        var askSide = (10 + 20 + 30 + 40) - (10 + 20 + 30 + 40 + 50);                                      // window <= 2006: next 100, previous 150
        Assert.Equal(bidSide - askSide, Depth5Metrics.Ofi5Contribution(previous, next));
        // A rank-based (level i vs level i) multi-level OFI would book +10+20+30+40 of already-resting quantity here; the price-aligned one must not.
        var bidOnly = Depth5Metrics.Ofi5Contribution(previous, Book([(2001, 5), (2000, 10), (1999, 20), (1998, 30), (1997, 40)], [(2002, 10), (2003, 20), (2004, 30), (2005, 40), (2006, 50)]));
        Assert.Equal(5L, bidOnly);
    }

    [Fact]
    public void Ofi5_DepthWindowEntryAndExit_IsLimitedToThePricesObservableInBothSnapshots()
    {
        var previous = Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        // best bid falls: bids 1999:20 1998:30 1997:40 1996:50 1995:60. Floor = max(1996, 1995) = 1996. The 2000 level (10) left the book; the 1995 level entered below the window and is ignored.
        var next = Book([(1999, 20), (1998, 30), (1997, 40), (1996, 50), (1995, 60)], [(2002, 10), (2003, 20), (2004, 30), (2005, 40), (2006, 50)]);
        var expectedBid = (20 + 30 + 40 + 50) - (10 + 20 + 30 + 40 + 50);
        Assert.Equal((long)expectedBid, Depth5Metrics.Ofi5Contribution(previous, next));                    // -10: the removed best level only
    }

    [Fact]
    public void Ofi5_ThinBook_ExhaustedSideMeansEverythingBelowIsKnownEmpty()
    {
        // previous: 2 bid levels (book exhausted below). next: a third level appears => it is real new quantity (floor 0 because the previous book showed every level).
        var previous = Book([(2000, 10), (1999, 20)], [(2002, 10)]);
        var next = Book([(2000, 10), (1999, 20), (1998, 35)], [(2002, 10)]);
        Assert.Equal(35L, Depth5Metrics.Ofi5Contribution(previous, next));
        // Level 5 of a full book falling out of the window while the other snapshot is thin: window is bounded by the full book.
        var full = Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        var thin = Book([(2000, 10), (1999, 20)], [(2002, 10)]);
        // bid floor = max(1996, 0) = 1996: thin sum(2000,1999)=30 - full sum(2000..1996)=150 => -120 ; ask ceiling = min(2006, +inf) = 2006: thin 10 - full 150 => -140 -> ask side contributes +140
        Assert.Equal(-120L + 140L, Depth5Metrics.Ofi5Contribution(full, thin));
    }

    [Fact]
    public void Ofi5_PaddedZeroQuantityPriceLevels_AreTreatedAsEmpty()
    {
        var padded = new Depth5Book(
            [new(P(2000), 10), new(P(1999), 0), new(0, 0), new(0, 0), new(0, 0)],      // price>0 with qty 0 is an empty level
            Pad([(2002, 10)]));
        var plain = Book([(2000, 10)], [(2002, 10)]);
        Assert.Equal(0L, Depth5Metrics.Ofi5Contribution(padded, plain));
        Assert.Equal(0L, Depth5Metrics.Ofi5Contribution(plain, padded));
    }

    [Fact]
    public void Ofi5_Sequence_NetGrossTransitions_AndRebaselineAcrossGaps()
    {
        var a = Std([10, 20, 30, 40, 50], [10, 20, 30, 40, 50]);
        var up = Std([17, 20, 30, 40, 50], [10, 20, 30, 40, 50]);          // +7 vs a
        var down = Std([17, 20, 30, 40, 50], [16, 20, 30, 40, 50]);         // ask add 6 => -6 vs up
        var crossed = Book([(2003, 5)], [(2002, 5)]);
        Assert.Equal(new Ofi5Totals(7 - 6, 7 + 6, 2), Depth5Metrics.Ofi5([a, up, down]));
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a]));                               // a baseline alone is not evidence
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([]));
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, crossed, up]));                  // never bridge an invalid book
        Assert.Equal(new Ofi5Totals(null, null, 0), Depth5Metrics.Ofi5([a, null, up]));
        Assert.Equal(new Ofi5Totals(-6, 6, 1), Depth5Metrics.Ofi5([a, crossed, up, down]));
    }

    [Fact]
    public void Ofi5_IsAdditiveOverSplitSequences_WhenTheBoundaryTransitionIsAssignedToTheLaterPart()
    {
        var rng = new Random(5);
        var seq = Enumerable.Range(0, 60).Select(_ => (Depth5Book?)RandomBook(rng)).ToArray();
        var whole = Depth5Metrics.Ofi5(seq);
        for (var cut = 1; cut < seq.Length - 1; cut += 7)
        {
            var first = Depth5Metrics.Ofi5(seq[..cut]);
            var second = Depth5Metrics.Ofi5(seq[(cut - 1)..]);          // the baseline book is carried into the next part, like a bar's reference state
            Assert.Equal(whole.Net, (first.Net ?? 0) + (second.Net ?? 0));
            Assert.Equal(whole.Gross, (first.Gross ?? 0) + (second.Gross ?? 0));
            Assert.Equal(whole.Transitions, first.Transitions + second.Transitions);
        }
    }

    // ------------------------------------------------------------------ OFI5 == Level-1 OFI

    [Fact]
    public void Ofi5_ClampedToOneLevel_EqualsTheExistingLevelOneContribution_ForEveryBranch()
    {
        // bid rises / equal / falls x ask rises / equal / falls, with changed quantities
        foreach (var db in new[] { +2, 0, -2 })
        foreach (var da in new[] { +3, 0, -3 })
        {
            var p = Book([(2000, 40)], [(2005, 70)]);
            var n = Book([(2000 + db, 25)], [(2005 + da, 90)]);
            var l1 = FuturesMicrostructureTracker.Contribution(
                new FuturesBookState(DateTimeOffset.UnixEpoch, 1, P(2000), P(2005), 40, 70),
                new FuturesBookState(DateTimeOffset.UnixEpoch, 2, P(2000 + db), P(2005 + da), 25, 90));
            Assert.Equal(l1, Depth5Metrics.Ofi5Contribution(p, n, levels: 1));
        }
    }

    [Fact]
    public void Ofi5_ClampedToOneLevel_MatchesLevelOne_OnRandomBooks()
    {
        var rng = new Random(101);
        for (var i = 0; i < 30_000; i++)
        {
            var p = RandomBook(rng); var n = RandomNear(rng, p);
            var l1 = FuturesMicrostructureTracker.Contribution(Level1(p, 1), Level1(n, 2));
            Assert.Equal(l1, Depth5Metrics.Ofi5Contribution(p, n, levels: 1));
        }
    }

    // ------------------------------------------------------------------ OFI5 differential test against an independent reference

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    [InlineData(2)]
    public void Ofi5_MatchesAnIndependentGridReference_OnRandomBooks(int levels)
    {
        var rng = new Random(2026 + levels);
        for (var i = 0; i < 20_000; i++)
        {
            var p = RandomBook(rng);
            var n = (i & 1) == 0 ? RandomNear(rng, p) : RandomBook(rng);
            Assert.Equal(GridReference(p, n, levels), Depth5Metrics.Ofi5Contribution(p, n, levels));
        }
    }

    /// <summary>
    /// Independent implementation: integer tick grid, explicit per-tick quantity arrays and an explicit observability test per price per snapshot (a price is observable in a snapshot when it lies
    /// inside that snapshot's depth window; a side with fewer than <c>levels</c> levels shows everything). A price counts only if observable in BOTH snapshots. No shared code with the product path.
    /// </summary>
    static long? GridReference(Depth5Book p, Depth5Book n, int levels)
    {
        var ticks = p.Bids.Concat(p.Asks).Concat(n.Bids).Concat(n.Asks).Where(l => !l.IsEmpty).Select(l => TickOf(l.Price)).ToList();
        int lo = ticks.Min() - 1, hi = ticks.Max() + 1;       // every price outside this range is empty in both snapshots; the whole grid in between is scanned
        var pb = Grid(p.Bids, levels, lo, hi, out var pbLow, out _); var nb = Grid(n.Bids, levels, lo, hi, out var nbLow, out _);
        var pa = Grid(p.Asks, levels, lo, hi, out _, out var paHigh); var na = Grid(n.Asks, levels, lo, hi, out _, out var naHigh);
        long total = 0;
        for (var t = lo; t <= hi; t++)
        {
            var i = t - lo;
            if (t >= pbLow && t >= nbLow) total += nb[i] - pb[i];                  // observable in BOTH bid windows
            if (t <= paHigh && t <= naHigh) total -= na[i] - pa[i];                // observable in BOTH ask windows
        }

        return total;
    }

    /// <summary>Per-tick quantity array of the first <paramref name="levels"/> real levels plus the observability bounds: a side showing fewer than <c>levels</c> levels shows everything.</summary>
    static long[] Grid(IReadOnlyList<Depth5Level> side, int levels, int lo, int hi, out int lowBound, out int highBound)
    {
        var q = new long[hi - lo + 1];
        var real = side.Where(l => !l.IsEmpty).Take(levels).ToList();
        foreach (var l in real) q[TickOf(l.Price) - lo] += l.Qty;
        var full = real.Count == levels;
        lowBound = full ? real.Min(l => TickOf(l.Price)) : int.MinValue;
        highBound = full ? real.Max(l => TickOf(l.Price)) : int.MaxValue;
        return q;
    }

    static int TickOf(double price) => (int)Math.Round(price / Tick);

    // ------------------------------------------------------------------ determinism and numeric precision

    [Fact]
    public void Calculations_AreDeterministic_BitForBit()
    {
        var rng = new Random(77);
        for (var i = 0; i < 2000; i++)
        {
            var seed = rng.Next();
            var a = RandomBook(new Random(seed)); var b = RandomBook(new Random(seed));
            Assert.Equal(BitConverter.DoubleToInt64Bits(Depth5Metrics.MicroDev5Proxy(a)!.Value), BitConverter.DoubleToInt64Bits(Depth5Metrics.MicroDev5Proxy(b)!.Value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(Depth5Metrics.NotionalImbalance(a)!.Value), BitConverter.DoubleToInt64Bits(Depth5Metrics.NotionalImbalance(b)!.Value));
            Assert.Equal(Depth5Metrics.Ofi5Contribution(a, RandomNear(new Random(seed + 1), a)), Depth5Metrics.Ofi5Contribution(b, RandomNear(new Random(seed + 1), b)));
        }
    }

    [Fact]
    public void LargeQuantitiesAndPrices_KeepFullPrecision_AndOverflowIsLoud()
    {
        var big = Book([(500_000, 4_000_000_000L), (499_999, 4_000_000_000L)], [(500_002, 3_000_000_000L)]);
        Assert.Equal(8_000_000_000L, Depth5Metrics.TotalBidQuantity(big));
        var v = Depth5Metrics.MicroDev5Proxy(big)!.Value;
        Assert.Equal(((P(500_002) - P(500_000)) / 2d) * (8_000_000_000d - 3_000_000_000d) / 11_000_000_000d, v, 9);
        var huge = Book([(2000, long.MaxValue)], [(2002, 1)]);
        Assert.Throws<OverflowException>(() => Depth5Metrics.MicroDev5Proxy(huge));                          // never wraps silently
    }

    // ------------------------------------------------------------------ generators

    /// <summary>A random valid book: best bid tick in [1000, 3000], 1..5 levels per side with gaps of 1..3 ticks, quantities 1..500, spread 0..6 ticks (locked allowed).</summary>
    static Depth5Book RandomBook(Random rng)
    {
        var bestBid = rng.Next(1000, 3000);
        var bids = Side(rng, bestBid, -1);
        var asks = Side(rng, bestBid + rng.Next(0, 7), +1);
        return Book(bids, asks);
    }

    static (int, long)[] Side(Random rng, int best, int direction)
    {
        var count = rng.Next(1, 6);
        if (rng.Next(3) != 0) count = 5;
        var list = new (int, long)[count];
        var tick = best;
        for (var i = 0; i < count; i++)
        {
            list[i] = (tick, rng.Next(1, 501));
            tick += direction * rng.Next(1, 4);
        }

        return list;
    }

    /// <summary>A book close to <paramref name="p"/>: shifted best price by -2..+2 ticks, most quantities unchanged, some changed, shapes preserved where possible (exercises rank shifts and window moves).</summary>
    static Depth5Book RandomNear(Random rng, Depth5Book p)
    {
        var shiftBid = rng.Next(-2, 3); var shiftAsk = rng.Next(-2, 3);
        (int, long)[] Move(IReadOnlyList<Depth5Level> side, int shift) =>
            side.Where(l => !l.IsEmpty).Select(l => (TickOf(l.Price) + (rng.Next(4) == 0 ? shift : 0), rng.Next(3) == 0 ? (long)rng.Next(1, 501) : l.Qty)).ToArray();

        var bids = Move(p.Bids, shiftBid).OrderByDescending(x => x.Item1).GroupBy(x => x.Item1).Select(g => g.First()).Take(5).ToArray();
        var asks = Move(p.Asks, shiftAsk).OrderBy(x => x.Item1).GroupBy(x => x.Item1).Select(g => g.First()).Take(5).ToArray();
        if (rng.Next(4) == 0 && bids.Length > 0) bids = [(bids[0].Item1 + 1, rng.Next(1, 501)), .. bids.Take(4)];      // a new best bid pushes the others down a rank
        if (rng.Next(4) == 0 && asks.Length > 0) asks = [(asks[0].Item1 - 1, rng.Next(1, 501)), .. asks.Take(4)];
        var next = Book(bids, asks);
        return next.IsValid ? next : RandomBook(rng);
    }

    static FuturesBookState Level1(Depth5Book b, long id) =>
        new(DateTimeOffset.UnixEpoch, id, b.Bids[0].Price, b.Asks[0].Price, b.Bids[0].Qty, b.Asks[0].Qty);
}
