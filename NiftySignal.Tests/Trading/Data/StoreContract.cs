using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Tests.Trading.Recovery;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>
/// What a store implementation must offer to the contract suite besides <see cref="IAsyncRunStore"/> itself: the ways of provoking the situations the
/// contract talks about (a lost acknowledgement, a dead process, a successor taking over, a cancellation racing a commit). The in-memory fixture
/// implements them directly; a PostgreSQL fixture (CP6.3) implements them with a fault-injecting connection layer, a second connection that
/// terminates the first, and a real cancellation token.
/// </summary>
public interface IStoreContractFixture : IDisposable
{
    IAsyncRunStore Store { get; }

    /// <summary>Makes the Nth write of the kind FROM NOW fail as observed by the caller (see <see cref="StoreFaultMode"/>); earlier calls (a test's own setup) are not counted.</summary>
    void Inject(StoreOperation operation, int nth, StoreFaultMode mode);

    /// <summary>The owner's process dies abruptly: its lock is released but its token stays stale.</summary>
    void SimulateProcessDeath(string ownerId);

    /// <summary>A successor takes the run over although the previous owner still holds it.</summary>
    Task<OwnershipToken> TakeOverAsync(string runId, string newOwnerId);

    /// <summary>The given token source is cancelled after the next commit has been submitted and before its outcome is known to the caller.</summary>
    void CancelAfterNextCommitSubmitted(CancellationTokenSource source);
}

public sealed class InMemoryContractFixture : IStoreContractFixture
{
    private readonly InMemoryRunStore _inner = new();
    private readonly SyncRunStoreAdapter _adapter;

    public InMemoryContractFixture() => _adapter = new SyncRunStoreAdapter(_inner);

    public IAsyncRunStore Store => _adapter;

    public InMemoryRunStore Inner => _inner;

    public void Inject(StoreOperation operation, int nth, StoreFaultMode mode) => _inner.Inject(new StoreFault(operation, _inner.CallsOf(operation) + nth, mode));

    public void SimulateProcessDeath(string ownerId) => _inner.SimulateProcessDeath(ownerId);

    public Task<OwnershipToken> TakeOverAsync(string runId, string newOwnerId) => Task.FromResult(_inner.Takeover(runId, newOwnerId));

    public void CancelAfterNextCommitSubmitted(CancellationTokenSource source)
        => _adapter.AfterSubmit = operation =>
        {
            if (operation == "Commit")
            {
                _adapter.AfterSubmit = null;
                source.Cancel();
            }
        };

    public void Dispose()
    {
    }
}

/// <summary>Valid records for the contract tests, produced by a real durable run over the synthetic session (with two feed-health controls) so that nothing in them is hand-built.</summary>
internal static class ContractData
{
    private static readonly IReadOnlyList<SourceTick> Rows = RecoveryKit.ShortRows();
    private static readonly SessionFreeze Freeze = SyntheticSession.Freeze(Rows);
    public static readonly DurableRunSpec Spec = RecoveryKit.Spec(Freeze);
    public const string RunId = "run-1";

    private static readonly Lazy<RunSnapshot> Source = new(Build);

    public static RunSnapshot Snapshot => Source.Value;

    private static RunSnapshot Build()
    {
        var store = new InMemoryRunStore();
        using var run = DurableRun.Open(store, "builder", Spec);
        var degraded = new FeedHealthEvent(At(4_000), 1, FeedHealthState.Degraded, FeedFaultReason.NoMessageWatchdog, "contract data");
        var healthy = new FeedHealthEvent(At(4_600), 2, FeedHealthState.Healthy, FeedFaultReason.None, "contract data");
        for (var i = 0; i < Rows.Count; i++)
        {
            if (i == 4_000)
            {
                run.Control(degraded);
            }

            if (i == 4_600)
            {
                run.Control(healthy);
            }

            run.Offer(Rows[i]);
        }

        run.Finish();
        return store.Load(RunId);
    }

    private static DateTimeOffset At(int index) => Rows[index].ReceivedAt > Rows[index].ExchangeTimestamp ? Rows[index].ReceivedAt : Rows[index].ExchangeTimestamp;

