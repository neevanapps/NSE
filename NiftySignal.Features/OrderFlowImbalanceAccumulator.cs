using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// Order Flow Imbalance (OFI) -- the standard Cont/Kukanov/Stoikov top-of-book measure, added
/// 2026-09-17 as a new candidate distinct from the already-ported <see cref="DepthImbalanceAccumulator"/>.
/// The two answer genuinely different questions: DepthImbalance is a resting-book SNAPSHOT
/// (bid qty minus ask qty at an instant); OFI is the CHANGE in that book between two ticks --
/// whether fresh size was added, pulled, or a price level itself moved -- which is closer to
/// order FLOW than order STATE. Per-tick contribution, top-of-book only (Bid1/Ask1):
///
/// <code>
/// bidContribution =  Bid1Qty(now)                       if Bid1Price(now) &gt; Bid1Price(prev)
///                   Bid1Qty(now) - Bid1Qty(prev)        if Bid1Price(now) == Bid1Price(prev)
///                  -Bid1Qty(prev)                       if Bid1Price(now) &lt; Bid1Price(prev)
/// askContribution =  Ask1Qty(now)                       if Ask1Price(now) &lt; Ask1Price(prev)
///                   Ask1Qty(now) - Ask1Qty(prev)        if Ask1Price(now) == Ask1Price(prev)
///                  -Ask1Qty(prev)                       if Ask1Price(now) &gt; Ask1Price(prev)
/// OFI = bidContribution - askContribution
/// </code>
///
/// The "previous quote" state deliberately persists across <see cref="ResetCadence"/> (same
/// reasoning as <see cref="FutureFlowAccumulator"/>'s own `_previousVolume`) -- OFI is a
/// tick-to-tick diff, so the first tick of a new bar must still diff against the LAST tick of the
/// previous bar, not read as a contribution-free "no prior quote" case every single bar boundary.
/// </summary>
public sealed class OrderFlowImbalanceAccumulator
{
    decimal? _previousBidPrice;
    long? _previousBidQty;
    decimal? _previousAskPrice;
    long? _previousAskQty;

    double _cadenceSum;
    int _cadenceCount;

    public void ApplyTick(MarketDepth depth)
    {
        var bidContribution = (_previousBidPrice, _previousBidQty) switch
        {
            ({ } prevPrice, { } prevQty) when depth.Bid1Price > prevPrice => (double)depth.Bid1Qty,
            ({ } prevPrice, { } prevQty) when depth.Bid1Price == prevPrice => depth.Bid1Qty - (double)prevQty,
            ({ } prevPrice, { } prevQty) => -(double)prevQty,
            _ => 0.0, // no prior quote to diff against -- first tick this accumulator has ever seen
        };

        var askContribution = (_previousAskPrice, _previousAskQty) switch
        {
            ({ } prevPrice, { } prevQty) when depth.Ask1Price < prevPrice => (double)depth.Ask1Qty,
            ({ } prevPrice, { } prevQty) when depth.Ask1Price == prevPrice => depth.Ask1Qty - (double)prevQty,
            ({ } prevPrice, { } prevQty) => -(double)prevQty,
            _ => 0.0,
        };

        _cadenceSum += bidContribution - askContribution;
        _cadenceCount++;

        _previousBidPrice = depth.Bid1Price;
        _previousBidQty = depth.Bid1Qty;
        _previousAskPrice = depth.Ask1Price;
        _previousAskQty = depth.Ask1Qty;
    }

    /// <summary>Net OFI this cadence -- null if no depth-bearing tick arrived this cadence (not zero; matches every other accumulator's own null-vs-zero convention in this codebase).</summary>
    public double? CadenceNet => _cadenceCount > 0 ? _cadenceSum : null;

    public void ResetCadence()
    {
        _cadenceSum = 0;
        _cadenceCount = 0;
    }
}
