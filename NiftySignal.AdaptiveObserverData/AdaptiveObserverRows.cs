using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;

namespace NiftySignal.AdaptiveObserverData;

public sealed class AdaptiveSessionStateRow
{
    public long Id { get; set; }
    public required DateOnly TradeDate { get; set; }
    public required string ModelVersion { get; set; }
    public required string SourceBranch { get; set; }
    public required string SourceCommitSha { get; set; }
    public required DateTimeOffset BuildUtc { get; set; }
    public bool IsHistoricalSeed { get; set; }
    public required string FutureToken { get; set; }
    public required string FutureSymbol { get; set; }
    public required DateOnly FutureExpiry { get; set; }
    public int LotSize { get; set; }
    public double RiskFreeRate { get; set; }
    public string OptionUniverseJson { get; set; } = "[]";
    public required DateTimeOffset OpeningWindowStartUtc { get; set; }
    public required DateTimeOffset OpeningWindowEndUtc { get; set; }
    public long OpeningVolume { get; set; }
    // Null on legacy sessions. OpeningVolume always remains the actually observed quantity.
    public long? EstimatorInputOpeningVolume { get; set; }
    public bool UsesMedianOpeningFallback { get; set; }
    public int? OpeningCoverageMinutes { get; set; }
    public int? MedianOpeningSampleCount { get; set; }
    public DateTimeOffset? ObservationStartUtc { get; set; }
    public required string EstimatorName { get; set; }
    public double EstimatorIntercept { get; set; }
    public double EstimatorSlope { get; set; }
    public int TargetBarsPerDay { get; set; }
    public int RoundingLots { get; set; }
    public double EstimatedFullDayVolume { get; set; }
    public long BaseBarVolume { get; set; }
    public int RollingWindowBars { get; set; }
    public long RollingWindowVolume { get; set; }
    public double StrongQuantile { get; set; }
    public double? StrongThreshold { get; set; }
    public int StrongThresholdPriorStateCount { get; set; }
    public DateOnly? WeeklyOptionExpiry { get; set; }
    public double? Future0930 { get; set; }
    public double? SyntheticWeeklyUnderlying0930 { get; set; }
    public double? ResidualCenterStrike { get; set; }
    public DateTimeOffset? ResidualAnchorCompletedAtUtc { get; set; }
    public required DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AdaptiveFutureBarRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    public required DateTimeOffset StartAvailableAtUtc { get; set; }
    public required DateTimeOffset EndAvailableAtUtc { get; set; }
    public long FirstSourceTickId { get; set; }
    public long LastSourceTickId { get; set; }
    public double DurationSeconds { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public double BarPriceDisplacement { get; set; }
    public double Vwap { get; set; }
    public long Volume { get; set; }
    public int TradeUpdates { get; set; }
    public long StrictBuyVolume { get; set; }
    public long StrictSellVolume { get; set; }
    public long StrictUnknownVolume { get; set; }
    public long StrictDelta { get; set; }
    public double StrictDeltaRatioTotal { get; set; }
    public double StrictCoverage { get; set; }
    public long EnrichedBuyVolume { get; set; }
    public long EnrichedSellVolume { get; set; }
    public long EnrichedUnknownVolume { get; set; }
    public long EnrichedDelta { get; set; }
    public double EnrichedDeltaRatio { get; set; }
    public long? OiOpen { get; set; }
    public long? OiClose { get; set; }
    public long? OiChange { get; set; }
    public double? Bid { get; set; }
    public double? Ask { get; set; }
    public long? BidQty { get; set; }
    public long? AskQty { get; set; }
    public double? Spread { get; set; }
    public double? BookImbalance { get; set; }
    public double? Microprice { get; set; }
}

public sealed class AdaptiveRollingStateRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int StartBarSeq { get; set; }
    public int EndBarSeq { get; set; }
    public required DateTimeOffset StartAvailableAtUtc { get; set; }
    public required DateTimeOffset EndAvailableAtUtc { get; set; }
    public int WindowBars { get; set; }
    public long WindowVolume { get; set; }
    public double ElapsedSeconds { get; set; }
    public double StartPrice { get; set; }
    public double EndPrice { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double PriceDisplacement { get; set; }
    public double ReturnBps { get; set; }
    public double PathLength { get; set; }
    public double Efficiency { get; set; }
    public double SignedEfficiency { get; set; }
    public long StrictBuyVolume { get; set; }
    public long StrictSellVolume { get; set; }
    public long StrictUnknownVolume { get; set; }
    public long StrictDelta { get; set; }
    public double StrictQuoteCoverage { get; set; }
    public double StrictDeltaRatioTotal { get; set; }
    public double? StrictDeltaRatioClassified { get; set; }
    public long EnrichedBuyVolume { get; set; }
    public long EnrichedSellVolume { get; set; }
    public long EnrichedUnknownVolume { get; set; }
    public long EnrichedDelta { get; set; }
    public double EnrichedDeltaRatio { get; set; }
    public double FallbackShare { get; set; }
    public long? OiStart { get; set; }
    public long? OiEnd { get; set; }
    public long? OiChange { get; set; }
    public double? OiChangePct { get; set; }
    public double? VolumePerSecond { get; set; }
    public int TradeUpdates { get; set; }
    public double WindowRange { get; set; }
    public int PriceDirection { get; set; }
    public int StrictDeltaDirection { get; set; }
    public int EnrichedDeltaDirection { get; set; }
    public bool? PriceStrictDeltaAgree { get; set; }
    public bool? PriceEnrichedDeltaAgree { get; set; }
    public long? RollingStrictDeltaChange { get; set; }
    public long? RollingStrictAbsDeltaChange { get; set; }
    public long? RollingEnrichedDeltaChange { get; set; }
    public double? RollingOiChangePctChange { get; set; }
    public string? StrictDominanceEvolution { get; set; }
    public bool IsStrong { get; set; }
    public int WeakeningSequence { get; set; }
    public int? StrongBaseBarSeq { get; set; }
    public int? Weak1BarSeq { get; set; }
    public AdaptiveStateKind State { get; set; }
}

public sealed class AdaptiveOptionBandBarRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    public OptionType Side { get; set; }
    public double CenterStrike { get; set; }
    public required string BandStrikes { get; set; }
    public bool BandRolled { get; set; }
    public bool BandAvailable { get; set; }
    public string? UnavailableReason { get; set; }
    public required DateTimeOffset StartAvailableAtUtc { get; set; }
    public required DateTimeOffset EndAvailableAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public double? BandPremiumIndexOpen { get; set; }
    public double? BandPremiumIndexClose { get; set; }
    public double? BarBandPriceChange { get; set; }
    public double? BarBandReturnPct { get; set; }
    public double? RollingBandPriceChange { get; set; }
    public double? RollingReturnPct { get; set; }
    public double? RollingEfficiency { get; set; }

