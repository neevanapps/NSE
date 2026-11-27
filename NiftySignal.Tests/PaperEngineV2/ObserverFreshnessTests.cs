using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.ExecutionService;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;
using NiftySignal.Tests.AdaptiveObserver;
using NiftySignal.Tests.Trading.Data;
using Rig = NiftySignal.Tests.PaperEngineV2.ExecutionServicePostgresTests.Rig;
using ExitReason = NiftySignal.PaperEngineV2.Execution.ExitReason;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>The processing-freshness entry block (a live, beating Observer that is behind must not authorize entries): the pure gate.</summary>
public class ObserverFreshnessPolicyTests
{
    static readonly DateTimeOffset Now = FrameFixtures.End(10);
    static readonly ExecutionServiceOptions Defaults = new();

    static ProtectionContext Context(double? throughAge = 1, double? waitingAge = null, int? bar = 10, bool overdue = false, double readFromAge = 600) => new(
        Now, new TimeOnly(11, 0), new ObserverState(true, AdaptiveRuntimeStatus.Live, Now.AddSeconds(-1), false, throughAge is { } a ? Now.AddSeconds(-a) : null, bar), true, TimeSpan.Zero, Now.AddSeconds(-1), true, null,
        waitingAge is { } w ? Now.AddSeconds(-w) : null, Now.AddSeconds(-readFromAge), overdue);

    static void BlocksEntriesOnly(ProtectionContext c, string reason)
    {
        var s = ProtectionPolicy.Evaluate(c, Defaults);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + reason, s.EntryBlock);
        Assert.Null(s.Invalidate);                     // a waiting intent survives
        Assert.Null(s.Flatten);                        // nothing is closed because of it
    }

    [Theory]
    [InlineData(1d, null)] [InlineData(1d, 4.9)] [InlineData(1d, 5d)]
    [InlineData(300d, null)]                           // quiet market: an old position with nothing waiting is current
    [InlineData(300d, 0.5)]                            // quiet market, then a first tick the Observer has had no time to process
    public void Current_IsNotBlocked(double throughAge, double? waitingAge) =>
        Assert.Equal(SafetyState.Healthy, ProtectionPolicy.Evaluate(Context(throughAge, waitingAge), Defaults));

    [Fact]
    public void Backlog_IsBlockedBeyondTheExistingProcessingLagLimit() => BlocksEntriesOnly(Context(6, 5.001), " is behind the market data");

    [Fact]
    public void MissingOrInvalidPosition_FailsClosed()
    {
        BlocksEntriesOnly(Context(throughAge: null), " position is unavailable");
        BlocksEntriesOnly(Context(bar: null), " position is unavailable");
        BlocksEntriesOnly(Context(throughAge: -0.5), " position is invalid");
        BlocksEntriesOnly(Context(bar: -1), " position is invalid");
        BlocksEntriesOnly(Context(throughAge: 100, readFromAge: 60), " position predates the ticks execution has read");
        BlocksEntriesOnly(Context(overdue: true), ": a completed bar has not been evaluated");
    }

    [Fact]
    public void TheLimitIsTheConfiguredOne_AndStaysFiveSeconds() => Assert.Equal(TimeSpan.FromSeconds(5), Defaults.MaxProcessingLag);
}

/// <summary>
/// The same rule through the coordinator on a real PostgreSQL ledger, with the four shipped strategy files unmodified (skipped without <c>NSE_TRADING_TEST_DB</c>). The feed is scripted: the test
/// decides what the Observer has processed.
/// </summary>
[Collection("PostgresTradingTests")]
public class ObserverFreshnessCoordinatorTests
{
    public static TheoryData<string> All => ["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"];
    public static TheoryData<string> Avg5 => ["TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6_AVG5"];
    public static TheoryData<string> Immediate => ["TOTAL_ACTIVITY_5_10", "TOTAL_VOTE_5_OF_6"];

