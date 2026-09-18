using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// One instrument's own order-book depth imbalance, averaged over every real tick observed within
/// one cadence -- built from complete historical tick data, not a 3-second-stride sample (live
/// production only samples that coarsely because it can't wait around on a real-time feed; this
/// offline pass has no such constraint, per explicit instruction). Genuinely generic (nothing here
/// is future-specific) -- named `DepthImbalanceAccumulator` until 2026-09-12, when option-level depth
/// imbalance needed the exact same computation and this was renamed rather than duplicated.
///
/// 2026-09-17, moved here from `NiftySignal.BacktestData/CadencePopulator.cs` (unchanged logic,
/// same move already made for <see cref="FutureCvdProxyAccumulator"/>) so a volume-bar aggregation
/// pass (`NiftySignal.VolumeBarData`) can reuse it instead of a hand-copied duplicate. Deliberately
/// NOT the same class as `NiftySignal.Host/LiveFeatureEngine.cs`'s own `CoreDepthImbalanceAccumulator`
/// -- that one is a separate, not-yet-unified hand-port for live's own 3s-sample cadence; unifying
/// it is a live-code change and out of scope here (see this project's own standing rule not to
/// touch Host/Dashboard until backtesting has found a real edge).
/// </summary>
public sealed class DepthImbalanceAccumulator
{
    double _imbalanceSum;
    int _imbalanceCount;
    double _bidQtySum;
    double _askQtySum;
    int _rawSampleCount;

    public void ApplyTick(MarketDepth depth)
    {
        var bid = depth.TotalBidQty;
        var ask = depth.TotalAskQty;

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
