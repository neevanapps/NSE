using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>Store semantics (atomicity, fencing, idempotency, scripted faults) and how the durable run reacts to them. In-memory limits as stated in <see cref="RecoveryMatrixTests"/>.</summary>
public class StoreAndFaultTests
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    private static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);
    private static readonly Lazy<RecoveryKit.Outcome> Reference = new(() => RecoveryKit.Uninterrupted(Spec, Rows));

    /// <summary>Rebuilds the commit batch of one step exactly as the durable run produced it.</summary>
    private static CommitBatch BatchOf(RunSnapshot s, int step)
    {
        var record = s.Steps[step];
        var outputs = s.Outputs.Where(o => o.StepSeq == step).ToList();
        return new CommitBatch(record, outputs, ProjectionBuilder.Build(outputs), s.Checkpoints.FirstOrDefault(c => c.StepSeq == step), record.Kind == UnitKind.Finish ? s.Final : null);
    }

    private static (InMemoryRunStore Store, OwnershipToken Token, RunSnapshot Source) EmptyRunWithHeader()
    {
        var source = Reference.Value.Snapshot;
        var store = new InMemoryRunStore();
        var token = store.Acquire("run-1", "owner-1");
        store.CreateRun(token, source.Header!);
        return (store, token, source);
    }

    // ------------------------------------------------------------------ store contract

    [Fact]
    public void Commit_IsIdempotentForAnIdenticalStep_AndConflictsForADifferentOne()
    {
        var (store, token, source) = EmptyRunWithHeader();
        var batch = BatchOf(source, 0);

        Assert.Equal(CommitResult.Committed, store.Commit(token, batch));
        Assert.Equal(CommitResult.AlreadyCommitted, store.Commit(token, batch));
        Assert.Equal(1, store.CommitCount);
        var different = batch with { Step = StepRecord.Create(batch.Step with { OfferedRows = batch.Step.OfferedRows + 1, ContentHash = string.Empty }) };
        Assert.Throws<ConflictFaultException>(() => store.Commit(token, different));
        Assert.Single(store.Load("run-1").Steps);
    }

    [Fact]
    public void Commit_RejectsGapsInvalidHashesAndInconsistentBatches_WithoutChangingAnything()
    {
        var (store, token, source) = EmptyRunWithHeader();
        var step5 = BatchOf(source, 5);
        Assert.Throws<SequenceGapException>(() => store.Commit(token, step5));

        var batch = BatchOf(source, 0);
        var corruptStep = batch with { Step = batch.Step with { ContentHash = "00" } };
        Assert.Throws<ConflictFaultException>(() => store.Commit(token, corruptStep));

        if (batch.Outputs.Count > 0)
        {
            var corruptOutput = batch with { Outputs = new[] { batch.Outputs[0] with { Payload = batch.Outputs[0].Payload + " " } }.Concat(batch.Outputs.Skip(1)).ToList() };
            Assert.Throws<ConflictFaultException>(() => store.Commit(token, corruptOutput));
        }

        var wrongCount = batch with { Outputs = new List<OutputRecord> { new(0, 0, "AuditRecord", "S", "A", DateTimeOffset.UnixEpoch, "{}", "00") } };
        Assert.ThrowsAny<Exception>(() => store.Commit(token, wrongCount));
        var snapshot = store.Load("run-1");
        Assert.Empty(snapshot.Steps);
        Assert.Empty(snapshot.Outputs);
        Assert.Empty(snapshot.Projections);
    }

    [Fact]
    public void Header_AndControls_AreIdempotentByIdentity_AndConflictOnDifferentContent()
    {
        var (store, token, source) = EmptyRunWithHeader();

        Assert.Equal(AppendResult.Duplicate, store.CreateRun(token, source.Header!));
        Assert.Throws<ConflictFaultException>(() => store.CreateRun(token, RunHeader.Create(source.Header! with { Horizon = source.Header!.Horizon.AddMinutes(1), ContentHash = string.Empty })));
        var control = ControlCodec_Operator(7, "tester");
        Assert.Equal(AppendResult.Appended, store.AppendControl(token, control));
        Assert.Equal(AppendResult.Duplicate, store.AppendControl(token, control));
        Assert.Throws<ConflictFaultException>(() => store.AppendControl(token, ControlCodec_Operator(7, "someone-else")));
        Assert.Single(store.Load("run-1").Controls);
    }

    [Fact]
    public void EveryWriteMethod_IsFencedByTheOwnershipEpoch_AfterATakeover()
    {
        var (store, oldToken, source) = EmptyRunWithHeader();
        var newToken = store.Takeover("run-1", "owner-2");

        Assert.True(newToken.Epoch > oldToken.Epoch);
        Assert.Throws<FencedOutException>(() => store.CreateRun(oldToken, source.Header!));
        Assert.Throws<FencedOutException>(() => store.AppendControl(oldToken, ControlCodec_Operator(1, "x")));
        Assert.Throws<FencedOutException>(() => store.Commit(oldToken, BatchOf(source, 0)));
        Assert.Throws<FencedOutException>(() => store.AppendRecoveryLog(oldToken, new RecoveryLogEntry(1, 1, 0, 0, 0, "x", "x")));
        Assert.Equal(CommitResult.Committed, store.Commit(newToken, BatchOf(source, 0)));
        store.Release(oldToken);
        Assert.Throws<OwnershipConflictException>(() => store.Acquire("run-1", "owner-3"));
    }

    [Fact]
    public void ScriptedFaults_FailBeforeApply_FailDuringApply_AndBothUnknownModes_BehaveAsDocumented()
    {
        foreach (var (mode, applied, throws) in new[]
        {
            (StoreFaultMode.FailBeforeApply, false, typeof(StoreUnavailableException)),
            (StoreFaultMode.FailDuringApply, false, typeof(StoreUnavailableException)),
            (StoreFaultMode.UnknownApplied, true, typeof(UnknownCommitOutcomeException)),
            (StoreFaultMode.UnknownNotApplied, false, typeof(UnknownCommitOutcomeException)),
        })
        {
            var (store, token, source) = EmptyRunWithHeader();
            store.Inject(new StoreFault(StoreOperation.Commit, 1, mode));
            var batch = BatchOf(source, 0);

            Assert.Throws(throws, () => store.Commit(token, batch));

            var snapshot = store.Load("run-1");
            Assert.Equal(applied ? 1 : 0, snapshot.Steps.Count);
            Assert.Equal(applied ? batch.Outputs.Count : 0, snapshot.Outputs.Count);
            Assert.Equal(applied ? batch.Projections.Count : 0, snapshot.Projections.Count);
            Assert.Equal(applied ? CommitResult.AlreadyCommitted : CommitResult.Committed, store.Commit(token, batch));
        }
    }

    [Fact]
    public void AbruptProcessDeath_ReleasesTheLock_ButTheDeadTokenStaysFenced()
    {
        var (store, deadToken, source) = EmptyRunWithHeader();
        store.SimulateProcessDeath("owner-1");

        var token = store.Acquire("run-1", "owner-2");

        Assert.Throws<FencedOutException>(() => store.Commit(deadToken, BatchOf(source, 0)));
        Assert.Equal(CommitResult.Committed, store.Commit(token, BatchOf(source, 0)));
    }

    // ------------------------------------------------------------------ durable run reaction to faults

    [Theory]
    [InlineData(StoreFaultMode.FailBeforeApply)]
    [InlineData(StoreFaultMode.FailDuringApply)]
    public void ADefiniteCommitFailure_DiscardsTheInstance_AndAFreshOneResumesFromDurableData(StoreFaultMode mode)
    {
        var store = new InMemoryRunStore();
        store.Inject(new StoreFault(StoreOperation.Commit, 50, mode));
        var run = DurableRun.Open(store, "owner-1", Spec);
        var thrown = Assert.Throws<PersistenceFaultException>(() =>
        {
            foreach (var row in Rows)
            {
                run.Offer(row);
            }
        });

        Assert.True(run.Faulted);
        Assert.IsType<StoreUnavailableException>(thrown.InnerException);
        Assert.Equal(49, store.Load("run-1").Steps.Count);
        Assert.Throws<InstanceFaultedException>(() => run.Offer(Rows[0]));
        Assert.Throws<InstanceFaultedException>(() => run.Finish());
        LedgerCheck.Verify(store.Load("run-1"));
        run.Dispose();

        var resumed = RecoveryKit.ResumeAndFinish(store, Spec, Rows);

        RecoveryKit.AssertSameOutcome(Reference.Value, resumed);
    }

    [Fact]
    public void UnknownOutcome_AppliedOnTheStore_IsResolvedByIdentity_WithoutDuplicatingAnything()
    {
        var store = new InMemoryRunStore();
        store.Inject(new StoreFault(StoreOperation.Commit, 50, StoreFaultMode.UnknownApplied));

        var outcome = RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-1");

        RecoveryKit.AssertSameOutcome(Reference.Value, outcome);
        Assert.Equal(Reference.Value.Store.CommitCount, store.CommitCount);
    }

    [Fact]
    public void UnknownOutcome_NotAppliedOnTheStore_IsResolvedByReapplyingTheSameBatchOnce()
    {
        var store = new InMemoryRunStore();
        store.Inject(new StoreFault(StoreOperation.Commit, 50, StoreFaultMode.UnknownNotApplied));

        var outcome = RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-1");

        RecoveryKit.AssertSameOutcome(Reference.Value, outcome);
        Assert.Equal(Reference.Value.Store.CommitCount, store.CommitCount);
    }

    [Fact]
    public void UnknownOutcome_ThatCannotBeResolved_StopsTrading()
    {
        var store = new InMemoryRunStore();
        store.Inject(new StoreFault(StoreOperation.Commit, 50, StoreFaultMode.UnknownNotApplied));
        store.Inject(new StoreFault(StoreOperation.Commit, 51, StoreFaultMode.FailBeforeApply));
        var run = DurableRun.Open(store, "owner-1", Spec);

        Assert.Throws<PersistenceFaultException>(() =>
        {
            foreach (var row in Rows)
            {
                run.Offer(row);
            }
        });

        Assert.True(run.Faulted);
        Assert.Equal(49, store.Load("run-1").Steps.Count);
        run.Dispose();
        RecoveryKit.AssertSameOutcome(Reference.Value, RecoveryKit.ResumeAndFinish(store, Spec, Rows));
    }

    [Fact]
    public void ASuccessorTakingOver_FencesTheOldWriter_WhichCannotCommitAnotherStep()
    {
        var store = new InMemoryRunStore();
        var old = DurableRun.Open(store, "owner-1", Spec);
        var fed = 0;
        for (; fed < 2_000; fed++)
        {
            old.Offer(Rows[fed]);
        }

        var durableBefore = store.Load("run-1").Steps.Count;
        var takeover = store.Takeover("run-1", "operator");

        var thrown = Assert.Throws<PersistenceFaultException>(() =>
        {
            for (; fed < Rows.Count; fed++)
            {
                old.Offer(Rows[fed]);
            }
        });

        Assert.IsType<FencedOutException>(thrown.InnerException);
        Assert.True(old.Faulted);
        Assert.Equal(durableBefore, store.Load("run-1").Steps.Count);
        old.Dispose();
        store.Release(takeover);
        RecoveryKit.AssertSameOutcome(Reference.Value, RecoveryKit.ResumeAndFinish(store, Spec, Rows, "owner-2"));
    }

    [Fact]
    public void ACompetingOwner_IsRefusedWhileTheFirstIsAlive()
    {
        var store = new InMemoryRunStore();
        using var first = DurableRun.Open(store, "owner-1", Spec);

        Assert.Throws<OwnershipConflictException>(() => DurableRun.Open(store, "owner-2", Spec));
    }

    [Fact]
    public void ACommitFailure_NeverLeavesTheLedgerPartiallyWritten()
    {
        var store = new InMemoryRunStore();
        store.Inject(new StoreFault(StoreOperation.Commit, 5, StoreFaultMode.FailDuringApply));
        using var run = DurableRun.Open(store, "owner-1", Spec);
        Assert.ThrowsAny<Exception>(() =>
        {
            foreach (var row in Rows)
            {
                run.Offer(row);
            }
        });

        var snapshot = store.Load("run-1");

        Assert.Equal(4, snapshot.Steps.Count);
        Assert.Equal(snapshot.Steps[^1].OutputToExclusive, snapshot.Outputs.Count);
        LedgerCheck.Verify(snapshot);
    }

    private static ControlRecord ControlCodec_Operator(long sequence, string actor)
        => NiftySignal.Trading.Recovery.Codec.ControlCodec.ToRecord(new NiftySignal.Trading.Core.Events.OperatorCommandEvent(SyntheticSession.Ist(9, 40), sequence, NiftySignal.Trading.Core.Events.OperatorCommand.Kill, "*", actor, "test"));
}
