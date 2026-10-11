namespace NiftySignal.Trading.Recovery;

/// <summary>Proof of ownership of one run: the owner, and the fencing epoch that every durable write must still match.</summary>
public sealed record OwnershipToken(string RunId, string OwnerId, long Epoch);

/// <summary>Thrown by a test crash seam to model abrupt process death: nothing after the throw may run, flush or persist. Never caught by the library.</summary>
public sealed class SimulatedProcessDeath(string where) : Exception($"simulated process death at {where}");

public sealed class OwnershipConflictException(string runId, string holder) : Exception($"Run '{runId}' is owned by '{holder}'.");

/// <summary>The token's epoch is no longer the run's epoch (a successor took over): this writer must stop.</summary>
public sealed class FencedOutException(string runId, long epoch, long current) : Exception($"Run '{runId}': epoch {epoch} was fenced out by epoch {current}.");

/// <summary>The store definitely did NOT apply the operation.</summary>
public sealed class StoreUnavailableException(string detail) : Exception(detail);

/// <summary>The caller cannot know whether the operation was applied (for example the connection dropped while the commit was in flight).</summary>
public sealed class UnknownCommitOutcomeException(string detail) : Exception(detail);

/// <summary>An existing durable row has different content than the one being written under the same deterministic identity. Nothing is overwritten.</summary>
public sealed class ConflictFaultException(string detail) : Exception(detail);

/// <summary>A step or output sequence would not be contiguous.</summary>
public sealed class SequenceGapException(string detail) : Exception(detail);

public enum HaltKind
{
    Compatibility = 0,
    CorruptLedger = 1,
    SourceMismatch = 2,
    ReplayDivergence = 3,
    MissingInput = 4,
    Conflict = 5,
}

/// <summary>Recovery refused or stopped: no new input is accepted by this instance (contract section A.18.6).</summary>
public sealed class RecoveryHaltException(HaltKind kind, string message, IReadOnlyList<string> details) : Exception(message)
{
    public HaltKind Kind { get; } = kind;

    public IReadOnlyList<string> Details { get; } = details;
}

/// <summary>A persistence call failed or its outcome is unknown: the in-memory instance has run ahead of the durable state and must be discarded and reconstructed.</summary>
public sealed class PersistenceFaultException(string message, Exception inner) : Exception(message, inner);

/// <summary>The instance was faulted by an earlier failure; the only way forward is to reconstruct it from durable data.</summary>
public sealed class InstanceFaultedException(string message) : InvalidOperationException(message);

public enum CommitResult
{
    Committed = 0,

    /// <summary>The step already existed with identical content (a re-applied commit after an unknown outcome).</summary>
    AlreadyCommitted = 1,
}

public enum AppendResult
{
    Appended = 0,
    Duplicate = 1,
}

/// <summary>
/// A CACHE of the live state of one position for observers (the Dashboard), written with the step it was derived at: remaining quantity, TP1 progress, last executable bid
/// and the running MAE / MFE. Derived from the engine state, never an input of it, never part of any digest; replaying the inputs recomputes it and the next commit overwrites it.
/// </summary>
public sealed record PositionSnapshotRow(
    string PositionId,
    string StrategyId,
    string VariantId,
    long AsOfUtcTicks,
    string Status,
    long RemainingQuantity,
    long ReservedExitQuantity,
    bool ExitPending,
    string Tp1State,
    long Tp1FilledQuantity,
    string? LastBidText,
    long? LastBidUtcTicks,
    string? UnrealizedPnlText,
    string? MfeText,
    string? MaeText,
    string? MfePctText,
    string? MaePctText,
    string? HighestBidText,
    string? LowestBidText,
    int ExcursionObservations,
    int ExcursionGaps,
    string ExcursionCoverage);

/// <summary>Everything one commit makes durable, atomically.</summary>
public sealed record CommitBatch(
    StepRecord Step,
    IReadOnlyList<OutputRecord> Outputs,
    IReadOnlyList<ProjectionRow> Projections,
    CheckpointRecord? Checkpoint,
    FinalRecord? Final)
{
    /// <summary>The canonical inputs processed by this step (live runs only; a source-driven run derives its inputs from the source and leaves this empty).</summary>
    public IReadOnlyList<InputRecord> Inputs { get; init; } = Array.Empty<InputRecord>();

    /// <summary>The observer cache of the positions touched or still open at this step (live runs only; never part of the step identity).</summary>
    public IReadOnlyList<PositionSnapshotRow> PositionSnapshots { get; init; } = Array.Empty<PositionSnapshotRow>();
}

/// <summary>
/// The durable run store (contract section A.18.5). The interface is synchronous in CP6.1; the PostgreSQL implementation will wrap it. Every write
/// method takes the ownership token and fails with <see cref="FencedOutException"/> if the epoch is stale.
/// </summary>
public interface IRunStore
{
    /// <summary>Takes ownership of the run (exclusive; fails fast if another owner holds it) and returns a token with a new, higher epoch.</summary>
    OwnershipToken Acquire(string runId, string ownerId);

    /// <summary>Gives ownership up cleanly (a dead process simply loses it; see the in-memory store's process-death simulation).</summary>
    void Release(OwnershipToken token);

    RunSnapshot Load(string runId);

    /// <summary>Creates the header. Idempotent for an identical header; a different header under the same run id is a <see cref="ConflictFaultException"/>.</summary>
    AppendResult CreateRun(OwnershipToken token, RunHeader header);

    /// <summary>Journals a control / progress record (write-ahead). Idempotent by <see cref="ControlRecord.Id"/>; different content under the same id is a conflict.</summary>
    AppendResult AppendControl(OwnershipToken token, ControlRecord record);

    /// <summary>Atomically makes a step, its outputs, projections, optional checkpoint and optional final record durable. Idempotent for an identical step.</summary>
    CommitResult Commit(OwnershipToken token, CommitBatch batch);

    /// <summary>Appends a recovery-attempt diagnostic (never part of an economic digest).</summary>
    void AppendRecoveryLog(OwnershipToken token, RecoveryLogEntry entry);
}
