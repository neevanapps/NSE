using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// Sum-accumulated (not averaged) top-5-level order-book imbalance for ONE option instrument,
/// across every real tick observed within one volume bar -- built for the 2026-09-22 "Depth
/// Imbalance, transparent research" track (user's own exact spec, see chat: accumulate the per-tick
/// bid-minus-ask difference over the whole bar, in both a raw-quantity and a notional-quantity
/// version, then roll a sliding N-bar window over the accumulated per-bar values). Side-agnostic --
/// the same accumulator is applied to Call-only and Put-only instrument streams by its caller
/// (<see cref="NiftySignal.VolumeBarData.DepthImbalanceSumPopulator"/>); this class itself never
/// looks at option type.
///
/// Deliberately NOT <see cref="DepthImbalanceAccumulator"/>: that class produces a per-bar AVERAGE
/// of the per-tick imbalance RATIO (bid-ask)/(bid+ask); this class produces a per-bar SUM of the
/// per-tick imbalance DIFFERENCE (bid-ask), in both raw-qty and notional (qty*price) terms -- a
/// genuinely different quantity, not a refactor of the existing one.
/// </summary>
public sealed class DepthImbalanceSumAccumulator
{
    double _rawQtyDiffSum;
    double _notionalDiffSum;

    public void ApplyTick(MarketDepth depth)
    {
        var bidQty = depth.TotalBidQty;
        var askQty = depth.TotalAskQty;
        _rawQtyDiffSum += bidQty - askQty;

        var bidNotional =
            depth.Bid1Price * depth.Bid1Qty + depth.Bid2Price * depth.Bid2Qty + depth.Bid3Price * depth.Bid3Qty +
            depth.Bid4Price * depth.Bid4Qty + depth.Bid5Price * depth.Bid5Qty;
        var askNotional =
            depth.Ask1Price * depth.Ask1Qty + depth.Ask2Price * depth.Ask2Qty + depth.Ask3Price * depth.Ask3Qty +
            depth.Ask4Price * depth.Ask4Qty + depth.Ask5Price * depth.Ask5Qty;
        _notionalDiffSum += (double)(bidNotional - askNotional);
    }

    /// <summary>Sum, across every tick applied this bar, of (top-5 bid qty - top-5 ask qty). Zero (not null) if no tick arrived -- callers distinguish "no tick" via a separate tick count.</summary>
    public double RawQtyDiffSum => _rawQtyDiffSum;

    /// <summary>Sum, across every tick applied this bar, of (top-5 bid notional - top-5 ask notional).</summary>
    public double NotionalDiffSum => _notionalDiffSum;

    public void Reset()
    {
        _rawQtyDiffSum = 0;
        _notionalDiffSum = 0;
    }
}
