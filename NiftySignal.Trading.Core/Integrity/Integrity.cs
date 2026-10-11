using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Core.Strategies;

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

/// <summary>
/// Anything that can veto NEW entries (feed integrity, kill switches at global / family / variant scope, the risk profile's scoped limits, the shared capital).
/// Returns EVERY reason that holds in stable order (empty = entries allowed). <paramref name="newPremium"/> is the premium (price x quantity) the entry would add to
/// the committed capital; <paramref name="excludeOpportunityId"/> is the opportunity whose own outstanding intent must not be counted twice. Exits are never gated.
/// </summary>
public interface IEntryGate
{
    IReadOnlyList<RejectionReason> Evaluate(VariantKey variant, DateTimeOffset now, decimal newPremium, string? excludeOpportunityId);
}

/// <summary>
/// Kill / reset scope text. <c>*</c> is the whole engine (GLOBAL), a bare StrategyId such as <c>X</c> is a STRATEGY FAMILY and <c>StrategyId/VariantId</c> such as
/// <c>X/X1</c> is one VARIANT. A family scope covers every variant of that family and nothing else.
/// </summary>
public static class KillScopes
{
    public static bool IsGlobal(string scope) => scope == "*";

    public static bool IsFamily(string scope) => scope != "*" && VariantKey.IsValidPart(scope);

    public static bool IsVariant(string scope, out VariantKey key) => VariantKey.TryParse(scope, out key) && !key.IsGlobal;

    /// <summary>True when the scope text covers the variant.</summary>
    public static bool Covers(string scope, VariantKey variant)
        => IsGlobal(scope)
           || (IsFamily(scope) && string.Equals(scope, variant.StrategyId, StringComparison.Ordinal))
           || (IsVariant(scope, out var key) && key == variant);

    /// <summary>True when the scope is well formed and names something that exists among the variants.</summary>
    public static bool IsKnown(string scope, IEnumerable<VariantKey> variants)
        => IsGlobal(scope) || variants.Any(v => Covers(scope, v));
}

public sealed record IntegrityTransition(DateTimeOffset At, IntegrityState From, IntegrityState To, FeedFaultReason Reason, string Detail, long Sequence)
    : EngineOutput(At, VariantKey.Global);

public sealed record KillSwitchChanged(DateTimeOffset At, string Scope, OperatorCommand Command, KillMode Mode, string Actor, string Reason, string Effect, int CancelledPending, int CancelledWaiting, int LiquidationOrders)
    : EngineOutput(At, VariantKey.TryParse(Scope, out var scopeKey) ? scopeKey : VariantKey.Global);

/// <summary>An input event that was rejected before reaching the engine (late, conflicting duplicate or inconsistent), with the reason. The event has NO effect on state.</summary>
public sealed record EventQuarantined(DateTimeOffset At, EventKind Kind, long SourceId, string Token, string Reason, string Detail)
    : EngineOutput(At, VariantKey.Global);

public sealed record EvaluationSummary(DateTimeOffset At, VariantKey Variant, string? OpportunityId, string Scope, string Subject, string Reason, int Count, DateTimeOffset FirstAt, DateTimeOffset LastAt)
    : EngineOutput(At, Variant);

/// <summary>
/// Global feed integrity plus the manual kill switch (contract section A.14). Pure state: the owner (<c>TradingEngine</c>) performs the effects
/// (cancel pending entries, cancel waiting opportunities, request liquidation) and writes the outputs.
/// <para>
/// Scopes. <b>Global</b>: feed health (this class) and kill-all. <b>Variant</b>: a kill of one variant. <b>Contract</b>: a stale / missing / one-sided
/// quote of a single contract is NOT an integrity state; it is ordinary quote quality that blocks entries on that contract only (reason code
/// <c>QuoteStale</c> etc.) and never cancels other contracts' orders.
/// </para>
/// </summary>
public sealed class IntegrityManager
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

    /// <summary>Killed family scopes (bare StrategyIds).</summary>
    public IReadOnlyCollection<string> KilledFamilies => _killedVariants.Where(KillScopes.IsFamily).Order(StringComparer.Ordinal).ToList();

    /// <summary>Kill scopes are stored as the canonical text of the scope: a family kill is <c>StrategyId</c>, a variant kill is <c>StrategyId/VariantId</c>.</summary>
    public bool IsKilled(VariantKey variant) => KillAll || _killedVariants.Contains(variant.StrategyId) || _killedVariants.Contains(variant.ToString());

    /// <summary>Every kill scope that blocks entries of the variant, in stable order (global, family, variant).</summary>
    public IReadOnlyList<RejectionReason> KillReasons(VariantKey variant)
    {
        var reasons = new List<RejectionReason>();
        if (KillAll)
        {
            reasons.Add(RejectionReason.KillSwitchGlobal);
        }

        if (_killedVariants.Contains(variant.StrategyId))
        {
            reasons.Add(RejectionReason.KillSwitchFamily);
        }

        if (_killedVariants.Contains(variant.ToString()))
        {
            reasons.Add(RejectionReason.KillSwitchVariant);
        }

        return reasons;
    }

    /// <summary>The feed-integrity block (a global state), as a typed reason; empty when healthy.</summary>
    public IReadOnlyList<RejectionReason> IntegrityReasons()
        => State switch
        {
            IntegrityState.Degraded => new[] { RejectionReason.IntegrityDegraded },
            IntegrityState.Recovering => new[] { RejectionReason.IntegrityRecovering },
            _ => Array.Empty<RejectionReason>(),
        };

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

    public string? BlockReason(VariantKey variant, DateTimeOffset now)
    {
        if (IsKilled(variant))
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

    /// <summary>
    /// Clears a kill. Reset("*") clears everything. Reset(family) is ignored while a global kill is active; Reset(variant) is ignored while a global or its family kill
    /// is active ("ResetIgnored:GlobalKillActive" / "ResetIgnored:FamilyKillActive"). A reset clears exactly the named scope.
    /// </summary>
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

        if (KillScopes.IsVariant(scope, out var variant) && _killedVariants.Contains(variant.StrategyId))
        {
            return "ResetIgnored:FamilyKillActive";
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
