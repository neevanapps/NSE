using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Integrity;

public enum IntegrityState
{
    /// <summary>Normal operation.</summary>
    Healthy = 0,

    /// <summary>A global feed fault was reported: new entries are blocked and pending entries are cancelled; exits and liquidation continue on eligible quotes.</summary>
    Degraded = 1,

    /// <summary>The adapter reported recovery, but entries stay blocked until the first option quote at or after the recovery instant is accepted.</summary>
    Recovering = 2,
}

/// <summary>Anything that can veto NEW entries (feed integrity, manual kill switch, risk profile). Returns a reason code or null when entries are allowed.</summary>
public interface IEntryGate
{
    string? BlockReason(string variantId, DateTimeOffset now);
}

public sealed record IntegrityTransition(DateTimeOffset At, IntegrityState From, IntegrityState To, FeedFaultReason Reason, string Detail, long Sequence)
    : EngineOutput(At, "*");

public sealed record KillSwitchChanged(DateTimeOffset At, string Scope, OperatorCommand Command, KillMode Mode, string Actor, string Reason, string Effect, int CancelledPending, int CancelledWaiting, int LiquidationOrders)
    : EngineOutput(At, Scope);

/// <summary>An input event that was rejected before reaching the engine (late, conflicting duplicate or inconsistent), with the reason. The event has NO effect on state.</summary>
public sealed record EventQuarantined(DateTimeOffset At, EventKind Kind, long SourceId, string Token, string Reason, string Detail)
    : EngineOutput(At, "*");

public sealed record EvaluationSummary(DateTimeOffset At, string VariantId, string? OpportunityId, string Scope, string Subject, string Reason, int Count, DateTimeOffset FirstAt, DateTimeOffset LastAt)
    : EngineOutput(At, VariantId);

/// <summary>
/// Global feed integrity plus the manual kill switch (contract section A.14). Pure state: the owner (<c>TradingEngine</c>) performs the effects
/// (cancel pending entries, cancel waiting opportunities, request liquidation) and writes the outputs.
/// <para>
/// Scopes. <b>Global</b>: feed health (this class) and kill-all. <b>Variant</b>: a kill of one variant. <b>Contract</b>: a stale / missing / one-sided
/// quote of a single contract is NOT an integrity state; it is ordinary quote quality that blocks entries on that contract only (reason code
/// <c>QuoteStale</c> etc.) and never cancels other contracts' orders.
/// </para>
/// </summary>
public sealed class IntegrityManager : IEntryGate
{
    private readonly HashSet<string> _killedVariants = new(StringComparer.Ordinal);
    private DateTimeOffset? _recoveryRequestedAt;

    public IntegrityState State { get; private set; } = IntegrityState.Healthy;

    public FeedFaultReason FaultReason { get; private set; } = FeedFaultReason.None;

    public bool KillAll { get; private set; }

    public IReadOnlyCollection<string> KilledVariants => _killedVariants;

    public bool IsKilled(string variantId) => KillAll || _killedVariants.Contains(variantId);

    public string? BlockReason(string variantId, DateTimeOffset now)
    {
        if (IsKilled(variantId))
        {
            return "KillSwitch";
        }

        return State switch
        {
            IntegrityState.Degraded => "IntegrityDegraded",
            IntegrityState.Recovering => "IntegrityRecovering",
            _ => null,
        };
    }

    /// <summary>Applies a feed-health event. Returns the transition, or null when the event changes nothing (for example Degraded while already Degraded).</summary>
    public IntegrityTransition? OnFeedHealth(FeedHealthEvent e)
    {
        var from = State;
        if (e.State == FeedHealthState.Degraded)
        {
            if (State == IntegrityState.Degraded)
            {
                return null;
            }

            State = IntegrityState.Degraded;
            FaultReason = e.Reason;
            _recoveryRequestedAt = null;
            return new IntegrityTransition(e.AvailableAt, from, State, e.Reason, e.Detail, e.SourceId);
        }

        if (State != IntegrityState.Degraded)
        {
            return null;
        }

        State = IntegrityState.Recovering;
        _recoveryRequestedAt = e.AvailableAt;
        return new IntegrityTransition(e.AvailableAt, from, State, FaultReason, e.Detail, e.SourceId);
    }

