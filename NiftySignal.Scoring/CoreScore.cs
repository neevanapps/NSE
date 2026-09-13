namespace NiftySignal.Scoring;

/// <summary>
/// The Core score, -100 to +100 (2026-09-13 live-wiring plan A4) -- structurally parallel to
/// <see cref="CompositeScore"/> and <see cref="RatioScore"/> but a third, independent scoring
/// pipeline replicating what `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs` already
/// backtested. Two live trading engines read this type directly
/// (`CoreScoreHysteresisTradingEngine`, `CoreScoreCrossoverTradingEngine`) -- unlike
/// <see cref="RatioScore"/>, this one is NOT inert with respect to real trades; see the
/// replication plan's own trading-safety parity tests once those engines exist (Batch 5).
/// </summary>
public sealed record CoreScore(
    double? Score,
    bool IsWarmedUp,
    IReadOnlyList<ScoreComponentBreakdown> Components,
    string WeightSetVersion,
    DateTimeOffset ComputedAt);