    public long ContractTotalQuantity { get; set; }
    public long ContractStrictBuy { get; set; }
    public long ContractStrictSell { get; set; }
    public long ContractStrictUnknown { get; set; }
    public long ContractStrictDelta { get; set; }
    public double ContractStrictDeltaRatioTotal { get; set; }
    public double ContractStrictCoverage { get; set; }
    public long ContractEnrichedBuy { get; set; }
    public long ContractEnrichedSell { get; set; }
    public long ContractEnrichedUnknown { get; set; }
    public long ContractEnrichedDelta { get; set; }
    public double ContractEnrichedDeltaRatio { get; set; }
    public long? ContractRollingStrictDelta { get; set; }
    public long? ContractRollingStrictAbsDeltaChange { get; set; }
    public double? ContractRollingStrictDeltaRatioTotal { get; set; }
    public long? ContractRollingEnrichedDelta { get; set; }
    public long? ContractRollingEnrichedDeltaChange { get; set; }
    public double? ContractRollingEnrichedDeltaRatio { get; set; }

    public double NotionalTotal { get; set; }
    public double NotionalStrictBuy { get; set; }
    public double NotionalStrictSell { get; set; }
    public double NotionalStrictUnknown { get; set; }
    public double NotionalStrictDelta { get; set; }
    public double NotionalStrictDeltaRatioTotal { get; set; }
    public double NotionalStrictCoverage { get; set; }
    public double NotionalEnrichedBuy { get; set; }
    public double NotionalEnrichedSell { get; set; }
    public double NotionalEnrichedUnknown { get; set; }
    public double NotionalEnrichedDelta { get; set; }
    public double NotionalEnrichedDeltaRatio { get; set; }
    public double? NotionalRollingStrictDelta { get; set; }
    public double? NotionalRollingStrictAbsDeltaChange { get; set; }
    public double? NotionalRollingStrictDeltaRatioTotal { get; set; }
    public double? NotionalRollingEnrichedDelta { get; set; }
    public double? NotionalRollingEnrichedDeltaChange { get; set; }
    public double? NotionalRollingEnrichedDeltaRatio { get; set; }

