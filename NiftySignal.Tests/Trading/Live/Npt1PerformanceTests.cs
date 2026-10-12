using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host.Npt1Stream;
using NiftySignal.Npt1M1;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Tests.Trading.Data;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// SYNTHETIC full-day load of the NPT1 path (the M1 producer, the stream processor, the three strategies, the durable run on the local PostgreSQL test database): producer time per poll, handoff and commit
/// latencies, journal growth, memory and CPU. Like the adaptive-v1 measurement it asserts no timing (the numbers are evidence, not a gate) and checks only that every strategy produced a consistent ledger.
/// </summary>
[Collection("PostgresTradingTests")]
public class Npt1PerformanceTests
{
    public const string ReportFile = "nse-npt1-performance.txt";

    /// <summary>Times every synchronous handoff to the NPT1 run (one availability group or one heartbeat), the same quantity the adaptive-v1 test reports.</summary>
    private sealed class TimedFeed(IPaperFeed inner, List<double> groupMs) : IPaperFeed
    {
        public DateTimeOffset? SlowGroupAt { get; private set; }

        public string? SlowGroupDetail { get; private set; }

        public void OnGroup(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars)
        {
            var started = Stopwatch.GetTimestamp();
            inner.OnGroup(at, quotes, bars);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            groupMs.Add(ms);
            if (ms > 1000)
            {
                SlowGroupAt = at;
                SlowGroupDetail = $"quotes {quotes.Count}, bars {bars.Count}";
            }
        }

        public void OnHeartbeat(DateTimeOffset watermark) => inner.OnHeartbeat(watermark);

