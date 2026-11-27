using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.ExecutionService;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Persistence;
using NiftySignal.Tests.AdaptiveObserver;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class RecordingTelegram : ITelegramTextSender
{
    public List<string> Sent { get; } = [];

    public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct)
    {
        Sent.Add(text);
        return Task.FromResult(new TelegramDocumentResult(TelegramDocumentOutcome.Sent, Sent.Count));
    }
}

/// <summary>The standalone execution service without a database: the protection gate, settings, shipped configuration, isolation, alert deduplication and the bounded database feed.</summary>
public class ExecutionServicePureTests
{
    static readonly DateTimeOffset Now = FrameFixtures.End(10);
    static readonly ExecutionServiceOptions Defaults = new();

    static ProtectionContext Context(ObserverState? observer = null, bool open = true, TimeSpan notLive = default, double dataAge = 1, bool reconciled = true, string? frame = null, TimeOnly? ist = null) =>
        new(Now, ist ?? new TimeOnly(11, 0), observer ?? Observer(), open, notLive, Now.AddSeconds(-dataAge), reconciled, frame);

    static ObserverState Observer(AdaptiveRuntimeStatus status = AdaptiveRuntimeStatus.Live, double age = 1) => new(true, status, Now.AddSeconds(-age), false, Now.AddSeconds(-1), 0);

    [Fact]
    public void Healthy_BlocksNothing() => Assert.Equal(SafetyState.Healthy, ProtectionPolicy.Evaluate(Context(), Defaults));

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void StaleHeartbeat_BlocksAndFlattens_OnlyBeyondThirtySeconds(int age, bool stale)
    {
        var s = ProtectionPolicy.Evaluate(Context(Observer(age: age)), Defaults);
        Assert.Equal(stale, s.Flatten is not null);
        Assert.Equal(stale, s.EntryBlock is not null);
    }

    [Theory]
    [InlineData(AdaptiveRuntimeStatus.RecoveryFailed, true)]
    [InlineData(AdaptiveRuntimeStatus.Degraded, true)]
    [InlineData(AdaptiveRuntimeStatus.Rebuilding, false)]
    [InlineData(AdaptiveRuntimeStatus.Calibrating, false)]
    public void NotLive_AlwaysBlocksEntries_AndFlattensOnlyForTerminalStates(AdaptiveRuntimeStatus status, bool flatten)
    {
        var s = ProtectionPolicy.Evaluate(Context(Observer(status)), Defaults);
        Assert.NotNull(s.EntryBlock);
        Assert.Equal(flatten, s.Flatten is not null);
    }

    [Fact]
    public void OptionalNotLiveLimit_FlattensARebuildThatNeverEnds()
    {
        var o = Defaults with { FlattenWhenNotLiveAfter = TimeSpan.FromMinutes(2) };
        var rebuilding = Observer(AdaptiveRuntimeStatus.Rebuilding);
        Assert.Null(ProtectionPolicy.Evaluate(Context(rebuilding, notLive: TimeSpan.FromMinutes(1)), o).Flatten);
        Assert.NotNull(ProtectionPolicy.Evaluate(Context(rebuilding, notLive: TimeSpan.FromMinutes(3)), o).Flatten);
    }

    [Fact]
    public void MissingOrUnreadableObserver_FailsClosed()
    {
        Assert.NotNull(ProtectionPolicy.Evaluate(Context(new ObserverState(false, null, null)), Defaults).Flatten);
        Assert.Null(ProtectionPolicy.Evaluate(Context(new ObserverState(false, null, null), open: false), Defaults).Flatten);
        var recentlyRead = Observer() with { ReadFailed = true };
        Assert.Null(ProtectionPolicy.Evaluate(Context(recentlyRead), Defaults).Flatten);
        Assert.NotNull(ProtectionPolicy.Evaluate(Context(recentlyRead), Defaults).EntryBlock);
        Assert.NotNull(ProtectionPolicy.Evaluate(Context(Observer(age: 45) with { ReadFailed = true }), Defaults).Flatten);
        Assert.NotNull(ProtectionPolicy.Evaluate(Context(new ObserverState(false, null, null, ReadFailed: true)), Defaults).Flatten);
    }

