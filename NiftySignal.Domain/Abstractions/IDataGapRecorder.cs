namespace NiftySignal.Domain.Abstractions;

/// <summary>
/// Lets NiftySignal.Ingestion record a disconnect window (plan section 3.3/3.4) without
/// depending on NiftySignal.Persistence directly -- Persistence implements this, wired up
/// in Host, same pattern as <see cref="ITickSource"/>.
/// </summary>
public interface IDataGapRecorder
{
    /// <summary>Returns an id to pass back to <see cref="RecordGapEndedAsync"/> once reconnected.</summary>
    Task<long> RecordGapStartedAsync(DateTimeOffset startedAt, string reason, CancellationToken cancellationToken);

    Task RecordGapEndedAsync(long gapId, DateTimeOffset endedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Closes every currently-open gap, not just one this process instance is tracking by id.
    /// A gap can outlive the process that opened it (a service restart while disconnected
    /// leaves it dangling in the DB forever, since the new process's in-memory gap id starts
    /// fresh and has no way to find it) -- calling this on every successful auth, instead of
    /// closing by a remembered id, means a fresh process self-heals any gap left open by a
    /// previous one.
    /// </summary>
    Task CloseAllOpenGapsAsync(DateTimeOffset endedAt, CancellationToken cancellationToken);
}
