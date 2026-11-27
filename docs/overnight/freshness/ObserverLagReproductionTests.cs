// Reproduction only, NOT part of the release source tree. A passing test here means the behaviour named by the test EXISTS in candidate aa906ab.
// Real ProtectionPolicy + real StrategyRuntime + the four shipped strategy files (only their entry rules are replaced by the fixture's rules). No database.
using System.Text.Json.Nodes;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.ExecutionService;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

public partial class ExecutionLifecycleTests
{
    static CompiledStrategy ObserverLagShipped(string id)
    {
        var path = Path.Combine(TradingModelTests.RepoRoot(), "paper-engine-v2-strategies", "execution-service", id + ".json");
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        o["entry"] = JsonNode.Parse(ConfigTests.Valid)!["entry"]!.DeepClone();
        var parsed = StrategyConfigParser.Parse(o.ToJsonString());
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        return CompiledStrategy.Compile(parsed.Config!);
    }

    /// <summary>
    /// What the service's own gate says while the Observer is Live, its heartbeat is 0.5 s old and Execution's own ticks are 0.1 s old. Nothing in its input says how far the Observer
    /// has processed, so the answer is the same whether the Observer is current or minutes behind.
    /// </summary>
    static SafetyState ObserverLagGate(DateTimeOffset now, bool holding = false)
    {
        var ist = TimeOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var observer = new ObserverState(true, AdaptiveRuntimeStatus.Live, now.AddSeconds(-0.5));
        var safety = ProtectionPolicy.Evaluate(new ProtectionContext(now, ist, observer, holding, TimeSpan.Zero, now.AddMilliseconds(-100), true, null), new ExecutionServiceOptions());
        Assert.Equal(SafetyState.Healthy, safety);
        return safety;
    }

    /// <summary>Twenty warm-up bars (the shipped minimum), then an on-time bullish signal on bar 25. No bar after it unless a test adds one.</summary>
    static Sim ObserverLagSignal(string id)
    {
        var sim = New(ObserverLagShipped(id));
        for (var seq = 1; seq <= 24; seq++) sim.Bar(seq);
        sim.Bar(25, dir: 10);
        return sim;
    }

    [Theory]
    [InlineData("TOTAL_ACTIVITY_5_10_AVG5")] [InlineData("TOTAL_VOTE_5_OF_6_AVG5")]
    public void ObserverLag_PendingAvg5Entry_PlacesAndFills_TenMinutesAfterTheLastProcessedBar(string id)
    {
        var sim = ObserverLagSignal(id);
        // above the average: the intent waits
        sim.Tick(At(25), safety: ObserverLagGate(At(25)), vix: 14, averages: Average(100, bar: 25), quotes: [Ce()]);
        Assert.Empty(sim.Of<EntryOrderPlaced>());

        // ten minutes without any processed bar, then the pullback against the ten-minute-old average
        foreach (var s in new[] { 600, 601, 602 })
            sim.Tick(At(25, s), safety: ObserverLagGate(At(25, s)), vix: 14, averages: Average(110, bar: 25), quotes: [Ce(105, 106)]);

        Assert.Equal(At(25, 600), Assert.Single(sim.Of<EntryOrderPlaced>()).At);
        Assert.Single(sim.Of<EntryFilled>());
    }

    [Theory]
    [InlineData("TOTAL_ACTIVITY_5_10")] [InlineData("TOTAL_VOTE_5_OF_6")]
    public void ObserverLag_ImmediateReversal_OpensTheOpposite_FiveMinutesAfterTheLastProcessedBar(string id)
    {
        var sim = ObserverLagSignal(id);
        sim.Tick(At(25), safety: ObserverLagGate(At(25)), vix: 14, quotes: [Ce()]);
        sim.Tick(At(25, 2), safety: ObserverLagGate(At(25, 2), true), vix: 14, quotes: [Ce()]);
        Assert.Single(sim.Of<EntryFilled>());

        sim.Bar(27, dir: -10);                                            // opposite signal: exit + an intent that waits for the confirmed exit
        Assert.True(sim.Of<EntryIntentCreated>().Last().AwaitingFlat);

        // no further bar; the held contract becomes executable only five minutes later
        foreach (var s in new[] { 300, 301, 302 })
            sim.Tick(At(27, s), safety: ObserverLagGate(At(27, s), true), vix: 14, quotes: [Ce(), Pe()]);

        Assert.Single(sim.Of<ExitFilled>());
        var put = sim.Of<EntryOrderPlaced>().Last();
        Assert.Equal((OptionRight.Put, At(27, 300)), (put.Right, put.At));
        Assert.Equal(2, sim.Of<EntryFilled>().Count);
    }

    [Theory]
    [InlineData("TOTAL_ACTIVITY_5_10")] [InlineData("TOTAL_ACTIVITY_5_10_AVG5")] [InlineData("TOTAL_VOTE_5_OF_6")] [InlineData("TOTAL_VOTE_5_OF_6_AVG5")]
    public void ObserverLag_WithoutBars_TargetAndSessionExitsStillRun_ButNothingStopsALoss(string id)
    {
        var sim = ObserverLagSignal(id);
        foreach (var s in new[] { 1, 2, 3 })
            sim.Tick(At(25, s), safety: ObserverLagGate(At(25, s), s > 1), vix: 14, averages: Average(200, bar: 25), quotes: [Ce()]);
        var filled = Assert.Single(sim.Engine.Runtimes.Single().Positions());
        var quantity = filled.RemainingQuantity;

        // a collapse of the held contract: no exit is configured that reacts to it, and the opposite signal needs a new bar
        foreach (var s in new[] { 60, 61 })
            sim.Tick(At(25, s), safety: ObserverLagGate(At(25, s), true), vix: 14, quotes: [Ce(50, 50.5)]);
        Assert.Empty(sim.Of<ExitRequested>());

        // the partial profit target fires on quotes alone
        foreach (var s in new[] { 120, 121, 122 })
            sim.Tick(At(25, s), safety: ObserverLagGate(At(25, s), true), vix: 14, quotes: [Ce(190, 191)]);
        Assert.True(Assert.Single(sim.Engine.Runtimes.Single().Positions()).RemainingQuantity < quantity);

        // 15:08 IST liquidation fires on the clock alone
        foreach (var s in new[] { 1, 2, 3 })
            sim.Tick(At(338, s), safety: ObserverLagGate(At(338, s), true), vix: 14, quotes: [Ce()]);
        Assert.Equal(ExitReason.Terminal, sim.Of<ExitRequested>().Last().Reason);
        Assert.Equal(PositionState.Closed, Assert.Single(sim.Engine.Runtimes.Single().Positions()).State);
    }
}
