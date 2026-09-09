using NiftySignal.Domain.Enums;

namespace NiftySignal.Pricing;

/// <summary>
/// The shared "solve one reference IV from an ATM strike's own legs, call preferred, put as
/// fallback" logic (audit finding F33, fixed 2026-09-09) -- previously implemented twice,
/// structurally parallel but independent: LiveFeatureEngine.SolveAtmReferenceVol (live scoring,
/// single expiry, iterates its own tick state) and LiveDataService.BuildAtmReferenceVol
/// (dashboard display, every tracked expiry, iterates a different tick-state shape). The two
/// currently agreed, but nothing enforced that -- the next fix to one's fallback/solve logic
/// would have had to be remembered and manually re-applied to the other, or the dashboard would
/// quietly start showing different numbers than what's actually driving trades.
///
/// Deliberately narrow: only the fallback-and-solve step is shared. Each call site still owns
/// its own iteration and mark-price choice (live prefers bid/ask mid for stability, matching
/// this codebase's general live-quote handling; the dashboard uses LTP) -- those are legitimate,
/// pre-existing differences in what each caller is trying to show, not part of what needed
/// sharing to eliminate a divergence risk.
/// </summary>
public static class AtmReferenceVolSolver
{
    /// <summary>One leg's own mark price, in the order the caller wants legs tried.</summary>
    public readonly record struct Leg(OptionType OptionType, decimal MarkPrice);

    /// <summary>
    /// Tries each leg in <paramref name="legsInPreferenceOrder"/> until one produces a solved
    /// IV; returns null if every leg fails (illiquid/stale quotes on both sides). A leg with a
    /// non-positive mark price is skipped without even attempting a solve -- ImpliedVolatilitySolver.
    /// Solve already returns null for that case, but skipping it here avoids the wasted solver
    /// call across however many legs are offered.
    /// </summary>
    public static double? Solve(
        IEnumerable<Leg> legsInPreferenceOrder,
        decimal strike,
        decimal underlyingPrice,
        double timeToExpiryYears,
        double riskFreeRate)
    {
        foreach (var leg in legsInPreferenceOrder)
        {
            if (leg.MarkPrice <= 0)
            {
                continue;
            }

            var solved = ImpliedVolatilitySolver.Solve(
                leg.OptionType, (double)leg.MarkPrice, (double)underlyingPrice, (double)strike, timeToExpiryYears, riskFreeRate);
            if (solved is { } vol)
            {
                return vol;
            }
        }

        return null;
    }
}
