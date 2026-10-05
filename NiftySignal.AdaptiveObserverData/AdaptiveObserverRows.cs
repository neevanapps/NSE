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
    public required DateTimeOffset OpeningWindowStartUtc { get; set; }
    public required DateTimeOffset OpeningWindowEndUtc { get; set; }
    public long OpeningVolume { get; set; }
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
