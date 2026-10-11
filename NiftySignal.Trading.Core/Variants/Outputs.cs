using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Variants;

public enum OpportunityState
{
    Created = 0,

    /// <summary>Base strategy valid, contract selection not yet successful (NoEligibleContract); retried on later completed bars only.</summary>
    AwaitingContract = 1,

    /// <summary>Contract fixed; waiting for the entry condition and rechecks to pass on an eligible quote update.</summary>
    Waiting = 2,

    /// <summary>An entry intent is outstanding (reservation held). No second intent may exist until it is terminal.</summary>
    OrderPending = 3,

    /// <summary>A fill (full or partial) resolved the intent; the opportunity is spent. The slot stays occupied until the position is closed.</summary>
    Consumed = 4,

    Cancelled = 5,
    Expired = 6,
    Rejected = 7,
}

public enum EntryOutcomeKind
{
    Filled = 0,

    /// <summary>Any fill quantity below the requested one; per contract it consumes the opportunity.</summary>
    PartiallyFilled = 1,
    Cancelled = 2,
    Expired = 3,
    Rejected = 4,
}

/// <summary>Terminal result of an entry intent, reported by the execution model (CP3) or a test harness, from inside the engine loop.</summary>
public sealed record EntryOutcome(VariantKey Variant, string OpportunityId, string IntentId, EntryOutcomeKind Kind);

public enum CancelReason
{
    StrategyInvalidated = 0,
    EntryClose = 1,

    /// <summary>A global feed fault: the attempt is cancelled and the opportunity is re-armed once the engine is Healthy again (if still valid).</summary>
    IntegrityDegraded = 2,

    /// <summary>Manual kill: the attempt is cancelled and the opportunity ends (it is not resurrected by a reset).</summary>
    KillSwitch = 3,
}

/// <summary>Base of everything the variant engine emits. Outputs are plain data; the engine performs no I/O.</summary>
public abstract record EngineOutput(DateTimeOffset At, VariantKey Variant)
{
    /// <summary>The variant id inside its strategy family (convenience for <see cref="Variant"/>).</summary>
    public string VariantId => Variant.VariantId;

    /// <summary>The logical strategy family that owns this output (every output carries both parts of the identity).</summary>
    public string StrategyId => Variant.StrategyId;
}

/// <summary>
/// Marketable-limit entry intent at the observed qualifying ask (initial policy). Carries the premium limits so that CP3 can enforce
/// them: a buy limit caps the price but cannot guarantee a minimum fill price, so how fills below
/// <see cref="MinimumEntryPremium"/> are handled is a CP3 decision, and a fill inside the band must never be fabricated.
/// </summary>
public sealed record EntryIntent(
    DateTimeOffset At,
    VariantKey Variant,
    string IntentId,
    string DecisionId,
    string OpportunityId,
    int Attempt,
    int SlotNo,
    OptionContract Contract,
    int RequestedQuantity,
    int Lots,
    int LotSize,
    decimal LimitPrice,
    decimal QualifyingAsk,
    decimal MinimumEntryPremium,
    decimal MaximumEntryPremium,
    DateTimeOffset QuoteAvailableAt,
    long QuoteSourceId,
    string ConditionKind,
    decimal? ConditionAverage,
    int? ConditionN,
    CandlePriceSource? ConditionPriceSource) : EngineOutput(At, Variant);

/// <summary>Request to cancel the unfilled quantity of an outstanding entry intent. The reservation is retained until the outcome is reported.</summary>
public sealed record CancelRequest(DateTimeOffset At, VariantKey Variant, string IntentId, string OpportunityId, CancelReason Reason) : EngineOutput(At, Variant);

public sealed record OpportunityTransition(
    DateTimeOffset At,
    VariantKey Variant,
    string OpportunityId,
    OpportunityState From,
    OpportunityState To,
    string Reason) : EngineOutput(At, Variant);

/// <summary>
/// An Enter decision that reached the engine did not (yet) become an entry. Reasons are typed (<see cref="Risk.RejectionReason"/>) and listed in stable
/// order. One record per distinct reason set per opportunity (or per episode when no opportunity could be created); the stage says where it was blocked.
/// SourceSessionId and BarSeq identify the finalized input bar of the decision the block belongs to.
/// </summary>
public sealed record EntryRejected(
    DateTimeOffset At,
    VariantKey Variant,
    string? OpportunityId,
    string DecisionId,
    string SourceSessionId,
    long BarSeq,
    string Stage,
    string Reasons) : EngineOutput(At, Variant);

/// <summary>Why-record for accepted and rejected steps. Detail strings are built with invariant formatting only.</summary>
public sealed record AuditRecord(DateTimeOffset At, VariantKey Variant, string? OpportunityId, string Stage, string Outcome, string Detail) : EngineOutput(At, Variant);
