using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Execution;

/// <summary>
/// Why a position is being sold. The order of the members is the priority when several triggers qualify on the same event:
/// SessionLiquidation &gt; ManualKill &gt; TradeSl &gt; StrategyExit &gt; FinalTarget &gt; Tp1 (lowest value = highest priority).
/// </summary>
public enum ExitReason
{
    SessionLiquidation = 0,

    /// <summary>Operator kill with liquidation (contract section A.14.3). Same execution path as every other exit.</summary>
    ManualKill = 1,
    TradeSl = 2,
    StrategyExit = 3,
    FinalTarget = 4,
    Tp1 = 5,

    /// <summary>The position reached its maximum holding time (measured from the entry fill).</summary>
    MaxHold = 6,
}

public enum OrderStatus
{
    Working = 0,
    Filled = 1,
    Cancelled = 2,
    Expired = 3,
}

public enum PositionState
{
    Open = 0,
    Closed = 1,

    /// <summary>Session ended with quantity still open. The position is NOT closed in the books and has no manufactured exit.</summary>
    Unresolved = 2,
}

public enum Tp1State
{
    NotTriggered = 0,
    Triggered = 1,
    Done = 2,
    Skipped = 3,
}

/// <summary>An entry order resting in a variant's virtual book. Single fill event under the initial partial-entry policy.</summary>
public sealed class EntryOrder
{
    internal EntryOrder(EntryIntent intent, DateTimeOffset maturityAt, DateTimeOffset expiresAt)
    {
        Intent = intent;
        MaturityAt = maturityAt;
        ExpiresAt = expiresAt;
    }

    public EntryIntent Intent { get; }

    public string Id => Intent.IntentId;

    public DateTimeOffset MaturityAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public OrderStatus Status { get; internal set; } = OrderStatus.Working;

    internal string? LastNote { get; set; }

    internal Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)> NoteCounts { get; } = new(StringComparer.Ordinal);

    internal List<Scheduling.DeadlineHandle> Handles { get; } = new();
}

/// <summary>A sell order for part of a position. It rests until filled (or until the session ends); each attempt sells at the eligible bid.</summary>
public sealed class ExitOrder
{
    internal ExitOrder(string id, Position position, ExitReason reason, long quantity, decimal? triggerThreshold, DateTimeOffset createdAt)
    {
        Id = id;
        Position = position;
        Reason = reason;
        RequestedQuantity = quantity;
        TriggerThreshold = triggerThreshold;
        CreatedAt = createdAt;
    }

    public string Id { get; }

    public Position Position { get; }

    public ExitReason Reason { get; }

    public long RequestedQuantity { get; }

    public long FilledQuantity { get; internal set; }

    public long RemainingQuantity => RequestedQuantity - FilledQuantity;

    public decimal? TriggerThreshold { get; }

    public DateTimeOffset CreatedAt { get; }

    public int Attempts { get; internal set; }

    public OrderStatus Status { get; internal set; } = OrderStatus.Working;

    /// <summary>Fills, cumulative turnover and brokerage already charged on this order (drives per-order brokerage).</summary>
    public Costs.OrderChargeState Charged { get; internal set; }

    internal string? LastNote { get; set; }

    internal Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)> NoteCounts { get; } = new(StringComparer.Ordinal);

    internal Scheduling.DeadlineHandle? AttemptHandle { get; set; }
}

/// <summary>A long option position built only from actual simulated fills.</summary>
public sealed class Position
{
    internal Position(string id, VariantKey variant, string opportunityId, string entryDecisionId, OptionContract contract, int slotNo, DateTimeOffset openedAt)
    {
        Id = id;
        Variant = variant;
        OpportunityId = opportunityId;
        EntryDecisionId = entryDecisionId;
        Contract = contract;
        SlotNo = slotNo;
        OpenedAt = openedAt;
    }

    public string Id { get; }

    /// <summary>Owner of the position: (StrategyId, VariantId).</summary>
    public VariantKey Variant { get; }