    static CompiledStrategy Shipped(string id) => ExecutionSetup.Load(new ExecutionServiceOptions
    {
        Enabled = true, StrategyDirectory = "paper-engine-v2-strategies/execution-service", CostScheduleVersion = "npt1-frozen-fees-v1", BrokeragePerOrder = 0m,
    }, AppContext.BaseDirectory).Strategies.Single(s => s.Config.Identity.StrategyId == id);

    /// <summary>A frame carrying the metrics the shipped entry rules read: <paramref name="direction"/> +1 satisfies buyCall, -1 buyPut, 0 neither.</summary>
    static MarketStateFrame Frame(int seq, int direction)
    {
        var metrics = new List<MetricValue>
        {
            new(FrameFixtures.H("opt.total.coverage_full"), SummaryWindowId.W5, 1),
            new(FrameFixtures.H("opt.total.coverage_full"), SummaryWindowId.W10, 1),
            new(FrameFixtures.H("opt.total.activity.dir:raw"), SummaryWindowId.W10, direction * 10),
        };
        foreach (var name in new[] { "price", "enriched", "oi", "microdev_inv", "ofi", "activity" })
            metrics.Add(new(FrameFixtures.H($"opt.total.{name}.dir:raw"), SummaryWindowId.W5, direction * 10));
        var end = FrameFixtures.End(seq);
        var quotes = ExecFixtures.Default.Select(q => new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, end.AddMilliseconds(-q.AgeMs), 5_000 + seq)).ToList();
        return MarketStateFrame.Create(FrameFixtures.Identity(1, seq, end, null, 0, true), FrameFixtures.BasketId(), metrics, quotes, [], new VixAvailability(end.AddSeconds(-2), 2, "Fresh"));
    }

    /// <summary>One strategy on its own rig. The Observer is "current" unless a step says it makes no progress.</summary>
    sealed class Run(Rig rig, ExecutionCoordinator coordinator, ServiceAlerts alerts) : IAsyncDisposable
    {
        long _futureId = 900_000, _vixId = 800_000;

        public Rig Rig => rig;
        public ExecutionCoordinator C => coordinator;
        public ServiceAlerts Alerts => alerts;
        public List<T> Of<T>() where T : PaperEvent => coordinator.Engine!.Events.OfType<T>().ToList();
        public string? Block => coordinator.Snapshot!.EntryBlock;
        public bool Pending => coordinator.Engine!.Runtimes.Single().HasPendingEntry;

        public static async Task<Run> StartAsync(string id)
        {
            var rig = await Rig.CreateAsync();
            rig.Feed.Average = _ => 118d;                              // the usual quotes are above it (a 5-bar-average intent waits); a pullback goes below it
            var (c, alerts) = rig.New(strategies: [Shipped(id)]);
            var run = new Run(rig, c, alerts);
            for (var seq = 1; seq <= 24; seq++) await run.BarAsync(seq, 0);
            return run;
        }

        public async Task BarAsync(int seq, int direction, bool progress = true)
        {
            rig.Clock.Now = FrameFixtures.End(seq).AddSeconds(1);
            rig.Feed.Bars[seq] = Frame(seq, direction);
            await CycleAsync(progress);
        }

        public async Task CycleAsync(bool progress = true, bool call = true, bool put = true, double bid = 125, double ask = 126)
        {
            rig.Feed.Observer = rig.Feed.Observer with { HeartbeatUtc = rig.Clock.Now };
            if (progress) Progress();
            if (call) rig.Feed.Quote("CE26000", bid, ask, rig.Clock.Now.AddMilliseconds(-50));
            if (put) rig.Feed.Quote("PE26000", 125, 126, rig.Clock.Now.AddMilliseconds(-50));
            if (call || put) rig.Feed.Ticks.Enqueue(new FeedTick("VIX", new CleanObserverTick(++_vixId, rig.Clock.Now, rig.Clock.Now.AddMilliseconds(-50), 14, 14, 14, 1, 1, 1, null), true));
            await coordinator.RunCycleAsync(default);
        }

        /// <summary>The Observer has processed everything up to now and completed every published bar.</summary>
        public void Progress() => rig.Feed.Observer = rig.Feed.Observer with { ProcessedThroughUtc = rig.Clock.Now.AddMilliseconds(-20), LastCompletedBarSeq = rig.Feed.Bars.Count == 0 ? 0 : rig.Feed.Bars.Keys.Max() };

        public void FutureTick() => rig.Feed.Ticks.Enqueue(new FeedTick("FUT", new CleanObserverTick(++_futureId, rig.Clock.Now, rig.Clock.Now, 26000, 25999, 26001, 65, 65, 1, null), false, true));

        public async Task AdvanceAsync(int seconds, bool progress = true, bool call = true, bool put = true, double bid = 125, double ask = 126)
        {
            for (var i = 0; i < seconds; i++)
            {
                rig.Advance(1);
                await CycleAsync(progress, call, put, bid, ask);
            }
        }

        /// <summary>The pullback below the 5-bar average, inside the premium band.</summary>
        public Task PullbackAsync(int seconds, bool progress = true) => AdvanceAsync(seconds, progress, bid: 110, ask: 111);

        /// <summary>Signal on bar 25 and a filled call position, whatever the entry timing.</summary>
        public async Task HoldCallAsync()
        {
            await BarAsync(25, 1);
            await PullbackAsync(3);
            Assert.Single(Of<EntryFilled>());
        }

        public ValueTask DisposeAsync() => rig.DisposeAsync();
    }

    // ---- a backlog blocks every entry path ----

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task Avg5_PendingIntent_IsKeptThroughABacklog_AndPlacesOnlyAfterRecovery(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        Assert.True(run.Pending);

        await run.AdvanceAsync(8, progress: false);                    // ticks keep arriving, the Observer stops
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + " is behind the market data", run.Block);
        await run.PullbackAsync(5, progress: false);
        Assert.Empty(run.Of<EntryOrderPlaced>());
        Assert.Empty(run.Of<EntryIntentCancelled>());
        Assert.True(run.Pending);

        await run.PullbackAsync(3);
        Assert.Null(run.Block);
        Assert.Single(run.Of<EntryFilled>());
    }

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task Avg5_PendingIntent_IsCancelledByAnOppositeSignalOfACatchUpBar_BeforeItCanResume(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        await run.AdvanceAsync(8, progress: false);

        await run.BarAsync(26, -1);                                    // the Observer catches up: the bar arrives long after it ended on the market clock
        await run.PullbackAsync(3);
        Assert.Contains(run.Of<EntryIntentCancelled>(), e => e.IntentId.Contains(":25:", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Of<EntryOrderPlaced>(), e => e.Right == OptionRight.Call);
    }

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task ASignalEvaluatedDuringABacklog_PlacesNothing_AndIsNotRevivedLater(string id)
    {
        await using var run = await Run.StartAsync(id);
        run.Rig.Clock.Now = FrameFixtures.End(25).AddSeconds(-7);
        await run.CycleAsync(progress: false);                         // a tick 8 seconds old by the time bar 25 is evaluated
        await run.BarAsync(25, 1, progress: false);
        Assert.StartsWith(ProtectionPolicy.ProcessingPrefix, run.Block);
        await run.PullbackAsync(3);
        Assert.Empty(run.Of<EntryOrderPlaced>());
        Assert.False(run.Pending);
        Assert.Contains(run.Of<EntryRejected>(), e => e.Reason == RejectionReason.EntriesBlocked);
    }

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task Reversal_ExitStillFills_DuringABacklog_AndOnlyAnAvg5IntentSurvives(string id)
    {
        var avg5 = id.EndsWith("AVG5", StringComparison.Ordinal);
        await using var run = await Run.StartAsync(id);
        await run.HoldCallAsync();
        run.Rig.Feed.Average = _ => 140d;                              // from the opposite signal's bar on, the put is below its average
        await run.BarAsync(26, -1);
        Assert.True(run.Of<EntryIntentCreated>().Last().AwaitingFlat); // a reversal intent really exists
        Assert.True(run.Pending);
        await run.AdvanceAsync(8, progress: false, call: false);       // the call cannot be sold yet and the Observer stops
        Assert.Empty(run.Of<ExitFilled>());

        await run.AdvanceAsync(3, progress: false);
        Assert.Single(run.Of<ExitFilled>());                           // the exit never waits for the Observer
        Assert.Single(run.Of<EntryOrderPlaced>());                     // no put during the backlog
        Assert.Equal(avg5, run.Pending);

        await run.AdvanceAsync(3);
        Assert.Equal(avg5 ? 2 : 1, run.Of<EntryFilled>().Count);       // the immediate reversal was discarded
    }

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task AnUnfilledOrder_IsDroppedByABacklog_AndOnlyAnAvg5IntentIsRetried(string id)
    {
        var avg5 = id.EndsWith("AVG5", StringComparison.Ordinal);
        await using var run = await Run.StartAsync(id);
        if (avg5) run.Rig.Feed.Average = _ => 140d;                    // places at once, on the bar's own cycle
        run.Rig.Clock.Now = FrameFixtures.End(25).AddSeconds(1);
        run.Rig.Feed.Bars[25] = Frame(25, 1);
        run.Progress();
        await run.CycleAsync(progress: false);
        if (run.Of<EntryOrderPlaced>().Count == 0) await run.AdvanceAsync(1);
        Assert.Single(run.Of<EntryOrderPlaced>());
        Assert.Empty(run.Of<EntryFilled>());

        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { ProcessedThroughUtc = null };
        await run.AdvanceAsync(2, progress: false);
        Assert.Empty(run.Of<EntryFilled>());
        Assert.Equal(avg5, run.Pending);

        await run.AdvanceAsync(3);
        Assert.Equal(avg5 ? 1 : 0, run.Of<EntryFilled>().Count);
        Assert.Equal(avg5 ? 2 : 1, run.Of<EntryOrderPlaced>().Count);
    }

    // ---- quiet market against backlog ----

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task AnOldBar_WithACurrentObserver_IsNotABacklog(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        run.Rig.Advance(600);                                          // no bar for ten minutes
        await run.PullbackAsync(3);
        Assert.Null(run.Block);
        Assert.Single(run.Of<EntryFilled>());
    }

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task AQuietSpell_WithNothingWaiting_IsNotBlocked(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        run.Progress();
        await run.AdvanceAsync(8, progress: false, call: false, put: false);       // no tick at all, the watermark does not move
        Assert.Null(run.Block);
    }

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task AFutureBacklogAlone_Blocks_AndFutureTicksNeverCountAsOptionData(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        run.Rig.Advance(1);
        await run.CycleAsync();                                        // every option tick is processed
        run.Rig.Advance(0.5);
        run.FutureTick();                                              // only the future is not
        await run.CycleAsync(progress: false, call: false, put: false);
        run.Rig.Advance(6);
        await run.CycleAsync(progress: false, call: false, put: false);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + " is behind the market data", run.Block);

        for (var i = 0; i < 12; i++)
        {
            run.Rig.Advance(1);
            run.FutureTick();
            await run.CycleAsync(call: false, put: false);
        }

        Assert.Equal("market data is stale", run.Block);
        Assert.Empty(run.Of<EntryOrderPlaced>());
    }

    // ---- fail closed ----

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task AMissingOrInvalidPosition_AuthorizesNothing(string id)
    {
        await using var run = await Run.StartAsync(id);
        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { ProcessedThroughUtc = null };
        run.Rig.Feed.Average = _ => 140d;
        await run.BarAsync(25, 1, progress: false);
        await run.AdvanceAsync(2, progress: false);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + " position is unavailable", run.Block);

        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { ProcessedThroughUtc = run.Rig.Clock.Now.AddMinutes(1) };
        await run.AdvanceAsync(2, progress: false);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + " position is invalid", run.Block);
        Assert.Empty(run.Of<EntryOrderPlaced>());
    }

    [PostgresTheory]
    [MemberData(nameof(Avg5))]
    public async Task ACompletedBarThatIsNotEvaluated_IsAllowedBriefly_ThenBlocks(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        run.Rig.Advance(1);
        run.Progress();
        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { LastCompletedBarSeq = 26 };       // completed, never published
        await run.CycleAsync(progress: false);
        Assert.Null(run.Block);

        for (var i = 0; i < 7; i++)
        {
            run.Rig.Advance(1);
            run.Rig.Feed.Observer = run.Rig.Feed.Observer with { ProcessedThroughUtc = run.Rig.Clock.Now.AddMilliseconds(-20) };
            await run.CycleAsync(progress: false);
        }

        Assert.Equal(ProtectionPolicy.ProcessingPrefix + ": a completed bar has not been evaluated", run.Block);

        var (restarted, _) = run.Rig.New(strategies: [Shipped(id)]);
        await run.C.DisposeAsync();
        run.Rig.Advance(1);
        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { HeartbeatUtc = run.Rig.Clock.Now, ProcessedThroughUtc = run.Rig.Clock.Now.AddMilliseconds(-20) };
        run.Rig.Feed.Quote("CE26000", 110, 111, run.Rig.Clock.Now);
        await restarted.RunCycleAsync(default);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + ": a completed bar has not been evaluated", restarted.Snapshot!.EntryBlock);       // no allowance after a start
        Assert.Empty(restarted.Engine!.Events.OfType<EntryOrderPlaced>());
    }

    // ---- exits, diagnostics, restart ----

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task ProfitTargetAndSessionLiquidation_RunWithoutAnyPosition_FromTheObserver(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.HoldCallAsync();
        var quantity = run.C.Engine!.Runtimes.Single().Positions().Single().RemainingQuantity;
        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { ProcessedThroughUtc = null };

        await run.AdvanceAsync(4, progress: false, bid: 190, ask: 191);
        Assert.True(run.C.Engine.Runtimes.Single().Positions().Single().RemainingQuantity < quantity);

        run.Rig.Clock.Now = FrameFixtures.End(338);                    // 15:08
        await run.AdvanceAsync(4, progress: false);
        Assert.Equal(PositionState.Closed, run.C.Engine.Runtimes.Single().Positions().Single().State);
        Assert.Equal(ExitReason.Terminal, run.Of<ExitRequested>().Last().Reason);
        Assert.StartsWith(ProtectionPolicy.ProcessingPrefix, run.Block);
    }

    [PostgresTheory]
    [MemberData(nameof(All))]
    public async Task ARestartAfterABlock_ReproducesTheLedger(string id)
    {
        await using var run = await Run.StartAsync(id);
        await run.BarAsync(25, 1);
        await run.AdvanceAsync(8, progress: false);
        await run.PullbackAsync(3, progress: false);
        await run.PullbackAsync(3);
        var events = await run.Rig.CountAsync("pev2_events");
        await run.C.DisposeAsync();

        var (second, _) = run.Rig.New(strategies: [Shipped(id)]);
        run.Rig.Advance(2);
        run.Progress();
        run.Rig.Feed.Observer = run.Rig.Feed.Observer with { HeartbeatUtc = run.Rig.Clock.Now };
        await second.RunCycleAsync(default);
        Assert.True(second.Snapshot!.Reconciled, string.Join("; ", second.Snapshot.Conditions));
        Assert.Equal(events, await run.Rig.CountAsync("pev2_events"));
    }

    [PostgresTheory]
    [MemberData(nameof(Immediate))]
    public async Task ABacklog_RaisesNoAlertMessages(string id)
    {
        await using var run = await Run.StartAsync(id);
        var before = run.Alerts.Active.Count;
        await run.AdvanceAsync(8, progress: false);
        Assert.StartsWith(ProtectionPolicy.ProcessingPrefix, run.Block);
        Assert.Equal(before, run.Alerts.Active.Count);                 // visible in the heartbeat row and the log, not a message per episode
    }
}