    public long BarTradeUpdates { get; set; }
    public long? RollingTradeUpdates { get; set; }
    public double? ContractRollingStrictCoverage { get; set; }
    public double? NotionalRollingStrictCoverage { get; set; }
    public double? ContractRollingActivityPerSecond { get; set; }
    public double? NotionalRollingActivityPerSecond { get; set; }

    public long? BandOiOpen { get; set; }
    public long? BandOiClose { get; set; }
    public long? BarOiChange { get; set; }
    public double? BarOiChangePct { get; set; }
    public long? RollingOiChange { get; set; }
    public double? RollingOiChangePct { get; set; }
    public double? PremiumNotionalOiAtClose { get; set; }
    public double? PremiumNotionalOiChange { get; set; }
}

public sealed class AdaptiveResidualAnchorComponentRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public OptionType Side { get; set; }
    public double Strike { get; set; }
    public required string Token { get; set; }
    public required string TradingSymbol { get; set; }
    public int LotSize { get; set; }
    public bool IsCenterStrike { get; set; }
    public double Price0930 { get; set; }
    public double ImpliedVolatility0930 { get; set; }
    public required DateTimeOffset QuoteTimestampUtc { get; set; }
    public double QuoteAgeSeconds { get; set; }
}

public sealed class AdaptiveOptionResidualBarRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    public ResidualVariant Variant { get; set; }
    public required DateTimeOffset EndAvailableAtUtc { get; set; }
    public double CenterStrike { get; set; }
    public double FutureChangeFrom0930 { get; set; }
    public double ModeledWeeklyUnderlying { get; set; }
    public double CEActual { get; set; }
    public double CEExpected { get; set; }
    public double CEActualChange { get; set; }
    public double CEExpectedChange { get; set; }
    public double CEResidual { get; set; }
    public double CEResidualPct { get; set; }
    public double PEActual { get; set; }
    public double PEExpected { get; set; }
    public double PEActualChange { get; set; }
    public double PEExpectedChange { get; set; }
    public double PEResidual { get; set; }
    public double PEResidualPct { get; set; }
    public double DirectionalResidualPct { get; set; }
    public double? ResidualDelta { get; set; }
    public int ResidualDirection { get; set; }
    public int FuturesRollingDirection { get; set; }
    public required string Relationship { get; set; }
    public double CommonResidualPct { get; set; }
    public double MaxQuoteAgeSeconds { get; set; }
    public bool IsAvailable { get; set; }
    public string? UnavailableReason { get; set; }
}

public enum AdaptiveObservationStatus
{
    PendingH5 = 0,
    Completed = 1,
    Unavailable = 2,
}

