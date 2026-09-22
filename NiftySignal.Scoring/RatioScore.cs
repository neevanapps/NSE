namespace NiftySignal.Scoring;

/// <summary>
/// The ratio-based composite score, -100 to +100, structurally parallel to
/// <see cref="CompositeScore"/> but a separate type -- this is a second, independent scoring
/// pipeline (weekend build, 2026-09-09), not a replacement for the existing composite.
/// <c>LiveTradingEngine</c> reads neither this type nor any <c>ScoreSnapshot.Ratio*</c> column
/// -- see the fix plan's own structural-safety note (confirmed by
/// LiveTradingEngineTests' trading-safety parity test).
/// </summary>
public sealed record RatioScore(
    double? Score,
    bool IsWarmedUp,
    IReadOnlyList<ScoreComponentBreakdown> Components,
    string WeightSetVersion,
    DateTimeOffset ComputedAt);
