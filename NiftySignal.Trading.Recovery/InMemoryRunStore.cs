namespace NiftySignal.Trading.Recovery;

public enum StoreOperation
{
    CreateRun = 0,
    AppendControl = 1,
    Commit = 2,
}

public enum StoreFaultMode
{
    /// <summary>The operation fails before anything is applied (<see cref="StoreUnavailableException"/>).</summary>
    FailBeforeApply = 0,

    /// <summary>The operation fails after it started staging its effects; everything staged is discarded (atomicity) and <see cref="StoreUnavailableException"/> is thrown.</summary>
    FailDuringApply = 1,

    /// <summary>The operation IS applied durably, but the caller only sees <see cref="UnknownCommitOutcomeException"/> (the acknowledgement was lost).</summary>
    UnknownApplied = 2,

    /// <summary>The operation is NOT applied, but the caller only sees <see cref="UnknownCommitOutcomeException"/>.</summary>
    UnknownNotApplied = 3,
}

/// <summary>Scripted fault: make the <paramref name="Nth"/> (1-based) call of <paramref name="Operation"/> that passes the fence check fail in the given way.</summary>
public sealed record StoreFault(StoreOperation Operation, int Nth, StoreFaultMode Mode);

/// <summary>
/// In-memory run store with real transactional semantics: a commit is validated, staged and then published in one step, so a failure at any point
/// leaves no partial state; every write is fenced by the ownership epoch; faults are scripted by tests. LIMIT (stated in the contract): this proves
/// the library's logic against an ideal atomic store. It does NOT prove PostgreSQL transaction behaviour, unique-index enforcement, connection
/// loss semantics or genuine process death; those are CP6.3–CP6.5 and need the real database and a child process.
/// </summary>
public sealed class InMemoryRunStore : IRunStore
{
    /// <summary>The mutable durable data of one run. Public only as a test seam for corruption tests (<see cref="Unsafe"/>); the store itself never exposes it otherwise.</summary>
    public sealed class MutableRun
    {
        public RunHeader? Header { get; set; }

        public List<ControlRecord> Controls { get; } = new();

        public List<StepRecord> Steps { get; } = new();

        public List<OutputRecord> Outputs { get; } = new();

        public List<CheckpointRecord> Checkpoints { get; } = new();

        public List<ProjectionRow> Projections { get; } = new();

        public FinalRecord? Final { get; set; }

        public bool Completed { get; set; }

        public List<RecoveryLogEntry> RecoveryLog { get; } = new();

        public long Epoch { get; set; }

        public string? LockHolder { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, MutableRun> _runs = new(StringComparer.Ordinal);
    private readonly List<StoreFault> _faults = new();
    private readonly Dictionary<StoreOperation, int> _calls = new();

    public int CommitCount { get; private set; }

    public int ControlAppendCount { get; private set; }

    /// <summary>Scripts a fault for a later call.</summary>
    public void Inject(StoreFault fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
        }
    }

    /// <summary>Models abrupt death of the owning process: its lock disappears, its token stays stale (no graceful release, nothing flushed).</summary>
    public void SimulateProcessDeath(string ownerId)
    {
        lock (_gate)
        {
            foreach (var run in _runs.Values.Where(r => r.LockHolder == ownerId))
            {
                run.LockHolder = null;
            }
        }
    }

    /// <summary>Explicit takeover (operator action after a lease expiry): bumps the epoch even if the lock is held, so the old owner is fenced out.</summary>
    public OwnershipToken Takeover(string runId, string newOwnerId)
    {
        lock (_gate)
        {
            var run = Run(runId);
            run.Epoch++;
            run.LockHolder = newOwnerId;
            return new OwnershipToken(runId, newOwnerId, run.Epoch);
        }
    }

    public MutableRun Unsafe(string runId)
    {
        lock (_gate)
        {
            return Run(runId);
        }
    }

    public OwnershipToken Acquire(string runId, string ownerId)
    {
        lock (_gate)
        {
            var run = Run(runId);
            if (run.LockHolder is { } holder)
            {
                throw new OwnershipConflictException(runId, holder);
            }

            run.Epoch++;
            run.LockHolder = ownerId;
            return new OwnershipToken(runId, ownerId, run.Epoch);
        }
    }

    public void Release(OwnershipToken token)
    {
        lock (_gate)
        {
            var run = Run(token.RunId);
            if (run.Epoch == token.Epoch && run.LockHolder == token.OwnerId)
            {
                run.LockHolder = null;
            }
        }
    }

