using Npgsql;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class ScriptedTransport(params AlertSendResult[] script) : IAlertTransport
{
    int _i;

    public List<string> Sent { get; } = [];

    public Task<AlertSendResult> SendAsync(string text, CancellationToken ct)
    {
        Sent.Add(text);
        var r = script.Length == 0 ? new AlertSendResult(SendOutcome.Sent, 1000 + Sent.Count) : script[Math.Min(_i++, script.Length - 1)];
        return Task.FromResult(r);
    }
}

sealed class ThrowingTransport : IAlertTransport
{
    public Task<AlertSendResult> SendAsync(string text, CancellationToken ct) => throw new HttpRequestException("https://api.telegram.org/bot123:SECRET/sendMessage failed");
}

sealed class FakeTelegram(TelegramDocumentResult result) : ITelegramTextSender
{
    public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct) => Task.FromResult(result);
}

/// <summary>Phase 6e: opt-in alert classes (all OFF by default), the durable idempotent outbox, delivery semantics and isolation from the engine.</summary>
[Collection("PostgresTradingTests")]
public class AlertTests
{
    static readonly DateTimeOffset T = new(2026, 10, 7, 4, 5, 0, TimeSpan.Zero);
    static readonly DecisionKey Key = new(T, 9);

    static readonly PaperEvent Opened = new EntryFilled("S01:h:1:5:Call:filled", "S01", "h", 6, T, "i", "S01:h:1:5:Call", OptionRight.Call, "CE26000", 650, 127.5m, 33.37m, T, 12, 650);
    static readonly PaperEvent Closed = new ExitFilled("S01:h:1:5:Call:xfill", "S01", "h", 8, T, "S01:h:1:5:Call", ExitReason.Terminal, 650, 120.45m, 20.1m, -4553.25m, -4600.55m, T, 13, 1300);
    static readonly PaperEvent Rejected = new EntryRejected("S01:h:1:5:any:rej:SpreadTooWide", "S01", "h", 5, T, OptionRight.Call, null, RejectionReason.SpreadTooWide, "every in-range contract exceeds the spread limit");
    static readonly StrategyFaulted Faulted = new StrategyFaulted("S01:h:1:9:fault", "S01", "h", 9, T, "InvalidOperationException: boom");
    static readonly PaperEvent Unresolved = new PositionUnresolved("S01:h:1:5:Call:unres", "S01", "h", 9, T, "S01:h:1:5:Call", "not closed by the retry cut-off");
    static readonly PaperEvent Placed = new EntryOrderPlaced("S01:h:1:5:Call:placed", "S01", "h", 5, T, "i", OptionRight.Call, "CE26000", 26000, new DateOnly(2026, 10, 13), 65, 650, 126m, Key, T.AddSeconds(60));

    static PaperEvent[] All => [Opened, Closed, Rejected, Faulted, Unresolved, Placed];

    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static async Task<List<(string Id, int Status, int Attempts)>> RowsAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand("SELECT alert_id, status, attempts FROM pev2.pev2_alert_outbox ORDER BY alert_id");
        await using var r = await c.ExecuteReaderAsync();
        var list = new List<(string, int, int)>();
        while (await r.ReadAsync()) list.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    static AlertingEventSink Sink(NpgsqlDataSource db, AlertOptions o) => new(new PostgresEventStore(db), db, o, new ManualClock(T));

    // ---- pure ----

    [Fact]
    public void Defaults_AreAllOff_AndOnlyTheFiveClassesMap()
    {
        foreach (var c in Enum.GetValues<AlertClass>()) Assert.False(AlertOptions.AllOff.Enabled(c));
        Assert.Equal(AlertClass.Opened, AlertFormatter.ClassOf(Opened));
        Assert.Equal(AlertClass.Closed, AlertFormatter.ClassOf(Closed));
        Assert.Equal(AlertClass.OrderRejected, AlertFormatter.ClassOf(Rejected));
        Assert.Equal(AlertClass.ExecutionFailure, AlertFormatter.ClassOf(Faulted));
        Assert.Equal(AlertClass.ExecutionFailure, AlertFormatter.ClassOf(Unresolved));
        Assert.Null(AlertFormatter.ClassOf(Placed));
        Assert.DoesNotContain(typeof(PaperEvent).Assembly.GetTypes(), t => typeof(PaperEvent).IsAssignableFrom(t) && t.Name.Contains("Partial", StringComparison.Ordinal));      // all-or-none: no partial fill is ever produced
    }

