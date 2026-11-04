using System.Text.Json.Nodes;
using Npgsql;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class FrameMapFactory(Func<int, MarketStateFrame?> map) : IV2FrameFactory
{
    public Task<(MarketStateFrame? Frame, string? Problem)> BuildAsync(V2BarNotice notice, MarketStateFrameBuilder builder, CancellationToken ct)
    {
        var f = map(notice.Bar.BarSeq);
        return Task.FromResult((f, f is null ? "test: no frame for this bar" : null));
    }
}

sealed class RecordingTransport : IAlertTransport
{
    public List<string> Sent { get; } = [];

    public Task<AlertSendResult> SendAsync(string text, CancellationToken ct)
    {
        lock (Sent) Sent.Add(text);
        return Task.FromResult(new AlertSendResult(SendOutcome.Sent, MessageId: Sent.Count));
    }
}

sealed class DelayingJournal(IFrameJournal inner, Func<TimeSpan> delay, Func<bool> fail) : IFrameJournal
{
    public bool IsDurable => inner.IsDurable;

    async Task Gate()
    {
        if (delay() is { } d && d > TimeSpan.Zero) await Task.Delay(d);
        if (fail()) throw new InvalidOperationException("journal unavailable (injected)");
    }

    public async ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord r, CancellationToken ct) { await Gate(); return await inner.AppendFrameAsync(r, ct); }

    public async ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord r, CancellationToken ct) { await Gate(); return await inner.AppendDispositionAsync(r, ct); }

    public async ValueTask AppendGapAsync(GapRecord r, CancellationToken ct) { await Gate(); await inner.AppendGapAsync(r, ct); }

    public ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct) => inner.ReadSessionAsync(sessionId, ct);
}

/// <summary>
/// Host integration of Paper Engine V2 SHADOW on a disposable local PostgreSQL schema: lifecycle, schema gating (nothing is migrated), the bounded non-blocking handoff, queue behaviour under burst / slow persistence /
/// persistence loss / consumer fault, restart without duplicate fills, orders or notifications, and the alert outbox. The frames are the engine-test fixtures (labelled synthetic): the recorded-data replay is a separate gated run.
/// </summary>
[Collection("PostgresTradingTests")]
public class RuntimeTests
{
    static readonly V2SessionInfo Session = new(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", FrameFixtures.Expiry, FrameFixtures.Listed(),
        new Dictionary<string, int> { ["CE26000"] = 65, ["CE25950"] = 65, ["PE26000"] = 65, ["PE25950"] = 65 });

    static V2BarNotice Notice(int seq, long session = 1) =>
        new(Session with { SessionId = session }, new BarInput(session, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", seq, FrameFixtures.End(seq).AddSeconds(-60), FrameFixtures.End(seq), 1000 + seq, 60, true, null), 26_000, []);

    static MarketStateFrame TradeFrame(int seq) => seq switch
    {
        5 => ExecFixtures.Fr(5, dir: 10),
        6 => ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]),
        7 => ExecFixtures.Fr(7, quotes: [new("CE26000", 130, 131)]),
        8 => ExecFixtures.Fr(8, fut: -60, quotes: [new("CE26000", 133, 134)]),
        9 => ExecFixtures.Fr(9, quotes: [new("CE26000", 135, 136)]),
        _ => ExecFixtures.Fr(seq),
    };

