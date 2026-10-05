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
            row.StrongBaseBarSeq = row.IsStrong ? row.Bar.BarSeq : null;
            row.Weak1BarSeq = null;
            row.State = row.IsStrong ? AdaptiveStateKind.Strong : AdaptiveStateKind.Normal;

            var rolling = row.Rolling;
            if (rolling is null)
            {
                strongBase = null;
                weak1 = null;
                continue;
            }

            // IMPORTANT: evaluate an already-open strong -> weakening episode BEFORE deciding
            // that the current row can itself become a new strong base. In the frozen discovery
            // definition Weak1/Weak2 are allowed to remain above the strong threshold.
            if (strongBase?.Rolling is { } baseRolling)
            {
                var sameDirection =
                    rolling.StrictDeltaDirection == baseRolling.StrictDeltaDirection
                    && rolling.PriceDirection == baseRolling.PriceDirection
                    && rolling.StrictDeltaDirection == rolling.PriceDirection
                    && rolling.StrictDeltaDirection != 0;

                if (sameDirection && row.StrictDominanceEvolution == "Weakening")
                {
                    if (weak1 is null)
                    {
                        row.WeakeningSequence = 1;
                        row.StrongBaseBarSeq = strongBase.Bar.BarSeq;
                        row.State = AdaptiveStateKind.Weak1;
                        weak1 = row;
                        continue;
                    }

                    if (weak1.Rolling is { } weak1Rolling
                        && weak1Rolling.StrictDeltaDirection == rolling.StrictDeltaDirection
                        && weak1Rolling.PriceDirection == rolling.PriceDirection)
                    {
                        row.WeakeningSequence = 2;
                        row.StrongBaseBarSeq = strongBase.Bar.BarSeq;
                        row.Weak1BarSeq = weak1.Bar.BarSeq;
                        row.State = AdaptiveStateKind.Weak2;
                        strongBase = null;
                        weak1 = null;
                        continue;
                    }
                }

                // Episode broke. The current row is still eligible to seed a NEW episode if it
                // independently qualifies as strong.
                strongBase = null;
                weak1 = null;
            }

            if (row.IsStrong)
            {
                strongBase = row;
            }
        }
    }

    public static bool IsStrong(AdaptiveFlowState row, double strongThreshold) =>
        row.Rolling is { } s
        && Math.Abs(s.StrictDeltaRatioTotal) >= strongThreshold
        && s.PriceDirection != 0
        && s.PriceDirection == s.StrictDeltaDirection;
}
