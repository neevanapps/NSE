using NiftySignal.AdaptiveObserver;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class MemoryMarkStore : ILiveMarkStore
{
    readonly object _gate = new();

    public Dictionary<string, PositionMark> Rows { get; } = new(StringComparer.Ordinal);

    public List<MarkHeartbeat> Beats { get; } = [];

    public int Calls { get; private set; }

    public Exception? FailWith { get; set; }

    public Task PublishAsync(IReadOnlyList<PositionMark> changed, MarkHeartbeat hb, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls++;
            if (FailWith is { } f) throw f;
            foreach (var m in changed) Rows[m.PositionId] = m;
            Beats.Add(hb);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Near-real-time indicative marks: the P&amp;L maths, freshness and invalid-quote semantics, shared tokens, out-of-order protection, restart behaviour, and the proof that marks never change the ledger.</summary>
public class LiveMarkTests
{
    static readonly DateTimeOffset T = new(2026, 11, 4, 4, 30, 0, TimeSpan.Zero);          // 10:00 IST

    static MarkPosition Pos(string id = "P1", string token = "CE26000", PositionState state = PositionState.Open, decimal entry = 127.50m, int qty = 130, decimal entryFees = 30m, decimal slip = 0.05m,
        int maxAgeMs = 2000, string strategy = "S01", DateOnly? day = null, decimal realized = 0m) =>
        new(strategy, 2, day ?? new DateOnly(2026, 11, 4), 1, id, OptionRight.Call, token, 26000, new DateOnly(2026, 11, 10), qty, 2, entry, entryFees, T.AddMinutes(-5), state, maxAgeMs, slip, v => Math.Round(v * 0.001m, 2), realized,
            state == PositionState.Closed ? 140m : null, state == PositionState.Closed ? 1625m : null, state == PositionState.Closed ? 1560m : null);

    static MarkQuote Q(string token = "CE26000", double bid = 130, double ask = 131, double ltp = 130.5, double ageSeconds = 0.5, long tick = 100) => new(token, bid, ask, ltp, T.AddSeconds(-ageSeconds), tick);

    // ---- the maths ----

    [Fact]
    public void GrossIsBidMarked_AndNetLiquidationCountsEveryChargeOnce()
    {
        var m = PositionMarkCalculator.Compute(Pos(), Q(bid: 130), T, 1);
        Assert.Equal(MarkStatus.Fresh, m.Status);
        Assert.Equal((130m - 127.50m) * 130, m.GrossBidMarked);                                   // (bid - entry fill) x remaining quantity = 325.00
        var exitPrice = 130m - 0.05m;                                                              // the engine's exit fill: bid - exit slippage
        var exitFees = Math.Round(exitPrice * 130 * 0.001m, 2);
        Assert.Equal(exitFees, m.EstExitFees);
        Assert.Equal(((exitPrice - 127.50m) * 130) - 30m - exitFees, m.EstNetLiquidation);        // entry charges (incurred) + estimated exit charges, once each
        Assert.Equal(30m, m.EntryFeesIncurred);
        Assert.Equal(127.50m, m.EntryPrice); Assert.Equal(130, m.Quantity); Assert.Equal(2, m.Lots);
    }

    [Fact]
    public void ALosingMark_IsNegative_AndTheExitPriceNeverGoesBelowZero()
    {
        var m = PositionMarkCalculator.Compute(Pos(), Q(bid: 100, ask: 101), T, 1);
        Assert.True(m.GrossBidMarked < 0 && m.EstNetLiquidation < m.GrossBidMarked);             // charges only ever make the net worse
        var floor = PositionMarkCalculator.Compute(Pos(slip: 5m), Q(bid: 0.05, ask: 0.10), T, 1);
        Assert.Equal(0m, floor.EstExitFees);                                                       // max(0, bid - slippage) = 0 -> no sale value, no fee
        Assert.Equal((0m - 127.50m) * 130 - 30m, floor.EstNetLiquidation);
    }

    [Fact]
    public void TheMarkMatchesTheEnginesOwnExitFill_WhenTheSameBidIsFilled()
    {
        // Drive a real engine to an open position, then compare the indicative net with the ledger net of an exit filled at the same bid.
        var e = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options());
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, dir: 0, quotes: [new("CE26000", 125.5, 127)])]);
        var held = Assert.Single(e.MarkPositions, p => p.State == PositionState.Open);
        var indicative = PositionMarkCalculator.Compute(held, new MarkQuote(held.Token, 130, 131, 130.5, T, 9), T, 1);
        ExecFixtures.Run(e, [ExecFixtures.Fr(7, dir: 0, fut: -100, quotes: [new("CE26000", 130, 131)]), ExecFixtures.Fr(8, dir: 0, fut: -100, quotes: [new("CE26000", 130, 131)])]);
        var exit = Assert.Single(e.Events.OfType<ExitFilled>());
        Assert.Equal(exit.NetPnl, indicative.EstNetLiquidation);                                   // the estimate IS the ledger formula
        Assert.Equal(exit.Fees, indicative.EstExitFees);
    }

    // ---- freshness and invalid books ----

    [Theory]
    [InlineData(0.5, MarkStatus.Fresh)]
    [InlineData(2.0, MarkStatus.Fresh)]
    [InlineData(2.5, MarkStatus.Stale)]
    public void FreshnessFollowsTheStrategysQuoteAgeLimit(double age, MarkStatus expected)
    {
        var m = PositionMarkCalculator.Compute(Pos(), Q(ageSeconds: age), T, 1);
        Assert.Equal(expected, m.Status);
        if (expected == MarkStatus.Stale) { Assert.Null(m.GrossBidMarked); Assert.Null(m.EstNetLiquidation); Assert.NotNull(m.Bid); }      // the last quote is shown, flagged; no P&L comes from it
        Assert.Equal(age, m.QuoteAgeSeconds!.Value, 6);
    }

    [Fact]
    public void MissingAndInvalidQuotes_AreExplicit_NeverZero()
    {
        var missing = PositionMarkCalculator.Compute(Pos(), null, T, 1);
        Assert.Equal(MarkStatus.Missing, missing.Status); Assert.Null(missing.GrossBidMarked); Assert.Null(missing.Bid);
        foreach (var (bid, ask) in new[] { (0d, 131d), (130d, 0d), (131d, 130d), (double.NaN, 131d), (-1d, 2d) })
        {
            var m = PositionMarkCalculator.Compute(Pos(), Q(bid: bid, ask: ask), T, 1);
            Assert.Equal(MarkStatus.Invalid, m.Status);
            Assert.Null(m.GrossBidMarked); Assert.Null(m.EstNetLiquidation); Assert.Null(m.Bid);
        }

        Assert.Equal(MarkStatus.Invalid, PositionMarkCalculator.Compute(Pos(state: PositionState.Unresolved), Q(), T, 1).Status);
    }

    [Fact]
    public void AClosedPosition_ShowsTheLedgerNet_AndNeverALiveEstimate()
    {
        var m = PositionMarkCalculator.Compute(Pos(state: PositionState.Closed, realized: 1560m), Q(), T, 1);
        Assert.Equal(MarkStatus.Closed, m.Status);
        Assert.Equal(1560m, m.ClosedNet); Assert.Equal(1560m, m.RealizedNetToday);
        Assert.Null(m.GrossBidMarked); Assert.Null(m.EstNetLiquidation);                           // realized and unrealized are separate
    }

    [Fact]
    public void AClockSkewedFutureQuote_HasZeroAge_NotNegative()
    {
        var m = PositionMarkCalculator.Compute(Pos(), Q(ageSeconds: -3), T, 1);
        Assert.Equal(0d, m.QuoteAgeSeconds); Assert.Equal(MarkStatus.Fresh, m.Status);
    }

    // ---- the board ----

    static CleanObserverTick Tick(long id, DateTimeOffset at, double bid = 130, double ask = 131) => new(id, at, at, 130.5, bid, ask, 650, 650, 0, null);

    [Fact]
    public void AnOutOfOrderTick_NeverOverwritesANewerQuote()
    {
        var b = new LiveMarkBoard(["CE26000"]);
        b.Observe("CE26000", Tick(10, T));
        b.Observe("CE26000", Tick(9, T.AddSeconds(-1), bid: 99, ask: 100));                         // older availability
        b.Observe("CE26000", Tick(8, T, bid: 98, ask: 99));                                          // same availability, lower tick id
        var q = b.TryGet("CE26000")!;
        Assert.Equal((130d, 10L), (q.Bid, q.SourceTickId));
        Assert.Equal(2, b.IgnoredOutOfOrder);
        b.Observe("CE26000", Tick(11, T.AddSeconds(1), bid: 131, ask: 132));
        Assert.Equal(11L, b.TryGet("CE26000")!.SourceTickId);                                        // a newer one does replace it
    }

    [Fact]
    public void OnlyTheSessionsOptionUniverseIsTracked_AndATokenIsNeverSubstituted()
    {
        var b = new LiveMarkBoard(["CE26000", "PE26000"]);
        b.Observe("CE99999", Tick(1, T));
        Assert.Null(b.TryGet("CE99999")); Assert.Equal(0, b.Tracked);
        b.Observe("PE26000", Tick(2, T));
        Assert.Null(b.TryGet("CE26000"));                                                            // the CE has no quote: it is Missing, the PE is not used in its place
        Assert.NotNull(b.TryGet("PE26000"));
    }

    // ---- the publisher ----

    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    static (LiveMarkPublisher Pub, MemoryMarkStore Store, LiveMarkBoard Board, ManualClock Clock) Rig(Func<IReadOnlyList<MarkPosition>> positions, ILiveMarkStore? store = null)
    {
        var s = (MemoryMarkStore)(store ?? new MemoryMarkStore());
        var board = new LiveMarkBoard(["CE26000", "CE25800", "PE26000"]);
        var clock = new ManualClock(T);
        return (new LiveMarkPublisher(positions, () => board, s, clock, TimeSpan.FromSeconds(1)), s, board, clock);
    }

    [Fact]
    public async Task SharedTokens_AreOneQuote_MarkedPerPosition_AndAHeldContractOutsideTheBasketIsCovered()
    {
        var positions = new[] { Pos("P1", "CE26000", strategy: "S01"), Pos("P2", "CE26000", strategy: "S02", entry: 120m), Pos("P3", "CE25800", strategy: "S03") };        // CE25800 = a held strike far from ATM +-2
        var (pub, store, board, _) = Rig(() => positions);
        board.Observe("CE26000", Tick(1, T)); board.Observe("CE25800", Tick(2, T, bid: 210, ask: 212));
        Assert.Equal(3, await pub.TickAsync(default));
        Assert.Equal(2, board.Tracked);                                                              // two quotes serve three positions
        Assert.Equal(130d, store.Rows["P1"].Bid); Assert.Equal(130d, store.Rows["P2"].Bid);
        Assert.NotEqual(store.Rows["P1"].GrossBidMarked, store.Rows["P2"].GrossBidMarked);        // each position is marked against its OWN entry
        Assert.Equal(210d, store.Rows["P3"].Bid);
        Assert.Equal("CE25800", store.Rows["P3"].Token);
    }

    [Fact]
    public async Task OnlyChangedMarksAreWritten_ButTheHeartbeatIsWrittenEverySample()
    {
        var (pub, store, board, clock) = Rig(() => [Pos()]);
        board.Observe("CE26000", Tick(1, T));
        Assert.Equal(1, await pub.TickAsync(default));
        clock.Now = T.AddSeconds(1);
        Assert.Equal(0, await pub.TickAsync(default));                                              // same quote tick, same status -> no row rewritten
        board.Observe("CE26000", Tick(2, T.AddSeconds(1), bid: 131, ask: 132));
        Assert.Equal(1, await pub.TickAsync(default));                                              // a new quote tick -> one row
        clock.Now = T.AddSeconds(8);
        Assert.Equal(1, await pub.TickAsync(default));                                              // unchanged but older than 5 s -> refreshed (and its status has become Stale)
        Assert.Equal(MarkStatus.Stale, store.Rows["P1"].Status);
        Assert.Equal(4, store.Beats.Count);
        Assert.Equal([1L, 2L, 3L, 4L], store.Beats.Select(b => b.Seq).ToArray());
    }

    [Fact]
    public async Task AStoreFailure_IsContained_AndThePublisherKeepsSampling()
    {
        var store = new MemoryMarkStore { FailWith = new InvalidOperationException("database down") };
        var (pub, _, board, _) = Rig(() => [Pos()], store);
        board.Observe("CE26000", Tick(1, T));
        Assert.Equal(0, await pub.TickAsync(default));                                              // nothing thrown
        Assert.Equal(1, pub.Failures);
        store.FailWith = null;
        Assert.Equal(1, await pub.TickAsync(default));                                              // recovered on the next sample
    }

    [Fact]
    public async Task ARestart_StartsMissing_UntilAFreshQuoteArrives_AndNothingFromYesterdayIsCarriedOver()
    {
        var today = new DateOnly(2026, 11, 4);
        var (pub, store, board, _) = Rig(() => [Pos("P1", day: today)]);
        await pub.TickAsync(default);                                                                // the Host restarted: the position is rebuilt from the journal, the board is empty
        Assert.Equal(MarkStatus.Missing, store.Rows["P1"].Status);
        board.Observe("CE26000", Tick(1, T));
        await pub.TickAsync(default);
        Assert.Equal(MarkStatus.Fresh, store.Rows["P1"].Status);

        var (pub2, store2, _, _) = Rig(() => [Pos("OLD", day: new DateOnly(2026, 11, 3)), Pos("NEW", day: today)]);
        await pub2.TickAsync(default);
        Assert.Equal(DateOnly.Parse("2026-11-04"), store2.Rows["NEW"].TradeDate);                  // each mark carries its trade date, so the Dashboard can refuse another day's rows
        Assert.Equal(DateOnly.Parse("2026-11-03"), store2.Rows["OLD"].TradeDate);
    }

    // ---- the ledger is untouched ----

    [Fact]
    public async Task TheLedgerAndTheOpportunityLog_AreIdentical_WithMarksOnAndOff_EvenWhilePublishingConcurrently()
    {
        var off = new PaperEngine([ExecFixtures.Strat("S01"), ExecFixtures.Strat("S02")], ExecFixtures.Options());
        ExecFixtures.Run(off, ExecutionTests.Day());

        var on = new PaperEngine([ExecFixtures.Strat("S01"), ExecFixtures.Strat("S02")], ExecFixtures.Options());
        var store = new MemoryMarkStore();
        var board = new LiveMarkBoard(["CE26000", "CE25950", "PE26000", "PE25950"]);
        var pub = new LiveMarkPublisher(() => on.MarkPositions, () => board, store, TimeProvider.System, TimeSpan.FromMilliseconds(5));
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => pub.RunAsync(cts.Token));
        long id = 0;
        foreach (var f in ExecutionTests.Day())
        {
            foreach (var q in ExecFixtures.Default) board.Observe(q.Token, Tick(++id, DateTimeOffset.UtcNow, q.Bid, q.Ask));
            ExecFixtures.Process(on, f, ExecFixtures.Allowed);
            await Task.Delay(1);
        }

        cts.Cancel();
        await loop;
        Assert.Equal(off.Events, on.Events);                                                         // byte-for-byte the same fills, rejections and exits
        Assert.Equal(off.Opportunities, on.Opportunities);
        Assert.True(store.Calls > 0);
    }
}
