namespace NiftySignal.Domain.Entities;

/// <summary>
/// A disconnect window (plan section 3.3/3.4). Any rolling window spanning a gap must be
/// flagged degraded downstream and must not produce trade signals -- never interpolate
/// across one.
/// </summary>
public sealed class DataGap
{
    public long Id { get; set; }

    public required DateTimeOffset StartedAt { get; set; }

    /// <summary>Null while the gap is still open (reconnect hasn't succeeded yet).</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The failure that opened the gap. See <see cref="LastReason"/> for the most recent one.</summary>
    public required string Reason { get; set; }

    /// <summary>
    /// How many connection attempts failed during this gap, counting the one that opened it
    /// (2026-09-07). Without this, a gap's duration is uninterpretable: 07 Sep averaged 59
    /// seconds per gap against a backoff schedule that starts at 1 second, and there was no
    /// way to tell whether that meant one slow reconnect or six escalating retries against a
    /// gateway returning 502s. Only <see cref="Reason"/> was stored, and only for the first
    /// failure, so every retry inside the gap was invisible.
    /// </summary>
    public int ReconnectAttempts { get; set; }

    /// <summary>
    /// The most recent failure in this gap, when it differs from the one that opened it --
    /// the pattern that matters is "dropped by the server, then refused on the way back in."
    /// Null when the gap only ever saw its opening failure.
    /// </summary>
    public string? LastReason { get; set; }
}
