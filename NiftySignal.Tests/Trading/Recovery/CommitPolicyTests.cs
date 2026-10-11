using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Recovery;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>What a step is (contract section A.18.4): units, batching bounds, quiet-market commits and where a death between commits loses work.</summary>
public class CommitPolicyTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);
    private static readonly Lazy<RecoveryKit.Outcome> Reference = new(() => RecoveryKit.Uninterrupted(Spec, Rows));

    [Fact]
    public void EveryStep_EndsForADocumentedReason_AndTheUnitsAddUpToTheFinestGranularity()
    {
        var finest = RecoveryKit.Spec(Freeze, durable: new DurableOptions { MaxEngineTimeBetweenCommits = TimeSpan.FromTicks(1), CheckpointEverySteps = 1000 });
        var fine = RecoveryKit.Uninterrupted(finest, Rows);
        var steps = Reference.Value.Snapshot.Steps;
        var previous = DateTimeOffset.MinValue;
        var quiet = 0;

        foreach (var step in steps)
        {
            var hasOutput = step.OutputToExclusive > step.OutputFrom;
            var reasons = new List<string>();
            if (step.Kind != UnitKind.DataGroup) reasons.Add(step.Kind.ToString());
            if (hasOutput) reasons.Add("output");
            if (step.Units >= Spec.Durable!.MaxUnitsPerCommit) reasons.Add("max-units");
            if (previous != DateTimeOffset.MinValue && step.EngineTime - previous >= Spec.Durable!.MaxEngineTimeBetweenCommits) reasons.Add("engine-time");
            Assert.True(reasons.Count > 0 || step.StepSeq == 0, $"step {step.StepSeq} ended for no documented reason");
            if (!hasOutput && step.Kind == UnitKind.DataGroup)
            {
                quiet++;
            }

            previous = step.EngineTime;
        }

        // Batching only groups consecutive units: with the finest policy every data group is its own step, and the totals agree.
        Assert.Equal(fine.Snapshot.Steps.Count, steps.Sum(s => (long)s.Units));
        Assert.Equal(fine.Snapshot.Outputs.Select(o => o.ContentHash), Reference.Value.Snapshot.Outputs.Select(o => o.ContentHash));
        Assert.True(steps.Count < fine.Snapshot.Steps.Count / 10, "batching should cut the number of commits by an order of magnitude on this session");
        output.WriteLine($"steps={steps.Count} (finest policy {fine.Snapshot.Steps.Count}); output-less quiet-market commits={quiet}; total units={steps.Sum(s => (long)s.Units)}");
    }

    [Fact]
    public void ASilentStretch_StillAdvancesTheCursor_ThroughOutputlessCommitsEveryThirtySecondsOfEngineTime()
    {
        var steps = Reference.Value.Snapshot.Steps;
        var outputless = steps.Where(s => s.Kind == UnitKind.DataGroup && s.OutputToExclusive == s.OutputFrom).ToList();

        Assert.NotEmpty(outputless);
        Assert.All(outputless, s => Assert.True(s.Units >= 1));
        var previous = new Dictionary<long, DateTimeOffset>();
        foreach (var s in outputless.Where(s => s.StepSeq > 0))
        {
            var before = steps[(int)s.StepSeq - 1].EngineTime;
            Assert.True(s.EngineTime - before >= Spec.Durable!.MaxEngineTimeBetweenCommits || s.Units >= Spec.Durable.MaxUnitsPerCommit, $"output-less step {s.StepSeq} ended after only {(s.EngineTime - before).TotalSeconds:F1}s");
            previous[s.StepSeq] = before;
        }
    }

    [Fact]
    public void TheMaxUnitsBound_EndsAStepEvenWhenNothingElseWould()
    {
        var bounded = RecoveryKit.Spec(Freeze, durable: new DurableOptions { MaxUnitsPerCommit = 7, MaxEngineTimeBetweenCommits = TimeSpan.FromHours(1), CheckpointEverySteps = 1000 });

        var outcome = RecoveryKit.Uninterrupted(bounded, Rows);

        Assert.All(outcome.Snapshot.Steps, s => Assert.True(s.Units <= 7));
        Assert.Contains(outcome.Snapshot.Steps, s => s.Units == 7);
        Assert.Equal(Reference.Value.Snapshot.Outputs.Select(o => o.ContentHash), outcome.Snapshot.Outputs.Select(o => o.ContentHash));
    }

    [Theory]
    [InlineData(137)]
    [InlineData(2_001)]
    [InlineData(6_543)]
    [InlineData(9_876)]
    [InlineData(12_345)]
    [InlineData(16_999)]
    public void ADeathBetweenCommits_LosesOnlyTheUncommittedUnits_AndRecoveryRebuildsThemIdentically(int deathAfterRows)
    {
        var store = new InMemoryRunStore();

        RecoveryKit.RunUntilDeath(store, Spec, Rows, new Counting(), "owner-1", (_, fed) =>
        {
            if (fed == deathAfterRows)
            {
                throw new SimulatedProcessDeath($"after row {deathAfterRows}");
            }
        });

        var durable = store.Load("run-1");
        var lastOffered = durable.Steps.Count == 0 ? 0 : durable.Steps[^1].OfferedRows;
        Assert.True(lastOffered <= deathAfterRows);
        var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);
        RecoveryKit.AssertSameOutcome(Reference.Value, resumed);
        output.WriteLine($"death after row {deathAfterRows}: {durable.Steps.Count} steps durable (last at row {lastOffered}); {deathAfterRows - lastOffered} rows of work redone");
    }

    [Fact]
    public void TheCommitBounds_AreCompatibilityIdentity_ButTheCheckpointFrequencyIsNot()
    {
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 60));

        var otherBound = RecoveryKit.Spec(Freeze, durable: new DurableOptions { MaxEngineTimeBetweenCommits = TimeSpan.FromSeconds(31), CheckpointEverySteps = 5 });
        var halt = Assert.Throws<RecoveryHaltException>(() => RecoveryKit.ResumeAndFinish(store, otherBound, Rows));
        Assert.Equal(HaltKind.Compatibility, halt.Kind);
        Assert.Contains(halt.Details, d => d.StartsWith("DurableOptionsHash", StringComparison.Ordinal));

        var otherCheckpoints = RecoveryKit.Spec(Freeze, durable: new DurableOptions { CheckpointEverySteps = 3, KeepStateText = true });
        var resumed = RecoveryKit.ResumeAndFinish(store, otherCheckpoints, Rows);
        Assert.Equal(Reference.Value.Snapshot.Outputs.Select(o => o.ContentHash), resumed.Snapshot.Outputs.Select(o => o.ContentHash));
        Assert.Equal(Reference.Value.StateDigest, resumed.StateDigest);
    }

    [Fact]
    public void DurableOptions_RejectNonsense()
    {
        Assert.NotEmpty(new DurableOptions { MaxUnitsPerCommit = 0 }.Validate());
        Assert.NotEmpty(new DurableOptions { MaxEngineTimeBetweenCommits = TimeSpan.Zero }.Validate());
        Assert.NotEmpty(new DurableOptions { CheckpointEverySteps = 0 }.Validate());
        Assert.Empty(new DurableOptions().Validate());
        Assert.Throws<ArgumentException>(() => DurableRun.Open(new InMemoryRunStore(), "o", RecoveryKit.Spec(Freeze, durable: new DurableOptions { MaxUnitsPerCommit = 0 })));
    }

    [Fact]
    public void TheStateText_CanBeKeptForDiagnostics_AndNamesTheFirstDifferingLineOnDivergence()
    {
        var keep = RecoveryKit.Spec(Freeze, durable: new DurableOptions { CheckpointEverySteps = 1, KeepStateText = true });
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, keep, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 40));
        var run = store.Unsafe("run-1");
        var index = run.Checkpoints.FindIndex(c => c.StepSeq == 30);
        Assert.NotNull(run.Checkpoints[index].StateText);
        run.Checkpoints[index] = run.Checkpoints[index] with
        {
            StateText = run.Checkpoints[index].StateText!.Replace("loop/finished=0", "loop/finished=1"),
            StateDigest = "00" + run.Checkpoints[index].StateDigest[2..],
        };

        var halt = Assert.Throws<RecoveryHaltException>(() => RecoveryKit.ResumeAndFinish(store, keep, Rows));

        Assert.Equal(HaltKind.ReplayDivergence, halt.Kind);
        Assert.Contains(halt.Details, d => d.Contains("first differing line", StringComparison.Ordinal) && d.Contains("loop/finished", StringComparison.Ordinal));
    }
}
