namespace NiftySignal.AdaptiveObserver.Commentary;

// Pure domain for the Running Market Commentary (08-Oct plan sections 44-69). No database, no wall clock, no LLM.
// Directions assigned here are deterministic interpretation HYPOTHESES (plan section 0.5), not validated edge, and nothing here may be
// wired into trading, scoring, risk or execution.

public enum CommentaryEventType
{
    BuyerExpansion,
    SellerExpansion,
    ShortCovering,
    LongLiquidation,
    BuyerAbsorption,
    SellerAbsorption,
    FlowConflict,
    FamilyConflict,
    SellerRejectionConfirmed,
    BuyerRejectionConfirmed,
}

/// <summary>The direction an event implies for NIFTY under the plan's hypotheses. Not an order instruction.</summary>
public enum EventBias { Neutral, Long, Short }

public enum MarketRegime { Neutral, Bullish, Bearish, Transition, Conflict }

/// <summary>There is deliberately no "Active" value: an unchanged continuation bar persists nothing.</summary>
public enum CommentaryLifecycle { New, Strengthening, Weakening, Confirmed, Resolved, Flipped }

/// <summary>How many independent evidence families agree with the deterministic interpretation. NOT a probability or validated confidence.</summary>
public enum EvidenceAgreement { Low, Medium, High }

public enum CommentarySeverity { Info, Medium, High }

public enum EvidenceFamily { FuturesCore, Book, InterMarketBasis, Options, Residual }

public enum FamilyDirection { Unavailable, Neutral, Long, Short }

/// <summary>One structured fact behind an event. The renderer may only mention what is present in these lists.</summary>
public sealed record EvidenceItem(EvidenceFamily Family, string Metric, string Value);

/// <summary>
/// Immutable per-completed-bar input assembled from the finalized Futures, Options and Residual observations. All values are as available at the
/// bar boundary; null means unavailable (never zero). Basis is null when either boundary is missing or the spot is older than the approved 5-second freshness rule (plan section 9.6).
/// </summary>
public sealed record CommentaryFrame
{
    public required long SessionId { get; init; }
    public required DateOnly TradeDate { get; init; }
    public required int BarSeq { get; init; }
    public required DateTimeOffset BarEndAvailableAtUtc { get; init; }

    /// <summary>Shared ten-valid-contiguous-completed-bar gate (<c>AdaptiveReadinessPolicy</c>). Events are produced only when true.</summary>
    public bool ReadinessMet { get; init; }

    // Futures
    public double? BarPriceDisplacement { get; init; }
    public double? RollingPriceDisplacement { get; init; }
    public long? StrictDelta { get; init; }
    public long? EnrichedDelta { get; init; }
    public long? RollingStrictDelta { get; init; }
    public long? RollingEnrichedDelta { get; init; }
    public double? RollingStrictAbsDeltaChange { get; init; }
    public long? RollOiDelta { get; init; }
    public double? Urgency { get; init; }
    public double? MicroDev { get; init; }
    public long? Ofi { get; init; }
    public double? DeltaBasis { get; init; }
    public double? RollingEfficiency { get; init; }
    public string? Evolution { get; init; }
    public string? State { get; init; }

    // Options (center contract / ATM±2 band)
    public double? CenterStrike { get; init; }
    public string? CePosition { get; init; }
    public string? PePosition { get; init; }
    public long? CeStrictDelta { get; init; }
    public long? PeStrictDelta { get; init; }
    public double? CeDeltaIv { get; init; }
    public double? PeDeltaIv { get; init; }
    public double? IvSkew { get; init; }
    public double? VolPcr { get; init; }
    public double? RollVolPcr { get; init; }

    // Residual (selected primary variant; adjacent-bar deltas only)
    public bool ResidualAvailable { get; init; }
    public double? DirectionalResidualPct { get; init; }
    public double? AdjacentDirectionalResidualDelta { get; init; }
    public double? CeResidualDeltaPct { get; init; }
    public double? PeResidualDeltaPct { get; init; }
    public double? StraddleResidualPct { get; init; }
}

/// <summary>The event currently in force, with what is needed to detect explicit lifecycle changes on the next bar.</summary>
public sealed record ActiveCommentaryEvent(
    CommentaryEventType Type,
    EventBias Bias,
    int StartedBarSeq,
    EvidenceAgreement Agreement,
    IReadOnlyList<EvidenceFamily> SupportingFamilies,
    CommentaryLifecycle Phase);

/// <summary>Deterministic per-session state carried from bar to bar. Rebuildable by replaying bars in BarSeq order.</summary>
public sealed record CommentaryState(
    MarketRegime Regime,
    ActiveCommentaryEvent? Active,
    EventBias CurrentBias,
    int LastEvaluatedBarSeq)
{
    public static CommentaryState Initial { get; } = new(MarketRegime.Neutral, null, EventBias.Neutral, 0);
}

/// <summary>A persisted lifecycle event. NoMaterialEvent and unchanged continuation bars never produce one.</summary>
public sealed record CommentaryEvent(
    long SessionId,
    DateOnly TradeDate,
    int BarSeq,
    DateTimeOffset OccurredAtUtc,
    CommentaryEventType EventType,
    EventBias EventBias,
    MarketRegime MarketRegime,
    CommentaryLifecycle Lifecycle,
    EvidenceAgreement EvidenceAgreement,
    CommentarySeverity Severity,
    EventBias PreviousBias,
    bool BiasChanged,
    IReadOnlyList<EvidenceItem> PrimaryEvidence,
    IReadOnlyList<EvidenceItem> Confirmations,
    IReadOnlyList<EvidenceItem> Contradictions,
    IReadOnlyList<string> DataQuality,
    bool ShouldNotifyTelegram,
    string? NotificationReason,
    string CommentaryVersion,
    string RenderedCommentary)
{
    /// <summary>Deterministic idempotency identity: SessionId + BarSeq + EventType + Lifecycle + Bias.</summary>
    public string Identity => $"{CommentaryVersion}:{SessionId}:{BarSeq}:{EventType}:{Lifecycle}:{EventBias}";
}

/// <summary>Result of evaluating one bar. <see cref="Event"/> is null for NoMaterialEvent and for unchanged continuation; the state still advances.</summary>
public sealed record CommentaryStep(CommentaryState State, CommentaryEvent? Event);
