using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One live entry SIGNAL for <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> -- Phase C of
/// docs/LIVE_PARITY_PLAN.md, deliberately NOT a paper trade (no strike selection, no fill price, no
/// P&amp;L -- that is Phase D, out of scope here). A row is inserted the bar an entry fires
/// (<see cref="ExitBarIndex"/>/<see cref="ExitTimestamp"/>/<see cref="ExitReason"/> all null at that
/// point) and UPDATED in place once the same signal closes -- one row per signal's whole lifecycle,
/// not two, so "is a signal currently open" is answerable with a single
/// <c>WHERE ExitBarIndex IS NULL</c> query rather than a join.
///
/// "One position at a time" for this signal-only system (task's own required documented choice,
/// since there is no real trade lifecycle yet to hang the rule on): <see cref="TradingDaySession"/>
/// tracks at most one row with a null <see cref="ExitBarIndex"/> per trading day -- no new entry
/// signal is evaluated at all while one is open, mirroring <c>TradeSimulator.cs</c>'s own
/// <c>open is {} position</c> single-slot convention exactly, just without the real option leg
/// underneath it.
/// </summary>
public sealed class LiveEntrySignalRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required long BarVolumeThreshold { get; set; }

    /// <summary>Call = long-the-underlying-direction, Put = short -- same meaning <see cref="VolumeBarTrade.Side"/> carries in the offline backtest (which option TYPE would be bought, not a literal instrument yet).</summary>
    public required OptionType Side { get; set; }

    public required int EntryBarIndex { get; set; }

    public required DateTimeOffset EntryTimestamp { get; set; }

    /// <summary>The scaled (-100..100) score that fired this entry -- matches <see cref="VolumeBarTrade.EntryScore"/>'s own scale for direct comparability against the offline backtest's trades.</summary>
    public required double EntryScore { get; set; }

    public required double EntryPercentile { get; set; }

    /// <summary>Null while the signal is still open.</summary>
    public int? ExitBarIndex { get; set; }

    public DateTimeOffset? ExitTimestamp { get; set; }

    /// <summary>"ScoreInvalidated" (opposite extreme crossed) or "TimeCutoff" (15:15 IST force-close) or "EndOfDay" (day ended, e.g. Host stopped polling, with no cutoff/invalidation bar seen) -- the same 3 reasons <c>TradeSimulator.cs</c> uses minus "StopLoss"/"EndOfData" (no stop-loss concept and no literal last-bar-of-a-finite-dataset concept on the live side; "EndOfDay" is this session's own analog of the backtest's "EndOfData").</summary>
    public string? ExitReason { get; set; }
}