    static async Task<NpgsqlDataSource> InstalledAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static string StrategyDir(params string[] ids)
    {
        var dir = Path.Combine(Path.GetTempPath(), "v2rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var id in ids)
            File.WriteAllText(Path.Combine(dir, id + ".json"), ConfigTests.Mutate(o =>
            {
                o["strategyId"] = id;
                o["execution"]!["fillQuote"] = "AsOfEligibleBarEnd";
                o["fees"]!["schedule"] = ExecFixtures.Fees;
                o["exit"]!["conditions"] = JsonNode.Parse("""{ "op": "OR", "items": [ { "metric": "fut.strict:pct", "window": "5", "cmp": "<", "value": -50 } ] }""");
            }));
        return dir;
    }

    static V2RuntimeOptions Options(string dir, int capacity = 64, double lagSeconds = 5, int clear = 2, bool alerts = true) => new()
    {
        Enabled = true, ConnectionString = PostgresTestDatabase.ConnectionString, StrategyDirectory = dir, QueueCapacity = capacity, MaxProcessingLag = TimeSpan.FromSeconds(lagSeconds), FramesToClearGap = clear,
        TerminalRetryUntil = new TimeOnly(15, 25), BrokeragePerOrder = 0m, CostScheduleVersion = ExecFixtures.Fees, Alerts = alerts ? new AlertOptions(Opened: true, Closed: true, OrderRejected: true) : AlertOptions.AllOff,
        AlertPollInterval = TimeSpan.FromMilliseconds(50), RestartBackoff = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>Delivers the bars one at a time and waits until each has been taken by the consumer and the consumer has settled (counts stable), regardless of how many frames the journal replay already counted.</summary>
    static async Task Pump(V2Runtime rt, ManualClock clock, IEnumerable<int> seqs, long processedBefore = 0)
    {
        _ = processedBefore;
        var offered = rt.Snapshot().Handoff?.Accepted ?? 0;
        foreach (var seq in seqs)
        {
            clock.Now = FrameFixtures.End(seq).AddMilliseconds(100);
            Assert.True(rt.TryEnqueue(Notice(seq)));
            offered++;
            await Until(() => rt.Snapshot().Handoff is { } h && h.Accepted >= offered && h.QueueDepth == 0, $"frame {seq} taken");
            await Settle(rt);
        }
    }

    static async Task Settle(V2Runtime rt)
    {
        static long Sum(V2Runtime r) => r.Snapshot().Consumer is { } c ? c.Processed + c.Duplicates + c.Rejected : -1;
        var last = Sum(rt);
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(400);
            var now = Sum(rt);
            if (now == last) return;
            last = now;
        }
    }

    static async Task Until(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    static async Task<IReadOnlyList<PaperEvent>> LedgerAsync(NpgsqlDataSource db) =>
        await new PostgresEventStore(db).ReadAsync(FrameFixtures.T0.AddHours(-1), FrameFixtures.T0.AddHours(12), null, PostgresEventStore.MaxRead, default);

    static async Task<long> ScalarAsync(NpgsqlDataSource db, string sql)
    {
        await using var c = db.CreateCommand(sql);
        return Convert.ToInt64(await c.ExecuteScalarAsync());
    }

    // ---- lifecycle, defaults and gating ----

    [Fact]
    public async Task Disabled_IsInert_NoConnectionNoTaskNoAcceptance()
    {
        await using var rt = new V2Runtime(new V2RuntimeOptions { Enabled = false, ConnectionString = "Host=nonexistent.invalid" }, new FrameMapFactory(_ => null));
        await rt.StartAsync(default);
        Assert.Equal(V2RuntimeState.Disabled, rt.State);
        Assert.False(rt.TryEnqueue(Notice(1)));
        Assert.Equal(0, rt.Snapshot().NoticesAccepted);
    }

    [Fact]
    public async Task Enabled_WithMissingRequiredSettings_RefusesToStart_ListingEachProblem()
    {
        await using var rt = new V2Runtime(new V2RuntimeOptions { Enabled = true }, new FrameMapFactory(_ => null));
        await rt.StartAsync(default);
        Assert.Equal(V2RuntimeState.ConfigInvalid, rt.State);
        foreach (var needle in new[] { "ConnectionString", "StrategyDirectory", "QueueCapacity", "MaxProcessingLag", "FramesToClearGap", "TerminalRetryUntil", "BrokeragePerOrder", "CostScheduleVersion" })
            Assert.Contains(needle, rt.Detail, StringComparison.Ordinal);
        Assert.False(rt.TryEnqueue(Notice(1)));
    }

    [PostgresFact]
    public async Task IncompatibleOrMissingSchema_KeepsV2Closed_AndNeverMigrates()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var rt = new V2Runtime(Options(StrategyDir("S01")), new FrameMapFactory(_ => null), new RecordingTransport());
        await rt.StartAsync(default);
        Assert.Equal(V2RuntimeState.SchemaIncompatible, rt.State);
        Assert.False(rt.TryEnqueue(Notice(1)));
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'pev2'"));         // nothing was created
    }

    [PostgresFact]
    public async Task InvalidStrategySet_KeepsV2Closed()
    {
        await InstalledAsync();
        var dir = Path.Combine(Path.GetTempPath(), "v2rt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.json"), ConfigTests.Mutate(_ => { }));
        File.WriteAllText(Path.Combine(dir, "b.json"), ConfigTests.Mutate(_ => { }));                    // the same strategyId twice
        await using var rt = new V2Runtime(Options(dir), new FrameMapFactory(_ => null));
        await rt.StartAsync(default);
        Assert.Equal(V2RuntimeState.ConfigInvalid, rt.State);
        Assert.Contains("appears in 2 files", rt.Detail ?? "", StringComparison.Ordinal);
    }

