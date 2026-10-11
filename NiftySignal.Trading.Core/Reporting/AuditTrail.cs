using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Reporting;

public enum AuditCategory
{
    Opportunity = 0,
    Selection = 1,
    Evaluation = 2,
    Sizing = 3,
    EntryIntent = 4,
    EntryOrder = 5,
    Fill = 6,
    Cancellation = 7,
    Exit = 8,
    Position = 9,
    Integrity = 10,
    KillSwitch = 11,
    DailySl = 12,
    Quarantine = 13,
    Session = 14,
    Summary = 15,
}

/// <summary>Stable machine-readable reason codes. Free text never carries the meaning; it only accompanies it in <see cref="AuditEntry.Detail"/>.</summary>
public enum ReasonCode
{
    None = 0,
    EpisodeValid,
    ContractSelected,
    NoEligibleContract,
    IntentIssued,
    Rearmed,
    SessionNotEligible,
    StrategyNotValid,
    StrategyInvalidated,
    EntryClose,
    MaxAttempts,
    CapacityBusy,
    InsufficientBudget,
    InvalidQuantity,
    NotEvaluable,
    ConditionNotSatisfied,
    QuoteNotCausal,
    QuoteStale,
    QuoteNotExecutable,
    NoQuote,
    SpreadTooWide,
    NoLiquidity,
    AwaitingNewerQuote,
    AskBelowBand,
    AskAboveBand,
    AskAboveLimit,
    IntegrityDegraded,
    IntegrityRecovering,
    KillSwitch,
    RiskBlocked,
    Filled,
    PartiallyFilled,
    Expired,
    Cancelled,
    Tp1,
    TradeSl,
    FinalTarget,
    StrategyExit,
    SessionLiquidation,
    ManualKill,
    PositionOpened,
    PositionClosed,
    UnresolvedAtClose,
    UnresolvedAtSessionEnd,
    DailySlBreached,
    IntegrityTransition,
    KillSwitchCommand,
    EventQuarantined,
    SessionEnded,
    Other,
}

/// <summary>
/// One row of the structured audit trail. Applicable identifiers are filled, the rest are null. <see cref="ConfigHash"/> is the whole-config hash and
/// <see cref="VariantHash"/> the hash that determines this variant's behaviour (both are in every row, so a row can be tied to the exact configuration).
/// <see cref="Seq"/> is the position in the output stream (the order of occurrence).
/// </summary>
public sealed record AuditEntry(
    long Seq,
    DateTimeOffset At,
    string RunId,
    string RunMode,
    string AccountId,
    string VariantId,
    string? OpportunityId,
    string? OrderId,
    string? PositionId,
    string ConfigHash,
    string VariantHash,
    AuditCategory Category,
    ReasonCode Reason,
    string Event,
    string Detail);