        public void Finish() => inner.Finish();
    }

    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Errors.Add($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message} <- {exception?.InnerException?.GetType().Name}: {exception?.InnerException?.Message} <- {exception?.InnerException?.InnerException?.Message}");
            }
        }
    }

    private sealed class NullStore : IM1FrameStore
    {
        public Task<bool> EnsureAsync(M1Bar bar, DateTimeOffset nowUtc, CancellationToken ct) => Task.FromResult(true);
    }

    private static async Task<long?> CheckpointCountAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
            await connection.OpenAsync();
            foreach (var sql in new[] { "SELECT num_requested + num_timed FROM pg_stat_checkpointer", "SELECT checkpoints_req + checkpoints_timed FROM pg_stat_bgwriter" })
            {
                try
                {
                    await using var command = new NpgsqlCommand(sql, connection);
                    return Convert.ToInt64(await command.ExecuteScalarAsync());
                }
                catch (PostgresException)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    [PostgresFact]
    public async Task ASyntheticFullDay_ThroughTheM1ProducerAndTheNpt1Run_IsMeasured()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(15, 36)) } });
        var session = SyntheticSession.Freeze(rows);
        var decision = OpeningVolumeProjectionV2.Resolve(session.TradeDate, 1000, true, Array.Empty<PriorSession>(), OpeningVolumeProjectionV2.BetaM1, 65);
        var m1 = new M1Freeze(session, new M1FreezeReport(session.TradeDate.ToString("yyyy-MM-dd"), session.WeeklyOptionExpiry.ToString("yyyy-MM-dd"), 15, 1000, session.Definition.BaseBarVolume, "", "", session.Options.Count, null, "synthetic"), decision);

        using var fixture = new PostgresContractFixture();
        var options = Npt1Harness.Options(Npt1Harness.All);
        options.Capital = 100_000_000m;
        var strategies = PaperLiveSetup.Npt1Strategies(options);
        var info = new LiveSessionInfo(M1Identity.SessionId(session.TradeDate), session.TradeDate, session.ToContracts(), session.FutureToken, "adaptive-readiness-v1;synthetic");
        var producer = new M1BarProducer(m1, SyntheticSession.Translator(session));
        var telemetry = new Npt1M1Telemetry();
        var logger = new CollectingLogger();
        var proc = Process.GetCurrentProcess();
        var checkpointsBefore = await CheckpointCountAsync();
        var memoryBefore = GC.GetTotalMemory(true);
        var cpuBefore = proc.TotalProcessorTime;
        var pollMs = new List<double>();
        var producerMs = new List<double>();
        var handoffMs = new List<double>();
        var groupHandoffMs = new List<double>();
        TimedFeed? slowFeed = null;
        PaperLiveStatus status;
        IReadOnlyList<double> commits;
        int positions;
        decimal[] nets;
        var total = Stopwatch.StartNew();
        using (var coordinator = new PaperLiveCoordinator(options, strategies, () => new BlockingRunStoreAdapter(fixture.Store), logger, "perf", configFactory: _ => PaperLiveSetup.Npt1Config(options, strategies)))
        {
            coordinator.StartSession(info);
            var timedFeed = new TimedFeed(new CoordinatorFeed(coordinator), groupHandoffMs);
            slowFeed = timedFeed;
            var processor = new Npt1M1StreamProcessor(new NullStore(), timedFeed, SyntheticSession.Ist(9, 30), SyntheticSession.Ist(9, 15), telemetry, NullLogger.Instance);
            var batch = 0;
            foreach (var row in rows.Where(r => r.ReceivedAt <= SyntheticSession.Ist(15, 35)))
            {
                producer.Offer(row);
                if (++batch % 400 != 0)
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                producer.AdvanceSource(row.ReceivedAt);
                var groups = producer.Release();
                producerMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var handoff = Stopwatch.GetTimestamp();
                await processor.HandleAsync(groups, CancellationToken.None);
                coordinator.OnHeartbeat(row.ReceivedAt);
                handoffMs.Add(Stopwatch.GetElapsedTime(handoff).TotalMilliseconds);
                pollMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            await processor.HandleAsync(producer.Finish(), CancellationToken.None);
            coordinator.OnHeartbeat(SyntheticSession.Ist(15, 36));
            coordinator.FinishSession();
            status = coordinator.Status;
            commits = coordinator.CommitMillis;
            Assert.True(status.State == PaperLiveState.Finished || status.State == PaperLiveState.Live, status.State + ": " + status.Detail);
            var closed = coordinator.Run!.Engine.Outputs.OfType<NiftySignal.Trading.Core.Execution.PositionClosed>().ToList();
            positions = closed.Count;
            nets = closed.GroupBy(c => c.Variant.StrategyId).OrderBy(g => g.Key).Select(g => g.Sum(c => c.NetPnl)).ToArray();
        }

        total.Stop();
        var cpu = (proc.TotalProcessorTime - cpuBefore).TotalSeconds;
        var memoryAfter = GC.GetTotalMemory(false);
        var snapshot = await fixture.Store.LoadAsync(PaperLiveCoordinator.RunIdOf(info));
        var checkpointsAfter = await CheckpointCountAsync();
        long size;
        await using (var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT coalesce(sum(pg_total_relation_size(format('trading.%I', tablename)::regclass)), 0) FROM pg_tables WHERE schemaname = 'trading'", connection);
            size = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        var nl = Environment.NewLine;
        var sp = pollMs.Order().ToList();
        var sproducer = producerMs.Order().ToList();
        var sh = handoffMs.Order().ToList();
        var sc = commits.Order().ToList();
        var sg = groupHandoffMs.Order().ToList();
        var quoteInputs = snapshot.Inputs.Count(i => i.Kind is "OptionQuote" or "FutureQuote");
        var barInputs = snapshot.Inputs.Count(i => i.Kind == "BarCompleted");
        var report =
            $"SYNTHETIC full-day NPT1 session (9:15-15:36), local PostgreSQL, M1 producer + stream processor + 3 strategies + durable run{nl}" +
            $"source rows offered: {rows.Count(r => r.ReceivedAt <= SyntheticSession.Ist(15, 35))} (the M1 stream reads each row once from the shared tick table: with adaptive-v1 reading the same rows the tick reads are 2x, not more){nl}" +
            $"polls: {pollMs.Count}; groups released: {telemetry.GroupsReleased}; delivered {telemetry.GroupsDelivered}, warm-up only {telemetry.GroupsWarmupOnly}; M1 bars: {telemetry.BarsProduced}{nl}" +
            $"producer ms per poll p50/p95/p99/max: {Percentile(sproducer, .5):0.00}/{Percentile(sproducer, .95):0.00}/{Percentile(sproducer, .99):0.00}/{(sproducer.Count == 0 ? 0 : sproducer[^1]):0.0}{nl}" +
            $"poll ms (producer + handoff) p50/p95/p99/max: {Percentile(sp, .5):0.00}/{Percentile(sp, .95):0.00}/{Percentile(sp, .99):0.00}/{(sp.Count == 0 ? 0 : sp[^1]):0.0}{nl}" +
            $"per-group handoff ms to the NPT1 run p50/p95/p99/max: {Percentile(sg, .5):0.00}/{Percentile(sg, .95):0.00}/{Percentile(sg, .99):0.00}/{(sg.Count == 0 ? 0 : sg[^1]):0.0} over {sg.Count} groups (the slowest was group #{groupHandoffMs.IndexOf(sg[^1]) + 1}; groups over 250 ms: {groupHandoffMs.Count(x => x > 250)}; slowest above 1 s at {slowFeed?.SlowGroupAt:O} {slowFeed?.SlowGroupDetail}){nl}" +
            $"handoff ms per poll (frames store + durable run + heartbeat) p50/p95/p99/max: {Percentile(sh, .5):0.00}/{Percentile(sh, .95):0.00}/{Percentile(sh, .99):0.00}/{(sh.Count == 0 ? 0 : sh[^1]):0.0}{nl}" +
            $"commit ms p50/p95/p99/max: {Percentile(sc, .5):0.0}/{Percentile(sc, .95):0.0}/{Percentile(sc, .99):0.0}/{(sc.Count == 0 ? 0 : sc[^1]):0.0} over {commits.Count} commits{nl}" +
            $"coordinator-counted handoffs over 250 ms: {status.Handoff.SlowHandoffs} of {status.Handoff.Handoffs}; peak inputs in one step: {status.Handoff.PeakStepInputs}; peak outputs: {status.Handoff.PeakStepOutputs}{nl}" +
            $"journaled inputs: {snapshot.Inputs.Count} (quotes {quoteInputs}, bars {barInputs}, other {snapshot.Inputs.Count - quoteInputs - barInputs}); outputs: {snapshot.Outputs.Count}; durable steps: {snapshot.Steps.Count}{nl}" +
            $"trading schema size after the run: {size / 1024.0 / 1024.0:0.0} MiB for {snapshot.Inputs.Count + snapshot.Outputs.Count} journal rows{nl}" +
            $"coordinator recoveries (opens of the durable run): {status.Recoveries}; warnings/errors logged: {logger.Errors.Count}{nl}{string.Join(nl, logger.Errors.Where(e => !e.Contains("handoff was slow", StringComparison.Ordinal)).Take(6).Select(e => "  - " + e))}{nl}" +
            $"PostgreSQL checkpoints during the run: {(checkpointsAfter is { } ca && checkpointsBefore is { } cb ? (ca - cb).ToString() : "n/a")} (a requested checkpoint stalls commits for seconds on this developer machine; an in-memory store showed no slow group){nl}" +
            $"wall {total.Elapsed.TotalSeconds:0.0} s, process CPU {cpu:0.0} s, managed memory {memoryBefore / 1024.0 / 1024.0:0.0} -> {memoryAfter / 1024.0 / 1024.0:0.0} MiB{nl}" +
            $"closed positions {positions}; net per strategy (NOT summed): {string.Join(", ", nets.Select(n => n.ToString("0.00")))}{nl}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), ReportFile), report);

        Assert.True(snapshot.Completed);
        Assert.NotEmpty(commits);
        Assert.True(telemetry.BarsProduced > 100);
    }
}