    public RunSnapshot Load(string runId)
    {
        lock (_gate)
        {
            var run = Run(runId);
            return new RunSnapshot(
                run.Header,
                run.Controls.ToArray(),
                run.Steps.ToArray(),
                run.Outputs.ToArray(),
                run.Checkpoints.ToArray(),
                run.Projections.ToArray(),
                run.Final,
                run.Completed,
                run.RecoveryLog.ToArray());
        }
    }

    public AppendResult CreateRun(OwnershipToken token, RunHeader header)
    {
        lock (_gate)
        {
            var run = Fenced(token);
            var fault = NextFault(StoreOperation.CreateRun);
            if (header.HashIsValid is false)
            {
                throw new ConflictFaultException("The header's content hash does not match its content.");
            }

            ThrowIfFailsEarly(fault);
            if (run.Header is not null)
            {
                if (run.Header.ContentHash != header.ContentHash)
                {
                    throw new ConflictFaultException($"Run '{header.RunId}' already exists with a different header.");
                }

                ThrowAfter(fault);
                return AppendResult.Duplicate;
            }

            ThrowIfFailsDuring(fault);
            run.Header = header;
            ThrowAfter(fault);
            return AppendResult.Appended;
        }
    }

    public AppendResult AppendControl(OwnershipToken token, ControlRecord record)
    {
        lock (_gate)
        {
            var run = Fenced(token);
            var fault = NextFault(StoreOperation.AppendControl);
            if (!record.HashIsValid)
            {
                throw new ConflictFaultException($"Control '{record.Id}' has an invalid content hash.");
            }

            ThrowIfFailsEarly(fault);
            var existing = run.Controls.FirstOrDefault(c => c.Id == record.Id);
            if (existing is not null)
            {
                if (existing.ContentHash != record.ContentHash)
                {
                    throw new ConflictFaultException($"Control '{record.Id}' already exists with different content.");
                }

                ThrowAfter(fault);
                return AppendResult.Duplicate;
            }

            ThrowIfFailsDuring(fault);
            run.Controls.Add(record);
            ControlAppendCount++;
            ThrowAfter(fault);
            return AppendResult.Appended;
        }
    }

    public CommitResult Commit(OwnershipToken token, CommitBatch batch)
    {
        lock (_gate)
        {
            var run = Fenced(token);
            var fault = NextFault(StoreOperation.Commit);
            ThrowIfFailsEarly(fault);
            Validate(run, batch);

            if (batch.Step.StepSeq < run.Steps.Count)
            {
                VerifyIdentical(run, batch);
                ThrowAfter(fault);
                return CommitResult.AlreadyCommitted;
            }

            // Stage everything, then publish in one block: a failure before the block leaves the store untouched.
            var stagedOutputs = batch.Outputs.ToList();
            var stagedProjections = batch.Projections.ToList();
            ThrowIfFailsDuring(fault);

            run.Outputs.AddRange(stagedOutputs);
            run.Projections.AddRange(stagedProjections);
            run.Steps.Add(batch.Step);
            if (batch.Checkpoint is not null)
            {
                run.Checkpoints.Add(batch.Checkpoint);
            }

            if (batch.Final is not null)
            {
                run.Final = batch.Final;
                run.Completed = true;
            }

            CommitCount++;
            ThrowAfter(fault);
            return CommitResult.Committed;
        }
    }

    public void AppendRecoveryLog(OwnershipToken token, RecoveryLogEntry entry)
    {
        lock (_gate)
        {
            Fenced(token).RecoveryLog.Add(entry);
        }
    }

    // ------------------------------------------------------------------ validation

    private static void Validate(MutableRun run, CommitBatch batch)
    {
        if (run.Header is null)
        {
            throw new ConflictFaultException("The run has no header.");
        }

        var step = batch.Step;
        if (!step.HashIsValid)
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: content hash does not match its content.");
        }

        if (step.StepSeq > run.Steps.Count)
        {
            throw new SequenceGapException($"Step {step.StepSeq} would leave a gap after step {run.Steps.Count - 1}.");
        }

        if (batch.Outputs.Count != step.OutputToExclusive - step.OutputFrom)
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: output range [{step.OutputFrom},{step.OutputToExclusive}) disagrees with {batch.Outputs.Count} outputs.");
        }

        for (var i = 0; i < batch.Outputs.Count; i++)
        {
            var o = batch.Outputs[i];
            if (!o.HashIsValid)
            {
                throw new ConflictFaultException($"Output {o.Seq}: content hash does not match its payload.");
            }

            if (o.Seq != step.OutputFrom + i || o.StepSeq != step.StepSeq)
            {
                throw new SequenceGapException($"Output {o.Seq} is out of sequence in step {step.StepSeq}.");
            }
        }

