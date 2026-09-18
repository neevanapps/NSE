using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// One instrument's own TOP-OF-BOOK depth imbalance, averaged over every real tick observed
/// within one cadence -- deliberately distinct from <see cref="DepthImbalanceAccumulator"/>,
/// which uses <see cref="MarketDepth.TotalBidQty"/>/<see cref="MarketDepth.TotalAskQty"/> (summed
/// across all 5 levels). This one uses only <see cref="MarketDepth.Bid1Qty"/>/
/// <see cref="MarketDepth.Ask1Qty"/> -- the resting size right at the touch, which can diverge
/// sharply from the aggregate 5-level picture (a thin best offer with a heavy total book behind
/// it reads bullish at the touch and bearish in aggregate, at the same instant -- the exact
/// divergence flagged in the 2026-09-17 live-snapshot discussion that motivated this metric).
/// New 2026-09-17, 9th future-side volume-bar candidate (after the original 8) -- added because
/// the top-5 version is already the strongest validated candidate on this clock, and the
/// touch-only variant is cheap to build (no new tick accumulation, <see cref="MarketDepth"/>
/// already carries Bid1Qty/Ask1Qty) and structurally different enough to be worth its own
/// evaluation cycle rather than assumed redundant.
/// </summary>
public sealed class TopOfBookImbalanceAccumulator
{
    double _imbalanceSum;
    int _imbalanceCount;
    double _bidQtySum;
    double _askQtySum;
    int _rawSampleCount;

    public void ApplyTick(MarketDepth depth)
    {
        var bid = depth.Bid1Qty;
        var ask = depth.Ask1Qty;

        if (bid + ask > 0)
        {
            _imbalanceSum += (double)(bid - ask) / (bid + ask);
            _imbalanceCount++;
        }

        _bidQtySum += bid;
        _askQtySum += ask;
        _rawSampleCount++;
    }

    /// <summary>Average of the per-tick imbalance ratio -- null if no depth-bearing tick arrived this cadence.</summary>
    public double? CadenceImbalance => _imbalanceCount > 0 ? _imbalanceSum / _imbalanceCount : null;

    public double? AverageBidQty => _rawSampleCount > 0 ? _bidQtySum / _rawSampleCount : null;

    public double? AverageAskQty => _rawSampleCount > 0 ? _askQtySum / _rawSampleCount : null;

    public void Reset()
    {
        _imbalanceSum = 0;
        _imbalanceCount = 0;
        _bidQtySum = 0;
        _askQtySum = 0;
        _rawSampleCount = 0;
    }
}