public sealed class AdaptiveWeak2ObservationRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int StrongBaseBarSeq { get; set; }
    public int Weak1BarSeq { get; set; }
    public int TriggerBarSeq { get; set; }
    public int OldTrendDirection { get; set; }
    public int ReversalDirection { get; set; }
    public bool StrictNoTurnDiagnostic { get; set; }
    public required DateTimeOffset TriggerTimestampUtc { get; set; }
    public OptionType OptionSide { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public double? Strike { get; set; }
    public string? Token { get; set; }
    public string? TradingSymbol { get; set; }
    public double? EntryAsk { get; set; }
    public double? EntryBid { get; set; }
    public DateTimeOffset? EntryQuoteTimestampUtc { get; set; }
    public double? EntryQuoteLatencySeconds { get; set; }
    public long? CEOiStrongBase { get; set; }
    public long? PEOiStrongBase { get; set; }
    public long? CEOiTrigger { get; set; }
    public long? PEOiTrigger { get; set; }
    public long? PairOiStrongBase { get; set; }
    public long? PairOiTrigger { get; set; }
    public long? PairOiChange { get; set; }
    public double? PairOiChangePct { get; set; }
    public bool? OiGatePassed { get; set; }
    public double? AtmResidualDirectionalPct { get; set; }
    public double? BandResidualDirectionalPct { get; set; }
    public bool? ResidualSupportsReversalDiagnostic { get; set; }
    public int H5TargetBarSeq { get; set; }
    public AdaptiveObservationStatus Status { get; set; }
    public required string SelectionPolicy { get; set; }
    public string? UnavailableReason { get; set; }
    public double? ExitBid { get; set; }
    public double? ExitAsk { get; set; }
    public DateTimeOffset? ExitTimestampUtc { get; set; }
    public double? ExitLatencySeconds { get; set; }
    public double? HoldingSeconds { get; set; }
    public double? PnlPoints { get; set; }
    public double? ReturnPct { get; set; }
    public double? MfePointsExecutableBid { get; set; }
    public double? MaePointsExecutableBid { get; set; }
    public double? FuturesH5Move { get; set; }
}

public enum AdaptiveRuntimeStatus
{
    Calibrating = 0,
    Rebuilding = 1,
    Live = 2,
    Degraded = 3,
    Closed = 4,
}

public sealed class AdaptiveObserverRuntimeRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public AdaptiveRuntimeStatus RuntimeStatus { get; set; }
    public required DateTimeOffset LastHeartbeatUtc { get; set; }
    public DateTimeOffset? LastProcessedSourceAvailableAtUtc { get; set; }
    public long? LastProcessedSourceTickId { get; set; }
    public int LastCompletedBarSeq { get; set; }
    public long CurrentPartialBarVolume { get; set; }
    public DateTimeOffset? CurrentPartialBarStartedAtUtc { get; set; }
    public DateTimeOffset? LastRecoveryStartedUtc { get; set; }
    public DateTimeOffset? LastRecoveryCompletedUtc { get; set; }
    public int LastRecoveryReconciledBars { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Sidecar projection of futures Level-1 microstructure per completed adaptive bar (08-Oct plan, section 71).
/// Deliberately a separate table: the core adaptive rows are the parity-protected restart ledger and are never
/// extended with new observational fields. One row per (SessionId, BarSeq, MetricsVersion).
/// </summary>
public sealed class AdaptiveFuturesSupplementalRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    /// <summary>Calculation contract that produced this row; a row from another version is never overwritten.</summary>
    public required string MetricsVersion { get; set; }
    public double? TobStart { get; set; }
    public double? TobTimeWeighted { get; set; }
    public double? TobEnd { get; set; }
    public double? TobChange { get; set; }
    public double? TobMin { get; set; }
    public double? TobMax { get; set; }
    public double? MicroDevStart { get; set; }
    public double? MicroDevTimeWeighted { get; set; }
    public double? MicroDevEnd { get; set; }
    public double? MicroDevChange { get; set; }
    public long? Ofi { get; set; }
    public int OfiTransitions { get; set; }
    public int BookStateChanges { get; set; }
    public int InvalidBookEvents { get; set; }
    /// <summary>Seconds the book was structurally valid (positive, uncrossed prices). Includes seconds with zero quantity, which have no TOB/MicroDev.</summary>
    public double ValidBookSeconds { get; set; }
    public double InvalidBookSeconds { get; set; }
    /// <summary>Seconds with a usable TOB (valid prices AND non-zero total quantity): the real coverage of the TOB metrics.</summary>
    public double TobUsableSeconds { get; set; }
    /// <summary>Seconds with a usable MicroDev (same condition as TOB; kept separate so the two can diverge in a later version).</summary>
    public double MicroDevUsableSeconds { get; set; }
}

