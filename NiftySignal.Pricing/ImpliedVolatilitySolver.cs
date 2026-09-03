using NiftySignal.Domain.Enums;

namespace NiftySignal.Pricing;

/// <summary>
/// Newton-Raphson seeded with the Brenner-Subrahmanyam approximation, falling back to
/// bisection if Newton fails to converge (plan section 4.2). Returns null rather than a
/// fabricated value whenever the solve genuinely fails -- callers (feature computation
/// downstream) must treat null as "skip this component and renormalize weights," never as
/// zero, since zero is itself a meaningful IV value.
/// </summary>
public static class ImpliedVolatilitySolver
{
    public const int MaxIterations = 50;
    public const double Tolerance = 1e-6;

    const double MinVolatility = 1e-4;
    const double MaxVolatility = 5.0; // 500% -- generous upper bound for bisection bracketing

    // Absolute floor on dPrice/dSigma at the candidate solution. Deep ITM/OTM very close
    // to expiry (T floored per plan section 1.3) is where this matters: d1 grows huge, the
    // price collapses onto intrinsic value almost independent of volatility, and a wide
    // range of sigmas can satisfy the price-match Tolerance below -- "converged" but not
    // unique or meaningful. A near-zero Vega at the candidate is the signal that happened,
    // and the honest answer at that point is null, not whichever sigma the solver landed on.
    const double MinReliableVega = 1e-4;

    public static double? Solve(
        OptionType optionType,
        double marketPrice,
        double underlyingPrice,
        double strike,
        double timeToExpiryYears,
        double riskFreeRate)
    {
        if (marketPrice <= 0)
        {
            return null;
        }

        var seed = BrennerSubrahmanyamSeed(marketPrice, underlyingPrice, timeToExpiryYears);

        var candidate = TryNewtonRaphson(optionType, marketPrice, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, seed)
            ?? TryBisection(optionType, marketPrice, underlyingPrice, strike, timeToExpiryYears, riskFreeRate);

        if (candidate is null)
        {
            return null;
        }

        var vegaAtCandidate = BlackScholes.Calculate(optionType, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, candidate.Value).Greeks.Vega;
        return vegaAtCandidate < MinReliableVega ? null : candidate;
    }

    static double BrennerSubrahmanyamSeed(double marketPrice, double underlyingPrice, double timeToExpiryYears)
    {
        // Brenner & Subrahmanyam (1988): a crude ATM-centric approximation, used only as a
        // Newton-Raphson starting point, never returned directly.
        var seed = (marketPrice / underlyingPrice) * Math.Sqrt(2 * Math.PI / timeToExpiryYears);
        return Math.Clamp(seed, MinVolatility, MaxVolatility);
    }

    static double? TryNewtonRaphson(
        OptionType optionType, double marketPrice, double underlyingPrice, double strike,
        double timeToExpiryYears, double riskFreeRate, double seed)
    {
        var sigma = seed;

        for (var i = 0; i < MaxIterations; i++)
        {
            var result = BlackScholes.Calculate(optionType, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, sigma);
            var diff = result.Price - marketPrice;

            if (Math.Abs(diff) < Tolerance)
            {
                return sigma;
            }

            // Vega collapsing toward zero (deep ITM/OTM, or T pinned at its floor near
            // expiry -- plan section 1.3) makes the Newton step unreliable; bail to
            // bisection rather than risk a wild, wrong jump.
            if (result.Greeks.Vega < 1e-8)
            {
                return null;
            }

            var next = sigma - (diff / result.Greeks.Vega);
            if (next <= 0 || double.IsNaN(next) || double.IsInfinity(next))
            {
                return null;
            }

            sigma = next;
        }

        return null; // did not converge within MaxIterations
    }

    static double? TryBisection(
        OptionType optionType, double marketPrice, double underlyingPrice, double strike,
        double timeToExpiryYears, double riskFreeRate)
    {
        var lo = MinVolatility;
        var hi = MaxVolatility;

        var priceAtLo = BlackScholes.Calculate(optionType, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, lo).Price;
        var priceAtHi = BlackScholes.Calculate(optionType, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, hi).Price;

        // Price is monotonically increasing in sigma (Vega >= 0 always), so marketPrice
        // must lie inside [priceAtLo, priceAtHi] for a root to exist. Outside that range
        // means the input price itself is bad (e.g. below intrinsic value), not that the
        // solver needs more iterations -- returning null here is correct, not a cop-out.
        if (marketPrice < priceAtLo || marketPrice > priceAtHi)
        {
            return null;
        }

        for (var i = 0; i < MaxIterations; i++)
        {
            var mid = (lo + hi) / 2;
            var price = BlackScholes.Calculate(optionType, underlyingPrice, strike, timeToExpiryYears, riskFreeRate, mid).Price;

            if (Math.Abs(price - marketPrice) < Tolerance)
            {
                return mid;
            }

            if (price < marketPrice)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return null;
    }
}
