using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Rules;

namespace NiftySignal.Dashboard.Services;

public sealed record ScoreHistoryPoint(DateTimeOffset Timestamp, double Score);

/// <summary>
/// One score component's weight/z-score/contribution plus its own warm-up status
/// (2026-09-04, folded in from the former standalone Data Health panel -- see
/// LiveDataService.BuildComponentRows) so ScorePanel doesn't need to correlate two
/// separately-keyed lists by name.
/// </summary>
public sealed record ScoreComponentRow(
    string Name,
    double Weight,
    double ZScore,
    double WeightedContribution,
    bool IsWarmedUp,
    TimeSpan Window,
    TimeSpan Remaining);

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
