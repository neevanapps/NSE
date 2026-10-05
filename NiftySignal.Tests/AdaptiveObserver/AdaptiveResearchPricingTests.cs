using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveResearchPricingTests
{
    // Independent Python math.erf values from the frozen reference formula.
    [Theory]
    [InlineData(0d,.5)]
    [InlineData(.1,.539827837277029)]
    [InlineData(1d,.8413447460685429)]
    [InlineData(2d,.9772498680518208)]
    [InlineData(3d,.9986501019683699)]
    [InlineData(6d,.9999999990134123)]
    [InlineData(8d,.9999999999999993)]
    [InlineData(-8d,6.106226635438361e-16)]
    public void Gaussian_MatchesIndependentDoublePrecisionReference(double x,double expected)
        => Assert.InRange(Math.Abs(AdaptiveResearchPricing.NormalCdf(x)-expected),0,2e-15);

    [Theory]
    [InlineData(OptionType.Call)]
    [InlineData(OptionType.Put)]
    public void Iv_BracketAndVegaFailuresReturnUnavailable(OptionType side)
    {
        Assert.Null(AdaptiveResearchPricing.SolveIv(side,double.NaN,23000,23000,.01,.065));
        Assert.Null(AdaptiveResearchPricing.SolveIv(side,100000,23000,23000,.01,.065));
        // Expiry-degenerate intrinsic price: no reliable volatility information remains.
        var intrinsic=AdaptiveResearchPricing.Price(side,side==OptionType.Call?50000:1000,23000,120d/(365*86400),.065,.2);
        Assert.Null(AdaptiveResearchPricing.SolveIv(side,intrinsic,side==OptionType.Call?50000:1000,23000,120d/(365*86400),.065));
    }
}
