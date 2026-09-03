namespace NiftySignal.Scoring;

/// <summary>
/// The universal directional score (plan section 6), -100 (strongly bearish) to +100
/// (strongly bullish). <see cref="Score"/> is null and <see cref="IsWarmedUp"/> is false
/// until every component in <see cref="Components"/> has a non-null z-score -- per plan
/// section 5.4, the composite is not emitted from a partially-warm set of inputs.
/// </summary>
public sealed record CompositeScore(
    double? Score,
    bool IsWarmedUp,
    IReadOnlyList<ScoreComponentBreakdown> Components,
    string WeightSetVersion,
    DateTimeOffset ComputedAt);
