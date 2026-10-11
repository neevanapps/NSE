using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Variants;

/// <summary>
/// One entry opportunity of one variant within one base-strategy validity episode. The contract is selected once and never changes.
/// State changes happen only inside the engine; consumers read it.
/// </summary>
public sealed class Opportunity
{
    internal Opportunity(string id, VariantKey variant, string episodeId, long episodeStartBarSeq, OptionSide side, DateTimeOffset createdAt, AttributedDecision? entryDecision = null)
    {
        Id = id;
        Variant = variant;
        EntryDecision = entryDecision;
        EpisodeId = episodeId;
        EpisodeStartBarSeq = episodeStartBarSeq;
        Side = side;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    /// <summary>The owning variant: (StrategyId, VariantId).</summary>
    public VariantKey Variant { get; }

    public string StrategyId => Variant.StrategyId;

    public string VariantId => Variant.VariantId;

    /// <summary>The family decision (with its source bar identity) that opened the validity episode this opportunity belongs to.</summary>
    public AttributedDecision? EntryDecision { get; }

    /// <summary>The family decision (and finalized source bar) on which the entry intent of this opportunity was issued; null until an intent exists.</summary>
    public AttributedDecision? IntentDecision { get; internal set; }

    public string EpisodeId { get; }

    public long EpisodeStartBarSeq { get; }

    public OptionSide Side { get; }

    public DateTimeOffset CreatedAt { get; }

    public OpportunityState State { get; internal set; } = OpportunityState.Created;

    /// <summary>Fixed at the first successful selection; immutable afterwards.</summary>
    public OptionContract? Contract { get; internal set; }

    public ContractSelectionResult? Selection { get; internal set; }

    public int Attempts { get; internal set; }

    public string? ActiveIntentId { get; internal set; }

    public bool CancelRequested { get; internal set; }

    public CancelReason? CancelReason { get; internal set; }

    public string? TerminalReason { get; internal set; }

    public int SelectionAttempts { get; internal set; }

    /// <summary>Premium (limit x quantity) of the entry intent currently outstanding for this opportunity; 0 when none. Counts against the shared capital.</summary>
    public decimal IntentPremium { get; internal set; }

    /// <summary>Canonical reason set of the last EntryRejected emitted for this opportunity (a record is emitted only when the set changes).</summary>
    internal string? LastRejectionKey { get; set; }

    public int Evaluations { get; internal set; }

    internal string? LastEvaluationKey { get; set; }

    /// <summary>Per evaluation result: how often it occurred and when (the audit trail logs only the first occurrence; the count is flushed as an EvaluationSummary).</summary>
    internal Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)> NoteCounts { get; } = new(StringComparer.Ordinal);

    public bool IsTerminal => State is OpportunityState.Consumed or OpportunityState.Cancelled or OpportunityState.Expired or OpportunityState.Rejected;
}

public enum SlotState
{
    Free = 0,
    Reserved = 1,
    Occupied = 2,
}

/// <summary>One capacity slot of a variant. V1 has exactly one per variant; the numbering exists so that multi-position support needs no key change.</summary>
public sealed class CapacitySlot
{
    internal CapacitySlot(int slotNo) => SlotNo = slotNo;

    public int SlotNo { get; }

    public SlotState State { get; internal set; } = SlotState.Free;

    public string? OwnerOpportunityId { get; internal set; }
}

/// <summary>Runtime state of one variant. Fully isolated: no field is shared with another variant.</summary>
public sealed class VariantState
{
    internal VariantState(VariantSpec spec, int ordinal, IStrategyFamily family, string variantHash, int capacity)
    {
        Spec = spec;
        Key = spec.GetKey();
        Ordinal = ordinal;
        Family = family;
        VariantHash = variantHash;
        var slots = new List<CapacitySlot>();
        for (var i = 0; i < capacity; i++)
        {
            slots.Add(new CapacitySlot(i));
        }

        Slots = slots;
    }

    public VariantSpec Spec { get; }

    /// <summary>The canonical identity (StrategyId, VariantId): the key of this variant's capacity slot, opportunities, orders, positions and accounting.</summary>
    public VariantKey Key { get; }

    public string StrategyId => Key.StrategyId;

    public string VariantId => Key.VariantId;

    /// <summary>Position in the list sorted by (StrategyId, VariantId); used for deterministic processing and deadline ordering.</summary>
    public int Ordinal { get; }

    internal IStrategyFamily Family { get; }

    /// <summary>The latest decision the family made for this variant (attributed to its source bar). Part of the state digest.</summary>
    public AttributedDecision? LastDecision { get; internal set; }

    /// <summary>The decision that started the current validity episode.</summary>
    public AttributedDecision? EpisodeDecision { get; internal set; }

    public string VariantHash { get; }

    public IReadOnlyList<CapacitySlot> Slots { get; }

    public bool Valid { get; internal set; }

    public Instruments.OptionSide EpisodeSide { get; internal set; }

    public string? EpisodeId { get; internal set; }

    public long EpisodeStartBarSeq { get; internal set; }

    public bool EpisodeSpent { get; internal set; }

    public bool EpisodeBlockNoted { get; internal set; }

    /// <summary>Reason set of the last rejection recorded for this episode while no opportunity existed.</summary>
    internal string? EpisodeRejectionKey { get; set; }

    internal int RejectionOrdinal { get; set; }

    public Opportunity? Active { get; internal set; }

    public List<Opportunity> History { get; } = new();
}
