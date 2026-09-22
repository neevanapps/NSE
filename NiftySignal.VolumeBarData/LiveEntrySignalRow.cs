using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One live entry SIGNAL for a volume-bar-pipeline strategy -- Phase C of docs/LIVE_PARITY_PLAN.md
/// (originally <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> only; shared with the futures
/// crossover strategy since 2026-09-21, see <see cref="Strategy"/>), deliberately NOT a paper trade
/// (no strike selection, no fill price, no P&amp;L -- that is Phase D, out of scope here). A row is
/// inserted the bar an entry fires (<see cref="ExitBarIndex"/>/<see cref="ExitTimestamp"/>/
/// <see cref="ExitReason"/> all null at that point) and UPDATED in place once the same signal closes
/// -- one row per signal's whole lifecycle, not two, so "is a signal currently open" is answerable
/// with a single <c>WHERE Strategy = ... AND ExitBarIndex IS NULL</c> query rather than a join.
///
/// "One position at a time" for this signal-only system (task's own required documented choice,
/// since there is no real trade lifecycle yet to hang the rule on): each strategy's own day-scoped
/// session (<c>TradingDaySession</c> for <see cref="LiveVolumeBarStrategyId.Options"/>,
/// <c>LiveFuturesCrossoverSession</c> for <see cref="LiveVolumeBarStrategyId.FuturesCrossover"/>)
/// tracks at most one row with a null <see cref="ExitBarIndex"/> per trading day, INDEPENDENTLY of
/// the other strategy -- no new entry signal is evaluated at all while that strategy's own is open,
/// mirroring <c>TradeSimulator.cs</c>'s own <c>open is {} position</c> single-slot convention
/// exactly, just without the real option leg underneath it.
/// </summary>
public sealed class LiveEntrySignalRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>
    /// 2026-09-21, futures-crossover live-wiring task: which strategy this signal belongs to -- see
    /// this class's own doc comment. Both strategies trade off the SAME BarIndex sequence (one
    /// future, one <see cref="VolumeBarRow"/> builder), so without this discriminator a futures-
    /// crossover signal and an options signal firing at the same BarIndex on the same day would
    /// collide on this table's own (AsOfDate, BarVolumeThreshold, Strategy, EntryBarIndex) unique
    /// index (Strategy folded in as part of this task; every pre-existing row backfills to
    /// <see cref="LiveVolumeBarStrategyId.Options"/>, the only strategy that had ever written here).
    /// </summary>
    public required LiveVolumeBarStrategyId Strategy { get; set; }

    /// <summary>Call = long-the-underlying-direction, Put = short -- same meaning <see cref="VolumeBarTrade.Side"/> carries in the offline backtest (which option TYPE would be bought, not a literal instrument yet).</summary>
    public required OptionType Side { get; set; }

    public required int EntryBarIndex { get; set; }

    public required DateTimeOffset EntryTimestamp { get; set; }

    /// <summary>The scaled (-100..100) score that fired this entry (Options: the 3-way-switch score; FuturesCrossover: the fast/slow MA gap at the crossing bar) -- matches <see cref="VolumeBarTrade.EntryScore"/>'s own scale for direct comparability against the offline backtest's trades.</summary>
    public required double EntryScore { get; set; }

    /// <summary>
    /// The entry-percentile reading that gated this signal -- Options only (its own percentile-
    /// threshold entry gate). 2026-09-21: made nullable (was required) because the futures crossover
    /// strategy has no percentile concept at all (its own entry gate is a fast/slow MA crossing plus
    /// a fixed point-gap threshold, not a percentile-of-today's-distribution rule) -- null for every
    /// <see cref="LiveVolumeBarStrategyId.FuturesCrossover"/> row, never fabricated.
    /// </summary>
    public double? EntryPercentile { get; set; }

    /// <summary>Null while the signal is still open.</summary>
    public int? ExitBarIndex { get; set; }

    public DateTimeOffset? ExitTimestamp { get; set; }

    /// <summary>"ScoreInvalidated" (opposite extreme crossed) or "TimeCutoff" (15:15 IST force-close) or "EndOfDay" (day ended, e.g. Host stopped polling, with no cutoff/invalidation bar seen) -- the same 3 reasons <c>TradeSimulator.cs</c> uses minus "StopLoss"/"EndOfData" (no stop-loss concept and no literal last-bar-of-a-finite-dataset concept on the live side; "EndOfDay" is this session's own analog of the backtest's "EndOfData").</summary>
    public string? ExitReason { get; set; }
}
