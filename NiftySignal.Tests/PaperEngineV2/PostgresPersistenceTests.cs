using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Phase 6d on a real local PostgreSQL (the fixed <c>niftysignal_trading_test</c> database, loopback only, skipped without <c>NSE_TRADING_TEST_DB</c>): the reviewed owner-run script, the schema compatibility check, the durable
/// journal, the idempotent ledger and restart / crash recovery by deterministic replay. Only the <c>pev2</c> schema is dropped and recreated; the trading.* tables are never touched.
/// </summary>
[Collection("PostgresTradingTests")]
public class PostgresPersistenceTests
{
    static string Script() => File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql"));

    static NpgsqlDataSource Source() => NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);

    static async Task ExecAsync(NpgsqlDataSource db, string sql)
    {
        await using var c = db.CreateCommand(sql);
        await c.ExecuteNonQueryAsync();
    }

    /// <summary>Drops only the pev2 schema and installs it with the reviewed owner script.</summary>
    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = Source();
        await ExecAsync(db, "DROP SCHEMA IF EXISTS pev2 CASCADE");
        await ExecAsync(db, Script());
        return db;
    }

    static async Task<string> ObjectsAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand("SELECT string_agg(x, ';' ORDER BY x) FROM (SELECT 't:' || tablename AS x FROM pg_tables WHERE schemaname = 'pev2' UNION ALL SELECT 'i:' || indexname FROM pg_indexes WHERE schemaname = 'pev2' UNION ALL SELECT 'c:' || table_name || '.' || column_name || ':' || data_type FROM information_schema.columns WHERE table_schema = 'pev2') q");
        return (string)(await c.ExecuteScalarAsync())!;
    }

    static async Task<long> TradingTableCountAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand("SELECT count(*) FROM pg_tables WHERE schemaname = 'trading'");
        return (long)(await c.ExecuteScalarAsync())!;
    }

    // ---- schema, script, compatibility ----

    [PostgresFact]
    public async Task OwnerScript_CreatesTheSchema_CanRunTwice_AndMatchesTheMigration_WithoutTouchingTradingTables()
    {
        await PostgresTestDatabase.ResetAsync();                                    // makes sure the trading schema exists, so "untouched" is meaningful
        await using var db = Source();
        var tradingBefore = await TradingTableCountAsync(db);
        Assert.True(tradingBefore > 0);

        await ExecAsync(db, "DROP SCHEMA IF EXISTS pev2 CASCADE");
        await ExecAsync(db, Script());
        await ExecAsync(db, Script());                                              // idempotent
        var viaScript = await ObjectsAsync(db);
        Assert.Contains("t:pev2_frames", viaScript);
        Assert.Contains("t:pev2_events", viaScript);

        await ExecAsync(db, "DROP SCHEMA pev2 CASCADE");
        await using (var ctx = new PaperEngineV2DbContext(new DbContextOptionsBuilder<PaperEngineV2DbContext>()
                         .UseNpgsql(PostgresTestDatabase.ConnectionString!, n => n.MigrationsHistoryTable(PaperEngineV2DbContext.HistoryTable, V2Schema.SchemaName)).Options))
            await ctx.Database.MigrateAsync();
        Assert.Equal(viaScript.Replace(";t:__pev2_migrations_history", string.Empty, StringComparison.Ordinal), (await ObjectsAsync(db)).Replace(";t:__pev2_migrations_history", string.Empty, StringComparison.Ordinal));

        Assert.Equal(tradingBefore, await TradingTableCountAsync(db));
        await ExecAsync(db, "DROP SCHEMA pev2 CASCADE");
        await ExecAsync(db, Script());
        Assert.True((await V2SchemaCheck.CheckAsync(db, default)).Compatible);
    }

    [PostgresFact]
    public async Task SchemaCheck_FailsClosed_ForMissingOlderNewerOrForeignSchemas_AndUnreachableDatabases()
    {
        var db = await FreshAsync();
        Assert.True((await V2SchemaCheck.CheckAsync(db, default)).Compatible);

        await ExecAsync(db, "UPDATE pev2.pev2_schema_versions SET schema_version = 3");
        Assert.Contains("newer", Assert.Single((await V2SchemaCheck.CheckAsync(db, default)).Problems));
        await ExecAsync(db, "UPDATE pev2.pev2_schema_versions SET schema_version = 0");
        Assert.Contains("older", Assert.Single((await V2SchemaCheck.CheckAsync(db, default)).Problems));
        await ExecAsync(db, "UPDATE pev2.pev2_schema_versions SET schema_version = 2, journal_schema = 'other/9'");
        Assert.Contains("journal schema", Assert.Single((await V2SchemaCheck.CheckAsync(db, default)).Problems));
        await ExecAsync(db, "DELETE FROM pev2.pev2_schema_versions");
        Assert.False((await V2SchemaCheck.CheckAsync(db, default)).Compatible);

        await ExecAsync(db, "DROP SCHEMA pev2 CASCADE");
        Assert.False((await V2SchemaCheck.CheckAsync(db, default)).Compatible);       // never created implicitly
        await using var check = db.CreateCommand("SELECT count(*) FROM pg_namespace WHERE nspname = 'pev2'");
        Assert.Equal(0L, await check.ExecuteScalarAsync());

        await using var dead = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        Assert.False((await V2SchemaCheck.CheckAsync(dead, default)).Compatible);
        await ExecAsync(db, Script());
    }

    // ---- journal ----

    [PostgresFact]
    public async Task Journal_RoundTrips_IsIdempotent_AndNeverOverwrites()
    {
        var db = await FreshAsync();
        var j = new PostgresFrameJournal(db);
        Assert.True(j.IsDurable);
        var f1 = FrameRecord.From(FrameFixtures.Light(7, 1));
        var f2 = FrameRecord.From(FrameFixtures.Light(7, 2));
        Assert.Equal(AppendOutcome.Appended, await j.AppendFrameAsync(f1, default));
        Assert.Equal(AppendOutcome.Appended, await j.AppendFrameAsync(f2, default));
        Assert.Equal(AppendOutcome.DuplicateSame, await j.AppendFrameAsync(f1, default));
        Assert.Equal(AppendOutcome.ConflictDifferent, await j.AppendFrameAsync(f1 with { FrameHash = new string('a', 64) }, default));
        var d = new DispositionRecord(FrameRecord.CurrentSchema, 7, 1, true, IntegrityFlags.None, 12, 10, true, false);
        Assert.Equal(AppendOutcome.Appended, await j.AppendDispositionAsync(d, default));
        Assert.Equal(AppendOutcome.DuplicateSame, await j.AppendDispositionAsync(d, default));
        Assert.Equal(AppendOutcome.ConflictDifferent, await j.AppendDispositionAsync(d with { EntriesAllowed = false }, default));
        var g = new GapRecord(FrameRecord.CurrentSchema, 7, 3, 5, GapCause.Overflow, IntegrityFlags.Overflow, true);
        await j.AppendGapAsync(g, default);
        await j.AppendGapAsync(g, default);

        var s = await j.ReadSessionAsync(7, default);
        Assert.Equal(2, s.Frames.Count);
        Assert.Equal(f1 with { JournalSeq = s.Frames[0].JournalSeq }, s.Frames[0]);
        Assert.Equal(f2.PayloadJson, s.Frames[1].PayloadJson);
        Assert.True(s.Frames[0].JournalSeq < s.Frames[1].JournalSeq);
        Assert.Equal(d with { JournalSeq = s.Dispositions[0].JournalSeq }, Assert.Single(s.Dispositions));
        var gap = Assert.Single(s.Gaps);
        Assert.True(gap.Durable);
        Assert.Equal((3, 5, GapCause.Overflow), (gap.FromBarSeq, gap.ToBarSeq, gap.Cause));
        Assert.Empty((await j.ReadSessionAsync(8, default)).Frames);
    }

    [PostgresFact]
    public async Task Journal_Rejects_RowsThatBreakTheConstraints()
    {
        var db = await FreshAsync();
        var j = new PostgresFrameJournal(db);
        await Assert.ThrowsAsync<PostgresException>(async () => await j.AppendFrameAsync(FrameRecord.From(FrameFixtures.Light(1, 1)) with { FrameHash = "NOT-A-HASH" }, default));
    }

    // ---- ledger ----

    [Fact]
    public void EventKinds_EqualTheirTypeNames_AndEveryKindRoundTrips()
    {
        var t = new DateTimeOffset(2026, 10, 7, 4, 5, 0, TimeSpan.Zero);
        var key = new DecisionKey(t, 99);
        PaperEvent[] events =
        [
            new EntryRejected("e1", "S01", "h", 5, t, OptionRight.Call, "i", RejectionReason.SpreadTooWide, "d"),
            new EntryRejected("e2", "S01", "h", 5, t, null, null, RejectionReason.ConflictingSignals, "d"),
            new EntryOrderPlaced("e3", "S01", "h", 5, t, "i", OptionRight.Put, "PE1", 26000.5, new DateOnly(2026, 10, 13), 65, 650, 126.05m, key, t.AddSeconds(60)),
            new EntryFilled("e4", "S01", "h", 6, t, "i", "p", OptionRight.Call, "CE1", 650, 127.5m, 33.37m, t, 12, 650),
            new ExitRequested("e5", "S01", "h", 7, t, "p", ExitReason.StopLoss, key),
            new ExitFilled("e6", "S01", "h", 8, t, "p", ExitReason.Terminal, 650, 120.45m, 20.1m, -4553.25m, -4600.55m, t, 13, 1300, 1200.5m, -5100m),
            new PositionUnresolved("e7", "S01", "h", 9, t, "p", "detail"),
            new RiskBreached("e8", "S01", "h", 9, t, -21000.55m, 20000m),
            new StrategyFaulted("e9", "S01", "h", 9, t, "boom"),
        ];
        foreach (var e in events)
        {
            Assert.Equal(e.GetType().Name, e.Kind);
            Assert.Equal(e, EventSerializer.Deserialize(e.Kind, EventSerializer.Serialize(e)));
        }

        Assert.Throws<InvalidOperationException>(() => EventSerializer.Deserialize("Nope", "{}"));
    }

    [PostgresFact]
    public async Task EventStore_IsIdempotent_CountsConflicts_AndBoundsReads()
    {
        var db = await FreshAsync();
        var store = new PostgresEventStore(db);
        var t = new DateTimeOffset(2026, 10, 7, 4, 5, 0, TimeSpan.Zero);
        var e = new RiskBreached("ev-1", "S01", "h", 9, t, -21000.55m, 20000m);
        store.Append(e); store.Append(e);
        Assert.Equal((1L, 0L, 0L), (store.Written, store.Conflicts, store.Failures));
        store.Append(e with { RealizedNetToday = -1m });                              // same id, different payload: never overwritten
        Assert.Equal(1L, store.Conflicts);
        Assert.False(store.IsComplete);
        var read = await store.ReadAsync(t, t.AddMinutes(1), "S01", 10, default);
        Assert.Equal(e, Assert.Single(read));
        Assert.Empty(await store.ReadAsync(t, t.AddMinutes(1), "S02", 10, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadAsync(t, t.AddMinutes(1), null, PostgresEventStore.MaxRead + 1, default));
    }

    [PostgresFact]
    public async Task EventStore_Failure_IsCounted_AndTheEngineContainsIt()
    {
        var db = await FreshAsync();
        await ExecAsync(db, "DROP SCHEMA pev2 CASCADE");                              // the table is gone: every append fails
        var store = new PostgresEventStore(db);
        var engine = ExecFixtures.Engine([ExecFixtures.Strat()], sink: store);
        ExecFixtures.Run(engine, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Single(ExecFixtures.Of<EntryOrderPlaced>(engine));                   // the strategy is unaffected
        Assert.Equal(1L, store.Failures);
        Assert.Equal(1L, engine.SinkFailures);
        Assert.False(store.IsComplete);
        await ExecAsync(db, Script());
    }

    // ---- restart and crash recovery ----

    static CompiledStrategy[] Strategies() => [ExecFixtures.Strat("S01"), ExecFixtures.Strat("S02", o => o["position"]!["maxConcurrent"] = 2)];

    static async Task<(PaperEngine Engine, FrameConsumer Consumer)> RunLiveAsync(NpgsqlDataSource db, PostgresEventStore store, IEnumerable<MarketStateFrame> frames)
    {
        var engine = new PaperEngine(Strategies(), ExecFixtures.Options(), store);
        var clock = new ManualClock(FrameFixtures.End(1));
        var consumer = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), new PostgresFrameJournal(db), engine, clock);
        foreach (var f in frames) { clock.Now = f.Identity.BarEndAvailableAt.AddMilliseconds(50); await consumer.HandleAsync(f, default); }
        return (engine, consumer);
    }

    [PostgresFact]
    public async Task Restart_ReplaysTheDurableJournal_ToTheIdenticalLedger_AndStateAndContinues()
    {
        var db = await FreshAsync();
        var store = new PostgresEventStore(db);
        var frames = ExecutionTests.Day();
        var (live, liveConsumer) = await RunLiveAsync(db, store, frames.Take(40));
        Assert.NotEmpty(live.Events);
        Assert.True(liveConsumer.Status.ClaimsDurableRecording);
        var writtenBefore = store.Written;

        // "restart": brand-new objects, nothing in memory, recover from the database
        var store2 = new PostgresEventStore(db);
        var engine2 = new PaperEngine(Strategies(), ExecFixtures.Options(), store2);
        var clock2 = new ManualClock(FrameFixtures.End(41));
        var consumer2 = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), new PostgresFrameJournal(db), engine2, clock2);
        await consumer2.RecoverFromJournalAsync(1, default);
        Assert.Equal(live.Events, engine2.Events);
        Assert.Equal(live.Runtimes.Select(r => (r.StrategyId, r.Cash, r.RealizedToday)), engine2.Runtimes.Select(r => (r.StrategyId, r.Cash, r.RealizedToday)));
        Assert.Equal(live.Runtimes.SelectMany(r => r.Positions()), engine2.Runtimes.SelectMany(r => r.Positions()));
        Assert.Equal((0L, 0L, 0L), (store2.Written, store2.Conflicts, store2.Failures));    // nothing duplicated, nothing different
        var from = frames[0].Identity.BarEndAvailableAt.AddMinutes(-1);
        var to = frames[^1].Identity.BarEndAvailableAt.AddMinutes(1);
        Assert.True((await LedgerReconciler.ReconcileAsync(engine2.Events, store2, from, to, default)).Clean);
        Assert.True(writtenBefore > 0);

        // after recovery the run continues; recovery blocks entries until the clean run is rebuilt (FramesToClearGap = 1)
        foreach (var f in frames.Skip(40))
        {
            clock2.Now = f.Identity.BarEndAvailableAt.AddMilliseconds(50);
            await consumer2.HandleAsync(f, default);
        }

        Assert.True(consumer2.Status.Processed >= 60);
        Assert.Equal(0L, store2.Conflicts);
    }

    [PostgresFact]
    public async Task Crash_BetweenJournalAndLedger_IsRepairedByReplay_AndDriftIsReported()
    {
        var db = await FreshAsync();
        var store = new PostgresEventStore(db);
        var frames = ExecutionTests.Day();
        var (live, _) = await RunLiveAsync(db, store, frames);
        var from = frames[0].Identity.BarEndAvailableAt.AddMinutes(-1);
        var to = frames[^1].Identity.BarEndAvailableAt.AddMinutes(1);
        Assert.True((await LedgerReconciler.ReconcileAsync(live.Events, store, from, to, default)).Clean);

        // crash window: the journal is complete but the last ledger rows never reached the database
        await ExecAsync(db, "DELETE FROM pev2.pev2_events WHERE event_id IN (SELECT event_id FROM pev2.pev2_events ORDER BY at_utc_ticks DESC, event_id DESC LIMIT 3)");
        var damaged = await LedgerReconciler.ReconcileAsync(live.Events, store, from, to, default);
        Assert.Equal(3, damaged.MissingInStore);

        var store2 = new PostgresEventStore(db);
        var engine2 = new PaperEngine(Strategies(), ExecFixtures.Options(), store2);
        var consumer2 = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), new PostgresFrameJournal(db), engine2, new ManualClock(FrameFixtures.End(61)));
        await consumer2.RecoverFromJournalAsync(1, default);
        Assert.Equal(live.Events, engine2.Events);
        Assert.Equal(3L, store2.Written);                                            // exactly the missing rows were re-appended
        Assert.True((await LedgerReconciler.ReconcileAsync(engine2.Events, store2, from, to, default)).Clean);

        // a stored event the replay cannot reproduce is reported, never deleted
        store2.Append(new RiskBreached("ghost", "S01", "h", 9, frames[10].Identity.BarEndAvailableAt, -1m, 1m));
        Assert.Equal(1, (await LedgerReconciler.ReconcileAsync(engine2.Events, store2, from, to, default)).ExtraInStore);
    }

    [PostgresFact]
    public async Task UnavailableJournal_BlocksEntries_AndNeverClaimsDurability()
    {
        var db = await FreshAsync();
        await ExecAsync(db, "DROP SCHEMA pev2 CASCADE");                              // the journal cannot write
        var engine = new PaperEngine(Strategies(), ExecFixtures.Options());
        var consumer = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), new PostgresFrameJournal(db), engine, new ManualClock(FrameFixtures.End(1)));
        await consumer.HandleAsync(ExecFixtures.Fr(5, dir: 10), default);
        Assert.True(consumer.Status.JournalFaulted);
        Assert.False(consumer.Status.ClaimsDurableRecording);
        Assert.Empty(ExecFixtures.Of<EntryOrderPlaced>(engine));                    // fail closed: no entry without a journal
        await ExecAsync(db, Script());
    }

    // ---- release blockers ----

    [Fact]
    public void DataProject_NeverMigratesImplicitly_AndHasNoBrokerOrDashboardReference()
    {
        var root = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(".Migrate", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnsureCreated", text, StringComparison.Ordinal);
        }

        var refs = typeof(PostgresEventStore).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("NiftySignal", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
        Assert.Equal(["NiftySignal.PaperEngineV2.Core", "NiftySignal.PaperEngineV2.Execution"], refs);
        var sql = File.ReadAllText(Path.Combine(root, "Sql", "pev2-schema.idempotent.sql"));
        Assert.DoesNotContain("trading.", sql, StringComparison.Ordinal);
        foreach (var banned in new[] { "DROP TABLE", "ALTER TABLE", "TRUNCATE", "DELETE FROM" })
            Assert.DoesNotContain(banned, sql, StringComparison.OrdinalIgnoreCase);
    }
}
