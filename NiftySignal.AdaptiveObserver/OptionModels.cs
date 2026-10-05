using NiftySignal.Domain.Enums;

namespace NiftySignal.AdaptiveObserver;

public sealed record ObserverOptionInstrument(
    string Token,
    string TradingSymbol,
    OptionType OptionType,
    double Strike,
    DateOnly ExpiryDate,
    int LotSize);

public sealed record OptionQuoteSnapshot(
    string Token,
    DateTimeOffset AvailableAt,
    double Last,
    double Bid,
    double Ask,
    long? OpenInterest)
{
    public bool HasTwoSidedQuote => Bid > 0d && Ask > Bid;
    public double? Mid => HasTwoSidedQuote ? (Bid + Ask) / 2d : Last > 0d ? Last : null;
}

public sealed record OptionBandSelection(
    DateTimeOffset SelectedAtUtc,
    DateOnly ExpiryDate,
    double SyntheticUnderlying,
    double CenterStrike,
    IReadOnlyList<double> Strikes,
    IReadOnlyList<ObserverOptionInstrument> Calls,
    IReadOnlyList<ObserverOptionInstrument> Puts);

public sealed record OptionResidualAnchorComponent(
    ObserverOptionInstrument Instrument,
    double Price0930,
    double ImpliedVolatility0930,
    DateTimeOffset QuoteTimestampUtc,
    double QuoteAgeSeconds,
    bool IsCenterStrike);

public sealed record OptionResidualAnchor(
    DateTimeOffset AnchorTimeUtc,
    DateOnly ExpiryDate,
    double Future0930,
    double SyntheticUnderlying0930,
    double CenterStrike,
    IReadOnlyList<OptionResidualAnchorComponent> Calls,
    IReadOnlyList<OptionResidualAnchorComponent> Puts);

public enum ResidualVariant
{
    Atm = 0,
    AtmPlusMinus2 = 1,
}

public sealed record OptionResidualReading(
    ResidualVariant Variant,
    DateTimeOffset AsOfUtc,
    double CenterStrike,
    double FutureChangeFrom0930,
    double ModeledWeeklyUnderlying,
    double CEActual,
    double CEExpected,
    double CEActualChange,
    double CEExpectedChange,
    double CEResidual,
    double CEResidualPct,
    double PEActual,
    double PEExpected,
    double PEActualChange,
    double PEExpectedChange,
    double PEResidual,
    double PEResidualPct,
    double DirectionalResidualPct,
    double CommonResidualPct,
    double MaxQuoteAgeSeconds);

public sealed record InstrumentFlowSnapshot(
    string Token,
    DateTimeOffset? LastAvailableAt,
    double? LastPrice,
    double? Bid,
    double? Ask,
    long? OpenInterest,
    long StrictBuyQuantity,
    long StrictSellQuantity,
    long StrictUnknownQuantity,
    long EnrichedBuyQuantity,
    long EnrichedSellQuantity,
    long EnrichedUnknownQuantity,
    double StrictBuyNotional,
    double StrictSellNotional,
    double StrictUnknownNotional,
    double EnrichedBuyNotional,
    double EnrichedSellNotional,
    double EnrichedUnknownNotional,
    long TradeUpdates)
{
    public double? Mid => Bid is > 0d && Ask is > 0d && Ask > Bid ? (Bid.Value + Ask.Value) / 2d : LastPrice;
}

public sealed record OptionBandSideMetrics(
    OptionType Side,
    double CenterStrike,
    IReadOnlyList<double> Strikes,
    double? PremiumIndexOpen,
    double? PremiumIndexClose,
    double? BarPriceChange,
    long ContractTotalQuantity,
    long ContractStrictBuy,
    long ContractStrictSell,
    long ContractStrictUnknown,
    long ContractStrictDelta,
    double ContractStrictDeltaRatioTotal,
    double ContractStrictCoverage,
    long ContractEnrichedBuy,
    long ContractEnrichedSell,
    long ContractEnrichedUnknown,
    long ContractEnrichedDelta,
    double ContractEnrichedDeltaRatio,
    double NotionalTotal,
    double NotionalStrictBuy,
    double NotionalStrictSell,
    double NotionalStrictUnknown,
    double NotionalStrictDelta,
    double NotionalStrictDeltaRatioTotal,
    double NotionalStrictCoverage,
    double NotionalEnrichedBuy,
    double NotionalEnrichedSell,
    double NotionalEnrichedUnknown,
    double NotionalEnrichedDelta,
    double NotionalEnrichedDeltaRatio,
    long TradeUpdates,
    double? MaxQuoteAgeSeconds,
    long? OiOpen,
    long? OiClose,
    long? OiChange,
    double? OiChangePct,
    double? PremiumNotionalOiOpen,
    double? PremiumNotionalOiClose,
    double? PremiumNotionalOiChange);