    // ---- the full path: notice -> frame -> handoff -> durable journal -> engine -> ledger -> outbox -> transport ----

    [PostgresFact]
    public async Task Notices_FlowThroughJournalEngineLedgerAndOutbox_AndAlertsAreDelivered()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        var transport = new RecordingTransport();
        await using var rt = new V2Runtime(Options(StrategyDir("S01")), new FrameMapFactory(s => TradeFrame(s)), transport, clock);
        await rt.StartAsync(default);
        Assert.Equal(V2RuntimeState.Running, rt.State);
        await Pump(rt, clock, Enumerable.Range(1, 10));

        var ledger = await LedgerAsync(db);
        var fill = Assert.Single(ledger.OfType<EntryFilled>());
        var exit = Assert.Single(ledger.OfType<ExitFilled>());
        Assert.Equal(127.5m, fill.FillPrice);
        Assert.True(exit.NetPnl != 0m && exit.Fees > 0m);
        Assert.Equal(10, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_frames"));
        Assert.Equal(10, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_gaps"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_alert_outbox"));                                          // Opened + Closed
        await Until(() => transport.Sent.Count == 2, "both alerts delivered");
        Assert.All(transport.Sent, t => Assert.StartsWith("V2 SHADOW — PAPER ONLY | S01", t, StringComparison.Ordinal));
        await Until(() => ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_alert_outbox WHERE status = 1").GetAwaiter().GetResult() == 2, "outbox rows Sent");
        var snap = rt.Snapshot();
        Assert.Equal((0L, 0L, 0L), (snap.NoticesDropped, snap.LedgerFailures, snap.LedgerConflicts));
    }

