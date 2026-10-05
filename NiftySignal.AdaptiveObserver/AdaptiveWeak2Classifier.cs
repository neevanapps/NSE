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
        var ordered = states.OrderBy(x => x.Bar.BarSeq).ToArray();

        // First classify Strong independently. Discovery did not "consume" a strong state merely
        // because it also participated as Weak1/Weak2 in an earlier overlapping triplet.
        foreach (var row in ordered)
        {
            row.IsStrong = IsStrong(row, strongThreshold);
            row.WeakeningSequence = 0;
            row.StrongBaseBarSeq = row.IsStrong ? row.Bar.BarSeq : null;
            row.Weak1BarSeq = null;
            row.State = row.IsStrong ? AdaptiveStateKind.Strong : AdaptiveStateKind.Normal;
        }

        // Weak1: immediately follows an independently-strong, direction-aligned base.
        for (var i = 1; i < ordered.Length; i++)
        {
            var baseRow = ordered[i - 1];
            var current = ordered[i];
            if (!baseRow.IsStrong
                || baseRow.Rolling is not { } b
                || current.Rolling is not { } r
                || current.StrictDominanceEvolution != "Weakening"
                || !SameOriginalDirection(b, r))
            {
                continue;
            }

            current.WeakeningSequence = 1;
            current.StrongBaseBarSeq = baseRow.Bar.BarSeq;
            current.State = AdaptiveStateKind.Weak1;
        }

        // Weak2: frozen research definition evaluated directly over [t-2,t-1,t]. This permits
        // overlapping episodes exactly like the discovery script.
        for (var i = 2; i < ordered.Length; i++)
        {
            var baseRow = ordered[i - 2];
            var weak1 = ordered[i - 1];
            var current = ordered[i];

            if (!baseRow.IsStrong
                || baseRow.Rolling is not { } b
                || weak1.Rolling is not { } w1
                || current.Rolling is not { } r
                || weak1.StrictDominanceEvolution != "Weakening"
                || current.StrictDominanceEvolution != "Weakening"
                || !SameOriginalDirection(b, w1)
                || !SameOriginalDirection(b, r)
                || w1.StrictDeltaDirection != r.StrictDeltaDirection
                || w1.PriceDirection != r.PriceDirection)
            {
                continue;
            }

            current.WeakeningSequence = 2;
            current.StrongBaseBarSeq = baseRow.Bar.BarSeq;
            current.Weak1BarSeq = weak1.Bar.BarSeq;
            current.State = AdaptiveStateKind.Weak2;
        }
    }

    static bool SameOriginalDirection(AdaptiveRollingState original, AdaptiveRollingState candidate) =>
        original.StrictDeltaDirection != 0
        && original.PriceDirection == original.StrictDeltaDirection
        && candidate.StrictDeltaDirection == original.StrictDeltaDirection
        && candidate.PriceDirection == original.PriceDirection
        && candidate.StrictDeltaDirection == candidate.PriceDirection;

    public static bool IsStrong(AdaptiveFlowState row, double strongThreshold) =>
        row.Rolling is { } s
        && Math.Abs(s.StrictDeltaRatioTotal) >= strongThreshold
        && s.PriceDirection != 0
        && s.PriceDirection == s.StrictDeltaDirection;
}