    [Fact]
    public void OnlyInvalidObserverHealthAndUnreconciledState_InvalidatePendingIntents()
    {
        SafetyState Of(ProtectionContext c) => ProtectionPolicy.Evaluate(c, Defaults);
        // temporary: entries are blocked, a waiting intent survives
        foreach (var s in new[] { Of(Context(dataAge: 11)), Of(Context(frame: "bar 7 cannot be assembled")), Of(Context(Observer() with { ReadFailed = true })) })
        {
            Assert.NotNull(s.EntryBlock);
            Assert.Null(s.Invalidate);
        }

        // terminal: also with no open position
        foreach (var s in new[]
        {
            Of(Context(Observer(age: 31), open: false)), Of(Context(Observer(AdaptiveRuntimeStatus.Rebuilding), open: false)), Of(Context(Observer(AdaptiveRuntimeStatus.RecoveryFailed), open: false)),
            Of(Context(new ObserverState(false, null, null), open: false)), Of(Context(Observer(age: 31) with { ReadFailed = true }, open: false)), Of(Context(reconciled: false)),
        }) Assert.NotNull(s.Invalidate);
    }

    [Fact]
    public void StaleData_Unreconciled_OrAMissingFrame_BlockEntriesWithoutFlattening()
    {
        foreach (var s in new[]
        {
            ProtectionPolicy.Evaluate(Context(dataAge: 11), Defaults),
            ProtectionPolicy.Evaluate(Context(reconciled: false), Defaults),
            ProtectionPolicy.Evaluate(Context(frame: "bar 7 cannot be assembled"), Defaults),
        })
        {
            Assert.NotNull(s.EntryBlock);
            Assert.Null(s.Flatten);
        }
    }

    // ---- shipped configuration and isolation ----

    static ExecutionServiceOptions Shipped(params (string, string)[] overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.ExecutionService", "appsettings.json"))
            .AddInMemoryCollection(overrides.Select(o => new KeyValuePair<string, string?>(o.Item1, o.Item2))).Build();
        return ExecutionServiceOptions.Bind(configuration);
    }

    [Fact]
    public void ShippedSettings_AreDisabled_Valid_AndLoadExactlyTheFourApprovedStrategies()
    {
        var o = Shipped();
        Assert.False(o.Enabled);
        Assert.Empty(o.Validate());
        Assert.Equal((new TimeOnly(15, 0), new TimeOnly(15, 8), new TimeOnly(15, 10), TimeSpan.FromSeconds(30), TimeSpan.Zero),
            (o.Times.EntryCutoff, o.Times.LiquidationStart, o.Times.LiquidationDeadline, o.ObserverHeartbeatStale, o.FlattenGrace));
        var setup = ExecutionSetup.Load(o, TradingModelTests.RepoRoot());
        Assert.Equal(["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"],
            setup.Strategies.Select(s => s.Config.Identity.StrategyId).Order(StringComparer.Ordinal));
        Assert.Equal(["TOTAL_VOTE_4_OF_6"], setup.Disabled.Select(c => c.Identity.StrategyId));
    }

    [Fact]
    public void InvalidSettings_RefuseToStart_AndATypoIsNeverADefault()
    {
        Assert.Throws<InvalidOperationException>(() => ExecutionSetup.Load(Shipped(("Execution:LiquidationDeadline", "15:11")), TradingModelTests.RepoRoot()));
        Assert.Throws<InvalidOperationException>(() => ExecutionSetup.Load(Shipped(("Execution:EntryCutoff", "15:05")), TradingModelTests.RepoRoot()));
        Assert.Throws<InvalidOperationException>(() => ExecutionSetup.Load(Shipped(("Execution:UnresolvedAfter", "15:09")), TradingModelTests.RepoRoot()));
        Assert.Throws<FormatException>(() => Shipped(("Execution:ObserverHeartbeatStaleSeconds", "thirty")));
    }

