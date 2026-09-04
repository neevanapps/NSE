using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Entities;

/// <summary>
/// Token to strike/expiry/type mapping for one trading day (plan section 3.1/3.2).
/// Strikes and even tokens can be redefined day to day, so this is snapshotted per
/// <see cref="AsOfDate"/> rather than treated as a stable dimension table.
/// </summary>
public sealed class Instrument
{
    public long Id { get; set; }

    public required string Token { get; set; }

    public required Exchange Exchange { get; set; }

    public required string TradingSymbol { get; set; }

    public required InstrumentType InstrumentType { get; set; }

    public OptionType OptionType { get; set; } = OptionType.None;

    public decimal? StrikePrice { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    /// <summary>E.g. "NIFTY" -- ties an option/future back to its underlying index.</summary>
    public required string Underlying { get; set; }

    public int LotSize { get; set; }

    public decimal TickSize { get; set; }

    /// <summary>The trading day this snapshot of the instrument master applies to.</summary>
    public required DateOnly AsOfDate { get; set; }

    /// <summary>
    /// True once the ingestion worker has added this instrument to the live WebSocket feed.
    /// The daily 08:45-equivalent job's own resolved instruments are subscribed immediately
    /// (default true); a row inserted on-demand (Dashboard: "watch this strike even though
    /// it's outside the tracked ATM band") starts false, and the worker's poll loop flips
    /// it once the live subscribe call actually goes out.
    /// </summary>
    public bool Subscribed { get; set; } = true;
}
