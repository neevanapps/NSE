using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>08-Oct plan Slice 2A: TOB / MicroDev / OFI over a completed adaptive bar.</summary>
public sealed class FuturesMicrostructureTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);

    static CleanObserverTick Book(long id, double seconds, double bid, double ask, long bq, long aq, long volume = 0)
    {
        var at = T0.AddSeconds(seconds);
        return new CleanObserverTick(id, at, at, (bid + ask) / 2d, bid, ask, bq, aq, volume, 1000);
    }

    static FuturesMicrostructureTracker Track(params CleanObserverTick[] ticks)
    {
        var t = new FuturesMicrostructureTracker();
        foreach (var tick in ticks) t.Observe(tick);
        return t;
    }

    // ---- TOB and MicroDev ----

    [Fact]
    public void TobAndMicroDev_UseCrossWeightedMicroprice_AndEqualHalfSpreadTimesTob()
    {
        var state = new FuturesBookState(T0, 1, 22999, 23001, 300, 100);
        Assert.Equal(0.5, state.Tob!.Value, 12);
        Assert.Equal(0.5, state.MicroDev!.Value, 12);                       // micro 23000.5 - mid 23000
        Assert.Equal((23001 - 22999) / 2d * state.Tob.Value, state.MicroDev.Value, 12);

        var askHeavy = new FuturesBookState(T0, 2, 22999, 23001, 100, 300);
        Assert.Equal(-0.5, askHeavy.Tob!.Value, 12);
        Assert.Equal(-0.5, askHeavy.MicroDev!.Value, 12);                   // ask-heavy => micro below mid
    }

    [Theory]
    [InlineData(0, 23001, 10, 10)]       // bid <= 0
    [InlineData(22999, 0, 10, 10)]       // ask <= 0
    [InlineData(23001, 22999, 10, 10)]   // crossed (ask < bid)
    [InlineData(22999, 23001, -1, 10)]   // negative bid quantity
    [InlineData(22999, 23001, 10, -1)]   // negative ask quantity
    public void InvalidBook_HasNoTobOrMicroDev(double bid, double ask, long bq, long aq)
    {
        var state = new FuturesBookState(T0, 1, bid, ask, bq, aq);
        Assert.False(state.IsValid);
        Assert.Null(state.Tob); Assert.Null(state.MicroDev);
    }

    [Fact]
    public void ZeroQuantityDenominator_IsValidBookButHasNoTobOrMicroDev()
    {
        var state = new FuturesBookState(T0, 1, 22999, 23001, 0, 0);
        Assert.True(state.IsValid);
        Assert.Null(state.Tob); Assert.Null(state.MicroDev);
    }

    [Fact]
    public void TimeWeighting_IsByIntervalNotByRowCount_AndDuplicatesDoNotOverweight()
    {
        // 0..4s ask-light (TOB +0.5), 4..10s ask-heavy (TOB -0.5).
        var plain = Track(Book(1, 0, 22999, 23001, 300, 100), Book(2, 4, 22999, 23001, 100, 300));
        var bar = plain.Complete(T0, T0.AddSeconds(10));
        Assert.Equal(-0.1, bar.TobTimeWeighted!.Value, 12);
        Assert.Equal(-0.1, bar.MicroDevTimeWeighted!.Value, 12);
        Assert.Equal(0.5, bar.TobStart!.Value, 12); Assert.Equal(-0.5, bar.TobEnd!.Value, 12);
        Assert.Equal(-1.0, bar.TobChange!.Value, 12);
        Assert.Equal(-0.5, bar.TobMin!.Value, 12); Assert.Equal(0.5, bar.TobMax!.Value, 12);
        Assert.Equal(10d, bar.ValidBookSeconds, 12); Assert.Equal(0d, bar.InvalidBookSeconds, 12);

        // Fifty repeated identical snapshots of the first state must not change anything.
        var ticks = new List<CleanObserverTick> { Book(1, 0, 22999, 23001, 300, 100) };
        for (var i = 0; i < 50; i++) ticks.Add(Book(10 + i, 1 + i * 0.01, 22999, 23001, 300, 100));
        ticks.Add(Book(100, 4, 22999, 23001, 100, 300));
        var noisy = Track(ticks.ToArray()).Complete(T0, T0.AddSeconds(10));
        Assert.Equal(bar, noisy);
        Assert.Equal(1, noisy.BookStateChanges);
    }

    // ---- OFI ----

    [Fact]
    public void Ofi_MatchesTheLevelOneEventFormula()
    {
        var prev = new FuturesBookState(T0, 1, 100, 101, 10, 10);
        // Same prices, bid qty up to 15, ask qty down to 8: +15 -10 -8 +10 = 7.
        Assert.Equal(7, FuturesMicrostructureTracker.Contribution(prev, new FuturesBookState(T0, 2, 100, 101, 15, 8)));
        // Bid price up (qty 5): +5 only from the bid side; ask reprices up (qty 4): + prev ask qty 10 => 15.
        Assert.Equal(15, FuturesMicrostructureTracker.Contribution(prev, new FuturesBookState(T0, 2, 101, 102, 5, 4)));
        // Bid price down (qty 6): -prev bid qty 10; ask price down (qty 7): -7 and ask side prev term absent => -17.
        Assert.Equal(-17, FuturesMicrostructureTracker.Contribution(prev, new FuturesBookState(T0, 2, 99, 100, 6, 7)));
    }

    [Fact]
    public void Ofi_SumsUniqueBookTransitionsInsideTheBar_UsingTheStateAtBarStartAsReference()
    {
        // Reference state exists before the bar; the first in-bar change is evaluated against it.
        var tracker = Track(
            Book(1, -5, 100, 101, 10, 10),
            Book(2, 3, 100, 101, 15, 8),    // +7 vs the reference
            Book(3, 6, 101, 102, 5, 4));    // +15 vs previous
        var bar = tracker.Complete(T0, T0.AddSeconds(10));
        Assert.Equal(20, bar.Ofi);                         // +7 against the pre-bar reference, then +13 (bid 5 up, plus prior ask qty 8)
        Assert.Equal(2, bar.OfiTransitions);
        Assert.Equal(2, bar.BookStateChanges);
    }

    [Fact]
    public void Ofi_IsAdditiveAcrossConsecutiveBars_WithAllValidBooks()
    {
        var rng = new Random(20261008);
        var ticks = new List<CleanObserverTick>();
        double bid = 22990;
        for (var i = 0; i < 400; i++)
        {
            bid += rng.Next(-2, 3);
            ticks.Add(Book(i + 1, i * 0.7, bid, bid + 1 + rng.Next(0, 2), rng.Next(1, 500), rng.Next(1, 500)));
        }

        var tracker = Track(ticks.ToArray());
        var cuts = new[] { 0d, 37.5, 90.0, 91.0, 91.0, 150.2, 279.3 };
        long sum = 0;
        for (var i = 1; i < cuts.Length; i++) sum += tracker.Complete(T0.AddSeconds(cuts[i - 1]), T0.AddSeconds(cuts[i])).Ofi!.Value;
        Assert.Equal(tracker.Complete(T0, T0.AddSeconds(cuts[^1])).Ofi, sum); // no transition lost or double counted at boundaries
    }

    [Fact]
    public void InvalidOrCrossedBook_ResetsTheOfiBaseline_AndNeverBridgesTheGap()
    {
        var tracker = Track(
            Book(1, 0, 100, 101, 10, 10),
            Book(2, 2, 102, 101, 50, 50),   // crossed => invalid
            Book(3, 5, 100, 101, 15, 8));   // valid again: re-baseline, NOT +7 against the stale pre-gap book
        var bar = tracker.Complete(T0, T0.AddSeconds(10));
        Assert.Equal(0, bar.Ofi);
        Assert.Equal(0, bar.OfiTransitions);
        Assert.Equal(1, bar.InvalidBookEvents);
        Assert.Equal(3d, bar.InvalidBookSeconds, 12);
        Assert.Equal(7d, bar.ValidBookSeconds, 12);

        // The next valid change after the re-baseline is evaluated normally.
        tracker.Observe(Book(4, 12, 100, 101, 20, 8));
        var next = tracker.Complete(T0.AddSeconds(10), T0.AddSeconds(15));
        Assert.Equal(FuturesMicrostructureTracker.Contribution(
            new FuturesBookState(T0, 3, 100, 101, 15, 8), new FuturesBookState(T0, 4, 100, 101, 20, 8)), next.Ofi);
    }

    [Fact]
    public void NoValidBookAtAll_IsUnavailableNotZero()
    {
        var bar = Track(Book(1, 0, 0, 0, 0, 0)).Complete(T0, T0.AddSeconds(10));
        Assert.Null(bar.Ofi); Assert.Null(bar.TobTimeWeighted); Assert.Null(bar.MicroDevTimeWeighted);
        Assert.Equal(10d, bar.InvalidBookSeconds, 12);
        Assert.Equal(0d, bar.ValidBookSeconds, 12);

        var empty = new FuturesMicrostructureTracker().Complete(T0, T0.AddSeconds(10));
        Assert.Null(empty.Ofi); Assert.Equal(10d, empty.InvalidBookSeconds, 12);
    }

    [Fact]
    public void ZeroDurationBar_HasNoTimeWeightedValuesAndNoSyntheticOfi()
    {
        var tracker = Track(Book(1, 0, 100, 101, 10, 10), Book(2, 5, 100, 101, 15, 8));
        var bar = tracker.Complete(T0.AddSeconds(5), T0.AddSeconds(5));
        Assert.Null(bar.TobTimeWeighted); Assert.Null(bar.MicroDevTimeWeighted);
        Assert.Equal(0d, bar.ValidBookSeconds);
        Assert.Equal(0, bar.OfiTransitions);
        Assert.Equal(0, bar.Ofi);                                  // the 5s state is this bar's reference, not its own change
    }

    // ---- Through the engine ----

    static AdaptiveObserverEngine Engine() => new(
        new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
        new DateOnly(2026, 10, 15), .065, null, T0, [], null);

    static CleanObserverTick FutureTick(long id, double seconds, double bid, double ask, long bq, long aq, long volume)
    {
        var at = T0.AddSeconds(seconds);
        return new CleanObserverTick(id, at, at, 23000, bid, ask, bq, aq, volume, 5000);
    }

    [Fact]
    public void Engine_AttachesMicrostructureToTheCompletedBar_WithoutChangingCoreOutputs()
    {
        var engine = Engine();
        engine.ProcessAvailabilityGroup([("FUT", FutureTick(1, 0, 22999, 23001, 300, 100, 1000))]);
        Assert.Empty(engine.ProcessAvailabilityGroup([("FUT", FutureTick(2, 4, 22999, 23001, 100, 300, 1050))]));
        var package = Assert.Single(engine.ProcessAvailabilityGroup([("FUT", FutureTick(3, 10, 22999, 23001, 100, 300, 1100))]));

        Assert.Equal(T0, package.FutureBar.StartAvailableAtUtc);
        Assert.Equal(T0.AddSeconds(10), package.FutureBar.EndAvailableAtUtc);
        Assert.Equal(100, package.FutureBar.Volume);
        var m = package.Microstructure!;
        Assert.Equal(-0.1, m.TobTimeWeighted!.Value, 12);
        Assert.Equal(-0.1, m.MicroDevTimeWeighted!.Value, 12);
        Assert.Equal(-400, m.Ofi);                                 // 100 - 300 - 300 + 100 on the 4s book change
        Assert.Equal(1, m.OfiTransitions);
        Assert.Equal(1, m.BookStateChanges);                       // the 10s tick repeats the 4s book and collapses
        Assert.Equal(10d, m.ValidBookSeconds, 12);
    }

    [Fact]
    public void Engine_LiveGroupByGroupAndFullReplayProduceIdenticalMicrostructure()
    {
        var rng = new Random(7);
        var ticks = new List<CleanObserverTick>();
        double bid = 22995; long volume = 1000;
        for (var i = 0; i < 600; i++)
        {
            bid += rng.Next(-1, 2);
            volume += rng.Next(0, 60);
            ticks.Add(FutureTick(i + 1, i * 0.9, bid, bid + 1 + rng.Next(0, 2), rng.Next(1, 400), rng.Next(1, 400), volume));
        }

        var groups = ticks.GroupBy(t => t.AvailableAt).Select(g => g.Select(t => ("FUT", t)).ToArray()).ToArray();
        var live = Engine(); var replay = Engine();
        var liveBars = new List<AdaptiveCompletedBarPackage>(); var replayBars = new List<AdaptiveCompletedBarPackage>();
        foreach (var g in groups) liveBars.AddRange(live.ProcessAvailabilityGroup(g));
        foreach (var g in groups) replayBars.AddRange(replay.ProcessAvailabilityGroup(g));

        Assert.NotEmpty(liveBars);
        Assert.Equal(liveBars.Count, replayBars.Count);
        for (var i = 0; i < liveBars.Count; i++)
        {
            Assert.Equal(liveBars[i].FutureBar.BarSeq, replayBars[i].FutureBar.BarSeq);
            Assert.Equal(liveBars[i].Microstructure, replayBars[i].Microstructure);   // exact (deterministic) equality, record value semantics
            Assert.NotNull(liveBars[i].Microstructure);
        }
    }
}
