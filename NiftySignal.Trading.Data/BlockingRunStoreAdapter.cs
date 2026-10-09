using NiftySignal.Trading.Recovery;

namespace NiftySignal.Trading.Data;

/// <summary>
/// Presents an <see cref="IAsyncRunStore"/> to the synchronous <see cref="DurableRun"/> by blocking on each call (no synchronisation context is
/// captured). No cancellation token is passed: a durable run that needs a deadline supplies one through <paramref name="timeout"/>, which on expiry
/// is reported exactly as the rules above require (a write in flight ⇒ unknown outcome).
/// </summary>
public sealed class BlockingRunStoreAdapter(IAsyncRunStore inner, TimeSpan? timeout = null) : IRunStore
{
    public OwnershipToken Acquire(string runId, string ownerId) => Wait(ct => inner.AcquireAsync(runId, ownerId, ct));

    public void Release(OwnershipToken token) => Wait(ct => inner.ReleaseAsync(token, ct));

    public RunSnapshot Load(string runId) => Wait(ct => inner.LoadAsync(runId, ct));

    public AppendResult CreateRun(OwnershipToken token, RunHeader header) => Wait(ct => inner.CreateRunAsync(token, header, ct));

    public AppendResult AppendControl(OwnershipToken token, ControlRecord record) => Wait(ct => inner.AppendControlAsync(token, record, ct));

    public CommitResult Commit(OwnershipToken token, CommitBatch batch) => Wait(ct => inner.CommitAsync(token, batch, ct));

    public void AppendRecoveryLog(OwnershipToken token, RecoveryLogEntry entry) => Wait(ct => inner.AppendRecoveryLogAsync(token, entry, ct));

    private T Wait<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        return Task.Run(() => call(cts.Token)).GetAwaiter().GetResult();
    }

    private void Wait(Func<CancellationToken, Task> call)
        => Wait(ct => Task.Run(async () => { await call(ct).ConfigureAwait(false); return 0; }));
}