    [Fact]
    public void Service_ReferencesNoBrokerIngestionOrLiveProject()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<System.Reflection.Assembly>([typeof(ExecutionWorker).Assembly]);
        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies().Where(r => r.Name!.StartsWith("NiftySignal", StringComparison.Ordinal) && seen.Add(r.Name!)))
                queue.Enqueue(System.Reflection.Assembly.Load(reference));
        }

        Assert.Contains("NiftySignal.PaperEngineV2.Execution", seen);
        Assert.DoesNotContain(seen, n => n.Contains("Ingestion", StringComparison.Ordinal) || n == "NiftySignal.Execution" || n.EndsWith(".Live", StringComparison.Ordinal) || n.EndsWith(".Adapters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Alerts_AreDeduplicated_Reminded_AndRecovered()
    {
        var telegram = new RecordingTelegram();
        var clock = new ManualClock(Now);
        var alerts = new ServiceAlerts(telegram, clock, TimeSpan.FromMinutes(15));
        var down = new Dictionary<string, string> { ["observer"] = "Observer unavailable" };
        alerts.Update(down);
        alerts.Update(down);
        clock.Now = Now.AddMinutes(16);
        alerts.Update(down);
        alerts.Update(new Dictionary<string, string>());
        alerts.Update(new Dictionary<string, string>());
        await alerts.FlushAsync(default);
        Assert.Equal(3, telegram.Sent.Count);
        Assert.All(telegram.Sent, m => Assert.StartsWith(ServiceAlerts.Banner, m));
        Assert.Contains("STILL OPEN", telegram.Sent[1]);
        Assert.Contains("RECOVERED", telegram.Sent[2]);
    }

    // ---- the bounded feed (in-memory provider: query shape and cursor behaviour, not PostgreSQL semantics) ----

    [Fact]
    public async Task Feed_ReadsOnlyARecentWindowAtStart_ThenOnlyNewTicks_InBatches()
    {
        var name = Guid.NewGuid().ToString("N");
        var source = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(name).Options;
        var observer = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(name + "o").Options;
        var feed = new DbExecutionFeed(() => new AdaptiveObserverDbContext(observer), () => new NiftySignalDbContext(source), new ExecutionServiceOptions { TickBatchSize = 100 });
        var session = new V2SessionInfo(1, FrameFixtures.Day, "adaptive-fixed13650-v1", null, FrameFixtures.Expiry, FrameFixtures.Listed(), new Dictionary<string, int> { ["CE26000"] = 65 });

        async Task AddAsync(long firstId, int count, DateTimeOffset at, string token = "CE26000")
        {
            await using var db = new NiftySignalDbContext(source);
            for (var i = 0; i < count; i++)
                db.Ticks.Add(new Tick { Id = firstId + i, Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at.AddMilliseconds(i), ReceivedAt = at.AddMilliseconds(i), LastPrice = 120m + i % 5, Volume = i });
            await db.SaveChangesAsync();
        }

        await AddAsync(1, 5, Now.AddHours(-1));                      // older than the start-up look-back
        await AddAsync(10, 3, Now.AddSeconds(-5));
        var first = await feed.ReadTicksAsync(session, Now, default);
        Assert.Equal([10L, 11L, 12L], first.Select(t => t.Tick.Id));
        Assert.Empty(await feed.ReadTicksAsync(session, Now, default));
        await AddAsync(100, 250, Now.AddSeconds(1));                 // more than two batches
        await AddAsync(400, 4, Now.AddSeconds(2), "OTHER");          // outside the session universe
        var next = await feed.ReadTicksAsync(session, Now.AddSeconds(3), default);
        Assert.Equal(250, next.Count);
        Assert.Equal(next.Select(t => t.Tick.Id).Order(), next.Select(t => t.Tick.Id));
        Assert.Empty(await feed.ReadTicksAsync(session, Now.AddSeconds(4), default));
    }
}

/// <summary>Moves the paper ledger to another writer the way it happens in service: the owner's database session is gone and a second writer takes the free lock.</summary>
internal static class LedgerTransfer
{
    public static async Task<ExecutionOwnership> KillOwnerAndTakeAsync(NpgsqlDataSource db)
    {
        await using (var kill = db.CreateCommand("SELECT count(pg_terminate_backend(pid)) FROM pg_locks WHERE locktype = 'advisory' AND pid <> pg_backend_pid() AND database = (SELECT oid FROM pg_database WHERE datname = current_database())"))
            await kill.ExecuteNonQueryAsync();
        for (var i = 0; i < 200; i++)
        {
            if (await ExecutionOwnership.TryAcquireAsync(db, default) is { } taken) return taken;
            await Task.Delay(50);
        }

        throw new TimeoutException("the lock was never released");
    }

    /// <summary>Every ledger table's row count and content digest (the state row is excluded: a new owner moves its epoch).</summary>
    public static async Task<string> LedgerAsync(NpgsqlDataSource db)
    {
        var parts = new List<string>();
        foreach (var table in new[] { "pev2_events", "pev2_alert_outbox", "pev2_execution_journal", "pev2_frames", "pev2_dispositions", "pev2_gaps", "pev2_opportunities", "pev2_live_marks" })
        {
            await using var c = db.CreateCommand($"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM pev2.{table} t");
            parts.Add($"{table}={await c.ExecuteScalarAsync()}");
        }

        return string.Join("; ", parts);
    }
}

/// <summary>The coordinator on a real local PostgreSQL ledger (skipped without <c>NSE_TRADING_TEST_DB</c>) with a scripted feed: single writer, fencing, atomic commit, restart reconciliation and protection.</summary>
[Collection("PostgresTradingTests")]
public class ExecutionServicePostgresTests
{
    internal sealed class FakeFeed : IExecutionFeed
    {
        public V2SessionInfo? Session { get; set; } = new(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", FrameFixtures.Expiry, FrameFixtures.Listed(),
            FrameFixtures.Listed().SelectMany(s => new[] { s.CeToken!, s.PeToken! }).ToDictionary(t => t, _ => 65));
        public ObserverState Observer { get; set; } = new(true, AdaptiveRuntimeStatus.Live, null);
        public Queue<FeedTick> Ticks { get; } = new();
        public SortedDictionary<int, MarketStateFrame> Bars { get; } = [];
        long _tickId = 10_000;

        public void Quote(string token, double bid, double ask, DateTimeOffset at) =>
            Ticks.Enqueue(new FeedTick(token, new CleanObserverTick(++_tickId, at, at, ask, bid, ask, 650, 650, 1, null), false));

        public Task<SessionRead> ReadSessionAsync(DateOnly tradeDate, CancellationToken ct) => Task.FromResult(new SessionRead(Observer.ReadFailed ? null : Session, Observer));

        public Task<IReadOnlyList<FeedTick>> ReadTicksAsync(V2SessionInfo session, DateTimeOffset now, CancellationToken ct)
        {
            var list = Ticks.ToList();
            Ticks.Clear();
            return Task.FromResult<IReadOnlyList<FeedTick>>(list);
        }

        public Task<IReadOnlyList<PublishedBar>> ReadPublishedAsync(long sessionId, int afterBarSeq, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PublishedBar>>(Bars.Keys.Where(k => k > afterBarSeq).Take(limit).Select(k => new PublishedBar(k, true, null)).ToList());

        public Task<(MarketStateFrame? Frame, string? Problem)> BuildFrameAsync(V2SessionInfo session, PublishedBar bar, CancellationToken ct) =>
            Task.FromResult<(MarketStateFrame?, string?)>((Bars[bar.BarSeq], null));

        /// <summary>The rolling average of every asked token at a bar; none unless a test scripts it.</summary>
        public Func<int, double?>? Average { get; set; }

        public Task<IReadOnlyList<ContractAverage>> ReadAveragesAsync(V2SessionInfo session, IReadOnlyCollection<string> tokens, int barSeq, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ContractAverage>>(Average?.Invoke(barSeq) is { } value ? tokens.Select(t => new ContractAverage(t, value, barSeq)).ToList() : []);
    }

    internal sealed class Rig : IAsyncDisposable
    {
        public required NpgsqlDataSource Db { get; init; }
        public FakeFeed Feed { get; } = new();
        public ManualClock Clock { get; } = new(FrameFixtures.End(0));
        public RecordingTelegram Telegram { get; } = new();
        readonly List<ExecutionCoordinator> _coordinators = [];

        public static async Task<Rig> CreateAsync()
        {
            var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
            await ExecAsync(db, "DROP SCHEMA IF EXISTS pev2 CASCADE");
            await ExecAsync(db, File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
            return new Rig { Db = db };
        }

        public (ExecutionCoordinator Coordinator, ServiceAlerts Alerts) New(Func<CancellationToken, Task>? beforeCommit = null, IReadOnlyList<CompiledStrategy>? strategies = null, int? journalBatch = null)
        {
            var lots = new SessionLotSizes();
            var execution = new ExecutionOptions(new TimeOnly(15, 25), [ExecFixtures.Schedule(), ExecFixtures.OldSchedule()], lots);
            var alerts = new ServiceAlerts(Telegram, Clock, TimeSpan.FromMinutes(15));
            var coordinator = new ExecutionCoordinator(new ExecutionServiceOptions { LiveMarksEnabled = false, JournalReplayBatchSize = journalBatch ?? new ExecutionServiceOptions().JournalReplayBatchSize }, Db, Feed,
                strategies ?? [ExecFixtures.Strat()], execution, lots, alerts, Clock, beforeCommit: beforeCommit);
            _coordinators.Add(coordinator);
            return (coordinator, alerts);
        }

        /// <summary>One second after bar <paramref name="seq"/> ended: publishes it, quotes both ATM contracts and runs one cycle.</summary>
        public async Task BarAsync(ExecutionCoordinator c, int seq, double dir = 0)
        {
            Clock.Now = FrameFixtures.End(seq).AddSeconds(1);
            Feed.Bars[seq] = ExecFixtures.Fr(seq, dir);
            await CycleAsync(c);
        }

        public async Task CycleAsync(ExecutionCoordinator c, bool quotes = true, bool beat = true)
        {
            if (beat) Feed.Observer = Feed.Observer with { HeartbeatUtc = Clock.Now };
            // a current Observer: it has processed everything up to this instant and every published bar
            Feed.Observer = Feed.Observer with { ProcessedThroughUtc = Clock.Now.AddMilliseconds(-50), LastCompletedBarSeq = Feed.Bars.Count == 0 ? 0 : Feed.Bars.Keys.Max() };
            if (quotes) { Feed.Quote("CE26000", 125.5, 127, Clock.Now.AddMilliseconds(-100)); Feed.Quote("PE26000", 125, 126, Clock.Now.AddMilliseconds(-100)); }
            await c.RunCycleAsync(default);
        }

        public void Advance(double seconds) => Clock.Now = Clock.Now.AddSeconds(seconds);

        /// <summary>Bars 1-5 (signal on bar 5), then one later quote that fills the order.</summary>
        public async Task HoldAsync(ExecutionCoordinator c)
        {
            for (var seq = 1; seq <= 4; seq++) await BarAsync(c, seq);
            await BarAsync(c, 5, dir: 10);
            Advance(2);
            await CycleAsync(c);
            Assert.Single(c.Engine!.Events.OfType<EntryFilled>());
        }

        public async Task<long> CountAsync(string table, string? where = null)
        {
            await using var c = Db.CreateCommand($"SELECT count(*) FROM pev2.{table}" + (where is null ? "" : " WHERE " + where));
            return (long)(await c.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var c in _coordinators) await c.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    static async Task ExecAsync(NpgsqlDataSource db, string sql)
    {
        await using var c = db.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task SecondWriter_DoesNothing_AndTakesOverOnlyAfterTheFirstIsGone()
    {
        await using var rig = await Rig.CreateAsync();
        var (first, _) = rig.New();
        var (second, secondAlerts) = rig.New();
        await rig.BarAsync(first, 1);
        await rig.CycleAsync(second);
        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
        Assert.Contains(secondAlerts.Active, a => a.Contains("another execution writer"));
        Assert.Null(await ExecutionOwnership.TryAcquireAsync(rig.Db, default));
        Assert.Equal(1L, await rig.CountAsync("pev2_execution_journal"));

        await first.DisposeAsync();
        rig.Advance(2);
        await rig.CycleAsync(second);
        Assert.True(second.IsOwner);
        Assert.Equal(2, second.Epoch);
        Assert.True(second.Snapshot!.Reconciled);
    }

    [PostgresFact]
    public async Task InHostRuntime_RefusesToStart_WhileTheServiceOwnsTheLedger()
    {
        await using var rig = await Rig.CreateAsync();
        await using var owner = await ExecutionOwnership.TryAcquireAsync(rig.Db, default);
        Assert.NotNull(owner);
        var options = new V2RuntimeOptions
        {
            Enabled = true, ConnectionString = PostgresTestDatabase.ConnectionString, QueueCapacity = 64, MaxProcessingLag = TimeSpan.FromSeconds(5), FramesToClearGap = 3, TerminalRetryUntil = new TimeOnly(15, 25), BrokeragePerOrder = 0m, CostScheduleVersion = "npt1-frozen-fees-v1",
            StrategyDirectory = Path.Combine(TradingModelTests.RepoRoot(), "paper-engine-v2-strategies", "execution-service"),
        };
        await using var runtime = new V2Runtime(options, new InputSourceFrameFactory(new DbFrameInputSource(() => throw new InvalidOperationException(), () => throw new InvalidOperationException())));
        await runtime.StartAsync(default);
        Assert.False(runtime.Accepting);
        Assert.Contains("another execution writer", runtime.Detail);
    }

    [PostgresFact]
    public async Task SupersededWriter_CannotCommit_AndLetsGo()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, _) = rig.New();
        await rig.BarAsync(c, 1);
        await new ExecutionStore(rig.Db).ClaimAsync("intruder", rig.Clock.Now, default);
        await Assert.ThrowsAsync<OwnershipLostException>(() => rig.BarAsync(c, 2));
        Assert.False(c.IsOwner);
        Assert.Equal(1L, await rig.CountAsync("pev2_execution_journal"));
    }

    /// <summary>
    /// The race: the writer has evaluated the bar (the entry signal of bar 5 is decided, in memory) and ownership moves before it is persisted - to a newer epoch, or to a legacy writer after the lock
    /// connection is gone. Nothing of that decision may reach the ledger, and the next owner starts from what was committed.
    /// </summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipMovingBetweenEvaluationAndPersistence_NothingOfTheDecisionIsStored(bool connectionLost)
    {
        await using var rig = await Rig.CreateAsync();
        ExecutionOwnership? other = null;
        var transfer = false;
        var (c, _) = rig.New(async _ =>
        {
            if (!transfer) return;
            transfer = false;
            if (connectionLost) other = await LedgerTransfer.KillOwnerAndTakeAsync(rig.Db);
            else await new ExecutionStore(rig.Db).ClaimAsync("intruder", rig.Clock.Now, default);
        });
        for (var seq = 1; seq <= 4; seq++) await rig.BarAsync(c, seq);
        var before = await LedgerTransfer.LedgerAsync(rig.Db);

        transfer = true;
        await Assert.ThrowsAsync<OwnershipLostException>(() => rig.BarAsync(c, 5, dir: 10));
        await using var held = other;
        Assert.False(transfer);                                                        // the transfer really happened at the persistence boundary
        Assert.False(c.IsOwner);
        Assert.Equal(before, await LedgerTransfer.LedgerAsync(rig.Db));               // no journal row, no event, no alert

        if (connectionLost) await held!.DisposeAsync();
        var (next, _) = rig.New();
        await rig.CycleAsync(next, quotes: false);
        Assert.True(next.Snapshot!.Reconciled, string.Join("; ", next.Snapshot.Conditions));
    }

    [PostgresFact]
    public async Task OneInstanceOwns_AndALostInstanceStaysRefused_EvenAfterTheProcessOwnsAgain()
    {
        await using var rig = await Rig.CreateAsync();
        var first = (await ExecutionOwnership.TryAcquireAsync(rig.Db, default))!;
        Assert.Null(await ExecutionOwnership.TryAcquireAsync(rig.Db, default));      // never two writers
        var stale = new PostgresEventStore(rig.Db, first);
        await first.DisposeAsync();

        await using var second = (await ExecutionOwnership.TryAcquireAsync(rig.Db, default))!;
        Assert.Equal(first.Epoch + 1, second.Epoch);
        var config = ExecFixtures.Strat().Config;
        await Assert.ThrowsAsync<OwnershipLostException>(() => stale.RecordConfigAsync(config, rig.Clock.Now, default));
        Assert.Equal(0L, await rig.CountAsync("pev2_strategy_configs"));
        await new PostgresEventStore(rig.Db, second).RecordConfigAsync(config, rig.Clock.Now, default);
        Assert.Equal(1L, await rig.CountAsync("pev2_strategy_configs"));
    }

    [PostgresFact]
    public async Task ANewEpoch_WaitsForAWriteInProgress_SoCheckAndWriteAreOneStep()
    {
        await using var rig = await Rig.CreateAsync();
        await using var owner = (await ExecutionOwnership.TryAcquireAsync(rig.Db, default))!;
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var write = owner.WriteAsync(async (connection, tx) =>
        {
            inside.SetResult();
            await release.Task;
            await using var cmd = new NpgsqlCommand("INSERT INTO pev2.pev2_gaps (session_id, from_bar_seq, to_bar_seq, cause, producer_signals) VALUES (1, 2, 3, 0, 0)", connection, tx);
            return await cmd.ExecuteNonQueryAsync();
        }, default);
        await inside.Task;
        var claim = new ExecutionStore(rig.Db).ClaimAsync("intruder", rig.Clock.Now, default);
        await Task.Delay(500);
        Assert.False(claim.IsCompleted);                                              // held back by the row lock of the fenced write
        release.SetResult();
        await write;
        Assert.Equal(owner.Epoch + 1, await claim);
        Assert.Equal(1L, await rig.CountAsync("pev2_gaps"));
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.WriteAsync((_, _) => Task.FromResult(0), default));
    }

    [PostgresFact]
    public async Task Fill_IsJournaledWithItsEvents_AndARestartReproducesItWithoutDuplicates()
    {
        await using var rig = await Rig.CreateAsync();
        var (first, _) = rig.New();
        await rig.HoldAsync(first);
        var events = await rig.CountAsync("pev2_events");
        var journal = await rig.CountAsync("pev2_execution_journal");
        await first.DisposeAsync();

        var (second, _) = rig.New();
        rig.Advance(2);
        await rig.CycleAsync(second, quotes: false);                          // nothing new: the restart itself writes no input and no event
        Assert.True(second.Snapshot!.Reconciled, string.Join("; ", second.Snapshot.Conditions));
        Assert.Equal(events, await rig.CountAsync("pev2_events"));
        Assert.Equal(journal, await rig.CountAsync("pev2_execution_journal"));
        Assert.Single(second.Snapshot.Positions, p => p.State == PositionState.Open);
        Assert.Equal(5, second.Snapshot.LastBarSeq);

        await rig.BarAsync(second, 6);                                        // continues with the next bar, none replayed twice
        Assert.Equal(6, second.Snapshot!.LastBarSeq);
        Assert.Single(second.Engine!.Events.OfType<EntryFilled>());
    }

    [PostgresFact]
    public async Task FailedCommit_DiscardsMemory_AndTheRebuiltStateFillsExactlyOnce()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, _) = rig.New();
        for (var seq = 1; seq <= 4; seq++) await rig.BarAsync(c, seq);
        await rig.BarAsync(c, 5, dir: 10);
        var journal = await rig.CountAsync("pev2_execution_journal");
        await ExecAsync(rig.Db, "CREATE FUNCTION pev2.refuse() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'refused by test'; END $$");
        await ExecAsync(rig.Db, "CREATE TRIGGER refuse BEFORE INSERT ON pev2.pev2_events FOR EACH ROW EXECUTE FUNCTION pev2.refuse()");

        rig.Advance(2);
        await Assert.ThrowsAsync<PostgresException>(() => rig.CycleAsync(c));
        Assert.Null(c.Engine);
        Assert.Equal(journal, await rig.CountAsync("pev2_execution_journal"));
        Assert.Equal(0L, await rig.CountAsync("pev2_events", "kind = 'EntryFilled'"));

        await ExecAsync(rig.Db, "DROP TRIGGER refuse ON pev2.pev2_events");
        rig.Advance(1);
        await rig.CycleAsync(c);
        Assert.Equal(1L, await rig.CountAsync("pev2_events", "kind = 'EntryFilled'"));
        Assert.True(c.Snapshot!.Reconciled);
    }

    [PostgresFact]
    public async Task StaleObserver_FlattensWithoutAnyBar_OnAQuoteNewerThanTheDecision()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, alerts) = rig.New();
        await rig.HoldAsync(c);

        rig.Advance(40);
        await rig.CycleAsync(c, beat: false);
        Assert.Equal(NiftySignal.PaperEngineV2.Execution.ExitReason.Protective, Assert.Single(c.Engine!.Events.OfType<ExitRequested>()).Reason);
        Assert.Empty(c.Engine.Events.OfType<ExitFilled>());
        Assert.Contains(alerts.Active, a => a.Contains("protective flatten"));

        rig.Advance(1);
        await rig.CycleAsync(c, beat: false);
        Assert.Single(c.Engine.Events.OfType<ExitFilled>());
        Assert.Equal(1L, await rig.CountAsync("pev2_events", "kind = 'ExitFilled'"));
    }

    [PostgresFact]
    public async Task ProtectiveExitWithoutQuotes_StaysOpen_AndIsAlerted()
    {
        await using var rig = await Rig.CreateAsync();
        var (c, alerts) = rig.New();
        await rig.HoldAsync(c);
        for (var i = 0; i < 3; i++)
        {
            rig.Advance(40);
            await rig.CycleAsync(c, quotes: false, beat: false);
        }

        Assert.Empty(c.Engine!.Events.OfType<ExitFilled>());
        Assert.Contains(alerts.Active, a => a.Contains("NOT filled"));
        Assert.Equal("Protecting", c.Snapshot!.Status);
    }

    [PostgresFact]
    public async Task LedgerThatDoesNotMatchTheJournal_BlocksEntries_ButExitsContinue()
    {
        await using var rig = await Rig.CreateAsync();
        var (first, _) = rig.New();
        await rig.HoldAsync(first);
        await first.DisposeAsync();
        await ExecAsync(rig.Db, "DELETE FROM pev2.pev2_events WHERE kind = 'EntryFilled'");

        var (second, alerts) = rig.New();
        rig.Advance(2);
        await rig.CycleAsync(second);
        Assert.False(second.Snapshot!.Reconciled);
        Assert.Contains("not reconciled", second.Snapshot.EntryBlock);
        Assert.Contains(alerts.Active, a => a.Contains("reconciliation failed"));

        rig.Advance(40);
        await rig.CycleAsync(second, beat: false);
        rig.Advance(1);
        await rig.CycleAsync(second, beat: false);
        Assert.Single(second.Engine!.Events.OfType<ExitFilled>());
    }

    [PostgresFact]
    public async Task RestartWhileTheObserverIsUnreadable_WaitsVisibly_ThenRecovers()
    {
        await using var rig = await Rig.CreateAsync();
        var (first, _) = rig.New();
        await rig.HoldAsync(first);
        await first.DisposeAsync();

        var (second, alerts) = rig.New();
        rig.Advance(2);
        rig.Feed.Observer = rig.Feed.Observer with { ReadFailed = true };
        await second.RunCycleAsync(default);
        Assert.Null(second.Engine);
        Assert.Equal("Recovering", second.Snapshot!.Status);
        Assert.Contains(alerts.Active, a => a.Contains("cannot be rebuilt"));

        rig.Feed.Observer = rig.Feed.Observer with { ReadFailed = false };
        rig.Advance(2);
        await rig.CycleAsync(second);
        Assert.True(second.Snapshot!.Reconciled);
        Assert.Single(second.Snapshot.Positions, p => p.State == PositionState.Open);
    }
}

