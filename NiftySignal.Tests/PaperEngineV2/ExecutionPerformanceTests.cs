using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;
using Xunit.Abstractions;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Phase 6g: INDEPENDENT measurements of the V2 engine for 1 / 10 / 30 strategies over a full 330-bar session - in memory (engine only) and against the local PostgreSQL test database (journal + ledger + alert outbox, with
/// full-size frames of 284 metrics x 5 windows). The assertions are deliberately loose regression guards (the observer thread never waits on any of this; see the 6b handoff tests); the numbers go to the output and the progress log.
/// </summary>
public class ExecutionPerformanceTests(ITestOutputHelper output)
{
    const int Bars = 330;

    static string Pct(List<double> us, string unit = "us")
    {
        us.Sort();
        double P(double q) => us[Math.Min(us.Count - 1, (int)Math.Ceiling(q * us.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"n={us.Count} p50={P(0.50):0.0} {unit} p95={P(0.95):0.0} {unit} p99={P(0.99):0.0} {unit} max={us[^1]:0.0} {unit}");
    }

    void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "paper-v2-execution-perf.txt"), line + Environment.NewLine);
    }

    static CompiledStrategy[] Grid(int n) => Enumerable.Range(1, n).Select(i => ExecFixtures.Strat($"S{i:00}", o =>
    {
        o["instrument"]!["premium"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 100 + (i % 5), ["max"] = 150 };
        o["quantity"]!["lots"] = 1 + i % 4;
        o["position"]!["maxConcurrent"] = 1 + i % 3;
        o["exits"] = System.Text.Json.Nodes.JsonNode.Parse(i % 2 == 0 ? """{ "stopLoss": { "type": "Percent", "value": 8 }, "maxHoldBars": 12 }""" : """{ "target": { "type": "Points", "value": 12 } }""");
    })).ToArray();

    static IEnumerable<ExecFixtures.Q> Book(int i) =>
    [
        new("CE26000", 120 + i % 7, 123 + i % 7), new("CE25950", 125 + i % 5, 128 + i % 5), new("CE26050", 118 + i % 4, 121 + i % 4),
        new("PE26000", 119 + i % 6, 122 + i % 6), new("PE25950", 124 + i % 3, 127 + i % 3), new("PE26050", 117 + i % 5, 120 + i % 5),
    ];

    /// <summary>A frame carrying ALL 284 x 5 metrics (real payload size) with the three driver metrics set by the scenario.</summary>
    static MarketStateFrame Full(int seq)
    {
        var dir = seq % 9 == 5 ? 10 : seq % 9 == 8 ? -10 : 0;
        var fut = seq % 13 == 0 ? -60 : 1;
        var drivers = new Dictionary<(int, SummaryWindowId), double>
        {
            [(FrameFixtures.H("opt.atm.strict.dir:pct"), SummaryWindowId.W10)] = dir,
            [(FrameFixtures.H("fut.strict:pct"), SummaryWindowId.W5)] = fut,
            [(FrameFixtures.H("vix.fresh"), SummaryWindowId.W5)] = 1,
        };
        var metrics = new List<MetricValue>();
        for (var h = 0; h < MetricRegistry.Count; h++)
            for (var w = 0; w < SummaryWindows.Count; w++)
                metrics.Add(new MetricValue(h, (SummaryWindowId)w, drivers.TryGetValue((h, (SummaryWindowId)w), out var v) ? v : ((h * 7 + w * 3 + seq) % 997) / 13.0));
        var e = FrameFixtures.End(seq);
        var quotes = Book(seq).Select(q => new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, e.AddMilliseconds(-q.AgeMs), 5_000 + seq)).ToList();
        return MarketStateFrame.Create(FrameFixtures.Identity(1, seq, e), FrameFixtures.BasketId(), metrics, quotes, [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"));
    }

    static MarketStateFrame Light(int seq) => ExecFixtures.Fr(seq, dir: seq % 9 == 5 ? 10 : seq % 9 == 8 ? -10 : 0, fut: seq % 13 == 0 ? -60 : 1, quotes: Book(seq));

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    public void Engine_InMemory_PerFrameLatencyAndAllocation(int strategies)
    {
        var frames = Enumerable.Range(1, Bars).Select(Light).ToList();
        var warm = new PaperEngine(Grid(strategies), ExecFixtures.Options());
        ExecFixtures.Run(warm, frames.Take(40));               // JIT warm-up

        var engine = new PaperEngine(Grid(strategies), ExecFixtures.Options());
        var us = new List<double>(Bars);
        MarketStateFrame? prev = null;
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        foreach (var f in frames)
        {
            var t0 = Stopwatch.GetTimestamp();
            engine.ProcessAsync(f, prev, ExecFixtures.Allowed, default).AsTask().GetAwaiter().GetResult();
            us.Add((Stopwatch.GetTimestamp() - t0) * 1_000_000d / Stopwatch.Frequency);
            prev = f;
        }

        var perFrameKb = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / (double)Bars / 1024d;
        var line = $"engine in-memory strategies={strategies} {Pct(us)} alloc/frame={perFrameKb:0.0} KB events={engine.Events.Count} faulted={engine.Runtimes.Count(r => r.Faulted)}";
        Report(line);
        Assert.Equal(0, engine.Runtimes.Count(r => r.Faulted));
        Assert.NotEmpty(engine.Events);
        Assert.True(us.OrderBy(x => x).ElementAt((int)(us.Count * 0.99)) < 20_000, line);       // a frame is one bar (minutes apart); 20 ms is a regression guard, not a target
    }

    [PostgresTheory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    public async Task FullPipeline_WithDurableJournalLedgerAndOutbox_PerFrameLatency(int strategies)
    {
        await using var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using (var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")))) await create.ExecuteNonQueryAsync();

        var clock = new ManualClock(FrameFixtures.End(1));
        var sink = new AlertingEventSink(new PostgresEventStore(db), db, new AlertOptions(true, true, true, false, true), clock);
        var engine = new PaperEngine(Grid(strategies), ExecFixtures.Options(), sink);
        var consumer = new FrameConsumer(null, new HandoffOptions(16, TimeSpan.FromHours(1), 1), new PostgresFrameJournal(db), engine, clock);
        var frames = Enumerable.Range(1, Bars).Select(Full).ToList();
        var buildBytes = frames.Sum(f => (long)f.PayloadJson.Length);                              // forces the canonical payload outside the timed region? no: measured separately below
        var ms = new List<double>(Bars);
        foreach (var f in frames)
        {
            clock.Now = f.Identity.BarEndAvailableAt.AddMilliseconds(50);
            var t0 = Stopwatch.GetTimestamp();
            await consumer.HandleAsync(f, default);
            ms.Add((Stopwatch.GetTimestamp() - t0) * 1000d / Stopwatch.Frequency);
        }

        await using var size = db.CreateCommand("SELECT (SELECT count(*) FROM pev2.pev2_events), (SELECT count(*) FROM pev2.pev2_alert_outbox), pg_total_relation_size('pev2.pev2_frames'), pg_total_relation_size('pev2.pev2_events')");
        await using var r = await size.ExecuteReaderAsync();
        await r.ReadAsync();
        var line = $"pipeline+PostgreSQL strategies={strategies} {Pct(ms, "ms")} payload/frame={buildBytes / (double)Bars / 1024d:0.0} KB events={r.GetInt64(0)} outbox={r.GetInt64(1)} frames_table={r.GetInt64(2) / 1024} KB events_table={r.GetInt64(3) / 1024} KB sinkFailures={engine.SinkFailures}";
        Report(line);
        Assert.Equal(0, engine.SinkFailures);
        Assert.True(consumer.Status.ClaimsDurableRecording);
        Assert.Equal(Bars, consumer.Status.Processed);
        Assert.True(ms.OrderBy(x => x).ElementAt((int)(ms.Count * 0.99)) < 500, line);            // durable path: 500 ms p99 is a regression guard (bars are minutes apart)
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresTestDatabase.Available) Skip = $"{PostgresTestDatabase.Variable} is not set: the local PostgreSQL test database tests are skipped.";
    }
}