    public static CommitBatch BatchOf(int step)
    {
        var s = Snapshot;
        var record = s.Steps[step];
        var outputs = s.Outputs.Where(o => o.StepSeq == step).ToList();
        return new CommitBatch(record, outputs, ProjectionBuilder.Build(outputs), s.Checkpoints.FirstOrDefault(c => c.StepSeq == step), record.Kind == UnitKind.Finish ? s.Final : null);
    }

    /// <summary>Journals every control of the source run and commits the first <paramref name="steps"/> steps.</summary>
    public static async Task<OwnershipToken> StartAsync(IAsyncRunStore store, string owner = "owner-1", int steps = 0, bool journalControls = true)
    {
        var token = await store.AcquireAsync(RunId, owner);
        await store.CreateRunAsync(token, Snapshot.Header!);
        if (journalControls)
        {
            foreach (var c in Snapshot.Controls)
            {
                await store.AppendControlAsync(token, c);
            }
        }

        for (var i = 0; i < steps; i++)
        {
            await store.CommitAsync(token, BatchOf(i));
        }

        return token;
    }
}

/// <summary>
/// The reusable store contract (contract section A.19.8). Every implementation of <see cref="IAsyncRunStore"/> must pass exactly these tests: the
/// in-memory store today, the PostgreSQL store in CP6.3 (a subclass supplies its fixture). The tests state the semantics once; no implementation may
/// weaken them. They cover atomicity, equality checks, unknown outcomes, cancellation, the control journal (durable versus applied), fencing,
/// diagnostics isolation and lossless round trips of precision-sensitive values.
/// </summary>
public abstract class RunStoreContractTests
{
    protected abstract IStoreContractFixture CreateFixture();

    private static async Task<RunSnapshot> LoadAsync(IStoreContractFixture f) => await f.Store.LoadAsync(ContractData.RunId);

    // ------------------------------------------------------------------ header and round trips

    [Fact]
    public async Task AnUnknownRun_LoadsAsEmpty()
    {
        using var f = CreateFixture();

        var s = await f.Store.LoadAsync("does-not-exist");

        Assert.Null(s.Header);
        Assert.Empty(s.Steps);
        Assert.Empty(s.Outputs);
        Assert.False(s.Completed);
    }

    [Fact]
    public async Task TheHeader_RoundTripsExactly_IncludingVariantsAndProvenance_AndIsIdempotentOrConflicting()
    {
        using var f = CreateFixture();
        var header = ContractData.Snapshot.Header! with
        {
            Provenance = new BinaryProvenance("abc123", true, "10.0.401", new Dictionary<string, string> { ["B.dll"] = "22", ["A.dll"] = "11" }),
        };
        header = RunHeader.Create(header with { ContentHash = string.Empty });
        var token = await f.Store.AcquireAsync(ContractData.RunId, "owner-1");

        Assert.Equal(AppendResult.Appended, await f.Store.CreateRunAsync(token, header));
        Assert.Equal(AppendResult.Duplicate, await f.Store.CreateRunAsync(token, header));
        await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.CreateRunAsync(token, RunHeader.Create(header with { Horizon = header.Horizon.AddTicks(1), ContentHash = string.Empty })));

