using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One volume bar's SUM-accumulated single-side (Call-only or Put-only) quote-rule CVD-proxy
/// volume -- 2026-09-22, "Options CVD Approximate, transparent research" track. Structurally
/// identical to <see cref="DepthImbalanceSumBarRow"/> (same band/side/bar identity shape), holding
/// a traded-volume quantity instead of a resting-depth quantity -- see
/// <see cref="NiftySignal.Features.CvdProxySumAccumulator"/>'s own doc comment for the formula.
/// </summary>
public sealed class CvdProxySumBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>Strikes in the band, e.g. 3 = ATM+/-1 -- same convention as <see cref="DepthImbalanceSumBarRow.BandWidth"/>.</summary>
    public required int BandWidth { get; set; }

    /// <summary>Call or Put -- never both in one row.</summary>
    public required OptionType Side { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    public required decimal FutureClosePrice { get; set; }

    /// <summary>Sum across the band's own-side instruments of each instrument's own SUM, over every tick in this bar, of the quote-rule-signed volume delta. Null only if no band instrument had a single qualifying tick this bar.</summary>
    public double? RawCvdSum { get; set; }

    /// <summary>Same shape as <see cref="RawCvdSum"/> but notional (signed volume x price) instead of raw contract count.</summary>
    public double? NotionalCvdSum { get; set; }

    public required string AtmToken { get; set; }

    public required decimal AtmStrike { get; set; }

    public required int DaysToExpiry { get; set; }
}
