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
}