        var loaded = (await LoadAsync(f)).Header!;
        Assert.True(loaded.HashIsValid);
        Assert.Equal(header.ContentHash, loaded.ContentHash);
        Assert.Equal(header.Compatibility, loaded.Compatibility);
        Assert.Equal(header.Variants, loaded.Variants.ToList());
        Assert.Equal(header.Provenance.GitCommit, loaded.Provenance.GitCommit);
        Assert.Equal(header.Provenance.AssemblyHashes.OrderBy(a => a.Key), loaded.Provenance.AssemblyHashes.OrderBy(a => a.Key));
        Assert.Equal((header.ConfigJson, header.FreezeSnapshot, header.SourceManifest, header.Horizon, header.TradeDate), (loaded.ConfigJson, loaded.FreezeSnapshot, loaded.SourceManifest, loaded.Horizon, loaded.TradeDate));
    }

    [Fact]
    public async Task AWholeRunCommittedStepByStep_LoadsBackIdentically_AllHashesValid_ProjectionsDerivable()
    {
        using var f = CreateFixture();
        var source = ContractData.Snapshot;
        var token = await ContractData.StartAsync(f.Store, steps: source.Steps.Count);

        var loaded = await LoadAsync(f);

        Assert.True(loaded.Completed);
        Assert.Equal(RunSnapshotDigest.Economic(source), RunSnapshotDigest.Economic(loaded));
        Assert.Equal(source.Steps.Select(s => s.ContentHash), loaded.Steps.Select(s => s.ContentHash));
        Assert.All(loaded.Steps, s => Assert.True(s.HashIsValid));
        Assert.All(loaded.Steps, s => Assert.Equal(9, s.Digests.Count));
        Assert.All(loaded.Outputs, o => Assert.True(o.HashIsValid));
        Assert.Equal(source.Steps.Select(s => string.Join(",", s.AppliedControls)), loaded.Steps.Select(s => string.Join(",", s.AppliedControls)));
        Assert.Equal(source.Checkpoints.Select(c => (c.StepSeq, c.StateDigest)), loaded.Checkpoints.Select(c => (c.StepSeq, c.StateDigest)));
        Assert.Equal(source.Final!.ContentHash, loaded.Final!.ContentHash);
        Assert.Equal(source.Final.AuditLines, loaded.Final.AuditLines);
        Assert.Equal(source.Final.SummaryLines, loaded.Final.SummaryLines);
        Assert.Equal(ProjectionBuilder.Build(loaded.Outputs).Select(p => (p.Table, p.Key, p.ContentHash)), loaded.Projections.Select(p => (p.Table, p.Key, p.ContentHash)));
        LedgerCheck.Verify(loaded);
        await f.Store.ReleaseAsync(token);
    }

    // ------------------------------------------------------------------ atomicity and equality checks

    [Fact]
    public async Task Commit_IsIdempotentForTheIdenticalStep_AndEqualityCheckedForADifferentOne()
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store);
        var batch = ContractData.BatchOf(0);

        Assert.Equal(CommitResult.Committed, await f.Store.CommitAsync(token, batch));
        Assert.Equal(CommitResult.AlreadyCommitted, await f.Store.CommitAsync(token, batch));
        var different = batch with { Step = StepRecord.Create(batch.Step with { OfferedRows = batch.Step.OfferedRows + 1, ContentHash = string.Empty }) };
        await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.CommitAsync(token, different));

        var s = await LoadAsync(f);
        Assert.Single(s.Steps);
        Assert.Equal(batch.Outputs.Count, s.Outputs.Count);
    }

    [Fact]
    public async Task ARejectedCommit_ChangesNothing()
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store, steps: 2);
        var before = RunSnapshotDigest.Economic(await LoadAsync(f));
        var next = ContractData.BatchOf(2);

        await Assert.ThrowsAsync<SequenceGapException>(() => f.Store.CommitAsync(token, ContractData.BatchOf(5)));
        await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.CommitAsync(token, next with { Step = next.Step with { ContentHash = "00" } }));
        if (next.Outputs.Count > 0)
        {
            var tampered = next.Outputs.ToList();
            tampered[0] = tampered[0] with { Payload = tampered[0].Payload + " " };
            await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.CommitAsync(token, next with { Outputs = tampered }));
        }

        var unjournaled = next with { Step = StepRecord.Create(next.Step with { AppliedControls = new[] { "Operator:999" }, ContentHash = string.Empty }) };
        await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.CommitAsync(token, unjournaled));

        Assert.Equal(before, RunSnapshotDigest.Economic(await LoadAsync(f)));
    }

    [Theory]
    [InlineData(StoreFaultMode.FailBeforeApply, false)]
    [InlineData(StoreFaultMode.FailDuringApply, false)]
    [InlineData(StoreFaultMode.UnknownApplied, true)]
    [InlineData(StoreFaultMode.UnknownNotApplied, false)]
    public async Task ACommitIsAllOrNothing_WhateverTheFault_AndRetryingTheSameBatchConverges(StoreFaultMode mode, bool applied)
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store, steps: 3);
        var batch = ContractData.BatchOf(3);
        var before = await LoadAsync(f);
        f.Inject(StoreOperation.Commit, 1, mode);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => f.Store.CommitAsync(token, batch));

        Assert.IsType(mode is StoreFaultMode.UnknownApplied or StoreFaultMode.UnknownNotApplied ? typeof(UnknownCommitOutcomeException) : typeof(StoreUnavailableException), thrown);
        var after = await LoadAsync(f);
        Assert.Equal(applied ? 4 : 3, after.Steps.Count);
        Assert.Equal(applied ? before.Outputs.Count + batch.Outputs.Count : before.Outputs.Count, after.Outputs.Count);
        Assert.Equal(applied ? before.Projections.Count + batch.Projections.Count : before.Projections.Count, after.Projections.Count);
        Assert.Equal(applied ? before.Checkpoints.Count + (batch.Checkpoint is null ? 0 : 1) : before.Checkpoints.Count, after.Checkpoints.Count);
        Assert.Equal(applied ? CommitResult.AlreadyCommitted : CommitResult.Committed, await f.Store.CommitAsync(token, batch));
        Assert.Equal(4, (await LoadAsync(f)).Steps.Count);
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task CancellationBeforeSubmission_AppliesNothing_ForEveryWrite()
    {
        using var f = CreateFixture();
        var token = await f.Store.AcquireAsync(ContractData.RunId, "owner-1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.CreateRunAsync(token, ContractData.Snapshot.Header!, cancelled.Token));
        Assert.Null((await LoadAsync(f)).Header);
        await f.Store.CreateRunAsync(token, ContractData.Snapshot.Header!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.AppendControlAsync(token, ContractData.Snapshot.Controls[0], cancelled.Token));
        Assert.Empty((await LoadAsync(f)).Controls);
        await f.Store.AppendControlAsync(token, ContractData.Snapshot.Controls[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.CommitAsync(token, ContractData.BatchOf(0), cancelled.Token));
        Assert.Empty((await LoadAsync(f)).Steps);
    }

    [Fact]
    public async Task CancellationAfterACommitWasSubmitted_IsAnUnknownOutcome_NeverARollback()
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store, steps: 1);
        using var source = new CancellationTokenSource();
        f.CancelAfterNextCommitSubmitted(source);
        var batch = ContractData.BatchOf(1);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => f.Store.CommitAsync(token, batch, source.Token));

        Assert.IsType<UnknownCommitOutcomeException>(thrown);
        Assert.IsNotType<OperationCanceledException>(thrown);
        var after = await LoadAsync(f);
        Assert.True(after.Steps.Count is 1 or 2, "all or nothing, never partial");
        Assert.Equal(after.Steps[^1].OutputToExclusive, after.Outputs.Count);
        var resolved = await f.Store.CommitAsync(token, batch);
        Assert.Equal(after.Steps.Count == 2 ? CommitResult.AlreadyCommitted : CommitResult.Committed, resolved);
        Assert.Equal(2, (await LoadAsync(f)).Steps.Count);
    }

    // ------------------------------------------------------------------ control journal: durable versus applied

    [Fact]
    public async Task AControl_IsDurableWhenJournaled_AndAppliedOnlyWhenAStepThatListsItCommits()
    {
        using var f = CreateFixture();
        var source = ContractData.Snapshot;
        var applyingStep = source.Steps.First(s => s.AppliedControls.Count > 0);
        var token = await ContractData.StartAsync(f.Store, steps: (int)applyingStep.StepSeq);

        var journaledOnly = await LoadAsync(f);
        Assert.Equal(source.Controls.Select(c => c.Id), journaledOnly.Controls.Select(c => c.Id));
        Assert.All(journaledOnly.Controls, c => Assert.True(c.HashIsValid));
        Assert.DoesNotContain(journaledOnly.Steps, s => s.AppliedControls.Count > 0);

        await f.Store.CommitAsync(token, ContractData.BatchOf((int)applyingStep.StepSeq));

        var applied = await LoadAsync(f);
        Assert.Equal(applyingStep.AppliedControls, applied.Steps[(int)applyingStep.StepSeq].AppliedControls.ToList());
    }

    [Fact]
    public async Task TheJournal_IsIdempotentByIdentity_ConflictsOnDifferentContent_AndKeepsAppendOrder()
    {
        using var f = CreateFixture();
        var token = await f.Store.AcquireAsync(ContractData.RunId, "owner-1");
        await f.Store.CreateRunAsync(token, ContractData.Snapshot.Header!);
        var t = ContractData.Snapshot.Header!.Horizon.AddHours(-1);
        var b = ControlCodec.ToRecord(new OperatorCommandEvent(t, 7, OperatorCommand.Kill, "*", "tester", "second by id, first appended"));
        var a = ControlCodec.ToRecord(new FeedHealthEvent(t.AddSeconds(1), 3, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, "gap"));

        Assert.Equal(AppendResult.Appended, await f.Store.AppendControlAsync(token, b));
        Assert.Equal(AppendResult.Appended, await f.Store.AppendControlAsync(token, a));
        Assert.Equal(AppendResult.Duplicate, await f.Store.AppendControlAsync(token, b));
        await Assert.ThrowsAsync<ConflictFaultException>(() => f.Store.AppendControlAsync(token, ControlCodec.ToRecord(new OperatorCommandEvent(t, 7, OperatorCommand.Kill, "*", "someone-else", "different"))));

        Assert.Equal(new[] { b.Id, a.Id }, (await LoadAsync(f)).Controls.Select(c => c.Id));
    }

    [Fact]
    public async Task AControlWriteFault_NeverLeavesAHalfWrittenRecord()
    {
        using var f = CreateFixture();
        var token = await f.Store.AcquireAsync(ContractData.RunId, "owner-1");
        await f.Store.CreateRunAsync(token, ContractData.Snapshot.Header!);
        var control = ContractData.Snapshot.Controls[0];
        f.Inject(StoreOperation.AppendControl, 1, StoreFaultMode.FailDuringApply);

        await Assert.ThrowsAsync<StoreUnavailableException>(() => f.Store.AppendControlAsync(token, control));
        Assert.Empty((await LoadAsync(f)).Controls);

        f.Inject(StoreOperation.AppendControl, 1, StoreFaultMode.UnknownApplied);
        await Assert.ThrowsAsync<UnknownCommitOutcomeException>(() => f.Store.AppendControlAsync(token, control));
        Assert.Single((await LoadAsync(f)).Controls);
        Assert.Equal(AppendResult.Duplicate, await f.Store.AppendControlAsync(token, control));
    }

    // ------------------------------------------------------------------ ownership and fencing

    [Fact]
    public async Task Ownership_IsExclusive_EpochsOnlyIncrease_AndADeadOwnersTokenStaysStale()
    {
        using var f = CreateFixture();
        var first = await f.Store.AcquireAsync(ContractData.RunId, "owner-1");
        await Assert.ThrowsAsync<OwnershipConflictException>(() => f.Store.AcquireAsync(ContractData.RunId, "owner-2"));
        f.SimulateProcessDeath("owner-1");

        var second = await f.Store.AcquireAsync(ContractData.RunId, "owner-2");

        Assert.True(second.Epoch > first.Epoch);
        await Assert.ThrowsAsync<FencedOutException>(() => f.Store.CreateRunAsync(first, ContractData.Snapshot.Header!));
        await f.Store.ReleaseAsync(second);
        var third = await f.Store.AcquireAsync(ContractData.RunId, "owner-3");
        Assert.True(third.Epoch > second.Epoch);
    }

    [Fact]
    public async Task EveryWrite_IsFencedAfterATakeover_AndTheOldOwnerChangesNothing()
    {
        using var f = CreateFixture();
        var old = await ContractData.StartAsync(f.Store, steps: 1);
        var successor = await f.TakeOverAsync(ContractData.RunId, "operator");
        var before = RunSnapshotDigest.Economic(await LoadAsync(f));

        Assert.True(successor.Epoch > old.Epoch);
        await Assert.ThrowsAsync<FencedOutException>(() => f.Store.CreateRunAsync(old, ContractData.Snapshot.Header!));
        await Assert.ThrowsAsync<FencedOutException>(() => f.Store.AppendControlAsync(old, ContractData.Snapshot.Controls[0]));
        await Assert.ThrowsAsync<FencedOutException>(() => f.Store.CommitAsync(old, ContractData.BatchOf(1)));
        await Assert.ThrowsAsync<FencedOutException>(() => f.Store.AppendRecoveryLogAsync(old, new RecoveryLogEntry(1, 1, 0, 0, 0, "x", "x")));

        Assert.Equal(before, RunSnapshotDigest.Economic(await LoadAsync(f)));
        Assert.Equal(CommitResult.Committed, await f.Store.CommitAsync(successor, ContractData.BatchOf(1)));
    }

    // ------------------------------------------------------------------ diagnostics isolation

    [Fact]
    public async Task RecoveryDiagnostics_AreStored_ButNeverChangeAnEconomicDigest()
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store, steps: 4);
        var before = RunSnapshotDigest.Economic(await LoadAsync(f));

        await f.Store.AppendRecoveryLogAsync(token, new RecoveryLogEntry(1, token.Epoch, 4, 10, 2, "Opened", "diagnostic"));
        await f.Store.AppendRecoveryLogAsync(token, new RecoveryLogEntry(1, token.Epoch, 4, 10, 2, "Halted:SourceMismatch", "same attempt number is allowed twice"));

        var after = await LoadAsync(f);
        Assert.Equal(2, after.RecoveryLog.Count);
        Assert.Equal(before, RunSnapshotDigest.Economic(after));
        Assert.Equal("Halted:SourceMismatch", after.RecoveryLog[1].Outcome);
    }

    // ------------------------------------------------------------------ precision

    [Fact]
    public async Task TimestampsAndDecimals_RoundTripExactly_IncludingOneTickApartInstantsAndNonAsciiText()
    {
        using var f = CreateFixture();
        var token = await ContractData.StartAsync(f.Store, journalControls: false);
        var instants = new[]
        {
            DateTimeOffset.UnixEpoch,
            new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1),
            new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero).AddTicks(1),
            new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero).AddTicks(9),
            new DateTimeOffset(2026, 9, 22, 4, 0, 0, TimeSpan.Zero).AddTicks(10),
            new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
        };
        var outputs = new List<OutputRecord>();
        for (var i = 0; i < instants.Length; i++)
        {
            var detail = i == 0 ? "café € 📈 \"quoted\" back\\slash\nnewline\ttab" : $"scale {1.10m * (i + 1):0.0000000000}";
            var payload = OutputCodec.Encode(new NiftySignal.Trading.Core.Variants.AuditRecord(instants[i], new NiftySignal.Trading.Core.Strategies.VariantKey("S", "A"), "opp-1", "Stage", "Outcome", detail));
            outputs.Add(new OutputRecord(i, 0, "AuditRecord", "S", "A", instants[i], payload, OutputCodec.ContentHash(payload)));
        }

        var digests = new[] { "audit", "bars", "candles", "opportunities", "outputs", "quotes", "readiness", "rowQuarantine", "summaries" }
            .Select(c => new ChannelDigestRecord(c, 1, new string('A', 64))).ToList();
        var step = StepRecord.Create(new StepRecord(0, UnitKind.DataGroup, instants[^1], 1, 1, 1, 1, instants[2], 1, 0, outputs.Count, Array.Empty<string>(), digests, string.Empty));
        await f.Store.CommitAsync(token, new CommitBatch(step, outputs, ProjectionBuilder.Build(outputs), null, null));

        var loaded = await LoadAsync(f);

        Assert.Equal(outputs.Select(o => (o.Seq, o.At.UtcTicks, o.Payload, o.ContentHash)), loaded.Outputs.Select(o => (o.Seq, o.At.UtcTicks, o.Payload, o.ContentHash)));
        Assert.All(loaded.Outputs, o => Assert.True(o.HashIsValid));
        Assert.Equal(instants.Length, loaded.Outputs.Select(o => o.At.UtcTicks).Distinct().Count());
        Assert.Equal(step.EngineTime.UtcTicks, loaded.Steps[0].EngineTime.UtcTicks);
        Assert.Equal(step.LastGroupAt!.Value.UtcTicks, loaded.Steps[0].LastGroupAt!.Value.UtcTicks);
        var audit = (NiftySignal.Trading.Core.Variants.AuditRecord)OutputCodec.Decode(loaded.Outputs[0].Payload);
        Assert.Equal("café € 📈 \"quoted\" back\\slash\nnewline\ttab", audit.Detail);
    }
}

public sealed class InMemoryRunStoreContractTests : RunStoreContractTests
{
    protected override IStoreContractFixture CreateFixture() => new InMemoryContractFixture();
}
