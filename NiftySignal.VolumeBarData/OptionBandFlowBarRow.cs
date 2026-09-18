namespace NiftySignal.VolumeBarData;

/// <summary>
/// The ATM±1 options complex's own trading FLOW during one future volume-bar's time window --
/// 2026-09-18, Phase 1 metric 2. Deliberately separate from <see cref="OptionAtmBarRow"/> (a
/// point-in-time snapshot at bar close) rather than extending it: this one needs a genuinely
/// different computation -- volume is a flow quantity, accumulated tick-by-tick ACROSS the bar's
/// whole time window (same shape as <see cref="NiftySignal.Features.FutureFlowAccumulator"/>
/// already uses for the future itself), not read once at the boundary. Same shared identity
/// (AsOfDate, BarIndex, BarVolumeThreshold) as every other options-phase table, joined back to
/// the future bar it describes.
///
/// Band definition, per the finalized options-phase plan: ATM = strike nearest the FUTURE's own
/// close price at this bar (not a synthetic forward -- that correction matters for IV solving,
/// not for picking a coarse 3-strike window, and the plan's own Phase 0 explicitly defines the
/// band this way). Band = ATM ± 1 strike, i.e. 3 strikes, both Call and Put (6 instruments).
/// </summary>
public sealed class OptionBandFlowBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Sum of (tick volume delta x tick price) across the band's Call-side instruments, for ticks falling within this bar's own time window. Notional (rupee-value), not raw contract count, per the plan's own "prefer notional" instruction -- makes an expensive ITM-ish strike and a cheap OTM-ish strike comparable on the same basis.</summary>
    public decimal CallNotionalVolume { get; set; }

    public decimal PutNotionalVolume { get; set; }
}