    [Fact]
    public void Messages_AreLabelledShadow_AndCarryTheFacts_NeverSecrets()
    {
        foreach (var e in new[] { Opened, Closed, Rejected, Faulted, Unresolved })
        {
            var text = AlertFormatter.Format(e);
            Assert.StartsWith("V2 SHADOW (paper only) | S01 | ", text, StringComparison.Ordinal);
            Assert.Contains("IST", text, StringComparison.Ordinal);
            Assert.True(text.Length < 600);
        }

        Assert.Contains("OPENED CALL CE26000 x650 @ 127.50", AlertFormatter.Format(Opened), StringComparison.Ordinal);
        Assert.Contains("net -4600.55", AlertFormatter.Format(Closed), StringComparison.Ordinal);
        Assert.Contains("SpreadTooWide", AlertFormatter.Format(Rejected), StringComparison.Ordinal);
        Assert.Contains("UNRESOLVED", AlertFormatter.Format(Unresolved), StringComparison.Ordinal);
        Assert.Equal("S01:h:1:5:Call:filled#Opened", AlertFormatter.AlertId(Opened, AlertClass.Opened));
        Assert.Throws<ArgumentException>(() => AlertFormatter.Format(Placed));
        Assert.True(AlertFormatter.Format(Faulted with { Message = new string('x', 5000) }).Length < 600);          // long details are cut
    }

    [Fact]
    public async Task Telegram_Adapter_MapsOutcomes()
    {
        var sent = await new TelegramAlertTransport(new FakeTelegram(new(TelegramDocumentOutcome.Sent, 42))).SendAsync("t", default);
        Assert.Equal((SendOutcome.Sent, 42L), (sent.Outcome, sent.MessageId));
        var rej = await new TelegramAlertTransport(new FakeTelegram(new(TelegramDocumentOutcome.Rejected, null, 30, "no"))).SendAsync("t", default);
        Assert.Equal((SendOutcome.Rejected, 30), (rej.Outcome, rej.RetryAfterSeconds));
        Assert.Equal(SendOutcome.Uncertain, (await new TelegramAlertTransport(new FakeTelegram(new(TelegramDocumentOutcome.Uncertain))).SendAsync("t", default)).Outcome);
    }

    // ---- outbox ----

    [PostgresFact]
    public async Task NothingIsQueued_ByDefault_AndEachClassIsAnIndependentOptIn()
    {
        var db = await FreshAsync();
        var off = Sink(db, AlertOptions.AllOff);
        foreach (var e in All) off.Append(e);
        Assert.Empty(await RowsAsync(db));
        Assert.Equal(0, off.Enqueued);

        await using (var wipe = db.CreateCommand("DELETE FROM pev2.pev2_events")) await wipe.ExecuteNonQueryAsync();
        var onlyOpened = Sink(db, new AlertOptions(Opened: true));
        foreach (var e in All) onlyOpened.Append(e);
        Assert.Equal(["S01:h:1:5:Call:filled#Opened"], (await RowsAsync(db)).Select(r => r.Id).ToArray());

        var failures = Sink(db, new AlertOptions(ExecutionFailure: true, OrderRejected: true));
        foreach (var e in All) failures.Append(e);
        Assert.Equal(new[] { "S01:h:1:5:Call:filled#Opened", "S01:h:1:5:Call:unres#ExecutionFailure", "S01:h:1:5:any:rej:SpreadTooWide#OrderRejected", "S01:h:1:9:fault#ExecutionFailure" }.Order(StringComparer.Ordinal).ToArray(),
            (await RowsAsync(db)).Select(r => r.Id).ToArray());
    }

