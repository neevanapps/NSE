using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.Pricing;

public class ImpliedVolatilitySolverTests
{
    const double Underlying = 42;
    const double Strike = 40;
    const double Rate = 0.10;
    const double Time = 0.5;

    [Theory]
    [InlineData(OptionType.Call, 0.20)]
    [InlineData(OptionType.Call, 0.05)]
    [InlineData(OptionType.Call, 1.50)]
    [InlineData(OptionType.Put, 0.20)]
    [InlineData(OptionType.Put, 0.45)]
    public void Solve_RoundTrip_PriceToIvToPrice_RecoversTheOriginalVolatility(OptionType optionType, double trueVolatility)
    {
        var price = BlackScholes.Calculate(optionType, Underlying, Strike, Time, Rate, trueVolatility).Price;

        var solvedVolatility = ImpliedVolatilitySolver.Solve(optionType, price, Underlying, Strike, Time, Rate);

        Assert.NotNull(solvedVolatility);
        Assert.Equal(trueVolatility, solvedVolatility.Value, 1e-4);
    }

    [Fact]
    public void Solve_RoundTrip_RecoveredVolatility_ReproducesTheOriginalPrice()
    {
        var truePrice = BlackScholes.Calculate(OptionType.Call, Underlying, Strike, Time, Rate, 0.30).Price;

        var solvedVolatility = ImpliedVolatilitySolver.Solve(OptionType.Call, truePrice, Underlying, Strike, Time, Rate);
        var reconstructedPrice = BlackScholes.Calculate(OptionType.Call, Underlying, Strike, Time, Rate, solvedVolatility!.Value).Price;

        Assert.Equal(truePrice, reconstructedPrice, ImpliedVolatilitySolver.Tolerance * 10);
    }

    [Fact]
    public void Solve_ReturnsNull_WhenPriceIsBelowIntrinsicValue()
    {
        // A deep ITM call priced below intrinsic value is not a valid option price at any
        // volatility -- must return null, never a garbage/negative implied vol.
        var intrinsic = Underlying - Strike;
        var impossiblePrice = intrinsic - 5;

        var result = ImpliedVolatilitySolver.Solve(OptionType.Call, impossiblePrice, Underlying, Strike, Time, Rate);

        Assert.Null(result);
    }

    [Fact]
    public void Solve_ReturnsNull_ForNonPositivePrice()
    {
        var result = ImpliedVolatilitySolver.Solve(OptionType.Call, marketPrice: 0, Underlying, Strike, Time, Rate);

        Assert.Null(result);
    }

    [Fact]
    public void Solve_ReturnsNull_WhenPriceExceedsWhatAnyReasonableVolatilityCouldProduce()
    {
        var absurdlyHighPrice = Underlying * 10;

        var result = ImpliedVolatilitySolver.Solve(OptionType.Call, absurdlyHighPrice, Underlying, Strike, Time, Rate);

        Assert.Null(result);
    }

    [Fact]
    public void Solve_NearExpiry_StillConverges_WithTimeFlooredByTimeToExpiry()
    {
        // Simulates expiry-afternoon conditions (plan section 1.3): T floored at 1 hour.
        // Deliberately ATM (strike == underlying), not the 5%-ITM Hull strike used
        // elsewhere in this file: at a floored T, a few percent of moneyness pushes d1 to
        // a huge value, price collapses onto intrinsic value almost independent of
        // volatility, and Vega goes essentially to zero -- price stops meaningfully
        // depending on sigma at all. That's not a solver bug, it's the exact numerical
        // instability plan section 1.3 warns about, and the correct behavior there is
        // returning null, not a precise (and meaningless) recovered volatility. ATM is
        // where near-expiry IV is still well-conditioned and actually measurable, so it's
        // the fair case to assert exact round-trip recovery against.
        var flooredTime = TimeToExpiry.Minimum.TotalDays / 365.0;
        const double atmStrike = Underlying;
        var price = BlackScholes.Calculate(OptionType.Call, Underlying, atmStrike, flooredTime, Rate, 0.25).Price;

        var result = ImpliedVolatilitySolver.Solve(OptionType.Call, price, Underlying, atmStrike, flooredTime, Rate);

        Assert.NotNull(result);
        Assert.Equal(0.25, result.Value, 1e-3);
    }

    [Fact]
    public void Solve_ReturnsNull_ForDeepItmNearExpiry_RatherThanAnUnreliableGuess()
    {
        // The companion case to the ATM test above: 5% moneyness (the Hull strike) at a
        // floored T is deep enough that price barely depends on sigma at all -- many
        // different volatilities satisfy the price-match tolerance equally well. Before
        // the Vega-floor guard was added, this returned a plausible-looking but essentially
        // arbitrary value (~1.03) instead of the true 0.25. Null is the honest answer.
        var flooredTime = TimeToExpiry.Minimum.TotalDays / 365.0;
        var price = BlackScholes.Calculate(OptionType.Call, Underlying, Strike, flooredTime, Rate, 0.25).Price;

        var result = ImpliedVolatilitySolver.Solve(OptionType.Call, price, Underlying, Strike, flooredTime, Rate);

        Assert.Null(result);
    }
}
