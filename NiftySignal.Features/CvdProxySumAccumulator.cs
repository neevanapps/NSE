using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// Sum-accumulated (not averaged) quote-rule CVD-proxy volume for ONE option instrument, across
/// every real tick observed within one volume bar -- 2026-09-22, "Options CVD Approximate,
/// transparent research" track (mirrors <see cref="DepthImbalanceSumAccumulator"/>'s own
/// accumulate-don't-average shape, but for traded volume instead of resting depth).
///
/// Classification rule is NOT new -- reuses this project's existing "quote rule" proxy exactly as
/// <see cref="FutureCvdProxyAccumulator"/> and <c>LiveFeatureEngine.ComputeVolumePcrAndCvdProxy</c>
/// already define it: a tick's ENTIRE volume delta since the previous tick is classified
/// buy-leaning if LastPrice sat at/above the bid-ask midpoint, sell-leaning otherwise. This is a
/// proxy, not true aggressor-tagged CVD -- the feed has no per-trade tape.
/// </summary>
public sealed class CvdProxySumAccumulator
{
    double _rawCvdSum;
    double _notionalCvdSum;

    /// <param name="lastPrice">This tick's own traded/last price.</param>
    /// <param name="depth">This tick's own resting depth -- needs a real two-sided quote (Bid1/Ask1 both &gt; 0) to classify; contributes nothing otherwise.</param>
    /// <param name="volumeDelta">This tick's own cumulative-day-volume delta since the previous tick for this SAME instrument -- caller's responsibility to track and reset per day. Contributes nothing if &lt;= 0.</param>
    public void ApplyTick(decimal lastPrice, MarketDepth? depth, long volumeDelta)
    {
        if (volumeDelta <= 0 || depth is null || depth.Bid1Price <= 0 || depth.Ask1Price <= 0)
        {
            return;
        }

        var midpoint = (depth.Bid1Price + depth.Ask1Price) / 2m;
        var buyLeaning = lastPrice >= midpoint;
        var signedVolume = buyLeaning ? volumeDelta : -volumeDelta;

        _rawCvdSum += signedVolume;
        _notionalCvdSum += (double)(signedVolume * lastPrice);
    }

    /// <summary>Sum, across every tick applied this bar, of the quote-rule-signed volume delta. Zero (not null) if no tick arrived or none qualified -- callers distinguish "no tick" via a separate tick count.</summary>
    public double RawCvdSum => _rawCvdSum;

    /// <summary>Same shape as <see cref="RawCvdSum"/> but notional (signed volume x price) instead of raw contract count.</summary>
    public double NotionalCvdSum => _notionalCvdSum;

    public void Reset()
    {
        _rawCvdSum = 0;
        _notionalCvdSum = 0;
    }
}