    [PostgresFact]
    public async Task Enqueue_IsIdempotent_AcrossDuplicatesAndAReplayRestart()
    {
        var db = await FreshAsync();
        var o = new AlertOptions(true, true, true, true, true);
        var sink = Sink(db, o);
        foreach (var e in All) { sink.Append(e); sink.Append(e); }
        var count = (await RowsAsync(db)).Count;
        Assert.Equal(5, count);

        // a full engine run, then a restart replay through a NEW sink: no duplicate alert rows
        var frames = ExecutionTests.Day();
        var live = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), Sink(db, o));
        ExecFixtures.Run(live, frames);
        var afterLive = (await RowsAsync(db)).Count;
        Assert.True(afterLive > count);
        var replay = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), Sink(db, o));
        ExecFixtures.Run(replay, frames);
        Assert.Equal(afterLive, (await RowsAsync(db)).Count);
        Assert.Equal(0, replay.SinkFailures);
    }

    [PostgresFact]
    public async Task AnOutboxFailure_NeverReachesTheStrategies_AndTheLedgerEventIsKept()
    {
        var db = await FreshAsync();
        await using (var drop = db.CreateCommand("DROP TABLE pev2.pev2_alert_outbox")) await drop.ExecuteNonQueryAsync();
        var sink = Sink(db, new AlertOptions(Opened: true, Closed: true, OrderRejected: true, ExecutionFailure: true));
        var engine = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), sink);
        ExecFixtures.Run(engine, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)])]);
        Assert.Single(ExecFixtures.Of<EntryFilled>(engine));                              // trading simulation unaffected
        Assert.True(sink.Failures >= 1);
        Assert.True(engine.SinkFailures >= 1);
        await using var c = db.CreateCommand("SELECT count(*) FROM pev2.pev2_events");
        Assert.True((long)(await c.ExecuteScalarAsync())! >= 1);                          // the ledger write came first
    }

    // ---- delivery ----

    static async Task<NpgsqlDataSource> SeededAsync(params PaperEvent[] events)
    {
        var db = await FreshAsync();
        var sink = Sink(db, new AlertOptions(true, true, true, true, true));
        foreach (var e in events) sink.Append(e);
        return db;
    }

    [PostgresFact]
    public async Task Dispatch_SendsOnce_RecordsTheMessageId_AndNeverResendsASentAlert()
    {
        var db = await SeededAsync(Opened, Closed);
        var transport = new ScriptedTransport();
        var d = new AlertDispatcher(db, transport, new AlertOptions(), new ManualClock(T.AddSeconds(5)));
        var r = await d.DispatchAsync(10, default);
        Assert.Equal((2, 2), (r.Claimed, r.Sent));
        Assert.Equal(2, transport.Sent.Count);
        Assert.All(await RowsAsync(db), x => Assert.Equal((int)AlertStatus.Sent, x.Status));
        Assert.Equal(0, (await d.DispatchAsync(10, default)).Claimed);
        Assert.Equal(2, transport.Sent.Count);
        await using var c = db.CreateCommand("SELECT count(*) FROM pev2.pev2_alert_outbox WHERE telegram_message_id IS NOT NULL");
        Assert.Equal(2L, await c.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task Rejected_IsRetriedLater_ThenFails_AfterMaxAttempts()
    {
        var db = await SeededAsync(Opened);
        var clock = new ManualClock(T.AddSeconds(5));
        var transport = new ScriptedTransport(new AlertSendResult(SendOutcome.Rejected, null, 30, "429"), new AlertSendResult(SendOutcome.Rejected, null, 30, "429"), new AlertSendResult(SendOutcome.Rejected, null, 30, "429"));
        var d = new AlertDispatcher(db, transport, new AlertOptions(MaxAttempts: 3), clock);
        Assert.Equal(1, (await d.DispatchAsync(10, default)).Retrying);
        Assert.Equal(0, (await d.DispatchAsync(10, default)).Claimed);                    // not due yet
        clock.Now = clock.Now.AddSeconds(31);
        Assert.Equal(1, (await d.DispatchAsync(10, default)).Retrying);
        clock.Now = clock.Now.AddSeconds(31);
        var last = await d.DispatchAsync(10, default);
        Assert.Equal(1, last.Failed);
        Assert.Equal((int)AlertStatus.Failed, Assert.Single(await RowsAsync(db)).Status);
        Assert.Equal(3, transport.Sent.Count);
    }

    [PostgresFact]
    public async Task Rejected_ThenAccepted_IsSentOnTheRetry()
    {
        var db = await SeededAsync(Opened);
        var clock = new ManualClock(T.AddSeconds(5));
        var d = new AlertDispatcher(db, new ScriptedTransport(new AlertSendResult(SendOutcome.Rejected, null, 10), new AlertSendResult(SendOutcome.Sent, 77)), new AlertOptions(), clock);
        await d.DispatchAsync(10, default);
        clock.Now = clock.Now.AddSeconds(11);
        Assert.Equal(1, (await d.DispatchAsync(10, default)).Sent);
        Assert.Equal((int)AlertStatus.Sent, Assert.Single(await RowsAsync(db)).Status);
    }

    [PostgresFact]
    public async Task UncertainOrThrowingTransport_IsNeverResentAutomatically_AndSecretsStayOutOfTheRow()
    {
        var db = await SeededAsync(Opened, Closed);
        var d = new AlertDispatcher(db, new ThrowingTransport(), new AlertOptions(), new ManualClock(T.AddSeconds(5)));
        var r = await d.DispatchAsync(10, default);
        Assert.Equal(2, r.Uncertain);
        Assert.All(await RowsAsync(db), x => Assert.Equal((int)AlertStatus.Uncertain, x.Status));
        var again = new ScriptedTransport();
        Assert.Equal(0, (await new AlertDispatcher(db, again, new AlertOptions(), new ManualClock(T.AddMinutes(1))).DispatchAsync(10, default)).Claimed);
        Assert.Empty(again.Sent);
        await using var c = db.CreateCommand("SELECT string_agg(coalesce(last_error, ''), '|') FROM pev2.pev2_alert_outbox");
        var errors = (string)(await c.ExecuteScalarAsync())!;
        Assert.DoesNotContain("SECRET", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("bot123", errors, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task StaleAlerts_AreExpired_NotSent_SoAReplayNeverBursts()
    {
        var db = await SeededAsync(Opened);
        var transport = new ScriptedTransport();
        var d = new AlertDispatcher(db, transport, new AlertOptions(MaxAge: TimeSpan.FromMinutes(15)), new ManualClock(T.AddHours(2)));
        var r = await d.DispatchAsync(10, default);
        Assert.Equal((0, 1), (r.Claimed, r.Expired));
        Assert.Empty(transport.Sent);
        Assert.Equal((int)AlertStatus.Expired, Assert.Single(await RowsAsync(db)).Status);
    }

    [PostgresFact]
    public async Task InterruptedClaim_BecomesUncertain_NotResent()
    {
        var db = await SeededAsync(Opened);
        await using (var c = db.CreateCommand("UPDATE pev2.pev2_alert_outbox SET status = 5, attempts = 1")) await c.ExecuteNonQueryAsync();          // a crash after the claim, before the outcome
        var transport = new ScriptedTransport();
        var d = new AlertDispatcher(db, transport, new AlertOptions(), new ManualClock(T.AddSeconds(5)));
        Assert.Equal(1, await d.ResolveInterruptedAsync(default));
        Assert.Equal(0, (await d.DispatchAsync(10, default)).Claimed);
        Assert.Equal((int)AlertStatus.Uncertain, Assert.Single(await RowsAsync(db)).Status);
        Assert.Empty(transport.Sent);
    }

    [PostgresFact]
    public async Task Batch_IsBounded_AndConcurrentDispatchersNeverDoubleSend()
    {
        var db = await FreshAsync();
        var sink = Sink(db, new AlertOptions(ExecutionFailure: true));
        for (var i = 0; i < 60; i++) sink.Append(new StrategyFaulted($"S01:h:1:{i}:fault", "S01", "h", i, T, "x"));
        var transport = new ScriptedTransport();
        var clock = new ManualClock(T.AddSeconds(1));
        var a = new AlertDispatcher(db, transport, new AlertOptions(), clock);
        var b = new AlertDispatcher(db, transport, new AlertOptions(), clock);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => a.DispatchAsync(AlertDispatcher.MaxBatch + 1, default));
        var reports = await Task.WhenAll(a.DispatchAsync(50, default), b.DispatchAsync(50, default), a.DispatchAsync(50, default));
        var total = reports.Sum(x => x.Sent);
        while (true)                                                                         // rows another dispatcher had locked are skipped (not lost): a follow-up round picks them up
        {
            var more = await a.DispatchAsync(50, default);
            if (more.Claimed == 0) break;
            total += more.Sent;
        }

        Assert.Equal(60, total);
        Assert.Equal(60, transport.Sent.Distinct().Count());
        Assert.Equal(60, transport.Sent.Count);
        Assert.Equal(60, (await RowsAsync(db)).Count(x => x.Status == (int)AlertStatus.Sent && x.Attempts == 1));
    }

    [Fact]
    public void AlertsAssembly_HasOnlyTheAllowedReferences()
    {
        var refs = typeof(AlertDispatcher).Assembly.GetReferencedAssemblies().Select(a => a.Name!).Where(n => n.StartsWith("NiftySignal", StringComparison.Ordinal)).OrderBy(n => n).ToArray();
        Assert.Empty(refs.Except(["NiftySignal.Notifications", "NiftySignal.PaperEngineV2.Data", "NiftySignal.PaperEngineV2.Execution"]));      // constants are inlined, so Data may not appear as a binary reference
    }
}
