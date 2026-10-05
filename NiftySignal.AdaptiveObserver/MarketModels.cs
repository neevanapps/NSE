namespace NiftySignal.AdaptiveObserver;

public readonly record struct ObserverRawTick(
    long Id,
    DateTimeOffset ExchangeTimestamp,
    DateTimeOffset ReceivedAt,
    double Last,
    double Bid,
    double Ask,
    long BidQty,
    long AskQty,
    long Volume,
    long? OpenInterest);

public readonly record struct CleanObserverTick(
    long Id,
    DateTimeOffset ExchangeTimestamp,
    DateTimeOffset AvailableAt,
    double Last,
    double Bid,
    double Ask,
    long BidQty,
    long AskQty,
    long Volume,
    long? OpenInterest)
{
    public bool HasTwoSidedQuote => Bid > 0d && Ask > 0d && Ask > Bid;
}

public enum ObserverTradeSide
{
    Unknown = 0,
    Buy = 1,
    Sell = -1,
}

public readonly record struct EnrichedObserverTick(
    CleanObserverTick Tick,
    long TradeVolume,
    ObserverTradeSide StrictSide,
    ObserverTradeSide EnrichedSide)
{
    public bool UsedTickFallback =>
        TradeVolume > 0 && StrictSide == ObserverTradeSide.Unknown && EnrichedSide != ObserverTradeSide.Unknown;
}

public sealed record AdaptiveSessionDefinition(
    DateOnly TradeDate,
    string FutureToken,
    string FutureSymbol,
    DateOnly FutureExpiry,
    int LotSize,
    long BaseBarVolume,
    int RollingWindowBars = 10)
{
    public long RollingWindowVolume => checked(BaseBarVolume * RollingWindowBars);
}

public sealed class ExactAdaptiveBar
{
    public required DateOnly TradeDate { get; init; }
    public required int BarSeq { get; init; }
    public required string Symbol { get; init; }
    public required bool IsComplete { get; init; }
    public required DateTimeOffset StartAvailableAtUtc { get; init; }
    public required DateTimeOffset EndAvailableAtUtc { get; init; }
    public required double Open { get; init; }
    public required double High { get; init; }
    public required double Low { get; init; }
    public required double Close { get; init; }
    public required double Vwap { get; init; }
    public required long Volume { get; init; }
    public required int TradeUpdates { get; init; }
    public required long FirstSourceTickId { get; init; }
    public required long LastSourceTickId { get; init; }
    public required long StrictBuyVolume { get; init; }
    public required long StrictSellVolume { get; init; }
    public required long StrictUnknownVolume { get; init; }
    public required long EnrichedBuyVolume { get; init; }
    public required long EnrichedSellVolume { get; init; }
    public required long EnrichedUnknownVolume { get; init; }
    public required long? OiOpen { get; init; }
    public required long? OiClose { get; init; }
    public required double? Bid { get; init; }
    public required double? Ask { get; init; }
    public required long? BidQty { get; init; }
    public required long? AskQty { get; init; }
    public required double? Spread { get; init; }
    public required double? BookImbalance { get; init; }
    public required double? Microprice { get; init; }

    public double DurationSeconds => (EndAvailableAtUtc - StartAvailableAtUtc).TotalSeconds;
    public double BarPriceDisplacement => Close - Open;
    public long StrictDelta => StrictBuyVolume - StrictSellVolume;
    public long EnrichedDelta => EnrichedBuyVolume - EnrichedSellVolume;
    public double StrictDeltaRatioTotal => Volume > 0 ? (double)StrictDelta / Volume : 0d;
    public double EnrichedDeltaRatio => Volume > 0 ? (double)EnrichedDelta / Volume : 0d;
    public double StrictCoverage => Volume > 0 ? (double)(StrictBuyVolume + StrictSellVolume) / Volume : 0d;
    public long? OiChange => OiOpen.HasValue && OiClose.HasValue ? OiClose.Value - OiOpen.Value : null;
}

public sealed class AdaptiveRollingState
{
    public required DateOnly TradeDate { get; init; }
    public required int WindowBars { get; init; }
    public required long BaseBarVolume { get; init; }
    public required long WindowVolume { get; init; }
    public required int StartBarSeq { get; init; }
    public required int EndBarSeq { get; init; }
    public required DateTimeOffset StartAvailableAtUtc { get; init; }
    public required DateTimeOffset EndAvailableAtUtc { get; init; }
    public required double StartPrice { get; init; }
    public required double EndPrice { get; init; }
    public required double High { get; init; }
    public required double Low { get; init; }
    public required double PriceDisplacement { get; init; }
    public required double ReturnBps { get; init; }
    public required double PathLength { get; init; }
    public required double Efficiency { get; init; }
    public required double SignedEfficiency { get; init; }
    public required long StrictBuyVolume { get; init; }
    public required long StrictSellVolume { get; init; }
    public required long StrictUnknownVolume { get; init; }
    public required long StrictDelta { get; init; }
    public required double StrictQuoteCoverage { get; init; }
    public required double StrictDeltaRatioTotal { get; init; }
    public required double? StrictDeltaRatioClassified { get; init; }
    public required long EnrichedBuyVolume { get; init; }
    public required long EnrichedSellVolume { get; init; }
    public required long EnrichedUnknownVolume { get; init; }
    public required long EnrichedDelta { get; init; }
    public required double EnrichedDeltaRatio { get; init; }
    public required double FallbackShare { get; init; }
    public required long? OiStart { get; init; }
    public required long? OiEnd { get; init; }
    public required long? OiChange { get; init; }
    public required double? OiChangePct { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required double? VolumePerSecond { get; init; }
    public required int TradeUpdates { get; init; }
    public required double WindowRange { get; init; }
    public required int PriceDirection { get; init; }
    public required int StrictDeltaDirection { get; init; }
    public required int EnrichedDeltaDirection { get; init; }
    public required bool? PriceStrictDeltaAgree { get; init; }
    public required bool? PriceEnrichedDeltaAgree { get; init; }
}

public enum AdaptiveStateKind
{
    Normal = 0,
    Strong = 1,
    Weak1 = 2,
    Weak2 = 3,
}

public sealed class AdaptiveFlowState
{
    public required ExactAdaptiveBar Bar { get; init; }
    public AdaptiveRollingState? Rolling { get; init; }
    public long? RollingStrictDeltaChange { get; init; }
    public long? RollingStrictAbsDeltaChange { get; init; }
    public long? RollingEnrichedDeltaChange { get; init; }
    public double? RollingOiChangePctChange { get; init; }
    public string? StrictDominanceEvolution { get; init; }
    public bool IsStrong { get; set; }
    public int WeakeningSequence { get; set; }
    public int? StrongBaseBarSeq { get; set; }
    public int? Weak1BarSeq { get; set; }
    public AdaptiveStateKind State { get; set; }
}
