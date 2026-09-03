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

    public required string Reason { get; set; }
}