    /// <summary>Called for every accepted option quote: completes a recovery once a quote at or after the recovery instant arrives.</summary>
    public IntegrityTransition? OnAcceptedOptionQuote(DateTimeOffset at, long sourceId)
    {
        if (State != IntegrityState.Recovering || _recoveryRequestedAt is not { } requested || at < requested)
        {
            return null;
        }

        var from = State;
        State = IntegrityState.Healthy;
        var reason = FaultReason;
        FaultReason = FeedFaultReason.None;
        _recoveryRequestedAt = null;
        return new IntegrityTransition(at, from, State, reason, "first accepted option quote after recovery", sourceId);
    }

    /// <summary>Marks a scope killed. Returns false when it already was (idempotent).</summary>
    public bool Kill(string scope)
    {
        if (scope == "*")
        {
            var already = KillAll;
            KillAll = true;
            return !already;
        }

        return _killedVariants.Add(scope);
    }

    /// <summary>Clears a kill. Reset("*") clears everything; Reset(variant) is ignored while a global kill is active (returns "ResetIgnored:GlobalKillActive").</summary>
    public string Reset(string scope)
    {
        if (scope == "*")
        {
            var any = KillAll || _killedVariants.Count > 0;
            KillAll = false;
            _killedVariants.Clear();
            return any ? "Reset" : "NothingToReset";
        }

        if (KillAll)
        {
            return "ResetIgnored:GlobalKillActive";
        }

        return _killedVariants.Remove(scope) ? "Reset" : "NothingToReset";
    }
}

/// <summary>
/// Admission control in front of the engine loop: rejects (quarantines) events that the loop would throw on or that are internally inconsistent, with
/// an <see cref="EventQuarantined"/> record. A quarantined event has no effect on any state. Exact duplicates are NOT quarantined (the loop ignores
/// them idempotently). Rules: a group at or before the last processed group, or before the engine clock, is <c>Late</c>; the same identity delivered with
/// different content is <c>ConflictingDuplicate</c>; a quote with a negative price or size is <c>Inconsistent:NegativeValue</c>; a bar whose sequence
/// does not increase is <c>Inconsistent:BarSequence</c>.
/// </summary>
public sealed class EventAdmission
{
    private readonly Action<EventQuarantined> _report;
    private long _lastBarSeq = long.MinValue;

    public EventAdmission(Action<EventQuarantined> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
    }

    public int QuarantinedCount { get; private set; }

    /// <summary>Returns the events of <paramref name="group"/> that may be given to <c>loop.ProcessGroup</c>.</summary>
    public IReadOnlyList<EngineEvent> Admit(EngineLoop loop, IReadOnlyList<EngineEvent> group)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(group);
        var admitted = new List<EngineEvent>(group.Count);
        foreach (var e in group)
        {
            var late = (loop.LastGroupTime is { } last && e.AvailableAt <= last) || e.AvailableAt < loop.Now;
            if (late && !(loop.TryGetSeen(e.Identity, out var seenEarlier) && seenEarlier.Equals(e)))
            {
                Quarantine(loop, e, "Late", $"AvailableAt {e.AvailableAt:O} is not after the processed boundary (last group {loop.LastGroupTime:O}, engine time {loop.Now:O}).");
                continue;
            }

            if (loop.TryGetSeen(e.Identity, out var seen) && !seen.Equals(e))
            {
                Quarantine(loop, e, "ConflictingDuplicate", $"Identity {e.Identity} was already delivered with different content.");
                continue;
            }

            if (e is QuoteEvent q && (q.Bid < 0m || q.Ask < 0m || q.Last < 0m || q.BidQty < 0 || q.AskQty < 0))
            {
                Quarantine(loop, e, "Inconsistent:NegativeValue", "A price or size is negative.");
                continue;
            }

            if (e is BarCompletedEvent bar)
            {
                if (bar.BarSeq <= _lastBarSeq)
                {
                    Quarantine(loop, e, "Inconsistent:BarSequence", $"Bar {bar.BarSeq} does not follow bar {_lastBarSeq}.");
                    continue;
                }

                _lastBarSeq = bar.BarSeq;
            }

            admitted.Add(e);
        }

        return admitted;
    }

    private void Quarantine(EngineLoop loop, EngineEvent e, string reason, string detail)
    {
        QuarantinedCount++;
        _report(new EventQuarantined(e.AvailableAt > loop.Now ? e.AvailableAt : loop.Now, e.Kind, e.SourceId, e.Token, reason, detail));
    }
}

/// <summary>Reason codes returned by the entry gates.</summary>
public static class GateReasons
{
    public const string KillSwitch = "KillSwitch";
    public const string IntegrityDegraded = "IntegrityDegraded";
    public const string IntegrityRecovering = "IntegrityRecovering";
}
