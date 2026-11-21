using System.Text.Json.Nodes;
using Npgsql;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// The 2026-11-05 incident end to end through the real V2Runtime on a local PostgreSQL schema: the first bar of the session cannot be built (no centre strike yet), every later bar is built and contiguous. Entries must
/// resume after FramesToClearGap clean frames, the journal must say why the early frames were blocked, and the original reason must be kept for diagnostics.
/// </summary>
[Collection("PostgresTradingTests")]
public class BuildFailureRuntimeTests
{
    static readonly V2SessionInfo Session = new(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", FrameFixtures.Expiry, FrameFixtures.Listed(),
        new Dictionary<string, int> { ["CE26000"] = 65, ["CE25950"] = 65, ["PE26000"] = 65, ["PE25950"] = 65 });

    static V2BarNotice Notice(int seq) =>
        new(Session, new BarInput(1, FrameFixtures.Day, "adaptive-fixed13650-v1", "spot-atm-v1", seq, FrameFixtures.End(seq).AddSeconds(-60), FrameFixtures.End(seq), 1000 + seq, 60, true, null), 26_000, []);

    static MarketStateFrame TradeFrame(int seq) => seq switch
    {
        5 => ExecFixtures.Fr(5, dir: 10),
        6 => ExecFixtures.Fr(6, quotes: [new("CE26000", 125.5, 127)]),
        7 => ExecFixtures.Fr(7, quotes: [new("CE26000", 130, 131)]),
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

    static string StrategyDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "v2bf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "S01.json"), ConfigTests.Mutate(o =>
        {
            o["strategyId"] = "S01";
            o["execution"]!["fillQuote"] = "AsOfEligibleBarEnd";
            o["fees"]!["schedule"] = ExecFixtures.Fees;
            o["exit"]!["conditions"] = JsonNode.Parse("""{ "op": "OR", "items": [ { "metric": "fut.strict:pct", "window": "5", "cmp": "<", "value": -50 } ] }""");
        }));
        return dir;
    }

    static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    static async Task<long> ScalarAsync(NpgsqlDataSource db, string sql)
    {
        await using var c = db.CreateCommand(sql);
        return Convert.ToInt64(await c.ExecuteScalarAsync());
    }

    static NiftySignal.PaperEngineV2.Alerts.AlertOptions AlertOptionsOff() => NiftySignal.PaperEngineV2.Alerts.AlertOptions.AllOff;

    static V2RuntimeOptions Options(bool marks = false) => new()
    {
        Enabled = true, ConnectionString = PostgresTestDatabase.ConnectionString, StrategyDirectory = StrategyDir(), QueueCapacity = 64, MaxProcessingLag = TimeSpan.FromSeconds(5), FramesToClearGap = 2,
        TerminalRetryUntil = new TimeOnly(15, 25), BrokeragePerOrder = 0m, CostScheduleVersion = ExecFixtures.Fees, Alerts = AlertOptionsOff(), RestartBackoff = TimeSpan.FromMilliseconds(50),
        LiveMarksEnabled = marks, LiveMarkInterval = TimeSpan.FromMilliseconds(250),
    };

    [PostgresFact]
    public async Task AnEarlyBuildFailure_DoesNotBlockTheWholeSession_AndTheReasonIsKept()
    {
        var db = await InstalledAsync();
        var clock = new ManualClock(FrameFixtures.T0);
        await using var rt = new V2Runtime(Options(), new FrameMapFactory(s => s == 1 ? null : TradeFrame(s)), null, clock);
        await rt.StartAsync(default);
        for (var seq = 1; seq <= 9; seq++)
        {
            clock.Now = FrameFixtures.End(seq).AddMilliseconds(100);
            Assert.True(rt.TryEnqueue(Notice(seq)));
            var want = seq;
            await Until(() => rt.Snapshot() is { } s && s.FramesBuilt + s.InputLoadFailures >= want && (s.Handoff?.QueueDepth ?? 0) == 0, $"notice {want} handled");
            await Until(() => seq == 1 || rt.Snapshot().Consumer is { } c && c.Processed >= seq - 1, $"frame {want} consumed");
        }

        var snap = rt.Snapshot();
        Assert.Equal(1, snap.InputLoadFailures);
        Assert.Equal("test: no frame for this bar", rt.LastInputProblem!.Value.Reason);
        Assert.Equal(1, rt.LastInputProblem.Value.BarSeq);
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE bar_seq = 1"));                     // no frame was built for bar 1
        Assert.Equal(2, await ScalarAsync(db, "SELECT block_reasons & 2 FROM pev2.pev2_dispositions WHERE bar_seq = 2"));             // the first frame is labelled BuildFailure (flag value 2)
        Assert.Equal(0, await ScalarAsync(db, "SELECT entries_allowed::int FROM pev2.pev2_dispositions WHERE bar_seq = 2"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE bar_seq >= 3 AND (block_reasons & 2) <> 0"));      // the flag is NOT latched
        Assert.Equal(7, await ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_dispositions WHERE bar_seq >= 3 AND entries_allowed"));           // entries resume once FramesToClearGap(2) clean frames were accepted
        var ledger = await new PostgresEventStore(db).ReadAsync(FrameFixtures.T0.AddHours(-1), FrameFixtures.T0.AddHours(12), null, PostgresEventStore.MaxRead, default);
        Assert.Single(ledger.OfType<EntryFilled>());                                                                                               // the bar-5 signal was acted on, not blocked
    }

    [PostgresFact]
    public async Task TheMarkPublisher_WritesItsHeartbeat_EvenWithNoOpenPosition_AndEvenWhileEveryBarFails()
    {
        var db = await InstalledAsync();
        await using var rt = new V2Runtime(Options(marks: true), new FrameMapFactory(_ => null), null);        // system clock; no frame is ever built
        await rt.StartAsync(default);
        rt.TryEnqueue(Notice(1));
        await Until(() => ScalarAsync(db, "SELECT count(*) FROM pev2.pev2_live_marks WHERE position_id = '_publisher'").GetAwaiter().GetResult() == 1, "heartbeat row");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)).ToString("yyyy-MM-dd");
        Assert.Equal(1, await ScalarAsync(db, $"SELECT count(*) FROM pev2.pev2_live_marks WHERE position_id = '_publisher' AND trade_date = '{today}'"));
        Assert.NotNull(rt.MarkStatus);
        Assert.Equal(0L, rt.MarkStatus!.Value.Failures);
    }
}
