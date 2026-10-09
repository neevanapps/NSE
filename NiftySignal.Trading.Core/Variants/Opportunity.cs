using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Selection;

namespace NiftySignal.Trading.Core.Variants;

/// <summary>
/// One entry opportunity of one variant within one base-strategy validity episode. The contract is selected once and never changes.
/// State changes happen only inside the engine; consumers read it.
/// </summary>
public sealed class Opportunity
{
    internal Opportunity(string id, string variantId, string episodeId, long episodeStartBarSeq, OptionSide side, DateTimeOffset createdAt)
    {
        Id = id;
        VariantId = variantId;
        EpisodeId = episodeId;
        EpisodeStartBarSeq = episodeStartBarSeq;
        Side = side;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public string VariantId { get; }

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

    public int Evaluations { get; internal set; }

    internal string? LastEvaluationKey { get; set; }

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
    internal VariantState(VariantSpec spec, int ordinal, IBaseStrategy strategy, string variantHash, int capacity)
    {
        Spec = spec;
        Ordinal = ordinal;
        Strategy = strategy;
        VariantHash = variantHash;
        var slots = new List<CapacitySlot>();
        for (var i = 0; i < capacity; i++)
        {
            slots.Add(new CapacitySlot(i));
        }

        Slots = slots;
    }

    public VariantSpec Spec { get; }

    public string VariantId => Spec.VariantId;

    /// <summary>Position in the sorted VariantId list; used for deterministic processing and deadline ordering.</summary>
    public int Ordinal { get; }

    internal IBaseStrategy Strategy { get; }

    public string VariantHash { get; }

    public IReadOnlyList<CapacitySlot> Slots { get; }

    public bool Valid { get; internal set; }

    public Instruments.OptionSide EpisodeSide { get; internal set; }

    public string? EpisodeId { get; internal set; }

    public long EpisodeStartBarSeq { get; internal set; }

    public bool EpisodeSpent { get; internal set; }

    public bool EpisodeBlockNoted { get; internal set; }

    public Opportunity? Active { get; internal set; }

    public List<Opportunity> History { get; } = new();
}
