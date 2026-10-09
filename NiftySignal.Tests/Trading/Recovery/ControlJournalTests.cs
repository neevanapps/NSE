using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>The write-ahead control journal (contract section A.18.4): operator commands, feed-health events, source-progress and clock-advance records.</summary>
public class ControlJournalTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);

    private static readonly Lazy<RecoveryKit.Outcome> Plain = new(() => RecoveryKit.Uninterrupted(Spec, Rows));

    /// <summary>The row index just after the first entry intent: the kill below lands while that entry is pending.</summary>
    private static int KillRowIndex()
    {
        var intent = Plain.Value.Snapshot.Outputs.First(o => o.Type == "EntryIntent");
        var at = intent.At.AddMilliseconds(300);
        return Rows.ToList().FindIndex(r => (r.ReceivedAt > r.ExchangeTimestamp ? r.ReceivedAt : r.ExchangeTimestamp) >= at);
    }

    private static OperatorCommandEvent Kill(int rowIndex, long sequence = 1, string actor = "tester")
        => new(Row(rowIndex), sequence, OperatorCommand.Kill, "*", actor, "scripted kill");

    private static DateTimeOffset Row(int index) => Rows[index].ReceivedAt > Rows[index].ExchangeTimestamp ? Rows[index].ReceivedAt : Rows[index].ExchangeTimestamp;

    /// <summary>Feeds all rows, submitting each control just before the first row it is available with (what a live operator console would do).</summary>
    private static RecoveryKit.Outcome Feed(InMemoryRunStore store, string owner, ICrashPoints? crash, params (int RowIndex, EngineEvent Event)[] controls)
    {
        using var run = DurableRun.Open(store, owner, Spec, crash);
        for (var i = 0; i < Rows.Count; i++)
        {
            foreach (var c in controls.Where(c => c.RowIndex == i))
            {
                run.Control(c.Event);
            }

            run.Offer(Rows[i]);
        }

        var result = run.Finish();
        var state = run.Run.CaptureState();
        return new RecoveryKit.Outcome(result, store.Load("run-1"), state.Text, state.Digest, store);
    }

    private static void FeedUntilDeath(InMemoryRunStore store, ICrashPoints crash, params (int RowIndex, EngineEvent Event)[] controls)
    {
        try
        {
            Feed(store, "owner-1", crash, controls);
            throw new InvalidOperationException("The crash point was never reached.");
        }
        catch (SimulatedProcessDeath)
        {
            store.SimulateProcessDeath("owner-1");
        }
    }

    [Fact]
    public void AKillWhileAnEntryIsPending_IsJournaledBeforeItIsApplied_AndCancelsTheEntry()
    {
        var index = KillRowIndex();
        var store = new InMemoryRunStore();

        var outcome = Feed(store, "owner-1", null, (index, Kill(index)));

        var snapshot = outcome.Snapshot;
        Assert.Contains(snapshot.Outputs, o => o.Type == "KillSwitchChanged");
        Assert.Contains(snapshot.Outputs, o => o.Type == "CancelRequest");
        Assert.Equal(new[] { "Operator:1" }, snapshot.Controls.Select(c => c.Id));
        Assert.Single(snapshot.Steps, s => s.AppliedControls.Contains("Operator:1"));
    }

    [Fact]
    public void PersistedButNotApplied_TheJournaledControlIsAppliedByRecovery_EvenIfTheCallerNeverResendsIt()
    {
        var index = KillRowIndex();
        var reference = Feed(new InMemoryRunStore(), "owner-ref", null, (index, Kill(index)));

        foreach (var resend in new[] { true, false })
        {
            var store = new InMemoryRunStore();
            FeedUntilDeath(store, new CrashAt(CrashPoint.JournalWritten, 1, (_, detail) => detail == "Operator:1"), (index, Kill(index)));

            var journaled = store.Load("run-1");
            Assert.Equal(new[] { "Operator:1" }, journaled.Controls.Select(c => c.Id));
            Assert.DoesNotContain(journaled.Steps, s => s.AppliedControls.Contains("Operator:1"));
            Assert.DoesNotContain(journaled.Outputs, o => o.Type == "KillSwitchChanged");

            var resumed = resend
                ? Feed(store, "owner-2", null, (index, Kill(index)))
                : RecoveryKit.ResumeAndFinish(store, Spec, Rows);

            RecoveryKit.AssertSameOutcome(reference, resumed);
            Assert.Equal(1, resumed.Snapshot.Steps.Count(s => s.AppliedControls.Contains("Operator:1")));
            Assert.Equal(1, resumed.Snapshot.Outputs.Count(o => o.Type == "KillSwitchChanged"));
        }
    }

    [Fact]
    public void AppliedButNotCheckpointed_ReplayDeliversTheControlInTheSameStepAgain()
    {
        var index = KillRowIndex();
        var reference = Feed(new InMemoryRunStore(), "owner-ref", null, (index, Kill(index)));
        var step = reference.Snapshot.Steps.First(s => s.AppliedControls.Contains("Operator:1")).StepSeq;
        var store = new InMemoryRunStore();

        FeedUntilDeath(store, new CrashAt(CrashPoint.BeforeCommit, 1, (s, _) => s == step), (index, Kill(index)));

        var durable = store.Load("run-1");
        Assert.Equal(step, durable.Steps.Count);
        Assert.Single(durable.Controls);
        var resumed = Feed(store, "owner-2", null, (index, Kill(index)));
        RecoveryKit.AssertSameOutcome(reference, resumed);
        Assert.Equal(step, resumed.Snapshot.Steps.First(s => s.AppliedControls.Contains("Operator:1")).StepSeq);
    }

    [Fact]
    public void DuplicateDelivery_IsRecognisedByIdentity_AndADifferentPayloadUnderTheSameIdentityIsAConflict()
    {
        var index = KillRowIndex();
        var store = new InMemoryRunStore();
        using var run = DurableRun.Open(store, "owner-1", Spec);
        for (var i = 0; i < index; i++)
        {
            run.Offer(Rows[i]);
        }

        Assert.True(run.Control(Kill(index)));
        Assert.False(run.Control(Kill(index)));
        Assert.Throws<ConflictFaultException>(() => run.Control(Kill(index, actor: "someone-else")));

        Assert.False(run.Faulted);
        Assert.Single(store.Load("run-1").Controls);
        for (var i = index; i < Rows.Count; i++)
        {
            run.Offer(Rows[i]);
        }

        var result = run.Finish();
        Assert.Equal(1, store.Load("run-1").Outputs.Count(o => o.Type == "KillSwitchChanged"));
        Assert.True(result.Outputs > 0);
    }

    [Fact]
    public void AControlWhoseTimeIsAlreadyDecided_IsRejected_AndNeverJournaled()
    {
        var store = new InMemoryRunStore();
        using var run = DurableRun.Open(store, "owner-1", Spec);
        for (var i = 0; i < 3_000; i++)
        {
            run.Offer(Rows[i]);
        }

        var late = new OperatorCommandEvent(Row(10), 9, OperatorCommand.Kill, "*", "tester", "late");

        Assert.Throws<ArgumentException>(() => run.Control(late));
        Assert.Empty(store.Load("run-1").Controls);
    }

    [Fact]
    public void ANewControl_CannotBeAcceptedWhileTheDurableStepsAreStillBeingReplayed()
    {
        var store = new InMemoryRunStore();
        RecoveryKit.RunUntilDeath(store, Spec, Rows, new CrashAt(CrashPoint.AfterCommit, 1, (s, _) => s == 40));
        using var run = DurableRun.Open(store, "owner-2", Spec);
        for (var i = 0; i < 100; i++)
        {
            run.Offer(Rows[i]);
        }

        Assert.Equal(RunPhase.Replaying, run.Phase);
        Assert.Throws<InvalidOperationException>(() => run.Control(new OperatorCommandEvent(Row(100), 3, OperatorCommand.Kill, "*", "tester", "too early")));
    }

    [Fact]
    public void FeedHealthEvents_AreJournaledAndRecovered_LikeOperatorCommands()
    {
        var down = 4_000;
        var up = 4_600;
        var degraded = new FeedHealthEvent(Row(down), 1, FeedHealthState.Degraded, FeedFaultReason.NoMessageWatchdog, "scripted outage");
        var healthy = new FeedHealthEvent(Row(up), 2, FeedHealthState.Healthy, FeedFaultReason.None, "scripted recovery");
        var reference = Feed(new InMemoryRunStore(), "owner-ref", null, (down, degraded), (up, healthy));
        var store = new InMemoryRunStore();

        FeedUntilDeath(store, new CrashAt(CrashPoint.JournalWritten, 2, null), (down, degraded), (up, healthy));
        var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);

        RecoveryKit.AssertSameOutcome(reference, resumed);
        Assert.Equal(new[] { "Health:1", "Health:2" }, resumed.Snapshot.Controls.Select(c => c.Id));
        Assert.All(new[] { "Health:1", "Health:2" }, id => Assert.Equal(1, resumed.Snapshot.Steps.Count(s => s.AppliedControls.Contains(id))));
    }

    // ------------------------------------------------------------------ quiet market: commits that are not caused by a data row

    private static (int Cut, DateTimeOffset At) QuietCut()
    {
        var step = Plain.Value.Snapshot.Steps[(int)Plain.Value.Snapshot.Outputs.First(o => o.Type == "EntryIntent").StepSeq];
        return ((int)step.OfferedRows, step.EngineTime);
    }

    private static RecoveryKit.Outcome QuietRun(InMemoryRunStore store, string owner, ICrashPoints? crash, bool redeclare)
    {
        var (cut, at) = QuietCut();
        using var run = DurableRun.Open(store, owner, Spec, crash);
        foreach (var row in Rows.Take(cut))
        {
            run.Offer(row);
        }

        if (redeclare)
        {
            run.DeclareSourceProgress(at.AddSeconds(30));
            run.AdvanceClock(at.AddSeconds(28));
        }

        var result = run.Finish();
        var state = run.Run.CaptureState();
        return new RecoveryKit.Outcome(result, store.Load("run-1"), state.Text, state.Digest, store);
    }

    [Fact]
    public void ASilentMarket_ProducesAdvanceCommits_ForDeadlinesThatNoDataRowWouldHaveFired()
    {
        var store = new InMemoryRunStore();

        var outcome = QuietRun(store, "owner-1", null, redeclare: true);

        var kinds = outcome.Snapshot.Steps.Select(s => s.Kind).ToList();
        Assert.Contains(UnitKind.Advance, kinds);
        Assert.Contains(outcome.Snapshot.Controls, c => c.Kind == ControlKind.SourceProgress);
        Assert.Contains(outcome.Snapshot.Controls, c => c.Kind == ControlKind.Advance);
        var advance = outcome.Snapshot.Steps.First(s => s.Kind == UnitKind.Advance);
        Assert.Contains(advance.AppliedControls, id => id.StartsWith("Advance:", StringComparison.Ordinal));
        Assert.True(advance.OutputToExclusive > advance.OutputFrom, "the advance fired a deadline that produced output");
    }

    [Theory]
    [InlineData(CrashPoint.JournalWritten, true)]
    [InlineData(CrashPoint.JournalWritten, false)]
    [InlineData(CrashPoint.BeforeCommit, true)]
    [InlineData(CrashPoint.BeforeCommit, false)]
    [InlineData(CrashPoint.AfterCommit, true)]
    [InlineData(CrashPoint.AfterCommit, false)]
    public void ASilentMarketRun_SurvivesADeathAtTheAdvance_WithOrWithoutTheCallerRedeclaring(CrashPoint point, bool redeclare)
    {
        var reference = QuietRun(new InMemoryRunStore(), "owner-ref", null, redeclare: true);
        var advanceStep = reference.Snapshot.Steps.First(s => s.Kind == UnitKind.Advance).StepSeq;
        var store = new InMemoryRunStore();
        var crash = new CrashAt(point, 1, (s, detail) => point == CrashPoint.JournalWritten ? detail.StartsWith("Advance:", StringComparison.Ordinal) : s == advanceStep && detail == nameof(UnitKind.Advance));
        try
        {
            QuietRun(store, "owner-1", crash, redeclare: true);
            throw new InvalidOperationException("never crashed");
        }
        catch (SimulatedProcessDeath)
        {
            store.SimulateProcessDeath("owner-1");
        }

        var resumed = QuietRun(store, "owner-2", null, redeclare);

        RecoveryKit.AssertSameOutcome(reference, resumed);
        Assert.Equal(1, resumed.Snapshot.Steps.Count(s => s.Kind == UnitKind.Advance));
    }

    [Fact]
    public void AnAdvanceThatFiresNothing_IsNotJournaledAndCommitsNothing()
    {
        var store = new InMemoryRunStore();
        using var run = DurableRun.Open(store, "owner-1", Spec);
        foreach (var row in Rows.Take(200))
        {
            run.Offer(row);
        }

        var steps = store.Load("run-1").Steps.Count;
        var controls = store.Load("run-1").Controls.Count;

        var applied = run.AdvanceClock(run.Run.Translator.Boundary);
        var repeated = run.DeclareSourceProgress(Rows[199].ReceivedAt.AddMilliseconds(-1));

        Assert.False(applied);
        Assert.False(repeated);
        Assert.Equal(steps, store.Load("run-1").Steps.Count);
        Assert.Equal(controls, store.Load("run-1").Controls.Count);
    }

    [Fact]
    public void TradingRun_AdvanceClock_DoesNothingWhenNoDeadlineFires_AndReportsAUnitWhenOneDoes()
    {
        var (cut, at) = QuietCut();
        var run = new TradingRun(Freeze, Spec.Translator, Spec.Config, Spec.Strategies, Spec.Settings, Spec.Recorder);
        var units = new List<UnitInfo>();
        run.UnitObserver = units.Add;
        foreach (var row in Rows.Take(cut))
        {
            run.Offer(row);
            run.Pump(heartbeat: false);
        }

        run.Translator.AdvanceSource(at.AddSeconds(30));
        run.Pump(heartbeat: false);
        var nextDeadline = run.Loop.PendingDeadlines.First().Time;
        var now = run.Loop.Now;
        var quiet = nextDeadline.AddTicks(-1);
        Assert.True(quiet > now, "the test needs a gap between now and the next pending deadline");
        var unitsBefore = units.Count;
        var outputsBefore = run.Engine.Outputs.Count;

        var applied = run.AdvanceClock(quiet);

        Assert.False(applied);
        Assert.Equal(now, run.Loop.Now);
        Assert.Equal(unitsBefore, units.Count);
        Assert.Equal(outputsBefore, run.Engine.Outputs.Count);

        Assert.True(run.AdvanceClock(nextDeadline.AddSeconds(1)));
        Assert.Equal(UnitKind.Advance, units[^1].Kind);
        Assert.Equal(unitsBefore + 1, units.Count);
        Assert.True(run.Loop.Now >= nextDeadline);
    }

    [Fact]
    public void TradingRun_ReportsOneUnitPerDeliveredGroup_AndAFinishUnit_AndTheFinalLinesMatchTheChannels()
    {
        var run = new TradingRun(Freeze, Spec.Translator, Spec.Config, Spec.Strategies, Spec.Settings, Spec.Recorder);
        var units = new List<UnitInfo>();
        run.UnitObserver = units.Add;
        foreach (var row in Rows)
        {
            run.Offer(row);
            run.Pump(heartbeat: false);
        }

        var result = run.Finish();
        var (audit, summaries) = run.FinalChannelLines();

        Assert.Equal(result.Translator.GroupsReleased - result.Transport.GroupsAfterHorizon, units.Count(u => u.Kind == UnitKind.DataGroup));
        Assert.Equal(UnitKind.Finish, units[^1].Kind);
        Assert.Single(units, u => u.Kind == UnitKind.Finish);
        Assert.Equal(result.Outputs, units[^1].OutputCount);
        Assert.Equal(result.Channels["audit"].Count, audit.Count);
        Assert.Equal(result.Channels["summaries"].Count, summaries.Count);
    }

    [Fact]
    public void AnAdvanceBeyondTheStableBoundary_IsRefused()
    {
        var store = new InMemoryRunStore();
        using var run = DurableRun.Open(store, "owner-1", Spec);
        foreach (var row in Rows.Take(200))
        {
            run.Offer(row);
        }

        Assert.Throws<ArgumentException>(() => run.AdvanceClock(run.Run.Translator.Boundary.AddMinutes(1)));
    }
}
