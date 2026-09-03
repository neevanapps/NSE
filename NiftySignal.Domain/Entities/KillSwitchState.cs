namespace NiftySignal.Domain.Entities;

/// <summary>
/// The persisted, cross-process kill switch (plan section 11: "dashboard toggle"). A
/// single row (Id=1) rather than config-file state -- the Dashboard and Host are separate
/// processes (ARCHITECTURE.md), and coordinating a toggle through the database both
/// processes already share is more robust than two processes fighting over the same JSON
/// file. Disables new entries only; ingestion keeps running regardless (plan section 1.6
/// / 11).
/// </summary>
public sealed class KillSwitchState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool EntriesEnabled { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who/what last changed it -- "Dashboard" for a manual toggle, or a reason string when the daily-loss circuit breaker trips it automatically.</summary>
    public required string UpdatedBy { get; set; }
}