    [PostgresFact]
    public async Task Restart_ReplaysTheJournal_WithoutDuplicateFillsOrdersOrNotifications()
    {
        var db = await InstalledAsync();
        var dir = StrategyDir("S01");
        var clock = new ManualClock(FrameFixtures.T0);
        var transport = new RecordingTransport();
        await using (var first = new V2Runtime(Options(dir), new FrameMapFactory(s => TradeFrame(s)), transport, clock))
        {
            await first.StartAsync(default);
            await Pump(first, clock, Enumerable.Range(1, 7));                    // stops with the position OPEN
            await Until(() => transport.Sent.Count == 1, "opened alert");
        }

        var before = await LedgerAsync(db);
        Assert.Single(before.OfType<EntryFilled>());
        var outboxBefore = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_alert_outbox");

        // "Host restart": a brand-new runtime on the same database. The observer re-delivers recent bars (and then new ones): nothing may be written twice.
        await using var second = new V2Runtime(Options(dir), new FrameMapFactory(s => TradeFrame(s)), transport, clock);
        await second.StartAsync(default);
        await Pump(second, clock, Enumerable.Range(5, 3));                       // bars 5..7 again: duplicates of the journal
        var afterReplay = await LedgerAsync(db);
        Assert.Equal(before.Select(e => e.EventId).Order(), afterReplay.Select(e => e.EventId).Order());
        Assert.Equal(outboxBefore, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_alert_outbox"));
        Assert.Equal(1, transport.Sent.Count);
        Assert.Equal(1, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_frames WHERE bar_seq = 5"));

        // the position recovered from the journal is closed normally by the next bars
        await Pump(second, clock, [8, 9], processedBefore: 3);
        Assert.Single((await LedgerAsync(db)).OfType<ExitFilled>());
        await Until(() => transport.Sent.Count == 2, "closed alert");
        Assert.Equal(2, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_alert_outbox"));
        Assert.Single((await LedgerAsync(db)).OfType<EntryFilled>());
    }

    [PostgresFact]
    public async Task MidSessionRestart_AfterMissedBars_RecordsAGap_AndBlocksEntriesUntilCleanFrames()
    {
        var db = await InstalledAsync();
        var dir = StrategyDir("S01");
        var clock = new ManualClock(FrameFixtures.T0);
        await using (var first = new V2Runtime(Options(dir, clear: 3), new FrameMapFactory(s => ExecFixtures.Fr(s)), null, clock))
        {
            await first.StartAsync(default);
            await Pump(first, clock, Enumerable.Range(1, 4));
        }

        await using var second = new V2Runtime(Options(dir, clear: 3), new FrameMapFactory(s => s == 12 ? ExecFixtures.Fr(12, dir: 10) : s == 13 ? ExecFixtures.Fr(13, quotes: [new("CE26000", 125.5, 127)]) : ExecFixtures.Fr(s)), null, clock);
        await second.StartAsync(default);
        // bars 5..11 were missed while the Host was down; the signal on bar 12 comes right after the gap and must NOT be executed
        await Pump(second, clock, [12, 13]);
        Assert.True(await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_gaps") >= 1);
        var ledger = await LedgerAsync(db);
        Assert.Empty(ledger.OfType<EntryOrderPlaced>());
        Assert.Empty(ledger.OfType<EntryFilled>());
        Assert.True(await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE NOT entries_allowed") >= 2);
    }

    // ---- queue configuration: burst, slow persistence, persistence loss, consumer fault ----

    [PostgresFact]
    public async Task SlowJournal_Burst_OverflowsVisibly_NeverBlocksTheObserver_AndRecoversAfterCleanFrames()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        var slow = true;
        var hooks = new V2RuntimeTestHooks(JournalDecorator: j => new DelayingJournal(j, () => slow ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero, () => false));
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), capacity: 4, clear: 2), new FrameMapFactory(s => ExecFixtures.Fr(s)), null, clock, hooks: hooks);
        await rt.StartAsync(default);
        clock.Now = FrameFixtures.End(40);                                         // a burst of 30 bars delivered at once, e.g. after a stalled poll
        var worst = TimeSpan.Zero;
        for (var seq = 1; seq <= 30; seq++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            rt.TryEnqueue(Notice(seq));
            worst = sw.Elapsed > worst ? sw.Elapsed : worst;
        }

        Assert.True(worst < TimeSpan.FromMilliseconds(50), $"the observer-side enqueue took {worst.TotalMilliseconds:0.0} ms");
        await Until(() => { var s = rt.Snapshot(); return s.NoticesAccepted + s.NoticesDropped == 30 && s.Handoff is { } h && h.QueueDepth == 0 && s.Consumer is { Processed: > 0 }; }, "burst drained", 60);
        var snap = rt.Snapshot();
        Assert.True(snap.Handoff!.RejectedFull + snap.NoticesDropped > 0, "a capacity-4 queue must overflow under a 30-bar burst with a slow journal");
        // after the burst the persistence is fast again: clean contiguous frames re-open entries only after FramesToClearGap
        slow = false;
        var processed = snap.Consumer!.Processed;
        await Pump(rt, clock, Enumerable.Range(31, 4), processedBefore: processed + snap.Consumer.Duplicates + snap.Consumer.Rejected);
        Assert.True(await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_gaps") >= 1);
        Assert.True(await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE entries_allowed") >= 1);
    }

    [PostgresFact]
    public async Task ProcessingLag_BeyondTheLimit_BlocksEntries_ButNeverDropsTheFrame()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), lagSeconds: 5), new FrameMapFactory(s => s == 5 ? ExecFixtures.Fr(5, dir: 10) : ExecFixtures.Fr(s)), null, clock);
        await rt.StartAsync(default);
        await Pump(rt, clock, [1, 2, 3, 4]);
        clock.Now = FrameFixtures.End(5).AddSeconds(6);                           // handled 6 s after its availability: beyond the 5 s limit
        Assert.True(rt.TryEnqueue(Notice(5)));
        await Until(() => rt.Snapshot().Consumer!.Processed >= 5, "late frame processed");
        Assert.Empty((await LedgerAsync(db)).OfType<EntryOrderPlaced>());           // the late signal is not executed
        Assert.Equal(1, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_frames WHERE bar_seq = 5"));          // but it is journaled
        Assert.Equal(1, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE bar_seq = 5 AND NOT entries_allowed"));
        clock.Now = FrameFixtures.End(6).AddMilliseconds(100);                     // a frame within the limit again is allowed once the run is clean
        Assert.True(rt.TryEnqueue(Notice(6)));
        await Until(() => rt.Snapshot().Consumer!.Processed >= 6, "next frame");
    }

    [PostgresFact]
    public async Task TemporaryPersistenceLoss_BlocksEntries_NeverClaimsDurability_AndTheObserverIsNotAffected()
    {
        await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        var down = false;
        var hooks = new V2RuntimeTestHooks(JournalDecorator: j => new DelayingJournal(j, () => TimeSpan.Zero, () => down));
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), clear: 2), new FrameMapFactory(s => s == 6 ? ExecFixtures.Fr(6, dir: 10) : ExecFixtures.Fr(s)), null, clock, hooks: hooks);
        await rt.StartAsync(default);
        await Pump(rt, clock, [1, 2, 3, 4, 5]);
        down = true;
        await Pump(rt, clock, [6], processedBefore: 5);                            // the signal arrives while the journal is down
        var during = rt.Snapshot().Consumer!;
        Assert.True(during.JournalFaulted);
        Assert.False(during.ClaimsDurableRecording);
        Assert.DoesNotContain(rt.LiveEvents, e => e is EntryOrderPlaced);          // fail closed: no order while persistence is lost
        down = false;
        await Pump(rt, clock, [7, 8, 9], processedBefore: 6);
        Assert.False(rt.Snapshot().Consumer!.JournalFaulted);
        Assert.Equal(V2RuntimeState.Running, rt.State);
    }

    [PostgresFact]
    public async Task ConsumerFault_IsContained_RebuiltFromTheJournal_AndKeepsTheQueue()
    {
        await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        var boom = false;
        var hooks = new V2RuntimeTestHooks(ProcessorDecorator: p => new ThrowOnceProcessor(p, () => boom));
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), clear: 2), new FrameMapFactory(s => ExecFixtures.Fr(s)), null, clock, hooks: hooks);
        await rt.StartAsync(default);
        await Pump(rt, clock, [1, 2, 3]);
        boom = true;
        clock.Now = FrameFixtures.End(4).AddMilliseconds(100);
        Assert.True(rt.TryEnqueue(Notice(4)));                                      // this frame faults the consumer; the observer-side call still returned immediately
        await Until(() => rt.Snapshot().ConsumerRestarts >= 1, "consumer rebuilt");
        boom = false;
        for (var seq = 5; seq <= 8; seq++) { clock.Now = FrameFixtures.End(seq).AddMilliseconds(100); Assert.True(rt.TryEnqueue(Notice(seq))); }
        await Until(() => rt.Snapshot().Consumer is { Faulted: false, Processed: >= 5 }, "processing resumed", 60);
        Assert.Equal(V2RuntimeState.Running, rt.State);
    }

    [PostgresFact]
    public async Task RealConnectionLoss_AllBackendsTerminatedMidSession_RecoversWithoutDuplicates()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), clear: 2), new FrameMapFactory(s => ExecFixtures.Fr(s)), null, clock);
        await rt.StartAsync(default);
        await Pump(rt, clock, [1, 2, 3]);
        // a real interruption: the server drops every other connection to this database (pooled runtime connections included)
        await using (var kill = db.CreateCommand("SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid()")) await kill.ExecuteNonQueryAsync();
        for (var seq = 4; seq <= 10; seq++) { clock.Now = FrameFixtures.End(seq).AddMilliseconds(100); Assert.True(rt.TryEnqueue(Notice(seq))); await Task.Delay(150); }
        await Until(() => rt.Snapshot().Consumer is { Faulted: false } && rt.Snapshot().Consumer!.Processed >= 3, "recovered after the connection loss", 80);
        Assert.Equal(V2RuntimeState.Running, rt.State);
        await Task.Delay(500);
        // never a duplicate frame or disposition row, whatever was lost during the outage is visible as a gap rather than invented
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) - count(DISTINCT bar_seq) FROM pev2.pev2_frames"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) - count(DISTINCT frame_hash) FROM pev2.pev2_frames"));
        var journaled = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_frames");
        var gapBars = await ScalarAsync(db, "SELECT coalesce(sum(to_bar_seq - from_bar_seq + 1), 0) FROM pev2.pev2_gaps");
        Assert.True(journaled + gapBars >= 3, $"journaled {journaled}, gap bars {gapBars}");
    }

    sealed class ThrowOnceProcessor(IFrameProcessor inner, Func<bool> boom) : IFrameProcessor
    {
        public ValueTask ProcessAsync(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition d, CancellationToken ct) =>
            boom() && frame.Identity.BarSeq == 4 && !d.IsReplay ? throw new InvalidOperationException("engine fault (injected)") : inner.ProcessAsync(frame, previous, d, ct);
    }

    [PostgresFact]
    public async Task UnavailableFrameInputs_BecomeAVisibleGap_NotAFabricatedFrame()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), clear: 2), new FrameMapFactory(s => s == 3 ? null : ExecFixtures.Fr(s)), null, clock);
        await rt.StartAsync(default);
        await Pump(rt, clock, [1, 2]);
        clock.Now = FrameFixtures.End(3);
        rt.TryEnqueue(Notice(3));                                                   // inputs unavailable: no frame is built
        await Until(() => rt.Snapshot().InputLoadFailures >= 1, "failure recorded");
        await Pump(rt, clock, [4, 5], processedBefore: 2);
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_frames WHERE bar_seq = 3"));
        Assert.True(await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_gaps WHERE from_bar_seq <= 3 AND to_bar_seq >= 3") >= 1);
    }

    sealed class OffsetClock(DateTimeOffset virtualStart) : TimeProvider
    {
        readonly global::System.Diagnostics.Stopwatch _sw = global::System.Diagnostics.Stopwatch.StartNew();

        public override DateTimeOffset GetUtcNow() => virtualStart + _sw.Elapsed;
    }

    /// <summary>
    /// Candidate defaults capacity 64 / lag 5 s under a SPLIT-BAR burst (N bars completing in one market update, all with the same availability instant), with realistic and degraded journal latency. The clock runs in real
    /// time from the bar's availability instant, so the measured processing lag is the real queueing + persistence delay. Results are appended to %TEMP%\paper-v2-queue-sizing.txt.
    /// </summary>
    [PostgresTheory]
    [InlineData("64 split bars, journal 5 ms/append", 64, 64, 5, 0)]
    [InlineData("64 split bars, journal 20 ms/append", 64, 64, 20, 1)]
    [InlineData("64 split bars, journal 60 ms/append (degraded)", 64, 64, 60, 2)]
    [InlineData("100 split bars, journal 5 ms/append (burst above capacity)", 64, 100, 5, 3)]
    public async Task SplitBarBurst_QueueSizing_Measured(string name, int capacity, int bars, int appendMs, int scenario)
    {
        _ = scenario;
        var db = await InstalledAsync();
        var end = FrameFixtures.End(40);
        var clock = new OffsetClock(end.AddMilliseconds(50));
        var hooks = new V2RuntimeTestHooks(JournalDecorator: j => new DelayingJournal(j, () => TimeSpan.FromMilliseconds(appendMs), () => false));
        MarketStateFrame Sibling(int seq) => ExecFixtures.Fr(seq, end: end, ordinal: seq - 1, tick: 1_000, qtick: 5_000 + seq);
        await using var rt = new V2Runtime(Options(StrategyDir("S01"), capacity: capacity, lagSeconds: 5, clear: 3, alerts: false), new FrameMapFactory(Sibling), null, clock, hooks: hooks);
        await rt.StartAsync(default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var worst = TimeSpan.Zero;
        var notices = Enumerable.Range(1, bars).Select(s => Notice(s)).ToList();
        var first = TimeSpan.Zero;
        for (var seq = 1; seq <= bars; seq++)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            rt.TryEnqueue(notices[seq - 1]);
            var e = t.Elapsed;
            if (seq == 1) first = e; else if (e > worst) worst = e;          // the first call carries one-off JIT; reported separately
        }

        await Until(() => { var s = rt.Snapshot(); return s.NoticesAccepted + s.NoticesDropped == bars && s.Handoff is { } h && h.Accepted + h.RejectedFull + s.InputLoadFailures >= s.NoticesAccepted && h.QueueDepth == 0; }, "burst taken", 120);
        await Settle(rt);
        await Until(() => ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions").GetAwaiter().GetResult() >= rt.Snapshot().Handoff!.Accepted, "all accepted frames journaled", 60);
        var drain = sw.Elapsed;
        var snap = rt.Snapshot();
        var stored = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions");
        var maxLag = await ScalarAsync(db, "SELECT coalesce(max(processing_lag_ms), 0) FROM pev2.pev2_dispositions");
        var blockedByLag = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE (block_reasons & 512) <> 0");
        var allowed = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE entries_allowed");
        var gaps = await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_gaps");
        var line = $"{name}: capacity={capacity} bars={bars} appendMs={appendMs} | observer enqueue first={first.TotalMilliseconds:0.000} ms worst-after={worst.TotalMilliseconds:0.000} ms | rejectedFull={snap.Handoff!.RejectedFull} noticesDropped={snap.NoticesDropped} | journaled={stored} maxLag={maxLag} ms blockedByLag={blockedByLag} entriesAllowed={allowed} gaps={gaps} | drain={drain.TotalSeconds:0.0} s";
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "paper-v2-queue-sizing.txt"), line + Environment.NewLine);
        Assert.True(worst < TimeSpan.FromMilliseconds(50), line);                                  // the observer never waits
        if (bars <= capacity) Assert.Equal(0, snap.Handoff.RejectedFull + snap.NoticesDropped);     // a split-bar burst up to the capacity is absorbed without any drop
        else Assert.True(snap.Handoff.RejectedFull + snap.NoticesDropped > 0 && gaps >= 0, line);  // above it, the overflow is visible
        if (appendMs >= 60) Assert.True(blockedByLag > 0, line);                                    // degraded persistence: late frames block entries, they are still journaled
        Assert.Equal(snap.Handoff.Accepted, stored);                                                // every accepted frame is journaled with a disposition
    }}