        if (batch.Projections.Any(p => !p.HashIsValid))
        {
            throw new ConflictFaultException($"Step {step.StepSeq}: a projection row's hash does not match its payload.");
        }

        if (batch.Checkpoint is { } cp && cp.StepSeq != step.StepSeq)
        {
            throw new ConflictFaultException("The checkpoint belongs to a different step.");
        }

        if (batch.Final is { } final && (step.Kind != Adapters.UnitKind.Finish || !final.HashIsValid))
        {
            throw new ConflictFaultException("A final record is only valid on a Finish step with a matching hash.");
        }

        if (step.StepSeq == run.Steps.Count)
        {
            if (step.OutputFrom != run.Outputs.Count)
            {
                throw new SequenceGapException($"Step {step.StepSeq} starts at output {step.OutputFrom} but {run.Outputs.Count} outputs are durable.");
            }

            foreach (var id in step.AppliedControls)
            {
                if (run.Controls.All(c => c.Id != id))
                {
                    throw new ConflictFaultException($"Step {step.StepSeq} lists control '{id}' as applied, but it was never journaled.");
                }
            }

            var existing = run.Projections.Select(p => (p.Table, p.Key)).ToHashSet();
            var duplicate = batch.Projections.FirstOrDefault(p => existing.Contains((p.Table, p.Key)));
            if (duplicate is not null)
            {
                throw new ConflictFaultException($"Projection row {duplicate.Table}/{duplicate.Key} already exists.");
            }
        }
    }

    private static void VerifyIdentical(MutableRun run, CommitBatch batch)
    {
        var existing = run.Steps[(int)batch.Step.StepSeq];
        if (existing.ContentHash != batch.Step.ContentHash)
        {
            throw new ConflictFaultException($"Step {batch.Step.StepSeq} already exists with different content.");
        }

        foreach (var o in batch.Outputs)
        {
            if (o.Seq >= run.Outputs.Count || run.Outputs[(int)o.Seq].ContentHash != o.ContentHash)
            {
                throw new ConflictFaultException($"Output {o.Seq} of step {batch.Step.StepSeq} already exists with different content.");
            }
        }

        foreach (var p in batch.Projections)
        {
            if (!run.Projections.Any(x => x.Table == p.Table && x.Key == p.Key && x.ContentHash == p.ContentHash))
            {
                throw new ConflictFaultException($"Projection row {p.Table}/{p.Key} of step {batch.Step.StepSeq} differs from the durable one.");
            }
        }

        if (batch.Checkpoint is { } cp && !run.Checkpoints.Any(x => x.StepSeq == cp.StepSeq && x.StateDigest == cp.StateDigest))
        {
            throw new ConflictFaultException($"The checkpoint of step {batch.Step.StepSeq} differs from the durable one.");
        }
    }

    // ------------------------------------------------------------------ plumbing

    private MutableRun Run(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            _runs[runId] = run = new MutableRun();
        }

        return run;
    }

    private MutableRun Fenced(OwnershipToken token)
    {
        var run = Run(token.RunId);
        if (run.Epoch != token.Epoch || run.LockHolder != token.OwnerId)
        {
            throw new FencedOutException(token.RunId, token.Epoch, run.Epoch);
        }

        return run;
    }

    private StoreFault? NextFault(StoreOperation operation)
    {
        _calls[operation] = _calls.GetValueOrDefault(operation) + 1;
        var fault = _faults.FirstOrDefault(f => f.Operation == operation && f.Nth == _calls[operation]);
        if (fault is not null)
        {
            _faults.Remove(fault);
        }

        return fault;
    }

    private static void ThrowIfFailsEarly(StoreFault? fault)
    {
        switch (fault?.Mode)
        {
            case StoreFaultMode.FailBeforeApply:
                throw new StoreUnavailableException("scripted: unavailable before apply");
            case StoreFaultMode.UnknownNotApplied:
                throw new UnknownCommitOutcomeException("scripted: outcome unknown (not applied)");
        }
    }

    private static void ThrowIfFailsDuring(StoreFault? fault)
    {
        if (fault?.Mode == StoreFaultMode.FailDuringApply)
        {
            throw new StoreUnavailableException("scripted: failed while applying; staged effects were discarded");
        }
    }

    private static void ThrowAfter(StoreFault? fault)
    {
        if (fault?.Mode == StoreFaultMode.UnknownApplied)
        {
            throw new UnknownCommitOutcomeException("scripted: outcome unknown (applied)");
        }
    }
}
