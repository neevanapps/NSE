namespace NiftySignal.Trading.Recovery;

/// <summary>
/// The asynchronous persistence boundary for real database I/O (contract section A.19.3). The deterministic core and <see cref="DurableRun"/> stay
/// synchronous and free of I/O; a database store implements this interface and is bridged to <see cref="IRunStore"/> by
/// <c>BlockingRunStoreAdapter</c> (in the Data project, because blocking on a task is deliberately not allowed in this deterministic assembly). The semantics are identical to <see cref="IRunStore"/> plus the cancellation rules below.
/// <para>
/// <b>Cancellation and unknown outcomes (binding for every implementation).</b>
/// (1) A token that is already cancelled, or is cancelled before the operation was <i>submitted</i> (before any statement of it was sent), makes the
/// operation throw <see cref="OperationCanceledException"/> and guarantees that nothing was applied.
/// (2) Once a write (<c>CreateRunAsync</c>, <c>AppendControlAsync</c>, <c>CommitAsync</c>) has been submitted, cancellation <b>must never be reported as
/// a rollback</b>: the implementation must either complete normally or throw <see cref="UnknownCommitOutcomeException"/>; it must not surface a bare
/// <see cref="OperationCanceledException"/> after submission, because the transaction may have committed. The caller resolves the unknown outcome by
/// identity exactly as <see cref="DurableRun"/> already does (re-read, compare, re-apply idempotently).
/// (3) Reads and <c>ReleaseAsync</c> may simply be cancelled. <c>AcquireAsync</c> cancelled after submission may have taken the lock and bumped the
/// epoch; no token is returned, so the owner must retry (a retry takes a higher epoch) — a stranded lock disappears with its connection.
/// </para>
/// </summary>
public interface IAsyncRunStore
{
    Task<OwnershipToken> AcquireAsync(string runId, string ownerId, CancellationToken cancellationToken = default);

    Task ReleaseAsync(OwnershipToken token, CancellationToken cancellationToken = default);

    Task<RunSnapshot> LoadAsync(string runId, CancellationToken cancellationToken = default);

    Task<AppendResult> CreateRunAsync(OwnershipToken token, RunHeader header, CancellationToken cancellationToken = default);

    Task<AppendResult> AppendControlAsync(OwnershipToken token, ControlRecord record, CancellationToken cancellationToken = default);

    Task<CommitResult> CommitAsync(OwnershipToken token, CommitBatch batch, CancellationToken cancellationToken = default);

    Task AppendRecoveryLogAsync(OwnershipToken token, RecoveryLogEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Presents a synchronous <see cref="IRunStore"/> through the asynchronous boundary. It honours the cancellation rules: cancelled before submission ⇒
/// <see cref="OperationCanceledException"/> and nothing applied; cancellation observed once a write has been submitted ⇒
/// <see cref="UnknownCommitOutcomeException"/> even though the underlying store already finished (a real store could not know either).
/// </summary>
public sealed class SyncRunStoreAdapter(IRunStore inner) : IAsyncRunStore
{
    /// <summary>Test seam: called right after a write was submitted to the inner store and before the adapter inspects the token again (a real cancellation racing the commit).</summary>
    public Action<string>? AfterSubmit { get; set; }

    public Task<OwnershipToken> AcquireAsync(string runId, string ownerId, CancellationToken cancellationToken = default)
        => Run(cancellationToken, () => inner.Acquire(runId, ownerId));

    public Task ReleaseAsync(OwnershipToken token, CancellationToken cancellationToken = default)
        => Run(cancellationToken, () => { inner.Release(token); return 0; });

    public Task<RunSnapshot> LoadAsync(string runId, CancellationToken cancellationToken = default)
        => Run(cancellationToken, () => inner.Load(runId));

    public Task<AppendResult> CreateRunAsync(OwnershipToken token, RunHeader header, CancellationToken cancellationToken = default)
        => Write(cancellationToken, "CreateRun", () => inner.CreateRun(token, header));

    public Task<AppendResult> AppendControlAsync(OwnershipToken token, ControlRecord record, CancellationToken cancellationToken = default)
        => Write(cancellationToken, "AppendControl", () => inner.AppendControl(token, record));

    public Task<CommitResult> CommitAsync(OwnershipToken token, CommitBatch batch, CancellationToken cancellationToken = default)
        => Write(cancellationToken, "Commit", () => inner.Commit(token, batch));

    public Task AppendRecoveryLogAsync(OwnershipToken token, RecoveryLogEntry entry, CancellationToken cancellationToken = default)
        => Run(cancellationToken, () => { inner.AppendRecoveryLog(token, entry); return 0; });

    private static Task<T> Run<T>(CancellationToken ct, Func<T> action)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }
    }

    private Task<T> Write<T>(CancellationToken ct, string operation, Func<T> action)
    {
        ct.ThrowIfCancellationRequested();
        T result;
        try
        {
            result = action();
        }
        catch (Exception e)
        {
            return Task.FromException<T>(e);
        }

        AfterSubmit?.Invoke(operation);
        if (ct.IsCancellationRequested)
        {
            return Task.FromException<T>(new UnknownCommitOutcomeException($"{operation}: cancellation was requested after the write was submitted; the outcome is unknown to the caller"));
        }

        return Task.FromResult(result);
    }
}

/// <summary>
/// The economic content of a run snapshot reduced to one hash (header, controls, steps, outputs, projections, final record). Recovery diagnostics
/// are deliberately NOT an input, so no diagnostic row can ever change an economic digest (contract section A.19.6).
/// </summary>
public static class RunSnapshotDigest
{
    public static string Economic(RunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sb = new System.Text.StringBuilder();
        sb.Append(snapshot.Header?.ContentHash).Append('|');
        foreach (var c in snapshot.Controls)
        {
            sb.Append(c.Id).Append(':').Append(c.ContentHash).Append(';');
        }

        sb.Append('|');
        foreach (var s in snapshot.Steps)
        {
            sb.Append(s.ContentHash).Append(';');
        }

        sb.Append('|');
        foreach (var o in snapshot.Outputs)
        {
            sb.Append(o.ContentHash).Append(';');
        }

        sb.Append('|');
        foreach (var p in snapshot.Projections)
        {
            sb.Append(p.Table).Append(':').Append(p.Key).Append(':').Append(p.ContentHash).Append(';');
        }

        sb.Append('|').Append(snapshot.Final?.ContentHash).Append('|').Append(snapshot.Completed);
        return Codec.OutputCodec.ContentHash(sb.ToString());
    }
}
