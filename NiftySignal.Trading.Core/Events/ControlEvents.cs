namespace NiftySignal.Trading.Core.Events;

public enum FeedHealthState
{
    Healthy = 0,
    Degraded = 1,
}

/// <summary>Why the feed is declared unhealthy. Supplied by the CP5 adapter; the engine never infers a global fault from a single quiet contract.</summary>
public enum FeedFaultReason
{
    None = 0,
    SocketDisconnected = 1,
    NoMessageWatchdog = 2,
    DataGapOpen = 3,
    SubscriptionLoss = 4,
    DataIntegrity = 5,
    Other = 99,
}

/// <summary>
/// A GLOBAL feed-health transition reported by the adapter (contract section A.14.2). It is an ordered input event like any other (kind rank
/// before data events of the same instant), so replay and live paper see it at the same position in the stream. <see cref="EngineEvent.SourceId"/>
/// is the adapter's strictly increasing health sequence number.
/// </summary>
public sealed record FeedHealthEvent : EngineEvent
{
    public FeedHealthEvent(DateTimeOffset availableAt, long sequence, FeedHealthState state, FeedFaultReason reason, string detail = "")
        : base(availableAt, EventKind.FeedHealth, sequence, string.Empty)
    {
        if (!Enum.IsDefined(state) || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        State = state;
        Reason = reason;
        Detail = detail ?? string.Empty;
    }

    public FeedHealthState State { get; }

    public FeedFaultReason Reason { get; }

    public string Detail { get; }
}

public enum OperatorCommand
{
    Kill = 0,
    Reset = 1,
}

public enum KillMode
{
    /// <summary>Block new entries and cancel pending / waiting entries of the scope; open positions keep their exit rules. Default.</summary>
    BlockEntries = 0,

    /// <summary>As BlockEntries, and additionally request liquidation of the scope's open positions through the normal exit path.</summary>
    BlockEntriesAndLiquidate = 1,
}

/// <summary>
/// A manual kill-switch command (contract section A.14.3). Scope is "*" (all variants) or one VariantId. It is an ordered, replayable input
/// event; <see cref="EngineEvent.SourceId"/> is the operator-command sequence number. Real operator input (Dashboard) is out of scope until
/// the Dashboard is wired; this is the engine-side contract.
/// </summary>
public sealed record OperatorCommandEvent : EngineEvent
{
    public OperatorCommandEvent(DateTimeOffset availableAt, long sequence, OperatorCommand command, string scope, string actor, string reason, KillMode mode = KillMode.BlockEntries)
        : base(availableAt, EventKind.Control, sequence, string.Empty)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enum.IsDefined(command) || !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        Command = command;
        Scope = scope;
        Actor = actor;
        Reason = reason;
        Mode = mode;
    }

    public OperatorCommand Command { get; }

    public string Scope { get; }

    public string Actor { get; }

    public string Reason { get; }

    public KillMode Mode { get; }
}

/// <summary>
/// The CP5 adapter's contract for feed-health input. Implementations (live: DataGap / receive watchdog; replay: derived from the recorded gaps)
/// return the health events that happened at or before <paramref name="watermark"/> and have not been returned yet, in strictly increasing
/// sequence order; the adapter merges them into the event stream. Nothing in Trading.Core polls a feed or reads a wall clock.
/// </summary>
public interface IFeedHealthSource
{
    IReadOnlyList<FeedHealthEvent> Drain(DateTimeOffset watermark);
}
