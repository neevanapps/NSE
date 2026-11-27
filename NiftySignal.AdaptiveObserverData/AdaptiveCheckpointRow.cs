namespace NiftySignal.AdaptiveObserverData;

/// <summary>
/// One durable Observer checkpoint (audit finding F73): the Observer's complete processing state at a consistent boundary, so a restart replays only the source rows after it. Written as a single row (atomic).
/// It holds no execution or position state. A checkpoint is only an accelerator: with none usable the whole day is replayed.
/// </summary>
public sealed class AdaptiveCheckpointRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>Availability instant of the last processed group (the processing boundary the state describes).</summary>
    public DateTimeOffset? ProcessedThroughUtc { get; set; }
    public int LastBarSeq { get; set; }
    /// <summary>Session, configuration and build identity; a checkpoint with another identity is never restored.</summary>
    public required string Identity { get; set; }
    public int FormatVersion { get; set; }
    /// <summary>SHA-256 (hex) of <see cref="Payload"/> as stored.</summary>
    public required string PayloadSha256 { get; set; }
    public required byte[] Payload { get; set; }
}
