namespace NiftySignal.Pricing;

/// <summary>
/// The underlying implied by the market's own option prices via put-call parity
/// (S = K*e^(-rT) + (C - P), matching this codebase's own BlackScholes.Calculate convention --
/// see <see cref="Compute"/>'s own comment), replacing raw spot as the Black-Scholes underlying
/// for every IV/Greeks calculation in the system (2026-09-07).
///
/// Why this exists: neither spot nor the tracked future is the right underlying for a weekly
/// option. Spot carries no cost-of-carry adjustment at all; the tracked future is a monthly
/// contract, weeks past a weekly option's own expiry (using it was the original 2026-09-04
/// bug -- overstated the underlying by the full month-vs-week carry gap). The fix at that time
/// (switch to spot) was directionally right but incomplete: understating the true forward
/// biases a call IV solve up and a put IV solve down by roughly equal and opposite amounts,
/// which is exactly what a same-day live check found (2026-09-07): put IV ran ~7-8 vol points
/// below call IV at the same strike, systematically, at every strike, all day -- a signature
/// of a too-low underlying, not genuine market skew (skew shows up across strikes, not as a
/// same-strike call/put split). A synthetic forward derived directly from the market's own
/// quoted option prices needs no dividend-yield assumption and no futures contract at all, and
/// makes call/put IV agree at each strike by construction, since it's derived from the same
/// relationship a wrong underlying was violating.
/// </summary>
public static class SyntheticForward
{
    /// <summary>
    /// Median of the per-strike parity estimates (not a mean) -- robust to any single strike's
    /// quote noise or wide spread without needing to pick "the best" strike. Null when no pair
    /// is supplied; callers should fall back to spot in that case (e.g. before both legs of any
    /// near strike have quoted yet) rather than block the calculation entirely.
    /// </summary>
    public static double? Compute(IReadOnlyList<(double Strike, double CallMid, double PutMid)> pairs, double timeToExpiryYears, double riskFreeRate)
    {
        if (pairs.Count == 0)
        {
            return null;
        }

        // Put-call parity for this codebase's own BlackScholes.Calculate convention
        // (dividendYield defaults to 0): C - P = S - K*e^(-rT), so S = K*e^(-rT) + (C - P).
        // Not S = K + (C-P)*e^(rT) -- that solves for a *different* quantity (the forward
        // F = S*e^(rT) implied by this S), which is a few points off S itself even at a few
        // days to expiry and would reintroduce a small version of the same systematic-bias
        // bug this class exists to fix, since S (not F) is what every call site here feeds
        // back into BlackScholes.Calculate/ImpliedVolatilitySolver.Solve as underlyingPrice.
        var presentValueFactor = Math.Exp(-riskFreeRate * timeToExpiryYears);
        var estimates = pairs.Select(p => (p.Strike * presentValueFactor) + (p.CallMid - p.PutMid)).OrderBy(f => f).ToList();

        var mid = estimates.Count / 2;
        return estimates.Count % 2 == 0 ? (estimates[mid - 1] + estimates[mid]) / 2.0 : estimates[mid];
    }
}
