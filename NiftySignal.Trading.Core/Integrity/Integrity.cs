using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Integrity;

public enum IntegrityState
{
    /// <summary>Normal operation.</summary>
    Healthy = 0,

    /// <summary>A global feed fault was reported: new entries are blocked and pending entries are cancelled; exits and liquidation continue on eligible quotes.</summary>
    Degraded = 1,

    /// <summary>The adapter reported recovery, but entries stay blocked until the configured recovery evidence (futures quote, ready bar; see <see cref="IntegrityPolicy"/>) has been delivered.</summary>
    Recovering = 2,
}

/// <summary>
/// What counts as evidence that the feed and the adaptive state are usable again after a reported fault (hashed configuration). Defaults demand
/// both a futures quote and a ready bar that started after the recovery instant. At least one requirement must stay on: recovery is never
/// granted from "any update".
/// </summary>
public sealed record IntegrityPolicy
{
    /// <summary>A futures quote must be delivered at or after the recovery instant.</summary>
    public bool RequireFutureQuoteAfterRecovery { get; init; } = true;

    /// <summary>A completed bar with <c>BarInfo.Ready</c> must be delivered at or after the recovery instant.</summary>
    public bool RequireReadyBarAfterRecovery { get; init; } = true;

    /// <summary>That ready bar must also have started at or after the recovery instant (all its ticks post-recovery).</summary>
    public bool ReadyBarMustStartAfterRecovery { get; init; } = true;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!RequireFutureQuoteAfterRecovery && !RequireReadyBarAfterRecovery)
        {
            errors.Add("At least one recovery evidence requirement (futures quote or ready bar) must be enabled.");
        }

        if (ReadyBarMustStartAfterRecovery && !RequireReadyBarAfterRecovery)
        {
            errors.Add("ReadyBarMustStartAfterRecovery needs RequireReadyBarAfterRecovery.");
        }

        return errors;
    }
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
    private readonly IntegrityPolicy _policy;
    private DateTimeOffset? _recoveryRequestedAt;
    private bool _futureQuoteSeen;
    private bool _readyBarSeen;
    private long _evidenceBarSeq;

    public IntegrityManager(IntegrityPolicy? policy = null)
    {
        _policy = policy ?? new IntegrityPolicy();
        var errors = _policy.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid integrity policy: " + string.Join(" ", errors), nameof(policy));
        }
    }

    public IntegrityState State { get; private set; } = IntegrityState.Healthy;

    public FeedFaultReason FaultReason { get; private set; } = FeedFaultReason.None;

    public bool KillAll { get; private set; }

    public IReadOnlyCollection<string> KilledVariants => _killedVariants;

    public bool IsKilled(string variantId) => KillAll || _killedVariants.Contains(variantId);

    /// <summary>Integrity state, kill switch and the recovery-evidence progress.</summary>
    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("integrity");
        w.Line("state", State);
        w.Line("fault", FaultReason);
        w.Line("killAll", KillAll);
        w.Line("killedVariants", string.Join(",", _killedVariants.Order(StringComparer.Ordinal)));
        w.Line("recoveryRequestedAt", _recoveryRequestedAt);
        w.Line("futureQuoteSeen", _futureQuoteSeen);
        w.Line("readyBarSeen", _readyBarSeen);
        w.Line("evidenceBarSeq", _evidenceBarSeq);
    }

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
        _futureQuoteSeen = false;
        _readyBarSeen = false;
        _evidenceBarSeq = 0;
        return new IntegrityTransition(e.AvailableAt, from, State, FaultReason, e.Detail, e.SourceId);
    }

    /// <summary>
    /// Recovery evidence (contract section A.16.2). A futures quote delivered at or after the recovery instant proves the futures stream is alive
    /// again. An option quote is NOT evidence (it says nothing about the futures / adaptive state); option-quote quality is checked per contract at
    /// entry time as always.
    /// </summary>
    public IntegrityTransition? OnFutureQuote(QuoteEvent quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (State != IntegrityState.Recovering || _recoveryRequestedAt is not { } requested || quote.Kind != EventKind.FutureQuote || quote.AvailableAt < requested)
        {
            return null;
        }

        _futureQuoteSeen = true;
        return TryComplete(quote.AvailableAt, quote.SourceId);
    }

    /// <summary>
    /// Recovery evidence: a completed bar whose readiness evidence says <c>Ready</c> (frozen session, enough consecutive valid bars, signal started)
    /// and — by default — that also STARTED at or after the recovery instant, so every tick of the bar was observed after the feed came back.
    /// A bar without readiness information is never evidence.
    /// </summary>
    public IntegrityTransition? OnBar(BarCompletedEvent bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (State != IntegrityState.Recovering || _recoveryRequestedAt is not { } requested || bar.AvailableAt < requested)
        {
            return null;
        }

        if (bar.Info is { Ready: true } info && (!_policy.ReadyBarMustStartAfterRecovery || info.StartAvailableAt >= requested))
        {
            _readyBarSeen = true;
            _evidenceBarSeq = bar.BarSeq;
        }

        return TryComplete(bar.AvailableAt, bar.SourceId);
    }

    private IntegrityTransition? TryComplete(DateTimeOffset at, long sourceId)
    {
        if ((_policy.RequireFutureQuoteAfterRecovery && !_futureQuoteSeen) || (_policy.RequireReadyBarAfterRecovery && !_readyBarSeen))
        {
            return null;
        }

        var from = State;
        State = IntegrityState.Healthy;
        var reason = FaultReason;
        FaultReason = FeedFaultReason.None;
        var detail = FormattableString.Invariant($"recovery evidence: futureQuote={(_futureQuoteSeen ? "yes" : "n/a")} readyBar={(_readyBarSeen ? "b" + _evidenceBarSeq : "n/a")} since {_recoveryRequestedAt:O}");
        _recoveryRequestedAt = null;
        return new IntegrityTransition(at, from, State, reason, detail, sourceId);
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

    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("admission");
        w.Line("quarantined", QuarantinedCount);
        w.Line("lastBarSeq", _lastBarSeq);
    }

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
