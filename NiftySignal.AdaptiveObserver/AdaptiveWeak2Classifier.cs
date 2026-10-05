namespace NiftySignal.AdaptiveObserver;

public static class AdaptiveWeak2Classifier
{
    public const double StrongQuantile = 2d / 3d;

    /// <summary>Linear-interpolated quantile, matching the discovery Python implementation.</summary>
    public static double Quantile(IEnumerable<double> values, double p)
    {
        if (p is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(p));
        var xs = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (xs.Length == 0) throw new InvalidOperationException("Cannot compute a quantile over an empty sequence.");
        if (xs.Length == 1) return xs[0];

        var z = (xs.Length - 1) * p;
        var lo = (int)Math.Floor(z);
        var hi = (int)Math.Ceiling(z);
        if (lo == hi) return xs[lo];
        var w = z - lo;
        return xs[lo] * (1d - w) + xs[hi] * w;
    }

    public static double ComputeStrongThreshold(IEnumerable<AdaptiveRollingState> priorSessionStates) =>
        Quantile(priorSessionStates.Select(x => Math.Abs(x.StrictDeltaRatioTotal)), StrongQuantile);

    /// <summary>
    /// Adds Strong/Weak1/Weak2 labels to already-built flow states using one frozen daily strong threshold.
    /// This is descriptive state; it does not place trades.
    /// </summary>
    public static void Apply(IReadOnlyList<AdaptiveFlowState> states, double strongThreshold)
    {
        AdaptiveFlowState? strongBase = null;
        AdaptiveFlowState? weak1 = null;

        foreach (var row in states.OrderBy(x => x.Bar.BarSeq))
        {
            row.IsStrong = IsStrong(row, strongThreshold);
            row.WeakeningSequence = 0;
            row.State = row.IsStrong ? AdaptiveStateKind.Strong : AdaptiveStateKind.Normal;

            var rolling = row.Rolling;
            if (rolling is null)
            {
                strongBase = null;
                weak1 = null;
                continue;
            }

            if (row.IsStrong)
            {
                strongBase = row;
                weak1 = null;
                continue;
            }

            if (strongBase?.Rolling is not { } baseRolling)
            {
                continue;
            }

            var sameDirection =
                rolling.StrictDeltaDirection == baseRolling.StrictDeltaDirection
                && rolling.PriceDirection == baseRolling.PriceDirection
                && rolling.StrictDeltaDirection == rolling.PriceDirection
                && rolling.StrictDeltaDirection != 0;

            if (!sameDirection || row.StrictDominanceEvolution != "Weakening")
            {
                strongBase = null;
                weak1 = null;
                continue;
            }

            if (weak1 is null)
            {
                row.WeakeningSequence = 1;
                row.State = AdaptiveStateKind.Weak1;
                weak1 = row;
                continue;
            }

            if (weak1.Rolling is { } weak1Rolling
                && weak1Rolling.StrictDeltaDirection == rolling.StrictDeltaDirection
                && weak1Rolling.PriceDirection == rolling.PriceDirection)
            {
                row.WeakeningSequence = 2;
                row.State = AdaptiveStateKind.Weak2;

                // Frozen definition is a three-state episode. Do not allow a third weakening row
                // to become another overlapping Weak2 off the same strong base.
                strongBase = null;
                weak1 = null;
            }
            else
            {
                strongBase = null;
                weak1 = null;
            }
        }
    }

    public static bool IsStrong(AdaptiveFlowState row, double strongThreshold) =>
        row.Rolling is { } s
        && Math.Abs(s.StrictDeltaRatioTotal) >= strongThreshold
        && s.PriceDirection != 0
        && s.PriceDirection == s.StrictDeltaDirection;
}
