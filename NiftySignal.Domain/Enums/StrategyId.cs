namespace NiftySignal.Domain.Enums;

/// <summary>
/// Which trading engine opened a <see cref="Entities.PaperTrade"/> (2026-09-13 live-wiring plan
/// Batch 5, A6) -- every existing (pre-Batch-5) row backfills to <see cref="LegacyComposite"/> in
/// the same migration that adds this column, the only strategy that has ever traded so far.
/// <see cref="CoreScoreHysteresis"/>/<see cref="CoreScoreCrossover"/> are the two backtested Core
/// score strategies locked in for live paper-trade watch (docs/replication_plan.md) -- each reads
/// its own risk limits and holds at most one open position at a time (<c>CoreScoreHysteresisTradingEngine</c>/
/// <c>CoreScoreCrossoverTradingEngine</c>'s own hardcoded MaxConcurrentPositions=1), independently
/// of each other and of the legacy engine's own (now permanently empty) positions.
/// </summary>
public enum StrategyId
{
    LegacyComposite,
    CoreScoreHysteresis,
    CoreScoreCrossover,
}
