using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>
/// The nine outcomes an opportunity (one strategy, one verified completed bar) can have. Numbered as in the owner's directive; a category is a classification of what the SIMULATION did, never an instruction to it.
/// </summary>
public enum OpportunityCategory
{
    NoDirectionalSignal = 1,
    ValidDirectionalSignal = 2,                  // a signal passed every protection and an order was placed (its fill / non-fill is a later Outcome row)
    SameDirectionWhileHolding = 3,
    OppositeSignalExit = 4,
    BlockedByPosition = 5,                       // an order is already pending / the position limit is reached without a same-direction holding
    BlockedByRiskOrWindow = 6,                   // entry window, warm-up, blocked frame, daily risk, capital
    OrderRejectedQuoteOrContract = 7,            // the order was attempted and refused for a quote or contract reason
    OrderAcceptedNotFilled = 8,                  // an order was placed and its lifetime elapsed without a qualifying fill quote
    OrderFilled = 9,
}

/// <summary>Decision = the verdict on the bar's signal; Outcome = how a placed order finally resolved (written on the bar where it resolved, keyed by the decision bar).</summary>
public enum OpportunityStage { Decision, Outcome }

/// <summary>
/// One append-only, deterministic opportunity row. The id is a pure function of (strategy, session, decision bar, stage), so replaying the journal after a restart regenerates identical rows (persisted idempotently).
/// It is OBSERVATION ONLY: it is derived after a strategy has finished a bar, from the same events the ledger records, and nothing in it feeds back into a decision. No order is ever fabricated for a suppressed signal.
/// </summary>
public sealed record OpportunityRecord(
    string OpportunityId, string StrategyId, int Version, string ConfigHash, long SessionId, DateOnly TradeDate, int BarSeq, OpportunityStage Stage, int ResolvedBarSeq, DateTimeOffset DecisionAt,
    string Direction, OpportunityCategory Category, string Reason, string Detail, bool InPosition, bool OrderEligible, bool OrderAttempted, string OrderResult,
    string? IntentId, string? Token, string? EventRef, string FrameRef, bool EntriesAllowed, string Coverage)
{
    public static string IdOf(string strategyId, long sessionId, int barSeq, OpportunityStage stage) => $"{strategyId}:{sessionId}:{barSeq}:{stage}";
}

/// <summary>Receives opportunity rows in order. A failure is contained and counted by the engine; it never reaches a strategy.</summary>
public interface IOpportunitySink
{
    void Append(OpportunityRecord record);
}

static class OpportunityClassifier
{
    public static string Direction(Tri buyCall, Tri buyPut) =>
        buyCall == Tri.True && buyPut == Tri.True ? "Unknown" : buyCall == Tri.True ? "CE" : buyPut == Tri.True ? "PE" : buyCall == Tri.Unknown || buyPut == Tri.Unknown ? "Unknown" : "Neutral";

    /// <summary>Category of an entry rejection that happened AT THE DECISION (no order existed).</summary>
    public static (OpportunityCategory Category, string Why) OfDecisionRejection(RejectionReason r, bool holdingSameDirection) => r switch
    {
        RejectionReason.PositionLimit => holdingSameDirection ? (OpportunityCategory.SameDirectionWhileHolding, "same-direction signal while the position is held") : (OpportunityCategory.BlockedByPosition, "a position or an entry order already exists"),
        RejectionReason.EntriesBlocked or RejectionReason.OutsideEntryWindow or RejectionReason.WarmupIncomplete or RejectionReason.RiskBreach or RejectionReason.InsufficientCapital => (OpportunityCategory.BlockedByRiskOrWindow, r.ToString()),
        RejectionReason.ConflictingSignals => (OpportunityCategory.NoDirectionalSignal, "buyCall and buyPut were both true"),
        _ => (OpportunityCategory.OrderRejectedQuoteOrContract, r.ToString()),
    };

    /// <summary>Category of a PLACED order's rejection (resolved on a later bar).</summary>
    public static OpportunityCategory OfOrderRejection(RejectionReason r) => r switch
    {
        RejectionReason.ExpiredOpportunity or RejectionReason.MissingQuote or RejectionReason.StaleQuote => OpportunityCategory.OrderAcceptedNotFilled,
        RejectionReason.SpreadTooWide or RejectionReason.InvalidInstrument or RejectionReason.NoQualifyingContract or RejectionReason.LotSizeViolation => OpportunityCategory.OrderRejectedQuoteOrContract,
        _ => OpportunityCategory.BlockedByRiskOrWindow,
    };
}
