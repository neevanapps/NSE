using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.Pricing;

public class SyntheticForwardTests
{
    const double Rate = 0.065;
    const double Time = 4.0 / 365.0; // ~4 days, a realistic weekly-option horizon

    [Fact]
    public void Compute_ReturnsNull_WhenNoPairsSupplied()
    {
        var result = SyntheticForward.Compute([], Time, Rate);

        Assert.Null(result);
    }

    [Fact]
    public void Compute_RecoversTheTrueUnderlying_FromAConsistentSinglePair()
    {
        // Prices built via this codebase's own BlackScholes.Calculate at a known underlying --
        // the whole point of the parity formula is to recover exactly that value back out,
        // regardless of what volatility was used to build the prices (put-call parity makes
        // C-P independent of sigma entirely).
        const double trueUnderlying = 24080.0;
        const double strike = 23950.0;
        var call = BlackScholes.Calculate(OptionType.Call, trueUnderlying, strike, Time, Rate, 0.15).Price;
        var put = BlackScholes.Calculate(OptionType.Put, trueUnderlying, strike, Time, Rate, 0.15).Price;

        var result = SyntheticForward.Compute([(strike, call, put)], Time, Rate);

        Assert.NotNull(result);
        Assert.Equal(trueUnderlying, result!.Value, precision: 6);
    }

    [Fact]
    public void Compute_RecoversTheSameUnderlying_RegardlessOfWhichVolatilityPricedTheOptions()
    {
        // Same true underlying, two completely different strikes each priced at their own
        // (different) volatility -- both must still recover the same underlying, since C-P
        // never depends on sigma. This is the property that makes call/put IV agree once this
        // underlying feeds back into the solver: see LiveFeatureEngine.ComputeUnderlyingPrice.
        const double trueUnderlying = 24080.0;
        var lowVolCall = BlackScholes.Calculate(OptionType.Call, trueUnderlying, 23950, Time, Rate, 0.10).Price;
        var lowVolPut = BlackScholes.Calculate(OptionType.Put, trueUnderlying, 23950, Time, Rate, 0.10).Price;
        var highVolCall = BlackScholes.Calculate(OptionType.Call, trueUnderlying, 24200, Time, Rate, 0.35).Price;
        var highVolPut = BlackScholes.Calculate(OptionType.Put, trueUnderlying, 24200, Time, Rate, 0.35).Price;

        var result = SyntheticForward.Compute([(23950, lowVolCall, lowVolPut), (24200, highVolCall, highVolPut)], Time, Rate);

        Assert.NotNull(result);
        Assert.Equal(trueUnderlying, result!.Value, precision: 6);
    }

    [Fact]
    public void Compute_UsesTheMedian_SoOneNoisyStrikeDoesNotDominate()
    {
        // Three strikes agreeing on ~24080, one wildly inconsistent outlier (e.g. a stale or
        // crossed quote) -- the median should land on the agreeing cluster, not get dragged
        // toward the outlier the way an average would.
        const double trueUnderlying = 24080.0;
        var call1 = BlackScholes.Calculate(OptionType.Call, trueUnderlying, 23950, Time, Rate, 0.15).Price;
        var put1 = BlackScholes.Calculate(OptionType.Put, trueUnderlying, 23950, Time, Rate, 0.15).Price;
        var call2 = BlackScholes.Calculate(OptionType.Call, trueUnderlying, 24000, Time, Rate, 0.15).Price;
        var put2 = BlackScholes.Calculate(OptionType.Put, trueUnderlying, 24000, Time, Rate, 0.15).Price;
        var call3 = BlackScholes.Calculate(OptionType.Call, trueUnderlying, 24050, Time, Rate, 0.15).Price;
        var put3 = BlackScholes.Calculate(OptionType.Put, trueUnderlying, 24050, Time, Rate, 0.15).Price;

        var pairs = new List<(double, double, double)>
        {
            (23950, call1, put1),
            (24000, call2, put2),
            (24050, call3, put3),
            (24500, 1.0, 500.0), // outlier: an obviously crossed/stale quote implying underlying ~1000pts away
        };

        var result = SyntheticForward.Compute(pairs, Time, Rate);

        Assert.NotNull(result);
        // Median of the four estimates (three near 24080, one wild outlier) is one of the
        // three agreeing ones -- stays close to the true value, not dragged toward the outlier.
        Assert.Equal(trueUnderlying, result!.Value, precision: 0);
    }

    [Fact]
    public void Compute_AveragesTheTwoMiddleEstimates_WhenCountIsEven()
    {
        var result = SyntheticForward.Compute([(100.0, 10.0, 5.0), (100.0, 10.0, 6.0)], 0.01, Rate);

        // Both pairs share the same strike/PV factor, differing only in put price (5 vs 6) --
        // estimates are (100*pv+5) and (100*pv+4); the "median" of two values is their mean.
        var pv = Math.Exp(-Rate * 0.01);
        var expected = (((100.0 * pv) + 5.0) + ((100.0 * pv) + 4.0)) / 2.0;

        Assert.NotNull(result);
        Assert.Equal(expected, result!.Value, precision: 9);
    }
}
