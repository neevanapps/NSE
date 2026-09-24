using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (section 8). One option contract's bar for
/// EXACTLY one <see cref="FutureEventBar"/>'s [StartTimestamp, EndTimestamp] interval (spec
/// section 6 -- the option market never determines its own boundaries). Built for every available
/// CE/PE strike (spec section 7), not just the current ATM, so strike selection can change later
/// without rebuilding historical bars.
/// </summary>
/// <param name="AverageLtp">
/// Arithmetic mean of every real LTP print strictly inside this bar's interval -- the baseline
/// research price (spec section 9.1: represents the WHOLE interval, not just the closing tick).
/// Null when <see cref="MissingData"/> is true.
/// </param>
/// <param name="Vwap">Quantity-weighted average price using each tick's own per-tick traded-qty delta (diffed from the exchange's cumulative Volume field, same convention as the futures builder). Null when no tick in this interval had a nonzero volume delta of its own.</param>
/// <param name="MissingData">True when no real tick for this contract fell inside this bar's interval -- OHLC/AverageLtp/Vwap are all null in that case, never fabricated (spec section 11).</param>
/// <param name="IsStale">
/// True whenever <see cref="MissingData"/> is true AND an earlier real print exists to fall back
/// on for analytical purposes (spec section 12) -- <see cref="LastKnownPrice"/>/
/// <see cref="LastTradeTimestamp"/>/<see cref="AgeMs"/> carry that earlier print, but it must never
/// be treated as a fresh executable quote for this interval.
/// </param>
public sealed record SynchronizedOptionEventBar(
    int EventId, DateOnly TradingDate, DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp,
    decimal Strike, OptionType OptionType, int Dte, string Token,
    decimal? Open, decimal? High, decimal? Low, decimal? Close,
    decimal? AverageLtp, double? Vwap, int TickCount, long TradedVolume,
    DateTimeOffset? FirstTickTimestamp, DateTimeOffset? LastTickTimestamp,
    bool MissingData, bool IsStale, decimal? LastKnownPrice, double? AgeMs);
