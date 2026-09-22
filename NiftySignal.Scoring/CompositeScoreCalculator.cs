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
    /// Components that don't gate warm-up for a reason *other than* being weight-0 -- see
    /// ScoreComponentInputs' VixChangeZ doc comment: VIX tracking can be legitimately absent
    /// for a day's whole instrument universe even though it carries a real (0.05) weight, so
    /// it needs its own name-based exception. GammaExposure/VolumePcr/SpreadRatio/
    /// VannaExposure/CharmExposure/CvdProxy/StraddleRichness are listed here too for backward
    /// documentation clarity, but as of the F47 fix below they'd be optional regardless (all
    /// currently weight 0.0) -- this list only still matters for VixChange.
    ///
    /// PENDING (audit finding F14, 2026-09-08 lead review -- see fix plan Batch 6): DepthImbalance
    /// carries a real, nonzero weight (0.1625) and is untouched by the F47 fix below, so it
    /// still gates warm-up for OiBuildupNet/Pcr/FuturesBasis/IvSkew/PriceMomentum too. F14 asks
    /// whether a missing depth book should instead silently contribute 0 like VixChange does --
    /// that's a real risk-behavior decision (the composite could warm up and trade having never
    /// seen a depth book at all), not a mechanical fix, and stays open pending that decision.
    /// </summary>
    const string VixComponentName = "VixChange";
    const string GammaExposureComponentName = "GammaExposure";
    const string VolumePcrComponentName = "VolumePcr";
    const string SpreadRatioComponentName = "SpreadRatio";
    const string VannaExposureComponentName = "VannaExposure";
    const string CharmExposureComponentName = "CharmExposure";
    const string CvdProxyComponentName = "CvdProxy";
    const string StraddleRichnessComponentName = "StraddleRichness";
    // Just VixChange -- the other seven are all weight-0.0 today (see ScoreWeights.Default) and
    // are therefore already optional via IsOptional's weight check below without needing to be
    // named here too. Kept as a single-entry array rather than a plain string comparison so a
    // future second name-based exception (like VixChange's) has an obvious place to go.
    static readonly string[] OptionalComponentNames = [VixComponentName];

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
    /// component has a z-score. A component is optional -- contributes when available,
    /// silently treated as 0 when not, never blocks the composite -- if it's named in
    /// <see cref="OptionalComponentNames"/> (VixChange's own separate-warm-up-clock reason) or
    /// if its weight is 0 (audit finding F47, 2026-09-10: a component contributing nothing to
    /// the score has no business being able to block every other component's warm-up just
    /// because it happened to be one of the original six -- e.g. PriceMomentum, cut to weight
    /// 0 by F4 but left on the required list, meaning a missing PriceMomentumZ could stall the
    /// whole composite for a component that would add exactly 0 even if present).
    /// </summary>
    static bool TryComputeRaw(List<ScoreComponentBreakdown> components, out double raw)
    {
        var required = components.Where(c => !IsOptional(c)).ToList();
        if (!required.All(c => c.ZScore is not null))
        {
            raw = 0;
            return false;
        }

        var optionalContribution = components.Where(IsOptional).Sum(c => c.WeightedContribution ?? 0.0);
        raw = required.Sum(c => c.WeightedContribution!.Value) + optionalContribution;
        return true;
    }

    static bool IsOptional(ScoreComponentBreakdown c) => OptionalComponentNames.Contains(c.Name) || c.Weight == 0.0;

    /// <summary>
    /// Names of the required components currently missing a z-score -- i.e. the ones actually
    /// responsible for <see cref="Calculate"/> returning a null score this cadence. Exists so a
    /// caller wanting to log *why* warm-up is blocked (audit finding F10) reads this class's own
    /// required/optional rule directly instead of hand-maintaining a second, parallel list that
    /// can silently drift out of sync with it -- which is exactly what happened to
    /// MarketDataIngestionWorker's old hardcoded six-name list once F47 made PriceMomentum
    /// optional: the log kept blaming PriceMomentumZ for blocking warm-up long after it no
    /// longer could.
    /// </summary>
    public static IReadOnlyList<string> DescribeMissingRequiredComponents(ScoreComponentInputs inputs, ScoreWeights weights) =>
        BuildComponents(inputs, weights).Where(c => !IsOptional(c) && c.ZScore is null).Select(c => c.Name).ToList();

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
        BuildComponent(VannaExposureComponentName, weights.VannaExposure, inputs.VannaExposureZ),
        BuildComponent(CharmExposureComponentName, weights.CharmExposure, inputs.CharmExposureZ),
        BuildComponent(CvdProxyComponentName, weights.CvdProxy, inputs.CvdProxyZ),
        BuildComponent(StraddleRichnessComponentName, weights.StraddleRichness, inputs.StraddleRichnessZ),
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