/// <summary>
/// Derives the structured audit trail from the engine's outputs (accepted and rejected opportunities, intents, orders, fills, cancellations, exits,
/// integrity transitions, daily-SL observations, quarantined events). Every state transition and accounting event of the output stream appears exactly
/// once. Repetitive per-quote rejections are bounded at the source: the first occurrence per opportunity / order is an entry, the rest are folded into one
/// <see cref="EvaluationSummary"/> row carrying the count and the first / last time.
/// </summary>
public static class AuditTrail
{
    public static IReadOnlyList<AuditEntry> Build(TradingEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var configHash = engine.Variants.ConfigHash.Value;
        var list = new List<AuditEntry>(engine.Outputs.Count);
        var positionOpportunity = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < engine.Outputs.Count; i++)
        {
            var o = engine.Outputs[i];
            var variant = o.VariantId;
            var hash = variant == "*" || !engine.Variants.Variants.Any(v => v.VariantId == variant) ? string.Empty : engine.Variants.GetVariant(variant).VariantHash;
            var account = hash.Length == 0 ? "*" : engine.Variants.GetVariant(variant).Spec.ResolveAccountId();
            AuditEntry E(AuditCategory category, ReasonCode reason, string ev, string detail, string? opportunity = null, string? order = null, string? position = null)
                => new(i, o.At, engine.RunId, engine.RunMode, account, variant, opportunity, order, position, configHash, hash, category, reason, ev, detail);

            switch (o)
            {
                case OpportunityTransition t:
                    list.Add(E(AuditCategory.Opportunity, Map(t.Reason), $"{t.From}->{t.To}", t.Reason, t.OpportunityId));
                    break;
                case AuditRecord a:
                    list.Add(E(CategoryOf(a.Stage), Map(a.Outcome), $"{a.Stage}:{a.Outcome}", a.Detail, a.OpportunityId));
                    break;
                case EntryIntent intent:
                    list.Add(E(AuditCategory.EntryIntent, ReasonCode.IntentIssued, "EntryIntent",
                        FormattableString.Invariant($"token={intent.Contract.Token} lots={intent.Lots} qty={intent.RequestedQuantity} limit={intent.LimitPrice} band=[{intent.MinimumEntryPremium},{intent.MaximumEntryPremium}] attempt={intent.Attempt} slot={intent.SlotNo} condition={intent.ConditionKind}"),
                        intent.OpportunityId, intent.IntentId));
                    break;
                case CancelRequest c:
                    list.Add(E(AuditCategory.Cancellation, c.Reason switch
                    {
                        CancelReason.EntryClose => ReasonCode.EntryClose,
                        CancelReason.IntegrityDegraded => ReasonCode.IntegrityDegraded,
                        CancelReason.KillSwitch => ReasonCode.KillSwitch,
                        _ => ReasonCode.StrategyInvalidated,
                    }, "CancelRequest", c.Reason.ToString(), c.OpportunityId, c.IntentId));
                    break;
                case ExitOrderCreated x:
                    positionOpportunity.TryGetValue(x.PositionId, out var opp);
                    list.Add(E(AuditCategory.Exit, MapExit(x.Reason), "ExitOrderCreated",
                        FormattableString.Invariant($"reason={x.Reason} qty={x.Quantity} firstAttempt={x.FirstAttemptAt:O} threshold={x.TriggerThreshold}"), opp, x.OrderId, x.PositionId));
                    break;
                case Tp1Resolved r:
                    list.Add(E(AuditCategory.Exit, ReasonCode.Tp1, "Tp1Resolved", FormattableString.Invariant($"state={r.State} targetQty={r.TargetQuantity} openLots={r.OpenLots}"), null, null, r.PositionId));
                    break;
                case FillRecord f:
                    list.Add(E(AuditCategory.Fill, f.Reason is { } fr ? MapExit(fr) : (f.BelowBand ? ReasonCode.AskBelowBand : ReasonCode.Filled), "Fill",
                        FormattableString.Invariant($"{f.Side} qty={f.Quantity} price={f.Price} notional={f.Notional} costs={f.Costs.Total} belowBand={f.BelowBand} afterFinalClose={f.AfterFinalClose} book={f.Book}"), null, f.OrderId, f.PositionId));
                    break;
                case PositionOpened p:
                    positionOpportunity[p.PositionId] = p.OpportunityId;
                    list.Add(E(AuditCategory.Position, ReasonCode.PositionOpened, "PositionOpened", FormattableString.Invariant($"token={p.Token} qty={p.Quantity} avgEntry={p.AvgEntryPrice}"), p.OpportunityId, null, p.PositionId));
                    break;
                case PositionClosed p:
                    positionOpportunity.TryGetValue(p.PositionId, out var closedOpp);
                    list.Add(E(AuditCategory.Position, ReasonCode.PositionClosed, "PositionClosed", FormattableString.Invariant($"gross={p.GrossPnl} costs={p.TotalCosts} net={p.NetPnl}"), closedOpp, null, p.PositionId));
                    break;
                case UnresolvedAtClose u:
                    list.Add(E(AuditCategory.Position, ReasonCode.UnresolvedAtClose, "UnresolvedAtClose", FormattableString.Invariant($"openQty={u.OpenQuantity} reserved={u.ReservedExitQuantity} liveOrders={u.LiveExitOrders} lastBid={u.LastEligibleBid} bidAge={u.LastBidAge}"), null, null, u.PositionId));
                    break;
                case UnresolvedAtSessionEnd u:
                    list.Add(E(AuditCategory.Position, ReasonCode.UnresolvedAtSessionEnd, "UnresolvedAtSessionEnd", FormattableString.Invariant($"openQty={u.OpenQuantity} lastBid={u.LastEligibleBid} bidAge={u.LastBidAge}"), null, null, u.PositionId));
                    break;
                case DailySlBreached d:
                    list.Add(E(AuditCategory.DailySl, ReasonCode.DailySlBreached, "DailySlBreached (observation only)", FormattableString.Invariant($"dailyPnl={d.DailyPnl} dailySl={d.DailySl} conservativeMark={d.ConservativeMark}; nothing was blocked or liquidated")));
                    break;
                case IntegrityTransition it:
                    list.Add(E(AuditCategory.Integrity, ReasonCode.IntegrityTransition, $"{it.From}->{it.To}", FormattableString.Invariant($"fault={it.Reason} seq={it.Sequence} {it.Detail}")));
                    break;
                case KillSwitchChanged k:
                    list.Add(E(AuditCategory.KillSwitch, ReasonCode.KillSwitchCommand, $"{k.Command}:{k.Effect}", FormattableString.Invariant($"scope={k.Scope} mode={k.Mode} actor={k.Actor} reason={k.Reason} cancelledPending={k.CancelledPending} cancelledWaiting={k.CancelledWaiting} liquidationOrders={k.LiquidationOrders}")));
                    break;
                case EventQuarantined q:
                    list.Add(E(AuditCategory.Quarantine, ReasonCode.EventQuarantined, q.Reason, FormattableString.Invariant($"{q.Kind} id={q.SourceId} token={q.Token} {q.Detail}")));
                    break;
                case EvaluationSummary s:
                    list.Add(E(AuditCategory.Summary, Map(s.Reason), $"{s.Scope}Summary", FormattableString.Invariant($"{s.Reason} x{s.Count} first={s.FirstAt:O} last={s.LastAt:O}"), s.OpportunityId, s.Scope == "Opportunity" ? null : s.Subject));
                    break;
                case SessionEndReport e:
                    list.Add(E(AuditCategory.Session, ReasonCode.SessionEnded, "SessionEndReport", FormattableString.Invariant($"fills={e.Fills} belowBandFills={e.BelowBandFills} lateFills={e.FillsAfterFinalClose} unresolved={e.UnresolvedPositions} netRealised={e.NetRealisedAllPositions}")));
                    break;
                default:
                    list.Add(E(AuditCategory.Session, ReasonCode.Other, o.GetType().Name, o.ToString() ?? string.Empty));
                    break;
            }
        }

