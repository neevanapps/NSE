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
    /// The components that don't gate warm-up -- see ScoreComponentInputs' VixChangeZ and
    /// GammaExposureZ doc comments for why. Excluded by name rather than restructuring the
    /// six required ones into their own type, since these are deliberate, individually-named
    /// exceptions, not a general "optional components" mechanism.
    /// </summary>
    const string VixComponentName = "VixChange";
    const string GammaExposureComponentName = "GammaExposure";
    const string VolumePcrComponentName = "VolumePcr";
    const string SpreadRatioComponentName = "SpreadRatio";
    static readonly string[] OptionalComponentNames = [VixComponentName, GammaExposureComponentName, VolumePcrComponentName, SpreadRatioComponentName];

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

    /// <param name="rawOverride">
    /// Substitutes a caller-supplied raw value (2026-09-07, e.g. LiveFeatureEngine's
    /// multi-cadence smoothed raw) for the tanh input, instead of the single-cadence raw this
    /// method would otherwise compute from <paramref name="inputs"/>. Warm-up still comes from
    /// <paramref name="inputs"/> itself -- an override doesn't manufacture a score out of
    /// components that genuinely aren't ready this cadence. Null (the default) reproduces the
    /// original single-cadence behavior exactly.
    /// </param>
    public static CompositeScore Calculate(
        ScoreComponentInputs inputs, ScoreWeights weights, DateTimeOffset computedAt, double k = DefaultK, double? rawOverride = null)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must be positive.");
        }

        var components = BuildComponents(inputs, weights);
        var isWarmedUp = TryComputeRaw(components, out var raw);
        var effectiveRaw = rawOverride ?? raw;
        var score = isWarmedUp ? 100.0 * Math.Tanh(effectiveRaw / k) : (double?)null;

        return new CompositeScore(score, isWarmedUp, components, weights.Version, computedAt);
    }

    /// <summary>
    /// True (with the weighted-z-sum in <paramref name="raw"/>) once every *required*
    /// component (everything except <see cref="OptionalComponentNames"/>) has a z-score. An
    /// optional component's own contribution is added when available and silently treated as
    /// 0 when not -- neither one blocks the composite, or prevents the required six from
    /// producing one.
    /// </summary>
    static bool TryComputeRaw(List<ScoreComponentBreakdown> components, out double raw)
    {
        var required = components.Where(c => !OptionalComponentNames.Contains(c.Name)).ToList();
        if (!required.All(c => c.ZScore is not null))
        {
            raw = 0;
            return false;
        }

        var optionalContribution = components
            .Where(c => OptionalComponentNames.Contains(c.Name))
            .Sum(c => c.WeightedContribution ?? 0.0);
        raw = required.Sum(c => c.WeightedContribution!.Value) + optionalContribution;
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
        BuildComponent(GammaExposureComponentName, weights.GammaExposure, inputs.GammaExposureZ),
        BuildComponent(VolumePcrComponentName, weights.VolumePcr, inputs.VolumePcrZ),
        BuildComponent(SpreadRatioComponentName, weights.SpreadRatio, inputs.SpreadRatioZ),
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