/// <summary>
/// Supplemental per-session identity (08-Oct plan section 71.3). Frozen once; recovery always reuses it and never re-resolves a different
/// instrument, including the "unresolved" outcome (<see cref="SpotToken"/> null), so sidecar values replay identically.
/// </summary>
public sealed class AdaptiveSessionSupplementalRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public required string MetricsVersion { get; set; }
    /// <summary>The NIFTY spot/index instrument token, or null when no unique valid instrument could be resolved (Basis then stays unavailable).</summary>
    public string? SpotToken { get; set; }
    public string? SpotSymbol { get; set; }
    public required string ResolutionProvenance { get; set; }
    public DateTimeOffset ResolvedAtUtc { get; set; }
}

/// <summary>Sidecar projection of futures-minus-spot basis per completed adaptive bar, with the approved 5-second spot freshness rule applied (separate table: new columns on the futures sidecar would not match rows written earlier).</summary>
public sealed class AdaptiveBasisSupplementalRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    public required string MetricsVersion { get; set; }
    public double? BasisStart { get; set; }
    public double? BasisTimeWeighted { get; set; }
    public double? BasisEnd { get; set; }
    public double? DeltaBasis { get; set; }
    public double? SpotAgeStartSeconds { get; set; }
    public double? SpotAgeEndSeconds { get; set; }
    public double? SpotAgeMaxSeconds { get; set; }
    public int BasisStateChanges { get; set; }
    public double CoveredSeconds { get; set; }
    public double UncoveredSeconds { get; set; }
    /// <summary>Usable / NoState / StaleStart / StaleEnd / StaleBoth.</summary>
    public required string Status { get; set; }
}

/// <summary>Sidecar projection of supplemental options observations per completed adaptive bar (08-Oct plan section 71; metrics contract options-supp-v2).</summary>
public sealed class AdaptiveOptionsSupplementalRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int BarSeq { get; set; }
    public required string MetricsVersion { get; set; }
    /// <summary>Frozen session observation start the subscribed-universe day volumes are counted since.</summary>
    public DateTimeOffset? ObservationStartUtc { get; set; }
    public double? CenterStrike { get; set; }
    public string? UnavailableReason { get; set; }
    public long? CeOiStart { get; set; }
    public long? CeOiEnd { get; set; }
    public long? CeOiDelta { get; set; }
    public long? PeOiStart { get; set; }
    public long? PeOiEnd { get; set; }
    public long? PeOiDelta { get; set; }
    public double? CeMidStart { get; set; }
    public double? CeMidEnd { get; set; }
    public double? CeMidDelta { get; set; }
    public double? PeMidStart { get; set; }
    public double? PeMidEnd { get; set; }
    public double? PeMidDelta { get; set; }
    public string? CePosition { get; set; }
    public string? PePosition { get; set; }
    public double? CeIvStart { get; set; }
    public double? CeIvEnd { get; set; }
    public double? CeDeltaIv { get; set; }
    public double? PeIvStart { get; set; }
    public double? PeIvEnd { get; set; }
    public double? PeDeltaIv { get; set; }
    public double? IvSkewStart { get; set; }
    public double? IvSkewEnd { get; set; }
    public double? DeltaSkew { get; set; }
    public long? BarCeQuantity { get; set; }
    public long? BarPeQuantity { get; set; }
    public double? VolPcr { get; set; }
    public long? RollCeQuantity { get; set; }
    public long? RollPeQuantity { get; set; }
    public double? RollVolPcr { get; set; }
    public long DayCeVolume { get; set; }
    public long DayPeVolume { get; set; }
    public int UniverseTokenCount { get; set; }
    public int TokensObserved { get; set; }
    public double? CeMicroDevTimeWeighted { get; set; }
    public long? CeOfi { get; set; }
    public double? PeMicroDevTimeWeighted { get; set; }
    public long? PeOfi { get; set; }
    public double? CeActivityPerSecond { get; set; }
    public double? PeActivityPerSecond { get; set; }
    public double? StraddleMidStart { get; set; }
    public double? StraddleMidEnd { get; set; }
    public double? StraddleDelta { get; set; }
}

