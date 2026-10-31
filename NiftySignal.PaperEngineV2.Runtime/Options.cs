using NiftySignal.PaperEngineV2.Alerts;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>
/// Paper Engine V2 SHADOW runtime configuration. <see cref="Enabled"/> is false unless the owner turns it on; an enabled instance with a missing or invalid required value refuses to start (visibly, fail closed)
/// instead of assuming anything. Queue capacity and maximum processing lag have NO defaults here: the candidate values (64 and 5 s) are recorded in appsettings and in the progress document with their burst evidence.
/// </summary>
public sealed record V2RuntimeOptions
{
    public bool Enabled { get; init; }

    /// <summary>Connection string of the trading database that holds the <c>pev2</c> schema (never created or migrated by the runtime).</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Folder of <c>pev2-strategy/1</c> JSON files. Disabled strategies in the folder are recorded as configuration only.</summary>
    public string? StrategyDirectory { get; init; }

    public int QueueCapacity { get; init; }

    public TimeSpan MaxProcessingLag { get; init; }

    /// <summary>Consecutive clean verified frames required before entries reopen after a gap, overflow, restart or other integrity event.</summary>
    public int FramesToClearGap { get; init; }

    /// <summary>Bar notices the observer may leave unread before the runtime starts dropping them (visibly: the missing bars become a journaled gap).</summary>
    public int NoticeCapacity { get; init; } = 256;

    /// <summary>Terminal-exit retry cut-off (must be later than 15:00 IST); after it an unfilled exit is reported UNRESOLVED.</summary>
    public TimeOnly TerminalRetryUntil { get; init; }

    /// <summary>Flat brokerage per order used for the paper cost schedules (the statutory charges come from the existing NSE schedules).</summary>
    public decimal? BrokeragePerOrder { get; init; }

    /// <summary>Version of the ONE cost schedule strategies may reference in <c>fees.schedule</c> (an owner decision: <c>npt1-frozen-fees-v1</c> or <c>nse-index-options-2026-04-01-provisional</c>); the two overlap in time, so exactly one is installed.</summary>
    public string? CostScheduleVersion { get; init; }

    public AlertOptions Alerts { get; init; } = AlertOptions.AllOff;

    public TimeSpan AlertPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay before a faulted consumer is rebuilt from the durable journal.</summary>
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(5);

    public bool AnyAlertEnabled => Alerts.Opened || Alerts.Closed || Alerts.OrderRejected || Alerts.PartialFill || Alerts.ExecutionFailure;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled) return errors;
        if (string.IsNullOrWhiteSpace(ConnectionString)) errors.Add("PaperEngineV2:ConnectionString (or a named connection string) is required");
        if (string.IsNullOrWhiteSpace(StrategyDirectory)) errors.Add("PaperEngineV2:StrategyDirectory is required");
        if (QueueCapacity is < 1 or > 100_000) errors.Add("PaperEngineV2:QueueCapacity must be in [1, 100000]");
        if (MaxProcessingLag <= TimeSpan.Zero) errors.Add("PaperEngineV2:MaxProcessingLagSeconds must be positive");
        if (FramesToClearGap < 1) errors.Add("PaperEngineV2:FramesToClearGap must be >= 1");
        if (NoticeCapacity < 1) errors.Add("PaperEngineV2:NoticeCapacity must be >= 1");
        if (TerminalRetryUntil <= new TimeOnly(15, 0)) errors.Add("PaperEngineV2:TerminalRetryUntil must be later than 15:00");
        if (CostScheduleVersion is not ("npt1-frozen-fees-v1" or "nse-index-options-2026-04-01-provisional")) errors.Add("PaperEngineV2:CostScheduleVersion must be npt1-frozen-fees-v1 or nse-index-options-2026-04-01-provisional");
        if (BrokeragePerOrder is not { } b || b < 0m) errors.Add("PaperEngineV2:BrokeragePerOrder is required (costs are never assumed zero)");
        return errors;
    }
}