        return list;
    }

    private static AuditCategory CategoryOf(string stage) => stage switch
    {
        "ContractSelection" => AuditCategory.Selection,
        "EntryEvaluation" => AuditCategory.Evaluation,
        "Sizing" => AuditCategory.Sizing,
        "EntryIntent" => AuditCategory.EntryIntent,
        "EntryOrder" => AuditCategory.EntryOrder,
        "ExitOrder" => AuditCategory.Exit,
        "Position" => AuditCategory.Position,
        _ => AuditCategory.Opportunity,
    };

    private static ReasonCode MapExit(ExitReason reason) => reason switch
    {
        ExitReason.Tp1 => ReasonCode.Tp1,
        ExitReason.TradeSl => ReasonCode.TradeSl,
        ExitReason.FinalTarget => ReasonCode.FinalTarget,
        ExitReason.StrategyExit => ReasonCode.StrategyExit,
        ExitReason.ManualKill => ReasonCode.ManualKill,
        _ => ReasonCode.SessionLiquidation,
    };

    /// <summary>Maps the engine's outcome / reason strings to stable codes (prefixes such as Blocked:, Working:, NotEvaluable: are stripped first).</summary>
    public static ReasonCode Map(string text)
    {
        foreach (var prefix in new[] { "Blocked:", "Working:", "Retry:", "Gate:", "Integrity:", "StrategyInvalidated:" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (prefix == "StrategyInvalidated:")
                {
                    return ReasonCode.StrategyInvalidated;
                }

                text = text[prefix.Length..];
                break;
            }
        }

        if (text.StartsWith("NotEvaluable:", StringComparison.Ordinal))
        {
            return ReasonCode.NotEvaluable;
        }

        if (text.StartsWith("NotSatisfied:", StringComparison.Ordinal))
        {
            return ReasonCode.ConditionNotSatisfied;
        }

        return text switch
        {
            "EpisodeValid" => ReasonCode.EpisodeValid,
            "ContractSelected" or "Selected" => ReasonCode.ContractSelected,
            "NoEligibleContract" => ReasonCode.NoEligibleContract,
            "IntentIssued" or "Issued" => ReasonCode.IntentIssued,
            "SessionNotEligible" => ReasonCode.SessionNotEligible,
            "StrategyNotValid" => ReasonCode.StrategyNotValid,
            "StrategyInvalidated" => ReasonCode.StrategyInvalidated,
            "EntryClose" => ReasonCode.EntryClose,
            "MaxAttempts" => ReasonCode.MaxAttempts,
            "CapacityBusy" => ReasonCode.CapacityBusy,
            "InsufficientBudget" => ReasonCode.InsufficientBudget,
            "InvalidQuantity" => ReasonCode.InvalidQuantity,
            "QuoteNotCausal" => ReasonCode.QuoteNotCausal,
            "QuoteStale" => ReasonCode.QuoteStale,
            "QuoteNotExecutable" => ReasonCode.QuoteNotExecutable,
            "NoQuote" => ReasonCode.NoQuote,
            "SpreadTooWide" => ReasonCode.SpreadTooWide,
            "NoLiquidity" => ReasonCode.NoLiquidity,
            "AwaitingNewerQuote" => ReasonCode.AwaitingNewerQuote,
            "AskBelowBand" => ReasonCode.AskBelowBand,
            "AskAboveBand" => ReasonCode.AskAboveBand,
            "AskAboveLimit" => ReasonCode.AskAboveLimit,
            "IntegrityDegraded" => ReasonCode.IntegrityDegraded,
            "IntegrityRecovering" => ReasonCode.IntegrityRecovering,
            "KillSwitch" => ReasonCode.KillSwitch,
            "Filled" or "FilledBelowBand" => ReasonCode.Filled,
            "PartiallyFilled" => ReasonCode.PartiallyFilled,
            "Expired" => ReasonCode.Expired,
            "Cancelled" or "CancelRequested" => ReasonCode.Cancelled,
            "Closed" => ReasonCode.PositionClosed,
            "SessionEnded" => ReasonCode.SessionEnded,
            _ when text.StartsWith("Rearmed", StringComparison.Ordinal) => ReasonCode.Rearmed,
            _ when text.StartsWith("Risk", StringComparison.Ordinal) => ReasonCode.RiskBlocked,
            _ => ReasonCode.Other,
        };
    }
}
