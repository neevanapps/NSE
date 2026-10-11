using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host.PaperLive;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Tests.Trading.Data;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// End to end through the live bridge: a recorded (synthetic) session is run through the Adaptive Observer engine exactly like the Host's poll loop does (cleaned ticks, availability
/// groups, completed bars), translated by the bridge, delivered to the coordinator and the durable live run. The result must be identical, output for output, to the historical
/// adapter run over the same session (which builds the same bars through the translator): replay and live paper cannot disagree. Also with restarts in the middle.
/// </summary>
[Collection("PostgresTradingTests")]
public class LiveBridgeEquivalenceTests
{
    private const string Session = "session:2026-09-22";

    private sealed record Group(DateTimeOffset At, IReadOnlyList<(string Token, CleanObserverTick Tick)> Ticks);

    private static (SessionFreeze Freeze, IReadOnlyList<SourceTick> Rows, TradingConfig Config) Fixture()
    {
        var rows = SyntheticSession.Rows();
        return (SyntheticSession.Freeze(rows), rows, SyntheticSession.Config());
    }

    private static IReadOnlyList<Group> Groups(SessionFreeze freeze, IReadOnlyList<SourceTick> rows)
    {
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var row in rows)
        {
            if (normalizer.Process(row.Token, row.ToRaw()) is { } tick && tick.AvailableAt >= freeze.ObservationStartUtc && tick.AvailableAt <= SyntheticSession.Ist(15, 35))
            {
                clean.Add((row.Token, tick));
            }
        }

