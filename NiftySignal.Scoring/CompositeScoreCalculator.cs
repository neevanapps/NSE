namespace NiftySignal.Scoring;

/// <summary>
/// Combines the six weighted z-scores into the -100..+100 composite (plan section 6):
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

    public static CompositeScore Calculate(
        ScoreComponentInputs inputs, ScoreWeights weights, DateTimeOffset computedAt, double k = DefaultK)
    {
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "Must be positive.");
        }

        var components = new List<ScoreComponentBreakdown>
        {
            BuildComponent("OiBuildupNet", weights.OiBuildupNet, inputs.OiBuildupNetZ),
            BuildComponent("Pcr", weights.Pcr, inputs.PcrZ),
            BuildComponent("FuturesBasis", weights.FuturesBasis, inputs.FuturesBasisZ),
            BuildComponent("IvSkew", weights.IvSkew, inputs.IvSkewZ),
            BuildComponent("PriceMomentum", weights.PriceMomentum, inputs.PriceMomentumZ),
            BuildComponent("DepthImbalance", weights.DepthImbalance, inputs.DepthImbalanceZ),
        };

        var isWarmedUp = components.All(c => c.ZScore is not null);

        double? score = null;
        if (isWarmedUp)
        {
            var raw = components.Sum(c => c.WeightedContribution!.Value);
            score = 100.0 * Math.Tanh(raw / k);
        }

        return new CompositeScore(score, isWarmedUp, components, weights.Version, computedAt);
    }

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
