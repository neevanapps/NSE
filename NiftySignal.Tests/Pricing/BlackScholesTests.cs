using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.Pricing;

public class BlackScholesTests
{
    // Hull, "Options, Futures, and Other Derivatives": S=42, K=40, r=10%, sigma=20%,
    // T=0.5y, q=0 -- a widely-cited textbook example, call price ~= 4.76.
    const double HullUnderlying = 42;
    const double HullStrike = 40;
    const double HullRate = 0.10;
    const double HullVolatility = 0.20;
    const double HullTime = 0.5;

    [Fact]
    public void Calculate_Call_MatchesKnownTextbookReferencePrice()
    {
        var result = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(4.76, result.Price, 0.01);
    }

    [Fact]
    public void Calculate_Put_MatchesKnownTextbookReferencePrice()
    {
        var result = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(0.81, result.Price, 0.01);
    }

    [Fact]
    public void Calculate_PutCallParity_HoldsToWithinFloatingPointTolerance()
    {
        // C - P = S*e^(-qT) - K*e^(-rT), an exact identity any correct implementation
        // must satisfy -- a stronger check than matching a rounded textbook decimal.
        var call = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);
        var put = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        var expected = HullUnderlying - (HullStrike * Math.Exp(-HullRate * HullTime));

        Assert.Equal(expected, call.Price - put.Price, 1e-9);
    }

    [Fact]
    public void Calculate_CallDelta_ApproachesOne_DeepInTheMoney()
    {
        var result = BlackScholes.Calculate(OptionType.Call, underlyingPrice: 500, strike: 100, HullTime, HullRate, HullVolatility);

        Assert.True(result.Greeks.Delta > 0.99);
    }

    [Fact]
    public void Calculate_CallDelta_ApproachesZero_DeepOutOfTheMoney()
    {
        var result = BlackScholes.Calculate(OptionType.Call, underlyingPrice: 100, strike: 500, HullTime, HullRate, HullVolatility);

        Assert.True(result.Greeks.Delta < 0.01);
    }

    [Fact]
    public void Calculate_PutDelta_IsCallDeltaMinusOne_AtTheSameStrike()
    {
        // Delta_call - Delta_put = e^(-qT), which is 1 when q=0 -- another exact identity.
        var call = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);
        var put = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(1.0, call.Greeks.Delta - put.Greeks.Delta, 1e-9);
    }

    [Fact]
    public void Calculate_Vega_IsSameForCallAndPut_AtTheSameStrike()
    {
        var call = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);
        var put = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(call.Greeks.Vega, put.Greeks.Vega, 1e-9);
    }

    [Fact]
    public void Calculate_Vega_IsPositive()
    {
        var result = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.True(result.Greeks.Vega > 0);
    }

    [Fact]
    public void Calculate_Gamma_IsSameForCallAndPut_AtTheSameStrike()
    {
        var call = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);
        var put = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(call.Greeks.Gamma, put.Greeks.Gamma, 1e-9);
    }

    [Fact]
    public void Calculate_Gamma_IsPositive()
    {
        var result = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.True(result.Greeks.Gamma > 0);
    }

    [Fact]
    public void Calculate_Vanna_MatchesFiniteDifferenceOfDeltaAgainstVolatility()
    {
        // Closed-form Vanna is a hand derivation -- verify it against the numerical derivative
        // of the already-correct (textbook-matched) Delta rather than trusting the algebra.
        const double epsilon = 1e-4;
        var up = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility + epsilon);
        var down = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility - epsilon);
        var expectedVanna = (up.Greeks.Delta - down.Greeks.Delta) / (2 * epsilon);

        var result = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(expectedVanna, result.Greeks.Vanna, 1e-4);
    }

    [Fact]
    public void Calculate_Vanna_IsSameForCallAndPut_AtTheSameStrike()
    {
        // Delta_call - Delta_put = e^(-qT), a constant w.r.t. sigma, so their sigma-derivatives
        // must be identical -- same identity Calculate_Vega_IsSameForCallAndPut relies on.
        var call = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);
        var put = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(call.Greeks.Vanna, put.Greeks.Vanna, 1e-9);
    }

    [Fact]
    public void Calculate_Charm_MatchesFiniteDifferenceOfDeltaAgainstCalendarTime()
    {
        // CharmPerDay is d Delta/d t (calendar time) -- as t increases, TimeToExpiry falls, so
        // the finite difference bumps TimeToExpiry the opposite way from t and divides by the
        // per-day step, not the per-year one.
        const double dayInYears = 1.0 / 365.0;
        const double epsilon = 1e-5;
        var later = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime - (dayInYears * epsilon), HullRate, HullVolatility);
        var earlier = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime + (dayInYears * epsilon), HullRate, HullVolatility);
        var expectedCharmPerDay = (later.Greeks.Delta - earlier.Greeks.Delta) / (2 * epsilon);

        var result = BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility);

        Assert.Equal(expectedCharmPerDay, result.Greeks.CharmPerDay, 1e-4);
    }

    [Fact]
    public void Calculate_Charm_MatchesFiniteDifference_WithNonZeroDividendYield()
    {
        // The dividend-yield terms are the part of the Charm derivation most likely to have a
        // sign error, and every call site in this codebase uses q=0 -- so the q=0 tests above
        // would stay green even if this branch were wrong. Exercise it directly.
        const double dividendYield = 0.02;
        const double dayInYears = 1.0 / 365.0;
        const double epsilon = 1e-5;
        var later = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime - (dayInYears * epsilon), HullRate, HullVolatility, dividendYield);
        var earlier = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime + (dayInYears * epsilon), HullRate, HullVolatility, dividendYield);
        var expectedCharmPerDay = (later.Greeks.Delta - earlier.Greeks.Delta) / (2 * epsilon);

        var result = BlackScholes.Calculate(OptionType.Put, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility, dividendYield);

        Assert.Equal(expectedCharmPerDay, result.Greeks.CharmPerDay, 1e-4);
    }

    [Fact]
    public void Calculate_ThrowsForNonOptionInstrumentType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlackScholes.Calculate(OptionType.None, HullUnderlying, HullStrike, HullTime, HullRate, HullVolatility));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_ThrowsForNonPositiveUnderlyingPrice(double underlyingPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlackScholes.Calculate(OptionType.Call, underlyingPrice, HullStrike, HullTime, HullRate, HullVolatility));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_ThrowsForNonPositiveTimeToExpiry(double timeToExpiryYears)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlackScholes.Calculate(OptionType.Call, HullUnderlying, HullStrike, timeToExpiryYears, HullRate, HullVolatility));
    }
}
