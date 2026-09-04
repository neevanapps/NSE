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
    bool IsAtm,
    decimal? Bid,
    decimal? Ask,
    decimal? Change);

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

/// <summary>
/// The Live Quote panel's fast (1s) refresh -- deliberately a small subset of
/// <see cref="OptionChainRow"/>'s fields (no IV, no 30-min OI lookback) so that path stays
/// cheap enough to poll every second without dragging the whole option chain along with it.
/// </summary>
public sealed record QuickQuote(decimal Ltp, decimal? Bid, decimal? Ask, decimal? Change);

public enum ConnectionStatus
{
    Connected,
    Reconnecting,
    Disconnected,
}
