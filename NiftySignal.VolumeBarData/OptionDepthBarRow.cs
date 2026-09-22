namespace NiftySignal.VolumeBarData;

/// <summary>
/// The ATM options complex's own resting order-book depth during one future volume-bar's time
/// window -- 2026-09-19, Phase 2 metric 1 (of 3). Tick-accumulated across the bar's whole window
/// (same shape as <see cref="OptionBandFlowBarRow"/>, and the same shape the futures side's own
/// `FutureDepthImbalance` already uses via <see cref="NiftySignal.Features.DepthImbalanceAccumulator"/>
/// -- average of the per-tick imbalance ratio across every real tick in the bar, not a
/// point-in-time snapshot at close).
///
/// <see cref="BandWidth"/> (strikes nearest the FUTURE's own close price at this bar) is a real
/// PARAMETER here, not a hardcoded ATM±1 like every earlier narrow-band options table (metrics 2/3)
/// -- 2026-09-19, user's own question: ATM±1 was the friend's Phase 0 plan default, never actually
/// tested against a wider band. Both widths can coexist in this same table (distinct rows, part of
/// the identity/unique index below) so ATM±1 and ATM±2 (or wider) can be populated and calibrated
/// side by side without a schema change per width. If a wider band turns out meaningfully better
/// here, that's a real signal to go back and re-test metrics 2/3's own ATM±1 assumption too --
/// deliberately not done pre-emptively, since those are already confirmed/locked from Phase 1.
///
/// Four raw averages per side (top-5-level qty, and touch-only qty) are persisted, not a single
/// pre-combined imbalance ratio -- three genuinely different Phase 2 metrics are derived from the
/// same eight numbers in <c>TradeSimulator</c>: the combined ATM complex imbalance, its
/// full-book-vs-touch divergence, and the call-side-vs-put-side comparison. Keeping the raw
/// averages here (not pre-computed ratios) avoids populating three separate near-duplicate tables.
/// </summary>
public sealed class OptionDepthBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>Strikes in the band, e.g. 3 = ATM±1, 5 = ATM±2 -- see this class's own doc comment.</summary>
    public required int BandWidth { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Sum, across the band's Call-side instruments, of each instrument's own average top-5 resting bid quantity over this bar's tick window -- same per-tick-average convention as <see cref="NiftySignal.Features.DepthImbalanceAccumulator.AverageBidQty"/>. Null if no depth-bearing tick arrived for any Call-side band instrument this bar.</summary>
    public double? CallBidQtyAvg { get; set; }

    public double? CallAskQtyAvg { get; set; }

    public double? PutBidQtyAvg { get; set; }

    public double? PutAskQtyAvg { get; set; }

    /// <summary>Same as <see cref="CallBidQtyAvg"/> but touch-only (best bid, Bid1Qty) -- not top-5. The pairing this enables (full-book vs. touch-only, same instrument set, same bar) is exactly what TobDepthDivergence needs.</summary>
    public double? CallTobBidQtyAvg { get; set; }

    public double? CallTobAskQtyAvg { get; set; }

    public double? PutTobBidQtyAvg { get; set; }

    public double? PutTobAskQtyAvg { get; set; }
}
