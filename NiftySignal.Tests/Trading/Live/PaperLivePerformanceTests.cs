using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host.PaperLive;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Tests.Trading.Data;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// SYNTHETIC full-day load of the live paper path on the local PostgreSQL test database (no recorded NIFTY session exists on this machine): the Observer engine, the bridge translation,
/// the coordinator, the durable run and PostgreSQL, one synchronous handoff per availability group exactly as the Host makes them. It reports the measurements to a file and checks that the
/// outputs still equal the historical adapter outputs. It asserts no timing (a timing assertion on a developer machine would be flaky); the numbers are evidence, not a gate.
/// </summary>
[Collection("PostgresTradingTests")]
public class PaperLivePerformanceTests
{
    public const string ReportFile = "nse-paperlive-performance.txt";

    private static double Percentile(IReadOnlyList<double> sorted, double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    [PostgresFact]
    public async Task ASyntheticFullDay_ThroughTheSynchronousHandoff_IsMeasured()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(15, 36)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var config = SyntheticSession.Config();
        var historical = SyntheticSession.NewRun(freeze);
        HistoricalAdapter.Run(historical, rows);

        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc && t.AvailableAt <= SyntheticSession.Ist(15, 35))
            {
                clean.Add((r.Token, t));
            }
        }

        var groups = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key)
            .Select(g => (At: g.Key, Ticks: (IReadOnlyList<(string Token, CleanObserverTick Tick)>)g.ToList())).ToList();
        using var fixture = new PostgresContractFixture();
        var options = new PaperLiveOptions { Enabled = true, Capital = 1_000_000m, BrokeragePerOrder = 20m, OptionTickSize = 0.05m };
        var families = LegacyStrategies.AsFamilies(config, SyntheticSession.Strategies());
        var strategies = families.Values.Select(f => new PaperLiveStrategy(f, config.Variants.Values.Where(v => v.StrategyId == f.StrategyId).ToList())).ToList();
        var info = new LiveSessionInfo("session:2026-09-22", freeze.TradeDate, freeze.ToContracts(), freeze.FutureToken, SyntheticSession.Translator(freeze).ReadinessSpec(freeze));
        var engine = new AdaptiveObserverEngine(freeze.Definition, freeze.WeeklyOptionExpiry, freeze.RiskFreeRate, freeze.StrongThreshold, freeze.SignalStartUtc, freeze.Options, freeze.ResidualAnchor);
        var tokens = freeze.Options.Select(o => o.Token).ToHashSet(StringComparer.Ordinal);
        var handoffs = new List<double>(groups.Count);
        var total = Stopwatch.StartNew();
        PaperLiveStatus status;
        IReadOnlyList<double> commits;
        IReadOnlyList<string> live;
        using (var coordinator = new PaperLiveCoordinator(options, strategies, () => new BlockingRunStoreAdapter(fixture.Store), NullLogger.Instance, "perf", configFactory: _ => config))
        {
            coordinator.StartSession(info);
            foreach (var g in groups)
            {
                var packages = engine.ProcessAvailabilityGroup(g.Ticks.ToArray());
                var (quotes, bars) = PaperLiveBridge.Translate(freeze.FutureToken, tokens, g.Ticks, packages.Select(p => (p, true)).ToList(), engine.FutureBars, freeze.Definition.BaseBarVolume, freeze.SignalStartUtc, info.ReadinessSpec);
                var started = Stopwatch.GetTimestamp();
                coordinator.OnGroup(g.At, quotes, bars);
                coordinator.OnHeartbeat(g.At);
                handoffs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            coordinator.FinishSession();
            status = coordinator.Status;
            commits = coordinator.CommitMillis;
            live = coordinator.Run!.Engine.Outputs.Select(OutputCodec.Encode).ToList();
        }

        total.Stop();
        var snapshot = await fixture.Store.LoadAsync("paper-live-v1:2026-09-22:session:2026-09-22");
        long size;
        await using (var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT coalesce(sum(pg_total_relation_size(format('trading.%I', tablename)::regclass)), 0) FROM pg_tables WHERE schemaname = 'trading'", connection);
            size = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        var sortedHandoffs = handoffs.Order().ToList();
        var sortedCommits = commits.Order().ToList();
        var nl = Environment.NewLine;
        var quoteInputs = snapshot.Inputs.Count(i => i.Kind is "OptionQuote" or "FutureQuote");
        var barInputs = snapshot.Inputs.Count(i => i.Kind == "BarCompleted");
        var report =
            $"SYNTHETIC full-day session (9:15-15:36), local PostgreSQL, synchronous handoff{nl}" +
            $"availability groups handed off: {groups.Count}{nl}" +
            $"journaled inputs: {snapshot.Inputs.Count} (quotes {quoteInputs}, bars {barInputs}, other {snapshot.Inputs.Count - quoteInputs - barInputs}){nl}" +
            $"outputs: {snapshot.Outputs.Count}; steps/commits durable: {snapshot.Steps.Count} (measured on this instance: {commits.Count}){nl}" +
            $"total wall time incl. Observer engine: {total.Elapsed.TotalSeconds:0.0} s{nl}" +
            $"commit ms p50/p95/p99/max: {Percentile(sortedCommits, .5):0.0}/{Percentile(sortedCommits, .95):0.0}/{Percentile(sortedCommits, .99):0.0}/{(sortedCommits.Count == 0 ? 0 : sortedCommits[^1]):0.0}{nl}" +
            $"handoff ms p50/p95/p99/max: {Percentile(sortedHandoffs, .5):0.00}/{Percentile(sortedHandoffs, .95):0.00}/{Percentile(sortedHandoffs, .99):0.00}/{sortedHandoffs[^1]:0.0}{nl}" +
            $"handoffs over 250 ms: {handoffs.Count(h => h > 250)}; coordinator-counted slow handoffs: {status.Handoff.SlowHandoffs}{nl}" +
            $"peak inputs in one step: {status.Handoff.PeakStepInputs}; peak outputs in one step: {status.Handoff.PeakStepOutputs}{nl}" +
            $"trading schema size after the run: {size / 1024.0 / 1024.0:0.0} MiB for {snapshot.Inputs.Count + snapshot.Outputs.Count} journal rows{nl}";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), ReportFile), report);

        Assert.Equal(historical.Engine.Outputs.Select(OutputCodec.Encode).ToList(), live);
        Assert.True(snapshot.Completed);
        Assert.NotEmpty(commits);
    }
}
