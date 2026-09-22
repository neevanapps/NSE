namespace NiftySignal.Domain.Enums;

/// <summary>
/// Which live volume-bar-pipeline strategy owns a given <c>NiftySignal.VolumeBarData.LiveEntrySignalRow</c>/
/// <c>LivePaperTradeRow</c> row -- 2026-09-21, futures-crossover live-wiring task. Mirrors the
/// existing <see cref="StrategyId"/> discriminator's own role for the (older) time-cadence live
/// engines: both volume-bar-pipeline strategies trade off the SAME <c>VolumeBarRow</c> BarIndex
/// sequence (one future, one bar builder, one <c>BarVolumeThreshold</c>=2600 per day) -- without a
/// discriminator on the shared <c>LiveEntrySignalRow</c>/<c>LivePaperTradeRow</c> tables, a
/// futures-crossover entry and an options entry that happen to fire at the same BarIndex on the
/// same day would collide on those tables' own (AsOfDate, BarVolumeThreshold, EntryBarIndex) unique
/// index. Each strategy still independently holds at most one open position at a time (enforced by
/// its own day-scoped session, <c>TradingDaySession</c> for <see cref="Options"/> and
/// <c>LiveFuturesCrossoverSession</c> for <see cref="FuturesCrossover"/>), completely independently
/// of the other strategy's own open position.
/// </summary>
public enum LiveVolumeBarStrategyId
{
    /// <summary>The locked <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> strategy (docs/LIVE_PARITY_PLAN.md Phases C/D) -- every row inserted before this discriminator existed backfills to this value (the only strategy that had ever traded on these tables).</summary>
    Options,

    /// <summary>The locked 8-fast/40-slow/5-point-threshold futures SMA-crossover strategy on <c>SessionGatedDepthDurationConfirmed</c>'s own FuturesScore (docs/LIVE_PARITY_PLAN.md's "Futures crossover: wired live" section), new 2026-09-21.</summary>
    FuturesCrossover,
}