/// <summary>
/// Persisted commentary lifecycle event (08-Oct plan section 56). PostgreSQL is the authoritative commentary store. Only lifecycle events are
/// stored: NoMaterialEvent and unchanged continuation bars never create a row. Immutable once written; outcomes are attached elsewhere, later.
/// </summary>
public sealed class AdaptiveCommentaryEventRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public DateOnly TradeDate { get; set; }
    public int BarSeq { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentaryEventType EventType { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.EventBias EventBias { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.MarketRegime MarketRegime { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentaryLifecycle Lifecycle { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.EvidenceAgreement EvidenceAgreement { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentarySeverity Severity { get; set; }
    public long? PreviousEventId { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.EventBias PreviousBias { get; set; }
    public bool BiasChanged { get; set; }
    public required string PrimaryEvidenceJson { get; set; }
    public required string ConfirmationEvidenceJson { get; set; }
    public required string ContradictionEvidenceJson { get; set; }
    public required string DataQualityJson { get; set; }
    public required string RenderedCommentary { get; set; }
    public bool ShouldNotifyTelegram { get; set; }
    public string? NotificationReason { get; set; }
    public required string CommentaryVersion { get; set; }
    /// <summary>CommentaryVersion:SessionId:BarSeq:EventType:Lifecycle:Bias. Unique and version-keyed: replaying a bar never duplicates an event, and a later version can coexist with v1 history.</summary>
    public required string EventIdentity { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// Per-session commentary checkpoint (plan section 57). Operational state, NOT market history: it is rebuildable by replaying persisted
/// completed bars, and it advances on every evaluated bar including the many that persist no event.
/// </summary>
public sealed class AdaptiveCommentaryRuntimeRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public int LastEvaluatedBarSeq { get; set; }
    /// <summary>Id of the most recently persisted event row for the session.</summary>
    public long? CurrentEventId { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentaryEventType? CurrentEventType { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.EventBias CurrentBias { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.MarketRegime CurrentRegime { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentaryLifecycle? CurrentLifecycle { get; set; }
    public int? LastTelegramBarSeq { get; set; }
    // Active-event detail needed to continue exactly after a restart (null when no event is active).
    public NiftySignal.AdaptiveObserver.Commentary.EventBias? ActiveBias { get; set; }
    public int? ActiveStartedBarSeq { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.EvidenceAgreement? ActiveAgreement { get; set; }
    public string? ActiveSupportingFamilies { get; set; }
    public NiftySignal.AdaptiveObserver.Commentary.CommentaryLifecycle? ActivePhase { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public required string CommentaryVersion { get; set; }
}

/// <summary>Projection components whose durable health is tracked outside the parity-protected core rows.</summary>
public static class AdaptiveProjectionComponents
{
    public const string FuturesSupplemental = "futures-supplemental";
    public const string OptionsSupplemental = "options-supplemental";
    public const string Commentary = "commentary";
    public const string BasisSupplemental = "basis-supplemental";
}

/// <summary>
/// Durable record that a derived projection disagreed with what was already persisted for one bar (sidecar replay mismatch, or commentary
/// replay mismatch). It is the restart-surviving distinction between "missing" and "known invalid": consumers must not trust the bar's sidecar
/// values, and commentary processing does not proceed past a commentary mismatch until the row is reconciled. Core rows are never involved.
/// </summary>
public sealed class AdaptiveProjectionHealthRow
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    public required string Component { get; set; }
    public required string Version { get; set; }
    public int BarSeq { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; }
}

/// <summary>
/// Suppressed is terminal and never delivered: Telegram commentary was disabled/unconfigured while the job was unsent, or the commentary
/// projection degraded upstream of the job's event. Nothing revives a Suppressed job.
/// </summary>
public enum AdaptiveCommentaryNotificationStatus { Pending = 0, Sending = 1, Sent = 2, DeliveryUncertain = 3, Suppressed = 4 }

/// <summary>
/// Durable Telegram outbox for commentary (08-Oct plan section 61). One job per event (unique <see cref="EventId"/>); never written by the
/// detector's send path, only enqueued with the event. Telegram acknowledgement and the database commit are not atomic, so exactly-once delivery
/// is not claimed: Sent jobs are never resent and DeliveryUncertain jobs are never retried automatically.
/// </summary>
public sealed class AdaptiveCommentaryNotificationJobRow
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public AdaptiveCommentaryNotificationStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public long? TelegramMessageId { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
