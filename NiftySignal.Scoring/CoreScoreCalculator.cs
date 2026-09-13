namespace NiftySignal.Scoring;

/// <summary>
/// Combines the Core score's 8 optional inputs (2026-09-13 live-wiring plan A4): <c>raw =
/// sum(weight_i * s_i) / sum(weight_i)</c> over whichever are non-null this cadence -- a
/// renormalized weighted average, so a cadence missing some terms isn't silently scaled down
/// relative to a cadence with all 8 present. <c>score = 100 * tanh(raw / k)</c>.
///
/// Structurally closer to <see cref="RatioScoreCalculator"/> than to
/// <see cref="CompositeScoreCalculator"/> -- both share the renormalize-by-present-weight policy
/// (rather than the main composite's all-required-components policy) -- but deliberately NOT
/// identical: <see cref="RatioScoreCalculator.MinRequiredComponents"/> requires 3 of 5 present
/// before publishing a score; the Core score backtest (`CoreScoreOptionSimulator.cs`) has no such
/// floor at all -- `presentWeight > 0` is its only condition (a single present term, however
/// small its own weight, is enough to publish). Copying `MinRequiredComponents` here would be a
/// silent behavioral change from what was actually backtested, not a stylistic choice -- don't
/// add one.
/// </summary>
public static class CoreScoreCalculator
{
    /// <summary>Matches the backtest's own <c>K</c> exactly (`CoreScoreOptionSimulator.cs`'s `CoreScoreWeights.K = 1.0`) -- this composite's raw range is genuinely different from the ratio composite's (8 terms, weights summing to 0.915, not 5 terms summing to 1.0), so DefaultK is not expected to match RatioScoreCalculator's 0.5; it's copied from what was actually tested, not re-derived from this composite's own shape.</summary>
    public const double DefaultK = 1.0;

    public static double? ComputeRaw(CoreScoreComponentInputs inputs, CoreScoreWeights weights) =>
        TryComputeRaw(BuildComponents(inputs, weights), out var raw) ? raw : null;

    /// <param name="rawOverride">
    /// Substitutes a caller-supplied raw value for the tanh input, instead of the single-cadence
    /// raw this method would otherwise compute from <paramref name="inputs"/> -- same mechanism
    /// as <see cref="CompositeScoreCalculator.Calculate"/>/<see cref="RatioScoreCalculator.Calculate"/>.
    /// Warm-up still comes from <paramref name="inputs"/> itself. NOT used for the crossover
    /// engine's fast/slow smoothing -- that smooths the already-computed, already-tanh'd
    /// <see cref="CoreScore.Score"/> directly (a plain post-hoc moving average), not a pre-tanh
    /// raw fed back through a second call here. This parameter exists only for symmetry with the
    /// other two calculators and isn't expected to see live use by either Core-score engine.
    /// </param>
    public static CoreScore Calculate(
        CoreScoreComponentInputs inputs, CoreScoreWeights weights, DateTimeOffset computedAt, double k = DefaultK, double? rawOverride = null)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must be positive.");
        }

        var components = BuildComponents(inputs, weights);
        var isWarmedUp = TryComputeRaw(components, out var raw);
        var effectiveRaw = rawOverride ?? raw;
        var score = isWarmedUp ? 100.0 * Math.Tanh(effectiveRaw / k) : (double?)null;

        return new CoreScore(score, isWarmedUp, components, weights.Version, computedAt);
    }

    /// <summary>
    /// True (with the renormalized weighted average in <paramref name="raw"/>) once at least one
    /// component has a signed value this cadence AND that component's own weight is positive --
    /// matching `CoreScoreOptionSimulator.cs`'s exact condition (`presentWeight > 0`), not a
    /// minimum-count floor.
    /// </summary>
    static bool TryComputeRaw(List<ScoreComponentBreakdown> components, out double raw)
    {
        var present = components.Where(c => c.ZScore is not null).ToList();
        var presentWeight = present.Sum(c => c.Weight);
        if (presentWeight <= 0)
        {
            raw = 0;
            return false;
        }

        raw = present.Sum(c => c.WeightedContribution!.Value) / presentWeight;
        return true;
    }

    static List<ScoreComponentBreakdown> BuildComponents(CoreScoreComponentInputs inputs, CoreScoreWeights weights) =>
    [
        BuildComponent("DepthImbalance", weights.DepthImbalance, inputs.DepthImbalance),
        BuildComponent("ItmSkew", weights.ItmSkew, inputs.ItmSkew),
        BuildComponent("FutureCvdNet5Min", weights.FutureCvdNet5Min, inputs.FutureCvdNet5Min),
        BuildComponent("NotionalVolumeRatio", weights.NotionalVolumeRatio, inputs.NotionalVolumeRatio),
        BuildComponent("GammaExposure", weights.GammaExposure, inputs.GammaExposure),
        BuildComponent("TrendReversion15m", weights.TrendReversion15m, inputs.TrendReversion15m),
        BuildComponent("BasisChange", weights.BasisChange, inputs.BasisChange),
        BuildComponent("OiChangeDiff15m", weights.OiChangeDiff15m, inputs.OiChangeDiff15m),
    ];

    /// <summary>
    /// <see cref="ScoreComponentBreakdown.ZScore"/> here holds a signed s_i (already in [-1,1]
    /// from the caller's session-rank transform, or in TrendReversion15m's case already bounded by
    /// construction), not an actual z-score -- the type is reused as-is, same convention
    /// <see cref="RatioScoreCalculator.BuildComponent"/> already established.
    /// </summary>
    static ScoreComponentBreakdown BuildComponent(string name, double weight, double? s)
    {
        if (s is null)
        {
            return new ScoreComponentBreakdown(name, weight, null, null);
        }

        // Defense in depth, same reasoning as the other two calculators' own BuildComponent --
        // callers are expected to pass an already-[-1,1]-bounded s, but re-clipping here costs
        // nothing.
        var clipped = Math.Clamp(s.Value, -1.0, 1.0);
        return new ScoreComponentBreakdown(name, weight, clipped, weight * clipped);
    }
}
