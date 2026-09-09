using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Rules;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// <paramref name="SpotPrice"/> (2026-09-05) comes from the score snapshot itself rather than a
/// separate tick lookup, so score and price are aligned to the same cadence instant by
/// construction -- the chart overlays them on a shared time axis.
/// </summary>
public sealed record ScoreHistoryPoint(DateTimeOffset Timestamp, double Score, double? SpotPrice);

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

/// <summary>
/// One ratio-composite metric's weight/clipped-value/contribution (audit finding F35, 2026-09-09
/// -- the ratio sidecar's own dashboard panel, mirroring ScoreComponentRow's shape for the
/// original composite). <paramref name="ClippedValue"/> holds the metric's already-clipped
/// [-1,1] s_i (see NiftySignal.Features.RatioMetricMath), not a z-score -- there's no rolling
/// window here, so unlike ScoreComponentRow there's no Window/Remaining warm-up countdown: a
/// ratio metric is either present this cadence (its own liquidity floor cleared) or it isn't,
/// per <paramref name="IsPresent"/>.
/// </summary>
public sealed record RatioComponentRow(
    string Name,
    double Weight,
    double? ClippedValue,
    double? WeightedContribution,
    bool IsPresent);

/// <summary>
/// Three additions on 2026-09-05: <paramref name="ExpiryDate"/> (rows for both tracked weeklies
/// now come back together, filtered per-viewer rather than in the shared singleton),
/// <paramref name="LotSize"/> (needed to express OI as money rather than contract count), and
/// <paramref name="TheoreticalPrice"/> (see LiveDataService.BuildOptionChainAsync for how it's
/// derived and why it isn't circular).
/// </summary>
public sealed record OptionChainRow(
    string TradingSymbol,
    OptionType OptionType,
    decimal Strike,
    DateOnly ExpiryDate,
    decimal Ltp,
    long OpenInterest,
    int LotSize,
    double OiChangePct,
    long? OiChange,
    double? ImpliedVolatility,
    double? TheoreticalPrice,
    double DepthImbalance,
    OiBuildupClassification Buildup,
    bool IsAtm,
    decimal? Bid,
    decimal? Ask,
    decimal? Change)
{
    /// <summary>Actual mid (or LTP when there's no book) minus theoretical. Positive = the market is paying above the ATM-vol baseline for this strike.</summary>
    public double? PriceVsTheoretical => TheoreticalPrice is { } theo ? (double)Ltp - theo : null;

    /// <summary>Open interest expressed as rupees at risk rather than contract count.</summary>
    public decimal NotionalValue => OpenInterest * LotSize * Ltp;

    /// <summary>
    /// The 30-min <see cref="OiChange"/> expressed in rupees rather than contract count -- null
    /// (not zero) when there's no 30-min-ago tick to compare against yet, distinct from a
    /// genuine zero change.
    /// </summary>
    public decimal? NotionalOiChange => OiChange is { } chg ? chg * LotSize * Ltp : null;
}

public sealed record PositionRow(
    // Not rendered -- kept so ApplyPushedTick (2026-09-08) can match a live tick to an open
    // position without a DB round trip, the same "no DB access on the push path" rule
    // QuickQuotes already follows.
    string InstrumentToken,
    string TradingSymbol,
    EntryDirection Direction,
    decimal EntryPremium,
    decimal CurrentPremium,
    // The remaining open quantity, not the original traded-at-entry quantity (2026-09-08
    // dashboard fix) -- PaperTrade.Quantity itself stays the original total (needed for the
    // final-exit blended P&L calc, see LiveTradingEngine.EvaluateExitAsync), but a position
    // that's already partially booked genuinely only has the remainder still open, and both
    // this display and UnrealizedPnl below were overstating it by counting the booked half
    // as if it were still at risk.
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
