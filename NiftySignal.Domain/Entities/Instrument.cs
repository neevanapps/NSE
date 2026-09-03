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
}
