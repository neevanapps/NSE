using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>
/// Crash-and-recover scenarios (contract section A.18.9). LIMIT, stated once and for all: these tests prove the library's logic against an ideal
/// in-memory store and a simulated process death (an exception that stops all further work). They do NOT prove PostgreSQL transaction behaviour,
/// connection loss, lock/lease behaviour or genuine process death; that evidence is CP6.3-CP6.5.
/// </summary>
public class RecoveryMatrixTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);
    private static readonly Lazy<RecoveryKit.Outcome> Reference = new(() => RecoveryKit.Uninterrupted(Spec, Rows));

    private static bool Is(OutputRecord o, string type) => o.Type == type;

    private static bool Fill(OutputRecord o, string side) => o.Type == "FillRecord" && o.Payload.Contains($"\"Side\":\"{side}\"", StringComparison.Ordinal);

    internal static long StepFor(string scenario)
    {
        var outputs = Reference.Value.Snapshot.Outputs;
        var steps = Reference.Value.Snapshot.Steps.Count;
        return scenario switch
        {
            "first-step" => 0,
            "middle" => steps / 2,
            "last-data-step" => steps - 2,
            "finish" => steps - 1,
            "entry-intent-pending" => outputs.First(o => Is(o, "EntryIntent")).StepSeq,
            "first-buy-fill" => outputs.First(o => Fill(o, "Buy")).StepSeq,
            "position-opened" => outputs.First(o => Is(o, "PositionOpened")).StepSeq,
            "tp1-resolved" => outputs.First(o => Is(o, "Tp1Resolved")).StepSeq,
            "exit-order-created" => outputs.First(o => Is(o, "ExitOrderCreated")).StepSeq,
            "first-sell-fill" => outputs.First(o => Fill(o, "Sell")).StepSeq,
            "position-closed" => outputs.First(o => Is(o, "PositionClosed")).StepSeq,
            "unresolved-at-close" => outputs.First(o => Is(o, "UnresolvedAtClose")).StepSeq,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    public static IEnumerable<object[]> Scenarios()
    {
        foreach (var name in new[] { "first-step", "middle", "last-data-step", "finish", "entry-intent-pending", "first-buy-fill", "position-opened", "tp1-resolved", "exit-order-created", "first-sell-fill", "position-closed", "unresolved-at-close" })
        {
            yield return new object[] { name, CrashPoint.BeforeCommit };
            yield return new object[] { name, CrashPoint.AfterCommit };
        }
    }

    [Fact]
    public void TheReferenceData_ActuallyContainsEveryLifecycleThatTheMatrixTargets()
    {
        // If the synthetic session ever stops producing one of these, a scenario below would silently test a different step: fail instead.
        var types = Reference.Value.Snapshot.Outputs.Select(o => o.Type).ToHashSet();
        foreach (var required in new[] { "EntryIntent", "PositionOpened", "Tp1Resolved", "ExitOrderCreated", "PositionClosed", "UnresolvedAtClose", "FillRecord" })
        {
            Assert.Contains(required, types);
        }

        Assert.Contains(Reference.Value.Snapshot.Outputs, o => Fill(o, "Buy"));
        Assert.Contains(Reference.Value.Snapshot.Outputs, o => Fill(o, "Sell"));
        Assert.True(Reference.Value.Snapshot.Steps.Count > 50);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void CrashThenRecover_EqualsTheUninterruptedRun_OnEveryChannelTheLedgerTheStateAndTheProjections(string scenario, CrashPoint point)
    {
        var step = StepFor(scenario);
        var store = new InMemoryRunStore();
        var crash = new CrashAt(point, 1, (s, _) => s == step);

        RecoveryKit.RunUntilDeath(store, Spec, Rows, crash);

        Assert.True(crash.Fired);
        var durable = store.Load("run-1");
        Assert.Equal(point == CrashPoint.BeforeCommit ? step : step + 1, durable.Steps.Count);
        var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);
        RecoveryKit.AssertSameOutcome(Reference.Value, resumed);
        Assert.Equal(2, resumed.Snapshot.RecoveryLog.Count(l => l.Outcome.StartsWith("Opened", StringComparison.Ordinal)));
        output.WriteLine($"{scenario}/{point}: step {step}, durable steps at death {durable.Steps.Count}");
    }

    [Theory]
    [InlineData("entry-intent-pending", CrashPoint.AfterCommit)]
    [InlineData("tp1-resolved", CrashPoint.BeforeCommit)]
    [InlineData("exit-order-created", CrashPoint.AfterCommit)]
    public void WithAStateDigestAtEveryStep_TheCursorItselfIsStateVerified(string scenario, CrashPoint point)
    {
        // Every committed step carries a canonical state digest, so the replay proves the state at the cursor itself, not only at the last periodic checkpoint.
        var strict = RecoveryKit.Spec(Freeze, durable: new DurableOptions { CheckpointEverySteps = 1 });
        var step = StepFor(scenario);
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, strict, Rows, new CrashAt(point, 1, (s, _) => s == step));

        using var resumed = DurableRun.Open(store, "owner-2", strict);
        foreach (var row in Rows)
        {
            resumed.Offer(row);
        }

        var result = resumed.Finish();

        Assert.True(resumed.CheckpointsVerified >= step, $"{resumed.CheckpointsVerified} checkpoints verified for {step} replayed steps");
        Assert.Equal(resumed.StepsReplayed, resumed.CheckpointsVerified);
        Assert.Equal(Reference.Value.Result.Outputs, result.Outputs);
        Assert.Equal(RecoveryKit.ChannelHashes(Reference.Value.Result), RecoveryKit.ChannelHashes(result));
    }

    [Fact]
    public void RepeatedRecovery_IncludingADeathWhileStillReplaying_ConvergesOnTheSameOutcome()
    {
        var store = new InMemoryRunStore();
        var firstStep = StepFor("entry-intent-pending");
        RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == firstStep));

        // Second incarnation dies in the middle of replay (the caller stops after a few hundred rows), writing nothing.
        var stepsBefore = store.Load("run-1").Steps.Count;
        RecoveryKit.RunUntilDeath(store, Spec, Rows, new Counting(), "owner-2", (run, fed) =>
        {
            if (fed == 300)
            {
                Assert.Equal(RunPhase.Replaying, run.Phase);
                throw new SimulatedProcessDeath("during replay");
            }
        });
        Assert.Equal(stepsBefore, store.Load("run-1").Steps.Count);

        // Third incarnation gets further and dies after a later commit; the fourth completes.
        var laterStep = StepFor("first-sell-fill");
        RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(CrashPoint.BeforeCommit, 1, (s, _) => s == laterStep), "owner-3");
        Assert.Equal(laterStep, store.Load("run-1").Steps.Count);

        var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-4");

        RecoveryKit.AssertSameOutcome(Reference.Value, resumed);
        Assert.Equal(4, resumed.Snapshot.RecoveryLog.Count(l => l.Outcome.StartsWith("Opened", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResumingACompletedRun_ReVerifiesEverything_AndWritesNothing()
    {
        var store = new InMemoryRunStore();
        var first = RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-1");
        var commits = store.CommitCount;

        var again = RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-2");

        RecoveryKit.AssertSameOutcome(first, again);
        Assert.Equal(commits, store.CommitCount);
    }

    [Fact]
    public void PerOrderBrokerage_IsChargedExactlyOnce_AfterRecoveryAtEveryFillBoundary()
    {
        var fillSteps = Reference.Value.Snapshot.Outputs.Where(o => o.Type == "FillRecord").Select(o => o.StepSeq).Distinct().Take(4).ToList();
        foreach (var step in fillSteps)
        {
            var store = new InMemoryRunStore();
            RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(CrashPoint.BeforeCommit, 1, (s, _) => s == step));
            var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);

            var fills = resumed.Snapshot.Outputs.Where(o => o.Type == "FillRecord").Select(o => (FillRecord)OutputCodec.Decode(o.Payload)).ToList();
            Assert.Equal(fills.Count, fills.Select(f => f.FillId).Distinct().Count());
            var orders = fills.GroupBy(f => f.OrderId).ToList();
            foreach (var order in orders)
            {
                var brokerage = order.Sum(f => f.Costs.Brokerage);
                Assert.Equal(HarnessSettings_BrokeragePerOrder, brokerage);
            }

            var referenceFills = Reference.Value.Snapshot.Outputs.Where(o => o.Type == "FillRecord").Select(o => (FillRecord)OutputCodec.Decode(o.Payload)).ToList();
            Assert.Equal(referenceFills.Sum(f => f.Costs.Total), fills.Sum(f => f.Costs.Total));
        }

        Assert.NotEmpty(fillSteps);
    }

    private const decimal HarnessSettings_BrokeragePerOrder = 20m;
}