    public string StrategyId => Variant.StrategyId;

    public string VariantId => Variant.VariantId;

    public string OpportunityId { get; }

    public string EntryDecisionId { get; }

    public OptionContract Contract { get; }

    public int SlotNo { get; }

    public DateTimeOffset OpenedAt { get; }

    public DateTimeOffset? ClosedAt { get; internal set; }

    public PositionState State { get; internal set; } = PositionState.Open;

    public long EntryQuantity { get; internal set; }

    /// <summary>Sum of entry fill price x quantity.</summary>
    public decimal EntryCostBasis { get; internal set; }

    /// <summary>Average entry FILL price (price only, costs excluded): the reference for every percentage trigger.</summary>
    public decimal AvgEntryPrice => EntryQuantity == 0 ? 0m : EntryCostBasis / EntryQuantity;

    public long OpenQuantity { get; internal set; }

    /// <summary>Quantity covered by live exit orders; prevents TP1, stop and liquidation from overselling.</summary>
    public long ReservedExitQuantity { get; internal set; }

    public long ExitedQuantity { get; internal set; }

    public decimal ExitProceeds { get; internal set; }

    public decimal EntryCosts { get; internal set; }

    public decimal ExitCosts { get; internal set; }

    public Tp1State Tp1 { get; internal set; } = Tp1State.NotTriggered;

    public long Tp1TargetQuantity { get; internal set; }

    public long Tp1FilledQuantity { get; internal set; }

    public int ExitSeq { get; internal set; }

    /// <summary>Instant at which the max-hold exit fires (entry fill time + MaxHold); null when the variant has no max hold.</summary>
    public DateTimeOffset? MaxHoldDeadline { get; internal set; }

    internal Scheduling.DeadlineHandle? MaxHoldHandle { get; set; }

    /// <summary>True while at least one live exit order exists for an open position (e.g. a mandatory liquidation waiting for an executable bid).</summary>
    public bool ExitPending => State == PositionState.Open && ExitOrders.Any(o => o.Status == OrderStatus.Working);

    // ---- bid-based excursion tracking (contract section A.14.5). Units: premium rupees per unit; the series is the contract's executable bid
    // observed on quote events of the held contract (plus the entry-fill quote), from the entry fill until the position closes or the session ends.
    // It is a property of the contract price path, so it keeps running after TP1 and is not split by partial exits.
    public int BidObservations { get; internal set; }

    public decimal? MaxBid { get; internal set; }

    public decimal? MinBid { get; internal set; }

    public DateTimeOffset? FirstBidObservationAt { get; internal set; }

    public DateTimeOffset? LastBidObservationAt { get; internal set; }

    /// <summary>Number of times two consecutive eligible-bid observations were further apart than the quote-age limit (the bid path was unobserved in between).</summary>
    public int BidObservationGaps { get; internal set; }

    public DateTimeOffset? FirstExitFillAt { get; internal set; }

    public decimal? MaxBidAtFirstExit { get; internal set; }

    public decimal? MinBidAtFirstExit { get; internal set; }

    public List<ExitOrder> ExitOrders { get; } = new();

    /// <summary>Price P&amp;L on the quantity already sold: proceeds - average entry price x sold quantity.</summary>
    public decimal GrossRealised => ExitProceeds - (AvgEntryPrice * ExitedQuantity);

    public decimal TotalCosts => EntryCosts + ExitCosts;

    /// <summary>Realised net P&amp;L = realised gross - ALL entry costs - ALL exit costs incurred so far (entry costs are charged in full as soon as the position exists).</summary>
    public decimal NetRealised => GrossRealised - TotalCosts;
}

public enum ValuationStatus
{
    Marked = 0,
    Missing = 1,
    Stale = 2,
    NotExecutable = 3,
}

/// <summary>Liquidation value of an open position from the eligible bid at a given time. Stale or missing valuations are flagged, never filled with another price.</summary>
public sealed record PositionValuation(
    string PositionId,
    long OpenQuantity,
    ValuationStatus Status,
    decimal? Bid,
    TimeSpan? QuoteAge,
    decimal? GrossValue,
    decimal? EstimatedExitCosts,
    decimal? LiquidationValue);