/// <summary>The Observer's bar publication on a disposable local PostgreSQL database (skipped without <c>NSE_ADAPTIVE_MIGRATION_TEST_DB</c>).</summary>
public class BarPublicationTests
{
    [PgMigrationFact]
    public async Task Publication_IsImmutable_AndRecoveredBarsArePublishedOnceWithTheUnverifiedBoundary()
    {
        await using var temp = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_obs_test_");
        await temp.MigrateAsync();
        string sessions, bars;
        await using (var db = temp.Context())
        {
            sessions = db.Model.FindEntityType(typeof(AdaptiveSessionStateRow))!.GetTableName()!;
            bars = db.Model.FindEntityType(typeof(AdaptiveFutureBarRow))!.GetTableName()!;
        }

        await temp.InsertFilledAsync(sessions, new Dictionary<string, object> { ["TradeDate"] = FrameFixtures.Day });
        var sessionId = await temp.ScalarAsync($"SELECT min(\"Id\") FROM {sessions}");
        for (var seq = 1; seq <= 4; seq++) await temp.InsertFilledAsync(bars, new Dictionary<string, object> { ["SessionId"] = sessionId, ["BarSeq"] = seq });

        await using var context = temp.Context();
        await AdaptiveBarPublisher.PublishAsync(context, sessionId, 1, verified: false, FrameFixtures.End(1), default);
        await AdaptiveBarPublisher.PublishAsync(context, sessionId, 1, verified: true, FrameFixtures.End(2), default);
        Assert.Equal(3, await AdaptiveBarPublisher.PublishMissingAsync(context, sessionId, unverifiedFromBar: 4, FrameFixtures.End(5), default));
        Assert.Equal(0, await AdaptiveBarPublisher.PublishMissingAsync(context, sessionId, null, FrameFixtures.End(6), default));

        var rows = await context.BarPublications.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Equal([(1, false), (2, true), (3, true), (4, false)], rows.Select(r => (r.BarSeq, r.Verified)));
        Assert.All(rows.Where(r => !r.Verified), r => Assert.Equal(AdaptiveBarPublisher.UnknownIntegrity, r.UnverifiedReason));
    }
}

