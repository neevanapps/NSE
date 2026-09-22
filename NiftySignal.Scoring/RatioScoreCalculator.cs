namespace NiftySignal.Scoring;

/// <summary>
/// Combines the ratio-based composite's five optional inputs: <c>raw = sum(weight_i * s_i) /
/// sum(weight_i)</c> over whichever are non-null this bar (a renormalized weighted average, so
/// a 3-of-5 bar isn't silently scaled down relative to a 5-of-5 bar), published once at least
/// <see cref="MinRequiredComponents"/> of 5 are present; <c>score = 100 * tanh(raw / k)</c>.
/// Structurally parallel to <see cref="CompositeScoreCalculator"/> but not sharing code with
/// it -- that class is too tightly coupled to its exact 14-named-component shape to reuse
/// directly here.
/// </summary>
public static class RatioScoreCalculator
{
    /// <summary>
    /// This composite's raw range is [-1,1] by construction (a weighted average of five clipped
    /// [-1,1] inputs), materially narrower than the existing composite's unbounded z-score sum
    /// -- CompositeScoreCalculator.DefaultK=1.0 would never let this reach even 80. k=0.5 gives
    /// tanh(2)~=96.4 at full 5-way agreement (all five s_i=1), real headroom without instant
    /// saturation. Provisional, like every constant here -- revisit after live data.
    /// </summary>
    public const double DefaultK = 0.5;

    /// <summary>At least this many of the five components must be non-null this bar for the composite to publish a score -- see the fix plan's "components are optional, not all-five-required" amendment.</summary>
    public const int MinRequiredComponents = 3;

    public static double? ComputeRaw(RatioComponentInputs inputs, RatioScoreWeights weights) =>
        TryComputeRaw(BuildComponents(inputs, weights), out var raw) ? raw : null;

    /// <param name="rawOverride">
    /// Substitutes a caller-supplied raw value (LiveFeatureEngine's own multi-cadence smoothed
    /// raw, via its own second FIFO) for the tanh input, instead of the single-cadence raw this
    /// method would otherwise compute from <paramref name="inputs"/> -- same mechanism as
    /// <see cref="CompositeScoreCalculator.Calculate"/>. Warm-up still comes from
    /// <paramref name="inputs"/> itself.
    /// </param>
    public static RatioScore Calculate(
        RatioComponentInputs inputs, RatioScoreWeights weights, DateTimeOffset computedAt, double k = DefaultK, double? rawOverride = null)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must be positive.");
        }

        var components = BuildComponents(inputs, weights);
        var isWarmedUp = TryComputeRaw(components, out var raw);
        var effectiveRaw = rawOverride ?? raw;
        var score = isWarmedUp ? 100.0 * Math.Tanh(effectiveRaw / k) : (double?)null;

        return new RatioScore(score, isWarmedUp, components, weights.Version, computedAt);
    }

    /// <summary>
    /// True (with the renormalized weighted average in <paramref name="raw"/>) once at least
    /// <see cref="MinRequiredComponents"/> of the five components have a clipped value this
    /// bar. Present components are renormalized by their own combined weight -- a 3-of-5 bar's
    /// raw isn't diluted just because two components' worth of weight happened to be missing
    /// this bar.
    /// </summary>
    static bool TryComputeRaw(List<ScoreComponentBreakdown> components, out double raw)
    {
        var present = components.Where(c => c.ZScore is not null).ToList();
        if (present.Count < MinRequiredComponents)
        {
            raw = 0;
            return false;
        }

        var presentWeight = present.Sum(c => c.Weight);
        if (presentWeight <= 0)
        {
            raw = 0;
            return false;
        }

        raw = present.Sum(c => c.WeightedContribution!.Value) / presentWeight;
        return true;
    }

    static List<ScoreComponentBreakdown> BuildComponents(RatioComponentInputs inputs, RatioScoreWeights weights) =>
    [
        BuildComponent("NotionalVolumeRatio", weights.NotionalVolumeRatio, inputs.NotionalVolumeRatio),
        BuildComponent("SizedOiFlowRatio", weights.SizedOiFlowRatio, inputs.SizedOiFlowRatio),
        BuildComponent("ResidualDifference", weights.ResidualDifference, inputs.ResidualDifference),
        BuildComponent("IvSkew25Delta", weights.IvSkew25Delta, inputs.IvSkew25Delta),
        BuildComponent("SpreadRatioAtm", weights.SpreadRatioAtm, inputs.SpreadRatioAtm),
    ];

    /// <summary>
    /// <see cref="ScoreComponentBreakdown.ZScore"/> here holds a clipped s_i (already in
    /// [-1,1] from <c>RatioMetricMath</c>), not an actual z-score -- the type is reused as-is
    /// rather than inventing a parallel one just to rename one field.
    /// </summary>
    static ScoreComponentBreakdown BuildComponent(string name, double weight, double? s)
    {
        if (s is null)
        {
            return new ScoreComponentBreakdown(name, weight, null, null);
        }

        // Defense in depth, same reasoning as CompositeScoreCalculator.BuildComponent -- callers
        // are expected to pass an already-clipped s from RatioMetricMath, but re-clipping here
        // costs nothing. [-1,1], not [-3,3] -- this composite's inputs are ratio-clips, not
        // z-scores.
        var clipped = Math.Clamp(s.Value, -1.0, 1.0);
        return new ScoreComponentBreakdown(name, weight, clipped, weight * clipped);
    }
}
