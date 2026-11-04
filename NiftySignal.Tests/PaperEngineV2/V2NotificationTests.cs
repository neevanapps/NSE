using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>V2 shadow notifications for every simulated entry and exit: content, the owner's setting set, one identity per alert, and no duplicates after a replay or restart. No real Telegram message is ever sent.</summary>
[Collection("PostgresTradingTests")]
public class V2NotificationTests
{
    static readonly DateTimeOffset T = new(2026, 11, 4, 4, 5, 0, TimeSpan.Zero);          // 09:35 IST
    static readonly DecisionKey Key = new(T, 9);
    static readonly EntryContext Ctx = new("S01:h:1:5:Call", OptionRight.Call, "CE26000", 26000, new DateOnly(2026, 11, 10), 127.5m, T, "i");
    static readonly EntryFilled Opened = new("S01:h:1:5:Call:filled", "S01", "h", 6, T, "i", "S01:h:1:5:Call", OptionRight.Call, "CE26000", 130, 127.5m, 33.37m, T, 12, 650);
    static readonly ExitFilled Closed = new("S01:h:1:5:Call:xfill", "S01", "h", 20, T.AddMinutes(38).AddSeconds(5), "S01:h:1:5:Call", ExitReason.OppositeSignal, 130, 140.25m, 36.1m, 1657.5m, 1588.03m, T, 13, 1300, 1700m, -100m);