public sealed class DisabledPaperPathTests
{
    [Fact]
    public void TheHostRegistersNeitherPaperBridge_UnlessItIsEnabled_AndTheObserverTreatsBothAsOptional()
    {
        var host = Path.Combine(NiftySignal.Tests.Trading.Data.TradingModelTests.RepoRoot(), "NiftySignal.Host");
        var program = File.ReadAllText(Path.Combine(host, "Program.cs")).Replace("\r\n", "\n");
        Assert.Matches(@"PaperLiveOptions\.Bind\(builder\.Configuration\)\.Enabled\)\n\s+\{\n\s+builder\.Services\.AddSingleton<NiftySignal\.Host\.PaperLive\.PaperLiveBridge>", program);
        Assert.Matches(@"GetValue<bool>\(""PaperEngineV2:Enabled""\)\)\n\s+\{[^}]*AddSingleton<NiftySignal\.Host\.PaperEngineV2\.PaperEngineV2Bridge>", program);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(program, @"AddSingleton<IAdaptiveLiveSink>").Count);      // one per bridge, inside the same condition
        var worker = File.ReadAllText(Path.Combine(host, "..", "NiftySignal.ObserverRuntime", "AdaptiveObserverWorker.cs"));
        Assert.Contains("IEnumerable<IAdaptiveLiveSink>? sinks = null", worker);                  // with no bridge registered there is nothing to call
        Assert.DoesNotContain("PaperEngineV2", worker);
    }
}

/// <summary>The strategy files must ship beside the service binaries: the configured directory is resolved against the exe's folder.</summary>
public sealed class ExecutionServicePackagingTests
{
    [Fact]
    public void The_shipped_strategy_directory_is_copied_to_the_output_and_loads_four_enabled_strategies()
    {
        var options = new NiftySignal.ExecutionService.ExecutionServiceOptions
        {
            Enabled = true, StrategyDirectory = "paper-engine-v2-strategies/execution-service", CostScheduleVersion = "npt1-frozen-fees-v1", BrokeragePerOrder = 0m,
        };

        var setup = NiftySignal.ExecutionService.ExecutionSetup.Load(options, AppContext.BaseDirectory);

        Assert.Equal(["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"], setup.Strategies.Select(s => s.Config.Identity.StrategyId).Order(StringComparer.Ordinal));
        Assert.Equal("TOTAL_VOTE_4_OF_6", Assert.Single(setup.Disabled).Identity.StrategyId);
    }
}
