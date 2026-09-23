using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One volume bar's SUM-accumulated single-side (Call-only or Put-only, never mixed) top-5
/// order-book imbalance -- 2026-09-22, "Depth Imbalance, transparent research" track (user's own
/// exact spec, see chat: sum the per-tick bid-minus-ask difference across every tick in the bar,
/// both raw-quantity and notional). <see cref="Side"/> was added 2026-09-22 (same day) once the
/// user asked for a Put-side confirmation run alongside the original Call-only track -- rather than
/// a second near-duplicate table, one table now carries both sides as distinct rows (same pattern
/// <see cref="OptionDepthBarRow.BandWidth"/> already uses to let ATM+/-1 and ATM+/-2 coexist).
/// Additive (SUM, not per-tick AVERAGE) -- see
/// <see cref="NiftySignal.Features.DepthImbalanceSumAccumulator"/>'s own doc comment for why this is
/// a new class rather than a reuse of the existing depth tables.
/// </summary>
public sealed class DepthImbalanceSumBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>Strikes in the band, e.g. 3 = ATM+/-1 -- same convention as <see cref="OptionDepthBarRow.BandWidth"/>.</summary>
    public required int BandWidth { get; set; }

    /// <summary>Call or Put -- never both in one row. See this class's own doc comment.</summary>
    public required OptionType Side { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>The future's own close price for this bar -- carried here so day-level reporting can plot price context without a second join back to VolumeBars.</summary>
    public required decimal FutureClosePrice { get; set; }

    /// <summary>Sum across the band's own-side instruments of each instrument's own SUM, over every tick in this bar, of (top-5 bid qty - top-5 ask qty). Null only if no band instrument had a single depth-bearing tick this bar.</summary>
    public double? RawQtyDiffSum { get; set; }

    /// <summary>Same shape as <see cref="RawQtyDiffSum"/> but notional (qty*price) instead of raw quantity.</summary>
    public double? NotionalDiffSum { get; set; }

    /// <summary>The ATM instrument's (this row's own <see cref="Side"/>) own token for this bar's band-center strike -- the actual tradable instrument used to mark entries/exits/MAE/MFE against real prices (<see cref="OptionPriceSeries"/>), not a synthetic band average.</summary>
    public required string AtmToken { get; set; }

    public required decimal AtmStrike { get; set; }

    /// <summary>Calendar days from AsOfDate to the nearest expiry used for this bar's chain (0 = expiry day). Calendar, not trading-day, count -- stated explicitly since this project treats DTE convention as worth being explicit about.</summary>
    public required int DaysToExpiry { get; set; }
}
