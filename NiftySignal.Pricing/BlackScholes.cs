using NiftySignal.Domain.Enums;

namespace NiftySignal.Pricing;

public readonly record struct BlackScholesResult(double Price, OptionGreeks Greeks);

/// <summary>
/// Black-Scholes-Merton, European style -- correct for cash-settled Nifty index options
/// (plan section 4.1). Underlying is expected to be the put-call-parity synthetic forward
/// (see SyntheticForward.Compute -- both current call sites, CadencePopulator and
/// LiveFeatureEngine.ComputeUnderlyingPrice, feed that value, not raw spot or the tracked
/// future's own price), which already implicitly handles the dividend/carry adjustment for
/// index options; dividendYield defaults to 0 per the plan's documented simplification, kept
/// as a parameter rather than hardcoded in case that assumption is revisited. (2026-09-13:
/// corrected from an earlier version of this comment that said "the futures price" -- stale
/// and contradicted by both current call sites, flagged by external review.)
/// </summary>
public static class BlackScholes
{
    public static BlackScholesResult Calculate(
        OptionType optionType,
        double underlyingPrice,
        double strike,
        double timeToExpiryYears,
        double riskFreeRate,
        double volatility,
        double dividendYield = 0)
    {
        if (optionType == OptionType.None)
        {
            throw new ArgumentOutOfRangeException(nameof(optionType), "Cannot price a non-option instrument.");
        }

        if (underlyingPrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(underlyingPrice), underlyingPrice, "Must be positive.");
        }

        if (strike <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(strike), strike, "Must be positive.");
        }

        if (timeToExpiryYears <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToExpiryYears), timeToExpiryYears, "Must be positive -- floor at TimeToExpiry's minimum before calling this.");
        }

        if (volatility <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(volatility), volatility, "Must be positive.");
        }

        var sqrtT = Math.Sqrt(timeToExpiryYears);
        var d1 = (Math.Log(underlyingPrice / strike) + ((riskFreeRate - dividendYield + (0.5 * volatility * volatility)) * timeToExpiryYears))
                 / (volatility * sqrtT);
        var d2 = d1 - (volatility * sqrtT);

        var expNegQT = Math.Exp(-dividendYield * timeToExpiryYears);
        var expNegRT = Math.Exp(-riskFreeRate * timeToExpiryYears);
        var pdfD1 = NormalDistribution.Pdf(d1);

        var gamma = expNegQT * pdfD1 / (underlyingPrice * volatility * sqrtT);
        var vega = underlyingPrice * expNegQT * pdfD1 * sqrtT;
        var decayTerm = -(underlyingPrice * expNegQT * pdfD1 * volatility) / (2 * sqrtT);

        // d Delta/d sigma -- identical for call and put (they differ only by the constant
        // e^(-qT), whose sigma-derivative is zero), so no per-branch version needed.
        var vanna = -expNegQT * pdfD1 * d2 / volatility;

        // d Delta_call/d t (calendar time, i.e. -d Delta_call/d TimeToExpiry -- matches
        // ThetaPerDay's calendar-time convention below, not the textbook time-to-expiry one).
        // Delta_put = e^(-qT)(N(d1) - 1), a constant-in-T shift of Delta_call by -e^(-qT), so
        // Charm_put = Charm_call - q*e^(-qT); the two branches share this term and diverge by
        // exactly that constant, applied per option type further down.
        var nD1ForCharm = NormalDistribution.Cdf(d1);
        var charmCallAnnual = (dividendYield * expNegQT * nD1ForCharm)
            - (expNegQT * pdfD1 * (((2 * (riskFreeRate - dividendYield) * timeToExpiryYears) - (d2 * volatility * sqrtT)) / (2 * timeToExpiryYears * volatility * sqrtT)));

        double price, delta, thetaAnnual, rho;

        if (optionType == OptionType.Call)
        {
            var nD1 = NormalDistribution.Cdf(d1);
            var nD2 = NormalDistribution.Cdf(d2);

            price = (underlyingPrice * expNegQT * nD1) - (strike * expNegRT * nD2);
            delta = expNegQT * nD1;
            thetaAnnual = decayTerm - (riskFreeRate * strike * expNegRT * nD2) + (dividendYield * underlyingPrice * expNegQT * nD1);
            rho = strike * timeToExpiryYears * expNegRT * nD2;
        }
        else
        {
            var nD1 = NormalDistribution.Cdf(d1);
            var nMinusD1 = NormalDistribution.Cdf(-d1);
            var nMinusD2 = NormalDistribution.Cdf(-d2);

            price = (strike * expNegRT * nMinusD2) - (underlyingPrice * expNegQT * nMinusD1);
            delta = expNegQT * (nD1 - 1);
            thetaAnnual = decayTerm + (riskFreeRate * strike * expNegRT * nMinusD2) - (dividendYield * underlyingPrice * expNegQT * nMinusD1);
            rho = -strike * timeToExpiryYears * expNegRT * nMinusD2;
        }

        var charmAnnual = optionType == OptionType.Call ? charmCallAnnual : charmCallAnnual - (dividendYield * expNegQT);

        var greeks = new OptionGreeks(delta, gamma, thetaAnnual / 365.0, vega, rho, vanna, charmAnnual / 365.0);
        return new BlackScholesResult(price, greeks);
    }
}
