namespace NiftySignal.Scoring;

/// <summary>
/// Combines the six required weighted z-scores (plan section 6) plus one optional one
/// (VixChange, 2026-09-04) into the -100..+100 composite:
/// <c>raw = sum(weight_i * clipped_z_i)</c>, <c>score = 100 * tanh(raw / k)</c>.
/// </summary>
public static class CompositeScoreCalculator
{
    /// <summary>
    /// Starting point only -- the plan says "k tuned so typical range spans usefully" and
    /// explicitly flags weights/k as things to revisit after real data flows (section 15).
    /// At k=1, a fully-saturated raw (every z at +/-3, default weights) maps to a score of
    /// ~99.5; a more typical raw of ~1.0 maps to ~76.
    /// </summary>
    public const double DefaultK = 1.0;

    /// <summary>
    /// The one component that doesn't gate warm-up (2026-09-04) -- see ScoreComponentInputs'
    /// VixChangeZ doc comment for why. Excluded by name rather than restructuring the six
    /// into their own type, since this is a single, deliberate, one-off exception, not a
    /// general "optional components" mechanism.
    /// </summary>
    const string VixComponentName = "VixChange";

    /// <summary>
    /// The pre-tanh weighted z-sum on its own (2026-09-04), for callers that need to build a
    /// self-normalizing <c>k</c> from the raw value's own recent spread (see
    /// <c>LiveFeatureEngine</c>'s dynamic-k rolling window) without first having to pick a
    /// <c>k</c> just to compute the number <c>k</c> itself is derived from. Null under the
    /// same "not every required component warmed up yet" condition as
    /// <see cref="Calculate"/>'s Score.
    /// </summary>
    public static double? ComputeRaw(ScoreComponentInputs inputs, ScoreWeights weights) =>
        TryComputeRaw(BuildComponents(inputs, weights), out var raw) ? raw : null;

    public static CompositeScore Calculate(
        ScoreComponentInputs inputs, ScoreWeights weights, DateTimeOffset computedAt, double k = DefaultK)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must be positive.");
        }

        var components = BuildComponents(inputs, weights);
        var isWarmedUp = TryComputeRaw(components, out var raw);
        var score = isWarmedUp ? 100.0 * Math.Tanh(raw / k) : (double?)null;

        return new CompositeScore(score, isWarmedUp, components, weights.Version, computedAt);
    }

    /// <summary>
    /// True (with the weighted-z-sum in <paramref name="raw"/>) once every *required*
    /// component (everything except <see cref="VixComponentName"/>) has a z-score. VIX's own
    /// contribution is added when available and silently treated as 0 when not -- it never
    /// blocks the composite, and never prevents the other six from producing one.
    /// </summary>
    static bool TryComputeRaw(List<ScoreComponentBreakdown> components, out double raw)
    {
        var required = components.Where(c => c.Name != VixComponentName).ToList();
        if (!required.All(c => c.ZScore is not null))
        {
            raw = 0;
            return false;
        }

        var vixContribution = components.FirstOrDefault(c => c.Name == VixComponentName)?.WeightedContribution ?? 0.0;
        raw = required.Sum(c => c.WeightedContribution!.Value) + vixContribution;
        return true;
    }

    static List<ScoreComponentBreakdown> BuildComponents(ScoreComponentInputs inputs, ScoreWeights weights) =>
    [
        BuildComponent("OiBuildupNet", weights.OiBuildupNet, inputs.OiBuildupNetZ),
        BuildComponent("Pcr", weights.Pcr, inputs.PcrZ),
        BuildComponent("FuturesBasis", weights.FuturesBasis, inputs.FuturesBasisZ),
        BuildComponent("IvSkew", weights.IvSkew, inputs.IvSkewZ),
        BuildComponent("PriceMomentum", weights.PriceMomentum, inputs.PriceMomentumZ),
        BuildComponent("DepthImbalance", weights.DepthImbalance, inputs.DepthImbalanceZ),
        BuildComponent(VixComponentName, weights.VixChange, inputs.VixChangeZ),
    ];

    static ScoreComponentBreakdown BuildComponent(string name, double weight, double? z)
    {
        if (z is null)
        {
            return new ScoreComponentBreakdown(name, weight, null, null);
        }

        // Defense in depth: callers are expected to pass an already-clipped z from
        // WelfordRollingWindow.ComputeZScore, but re-clipping here costs nothing and means
        // this method is safe by construction even if that precondition is ever violated.
        var clipped = Math.Clamp(z.Value, -3.0, 3.0);
        return new ScoreComponentBreakdown(name, weight, clipped, weight * clipped);
    }
}