// ------------------------------------------------------------------ outputs

public sealed record FillRecord(
    DateTimeOffset At,
    VariantKey Variant,
    string FillId,
    string OrderId,
    string PositionId,
    TradeSide Side,
    string Token,
    long Quantity,
    decimal Price,
    decimal Notional,
    CostBreakdown Costs,
    bool BelowBand,
    bool AfterFinalClose,
    ExitReason? Reason,
    decimal? TriggerThreshold,
    long? QuoteSourceId,
    string Book) : EngineOutput(At, Variant);

public sealed record PositionOpened(DateTimeOffset At, VariantKey Variant, string PositionId, string OpportunityId, string Token, long Quantity, decimal AvgEntryPrice) : EngineOutput(At, Variant);

public sealed record PositionClosed(
    DateTimeOffset At,
    VariantKey Variant,
    string PositionId,
    string Token,
    long EntryQuantity,
    decimal AvgEntryPrice,
    decimal GrossPnl,
    decimal TotalCosts,
    decimal NetPnl) : EngineOutput(At, Variant);

public sealed record ExitOrderCreated(
    DateTimeOffset At,
    VariantKey Variant,
    string OrderId,
    string PositionId,
    ExitReason Reason,
    long Quantity,
    DateTimeOffset FirstAttemptAt,
    decimal? TriggerThreshold) : EngineOutput(At, Variant);

/// <summary>
/// A mandatory exit (session liquidation or manual kill with liquidation) could not fill yet: no valid executable bid, no liquidity or a stale quote. The
/// position stays open, the exit order keeps retrying with executable quotes only (nothing is ever fabricated) and this record makes the unresolved condition
/// visible. Emitted when an order's failure reason first appears or changes.
/// </summary>
public sealed record MandatoryExitPending(
    DateTimeOffset At,
    VariantKey Variant,
    string PositionId,
    string OrderId,
    ExitReason Reason,
    long RemainingQuantity,
    int Attempts,
    string Failure) : EngineOutput(At, Variant);

public sealed record Tp1Resolved(DateTimeOffset At, VariantKey Variant, string PositionId, Tp1State State, long TargetQuantity, long OpenLots) : EngineOutput(At, Variant);

/// <summary>Reported at the final-close time (default 15:10:00) for every position still open. Orders keep trying until session end; later fills are flagged.</summary>
public sealed record UnresolvedAtClose(
    DateTimeOffset At,
    VariantKey Variant,
    string PositionId,
    string Token,
    long OpenQuantity,
    long ReservedExitQuantity,
    int LiveExitOrders,
    decimal? LastEligibleBid,
    TimeSpan? LastBidAge) : EngineOutput(At, Variant);

/// <summary>Reported at session end for every position still open. The position stays open in the books (state Unresolved); no fill is manufactured.</summary>
public sealed record UnresolvedAtSessionEnd(
    DateTimeOffset At,
    VariantKey Variant,
    string PositionId,
    string Token,
    long OpenQuantity,
    decimal? LastEligibleBid,
    TimeSpan? LastBidAge) : EngineOutput(At, Variant);

/// <summary>Record-only in ResearchV1: nothing is blocked, liquidated or capped when this is emitted.</summary>
public sealed record DailySlBreached(DateTimeOffset At, VariantKey Variant, decimal DailyPnl, decimal DailySl, bool ConservativeMark) : EngineOutput(At, Variant);

public sealed record SessionEndReport(
    DateTimeOffset At,
    VariantKey Variant,
    decimal NetRealisedAllPositions,
    long Fills,
    long BelowBandFills,
    long FillsAfterFinalClose,
    int UnresolvedPositions,
    decimal? WorstDailyPnl,
    bool DailySlBreachedRecorded,
    string Book) : EngineOutput(At, Variant);