    [Fact]
    public void TheOwnersSettingSet_IsBoundExactly()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PaperEngineV2:Alerts:Opened"] = "true", ["PaperEngineV2:Alerts:Closed"] = "true", ["PaperEngineV2:Alerts:ExecutionFailure"] = "true",
            ["PaperEngineV2:Alerts:OrderRejected"] = "false", ["PaperEngineV2:Alerts:PartialFill"] = "false",
        }).Build();
        var a = NiftySignal.Host.PaperEngineV2.PaperEngineV2Bridge.Bind(cfg).Alerts;
        Assert.True(a.Enabled(AlertClass.Opened)); Assert.True(a.Enabled(AlertClass.Closed)); Assert.True(a.Enabled(AlertClass.ExecutionFailure));
        Assert.False(a.Enabled(AlertClass.OrderRejected)); Assert.False(a.Enabled(AlertClass.PartialFill));
        Assert.False(NiftySignal.Host.PaperEngineV2.PaperEngineV2Bridge.Bind(new ConfigurationBuilder().Build()).Alerts.Opened);        // nothing is sent unless the owner opts in
    }

    [Fact]
    public void EntryAlert_CarriesTheRequiredFacts()
    {
        var text = AlertFormatter.Format(Opened, 2, Ctx);
        Assert.StartsWith("V2 SHADOW — PAPER ONLY | S01 v2 | ENTRY", text, StringComparison.Ordinal);
        foreach (var part in new[] { "CALL", "CE26000", "expiry 10-Nov-2026", "strike 26000", "fill 127.50 x130", "signal i", "09:35:00 IST" })
            Assert.Contains(part, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitAlert_CarriesTheRequiredFacts_IncludingGrossFeesNetAndHoldingDuration()
    {
        var text = AlertFormatter.Format(Closed, 2, Ctx);
        Assert.StartsWith("V2 SHADOW — PAPER ONLY | S01 v2 | EXIT", text, StringComparison.Ordinal);
        foreach (var part in new[] { "CE26000", "strike 26000", "x130", "entry 127.50 @ 09:35:00 IST", "exit 140.25 @ 10:13:05 IST", "reason OppositeSignal", "gross 1657.50", "fees 69.47", "net 1588.03", "held 38m 5s" })
            Assert.Contains(part, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAResolvedEntry_TheAlertDegradesToTheEventsOwnFields_NeverThrows()
    {
        Assert.Contains("ENTRY CALL CE26000 | fill 127.50 x130", AlertFormatter.Format(Opened), StringComparison.Ordinal);
        Assert.Contains("net 1588.03", AlertFormatter.Format(Closed), StringComparison.Ordinal);
        Assert.DoesNotContain(" v2", AlertFormatter.Format(Opened), StringComparison.Ordinal);                                           // the version is shown only when known
    }

    [Fact]
    public void NoMessageEverContainsACredentialShape()
    {
        foreach (var text in new[] { AlertFormatter.Format(Opened, 2, Ctx), AlertFormatter.Format(Closed, 2, Ctx) })
        {
            Assert.DoesNotMatch(@"\d{6,}:[A-Za-z0-9_-]{20,}", text);                       // a bot token shape
            Assert.DoesNotContain("api.telegram", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static async Task<List<(string Id, string Class, string Text)>> OutboxAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand("SELECT alert_id, alert_class, message_text FROM pev2.pev2_alert_outbox ORDER BY alert_id");
        await using var r = await c.ExecuteReaderAsync();
        var list = new List<(string, string, string)>();
        while (await r.ReadAsync()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    static readonly AlertOptions Owner = new(Opened: true, Closed: true, OrderRejected: false, PartialFill: false, ExecutionFailure: true, MaxAge: TimeSpan.FromDays(3650));

    [PostgresFact]
    public async Task AFullSimulatedDay_QueuesExactlyOneEntryAndOneExitAlert_WithTheResolvedContract_AndRejectionsStayQuiet()
    {
        var db = await FreshAsync();
        var engine = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), new AlertingEventSink(new PostgresEventStore(db), db, Owner, new ManualClock(T), _ => 3));
        ExecFixtures.Run(engine, ExecutionTests.Day());
        var ledger = engine.Events;
        var rows = await OutboxAsync(db);
        Assert.Equal(ledger.OfType<EntryFilled>().Count(), rows.Count(r => r.Class == "Opened"));
        Assert.Equal(ledger.OfType<ExitFilled>().Count(), rows.Count(r => r.Class == "Closed"));
        Assert.DoesNotContain(rows, r => r.Class == "OrderRejected");                       // OrderRejected is OFF
        Assert.NotEmpty(rows);
        foreach (var entry in rows.Where(r => r.Class == "Opened").Select(r => r.Text))
            foreach (var part in new[] { "S01 v3", "strike 26000", "expiry", "signal " }) Assert.Contains(part, entry, StringComparison.Ordinal);
        foreach (var exit in rows.Where(r => r.Class == "Closed").Select(r => r.Text))
            foreach (var part in new[] { "entry ", "held ", "strike 26000", "gross ", "net " }) Assert.Contains(part, exit, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task ARestartReplay_AndDuplicateOffers_NeverQueueASecondAlert_AndAnUncertainDeliveryIsNeverResent()
    {
        var db = await FreshAsync();
        var frames = ExecutionTests.Day();
        ExecFixtures.Run(new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), new AlertingEventSink(new PostgresEventStore(db), db, Owner, new ManualClock(T), _ => 3)), frames);
        var first = await OutboxAsync(db);
        ExecFixtures.Run(new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), new AlertingEventSink(new PostgresEventStore(db), db, Owner, new ManualClock(T), _ => 3)), frames);   // the restart replays the journal
        Assert.Equal(first, await OutboxAsync(db));
        Assert.Equal(first.Count, first.Select(r => r.Id).Distinct().Count());                // one identity per alert

        var uncertain = new ScriptedTransport(new AlertSendResult(SendOutcome.Uncertain, Error: "timeout"));
        var d = new AlertDispatcher(db, uncertain, Owner, new ManualClock(T.AddHours(9)));
        await d.DispatchAsync(10, default);
        var again = new ScriptedTransport();
        await new AlertDispatcher(db, again, Owner, new ManualClock(T.AddHours(9).AddMinutes(1))).DispatchAsync(10, default);
        Assert.Empty(again.Sent);                                                              // an uncertain delivery is never resent automatically
    }

    [PostgresFact]
    public async Task TheEntryLookup_SurvivesARestart_BecauseItReadsTheDurableLedger()
    {
        var db = await FreshAsync();
        var store = new PostgresEventStore(db);
        var placed = new EntryOrderPlaced("S01:h:1:5:Call:placed", "S01", "h", 5, T.AddSeconds(-30), "i", OptionRight.Call, "CE26000", 26000, new DateOnly(2026, 11, 10), 65, 130, 126m, Key, T.AddSeconds(60));
        store.Append(placed); store.Append(Opened); store.Append(Closed);
        var ctx = EntryContextLookup.Resolve(db, Closed);                                      // a brand-new process: nothing in memory
        Assert.NotNull(ctx);
        Assert.Equal((26000d, new DateOnly(2026, 11, 10), 127.5m, T), (ctx!.Strike, ctx.Expiry, ctx.FillPrice, ctx.EntryAt));
        Assert.Null(EntryContextLookup.Resolve(db, Closed with { PositionId = "other" }));
        Assert.Null(EntryContextLookup.Resolve(db, new StrategyFaulted("f", "S01", "h", 1, T, "x")));
    }
}
