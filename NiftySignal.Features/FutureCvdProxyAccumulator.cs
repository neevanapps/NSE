using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// The future's own trade-aggressor volume proxy (audit finding: 2026-09-12, user-raised) --
/// same "quote rule" as production's existing option-chain <c>CvdProxy</c>: a tick's ENTIRE
/// volume delta since the previous tick is classified buy-leaning if LastPrice sat at/above the
/// bid-ask midpoint, sell-leaning otherwise. FlatTrade's feed has no per-trade tape -- confirmed
/// directly against real ticks (11 Sep 13:55-13:56): single reported volume deltas routinely
/// bundle thousands of lots that almost certainly traded across many individual prints, some of
/// which could have gone the other direction. This is a proxy, not true CVD, exactly like the
/// option-chain version it mirrors -- a cadence with a large classified delta but few
/// TicksObservedFuture is exactly where this proxy is least trustworthy. A tick with no
/// two-sided depth quote, or zero volume delta, contributes nothing (not a guessed zero) -- see
/// <see cref="CadenceNet"/>/<see cref="CumulativeNet"/>'s own null semantics.
///
/// 2026-09-17, Phase 3 unification: moved here from
/// `NiftySignal.BacktestData/CadencePopulator.cs` (unchanged logic) so
/// `NiftySignal.Host/LiveFeatureEngine.cs` can reference the SAME class instead of maintaining its
/// own hand-inlined "byte-for-byte port" of this exact ApplyTick formula -- the two had already
/// drifted apart in form (a real class here vs raw fields there) even though the arithmetic itself
/// had stayed in sync by careful hand-copying. This closes the FutureCvdNet5Min gap that Phase 2
/// (CoreScoreOptionSimulator.cs vs LiveFeatureEngine.cs) couldn't reach, since the real duplication
/// for this metric was always here, against CadencePopulator's ETL.
/// </summary>
public sealed class FutureCvdProxyAccumulator
{
    long _cadenceNet;
    int _cadenceContributingTicks;
    long _cumulativeNet;
    int _cumulativeContributingTicks;

    /// <summary>Net buy-minus-sell classified volume this cadence. Null if no tick this cadence had both a two-sided depth quote and nonzero volume to classify.</summary>
    public long? CadenceNet => _cadenceContributingTicks > 0 ? _cadenceNet : null;

    /// <summary>Running total for the whole trading day -- read by its slope, same convention real CVD indicators use, not smoothed with a rolling window.</summary>
    public long? CumulativeNet => _cumulativeContributingTicks > 0 ? _cumulativeNet : null;

    public void ApplyTick(decimal lastPrice, MarketDepth depth, long volumeDelta)
    {
        if (depth.Bid1Price <= 0 || depth.Ask1Price <= 0 || volumeDelta <= 0)
        {
            return;
        }

        var midpoint = (depth.Bid1Price + depth.Ask1Price) / 2m;
        var signed = lastPrice >= midpoint ? volumeDelta : -volumeDelta;

        _cadenceNet += signed;
        _cadenceContributingTicks++;
        _cumulativeNet += signed;
        _cumulativeContributingTicks++;
    }

    public void ResetCadence()
    {
        _cadenceNet = 0;
        _cadenceContributingTicks = 0;
    }
}
