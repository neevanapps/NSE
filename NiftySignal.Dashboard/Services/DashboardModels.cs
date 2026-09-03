using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Rules;

namespace NiftySignal.Dashboard.Services;

public sealed record ScoreHistoryPoint(DateTimeOffset Timestamp, double Score);

public sealed record ScoreComponentRow(string Name, double Weight, double ZScore, double WeightedContribution);

public sealed record OptionChainRow(
    string TradingSymbol,
    OptionType OptionType,
    decimal Strike,
    decimal Ltp,
    long OpenInterest,
    double OiChangePct,
    double? ImpliedVolatility,
    double DepthImbalance,
    OiBuildupClassification Buildup,
    bool IsAtm);

public sealed record PositionRow(
    string TradingSymbol,
    EntryDirection Direction,
    decimal EntryPremium,
    decimal CurrentPremium,
    int Quantity,
    DateTimeOffset EntryTime,
    bool HasPartiallyBooked)
{
    public decimal UnrealizedPnl => (CurrentPremium - EntryPremium) * Quantity;

    public double UnrealizedPnlPct => EntryPremium == 0 ? 0 : (double)((CurrentPremium - EntryPremium) / EntryPremium * 100);
}

public sealed record ClosedTradeRow(
    string TradingSymbol,
    EntryDirection Direction,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    decimal NetPnl,
    ExitReason ExitReason);

public sealed record MetricWarmUpStatus(string Name, bool IsWarmedUp, TimeSpan Window, TimeSpan Remaining);

public enum ConnectionStatus
{
    Connected,
    Reconnecting,
    Disconnected,
}