/// <summary>The real database feed and a real Observer database: the processed position is read from the runtime row and the session future is read as progress-only (skipped without both test databases).</summary>
[Collection("PostgresTradingTests")]
public class ObserverFreshnessDatabaseFeedTests
{
    sealed class BothDatabasesFactAttribute : FactAttribute
    {
        public BothDatabasesFactAttribute()
        {
            if (!PostgresTestDatabase.Available || !AdaptivePg.Available) Skip = "set NSE_TRADING_TEST_DB and NSE_ADAPTIVE_MIGRATION_TEST_DB";
        }
    }

    [BothDatabasesFact]
    public async Task Coordinator_BlocksOnAStaleRuntimePosition_AndClearsWhenItAdvances()
    {
        await using var temp = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_obs_test_");
        await temp.MigrateAsync();
        await using var rig = await Rig.CreateAsync();
        var now = FrameFixtures.End(5);
        rig.Clock.Now = now;
        var universe = new[] { new ObserverOptionInstrument("CE26000", "CE", OptionType.Call, 26000, FrameFixtures.Expiry, 65) };
        var row = new AdaptiveSessionStateRow
        {
            TradeDate = FrameFixtures.Day, ModelVersion = "adaptive-fixed13650-v1", SourceBranch = "test", SourceCommitSha = "test", BuildUtc = now,
            FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = FrameFixtures.Expiry, OpeningWindowStartUtc = now.AddMinutes(-20), OpeningWindowEndUtc = now.AddMinutes(-5),
            OpeningVolume = 1000, EstimatorName = "test", CreatedAtUtc = now, WeeklyOptionExpiry = FrameFixtures.Expiry, OptionUniverseJson = System.Text.Json.JsonSerializer.Serialize(universe),
        };
        await using (var db = temp.Context())
        {
            db.Sessions.Add(row);
            await db.SaveChangesAsync();
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = row.Id, LastHeartbeatUtc = now, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastProcessedSourceAvailableAtUtc = now.AddSeconds(-10), LastCompletedBarSeq = 0 });
            await db.SaveChangesAsync();
        }

        var source = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        async Task TickAsync(long id, string token, DateTimeOffset at)
        {
            await using var db = new NiftySignalDbContext(source);
            db.Ticks.Add(new Tick { Id = id, Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 120m + id, Volume = id });
            await db.SaveChangesAsync();
        }

        async Task BeatAsync(DateTimeOffset? through)
        {
            await using var db = temp.Context();
            var runtime = await db.Runtime.SingleAsync(x => x.SessionId == row.Id);
            runtime.LastHeartbeatUtc = rig.Clock.Now;
            if (through is not null) runtime.LastProcessedSourceAvailableAtUtc = through;
            await db.SaveChangesAsync();
        }

        var feed = new DbExecutionFeed(temp.Context, () => new NiftySignalDbContext(source), new ExecutionServiceOptions());
        await using var coordinator = new ExecutionCoordinator(new ExecutionServiceOptions { LiveMarksEnabled = false }, rig.Db, feed, [ExecFixtures.Strat()],
            new ExecutionOptions(new TimeOnly(15, 25), [ExecFixtures.Schedule()], new SessionLotSizes()), new SessionLotSizes(), new ServiceAlerts(rig.Telegram, rig.Clock, TimeSpan.FromMinutes(15)), rig.Clock);

        // only the future is ahead of the Observer; the option tick is processed
        await TickAsync(1, "CE26000", now.AddSeconds(-11));
        await TickAsync(2, "FUT", now.AddSeconds(-8));
        await TickAsync(3, "CE26000", now.AddSeconds(-1));
        await BeatAsync(null);
        await coordinator.RunCycleAsync(default);
        Assert.Equal(ProtectionPolicy.ProcessingPrefix + " is behind the market data", coordinator.Snapshot!.EntryBlock);

        rig.Advance(1);
        await BeatAsync(rig.Clock.Now.AddSeconds(-1.5));
        await coordinator.RunCycleAsync(default);
        Assert.Null(coordinator.Snapshot!.EntryBlock);
    }
}