        return clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key)
            .Select(g => new Group(g.Key, g.ToList())).ToList();
    }

    private static IReadOnlyList<string> Historical(SessionFreeze freeze, IReadOnlyList<SourceTick> rows)
    {
        var run = SyntheticSession.NewRun(freeze);
        HistoricalAdapter.Run(run, rows);
        return run.Engine.Outputs.Select(OutputCodec.Encode).ToList();
    }

    private static PaperLiveCoordinator NewCoordinator(IRunStore store, SessionFreeze freeze, TradingConfig config, string owner)
    {
        var options = new PaperLiveOptions { Enabled = true, Capital = 1_000_000m, BrokeragePerOrder = 20m, OptionTickSize = 0.05m };
        var families = LegacyStrategies.AsFamilies(config, SyntheticSession.Strategies());
        var strategies = families.Values.Select(f => new PaperLiveStrategy(f, config.Variants.Values.Where(v => v.StrategyId == f.StrategyId).ToList())).ToList();
        return new PaperLiveCoordinator(options, strategies, () => store, NullLogger.Instance, owner, configFactory: _ => config);
    }

    private static LiveSessionInfo Info(SessionFreeze freeze)
        => new(Session, freeze.TradeDate, freeze.ToContracts(), freeze.FutureToken, SyntheticSession.Translator(freeze).ReadinessSpec(freeze));

    /// <summary>The Host's poll loop, for the groups [from, to): process the group in the Observer engine, translate what it produced, hand it to the coordinator, heartbeat.</summary>
    private static void Poll(PaperLiveCoordinator coordinator, AdaptiveObserverEngine engine, SessionFreeze freeze, IReadOnlyList<Group> groups, int from, int to, string readiness)
    {
        var optionTokens = freeze.Options.Select(o => o.Token).ToHashSet(StringComparer.Ordinal);
        for (var i = from; i < to; i++)
        {
            var group = groups[i];
            var packages = engine.ProcessAvailabilityGroup(group.Ticks.ToArray());
            var (quotes, bars) = PaperLiveBridge.Translate(freeze.FutureToken, optionTokens, group.Ticks, packages.Select(p => (p, true)).ToList(), engine.FutureBars, freeze.Definition.BaseBarVolume, freeze.SignalStartUtc, readiness);
            coordinator.OnGroup(group.At, quotes, bars);
            coordinator.OnHeartbeat(group.At);
        }
    }

    private static void AssertSame(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var first = Enumerable.Range(0, Math.Min(expected.Count, actual.Count)).FirstOrDefault(i => expected[i] != actual[i], -1);
        if (first >= 0)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "nse-equivalence.txt"), expected[first] + Environment.NewLine + actual[first]);
        }

        Assert.True(first < 0 && expected.Count == actual.Count, first < 0 ? $"counts {expected.Count} vs {actual.Count}" : $"output {first}: {expected[first]} <> {actual[first]}");
    }

    private static AdaptiveObserverEngine NewEngine(SessionFreeze f)
        => new(f.Definition, f.WeeklyOptionExpiry, f.RiskFreeRate, f.StrongThreshold, f.SignalStartUtc, f.Options, f.ResidualAnchor);

    [Fact]
    public void TheLiveBridgePath_ProducesTheSameOutputsAsTheHistoricalAdapter_OutputForOutput()
    {
        var (freeze, rows, config) = Fixture();
        var expected = Historical(freeze, rows);
        Assert.Contains(expected, p => p.Contains("\"type\":\"PositionClosed\"", StringComparison.Ordinal));          // the session really trades (plumbing strategies)
        var groups = Groups(freeze, rows);
        var store = new InMemoryRunStore();
        using var coordinator = NewCoordinator(store, freeze, config, "host");

        coordinator.StartSession(Info(freeze));
        Poll(coordinator, NewEngine(freeze), freeze, groups, 0, groups.Count, Info(freeze).ReadinessSpec);
        coordinator.FinishSession();

        Assert.Equal(PaperLiveState.Finished, coordinator.Status.State);
        AssertSame(expected, coordinator.Run!.Engine.Outputs.Select(OutputCodec.Encode).ToList());
        Assert.True(store.Load(coordinator.Status.RunId!).Completed);
    }

    [Fact]
    public void ARestartAnywhereInTheSession_ReDeliveringTheObserversRecovery_ConvergesToTheSameOutputs()
    {
        var (freeze, rows, config) = Fixture();
        var expected = Historical(freeze, rows);
        var groups = Groups(freeze, rows);
        var readiness = Info(freeze).ReadinessSpec;
        foreach (var split in new[] { 5, groups.Count / 7, groups.Count / 3, groups.Count / 2, groups.Count * 3 / 4, groups.Count - 2 })
        {
            var store = new InMemoryRunStore();
            using (var first = NewCoordinator(store, freeze, config, "host-1"))
            {
                first.StartSession(Info(freeze));
                Poll(first, NewEngine(freeze), freeze, groups, 0, split, readiness);
            }                                                                              // the process dies here (the uncommitted tail is lost)

            using var second = NewCoordinator(store, freeze, config, "host-2");
            second.StartSession(Info(freeze));
            // the Observer recovers by replaying ITS state from raw ticks (a fresh engine) and keeps polling: groups the paper path already has are offered again
            Poll(second, NewEngine(freeze), freeze, groups, 0, groups.Count, readiness);
            second.FinishSession();

            Assert.Equal(PaperLiveState.Finished, second.Status.State);
            AssertSame(expected, second.Run!.Engine.Outputs.Select(OutputCodec.Encode).ToList());
        }
    }

    [PostgresFact]
    public void OnPostgreSql_ARestartInTheMiddleOfARecordedSession_ConvergesToTheHistoricalOutputs()
    {
        var (freeze, rows, config) = Fixture();
        var expected = Historical(freeze, rows);
        var groups = Groups(freeze, rows);
        var readiness = Info(freeze).ReadinessSpec;
        using var fixture = new PostgresContractFixture();
        var store = new BlockingRunStoreAdapter(fixture.Store);
        using (var first = NewCoordinator(store, freeze, config, "host-1"))
        {
            first.StartSession(Info(freeze));
            Poll(first, NewEngine(freeze), freeze, groups, 0, groups.Count / 2, readiness);
        }

        using var second = NewCoordinator(store, freeze, config, "host-2");
        second.StartSession(Info(freeze));
        Poll(second, NewEngine(freeze), freeze, groups, 0, groups.Count, readiness);
        second.FinishSession();

        Assert.Equal(PaperLiveState.Finished, second.Status.State);
        AssertSame(expected, second.Run!.Engine.Outputs.Select(OutputCodec.Encode).ToList());
        Assert.Empty(fixture.Postgres.VerifyDerivedAsync(second.Status.RunId!).GetAwaiter().GetResult());
    }
}
