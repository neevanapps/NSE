using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>An entry signal that just fired -- see <see cref="LiveEntrySignalRow"/>'s own doc comment for what "signal, not a trade" means here.</summary>
public sealed record LiveSignalOpened(int EntryBarIndex, DateTimeOffset EntryTimestamp, OptionType Side, double EntryScore, double EntryPercentile);

/// <summary>An open signal that just closed -- carries only what changed; the caller already has (or can look up) the matching <see cref="LiveEntrySignalRow"/> by <see cref="EntryBarIndex"/>.</summary>
public sealed record LiveSignalClosed(int EntryBarIndex, int ExitBarIndex, DateTimeOffset ExitTimestamp, string ExitReason);

/// <summary>One bar's worth of <see cref="TradingDaySession.ProcessBar"/> output.</summary>
public sealed record LiveBarScoreResult(LiveOptionsScoreRow ScoreRow, LiveSignalOpened? Opened, LiveSignalClosed? Closed);

/// <summary>
/// The day-scoped owner the plan's "Day-start / day-end contract" section calls for (docs/
/// LIVE_PARITY_PLAN.md) -- Phase C. The ONLY thing allowed to construct or reset the 4
/// <see cref="SessionRankTracker"/> instances (depth/mid-IV/close-IV + the Max Pain confirmation
/// gate's own) that back the locked <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> metric.
/// One instance per trading day; discard it and construct a fresh one (or call
/// <see cref="RebuildAsync"/>) at the next day's first bar.
///
/// Calls into the SAME pure functions <c>TradeSimulator.SimulateDayAsync</c> calls for this metric
/// (<see cref="OptionsThreeWayScoreCalculator.ComputeScore"/>, <see cref="MaxPainConfirmationGate"/>)
/// -- this class is a thin, stateful adapter around them (owns the trackers and the
/// previousAtmIv/previousClose threading, mirrors the entry/exit rule set), not a re-derivation of
/// the scoring formula itself. Locked-metric parameters (bandWidth=5/ATM+/-2, EntryPercentile=90,
/// 09:30-15:00 entry window, 15:15 force-close, default Open/Mid=10:00 and Mid/Close=13:30
/// boundaries) are hardcoded rather than parameterized, deliberately -- this class exists to trade
/// the ONE locked live target, not to be a general-purpose live simulator for every
/// <c>VolumeBarMetric</c> the offline backtest can sweep.
///
/// <b>Idempotency / restart-safety</b>: bars must be fed via <see cref="ProcessBar"/> in strict
/// BarIndex order, each exactly once -- <see cref="ProcessBar"/> throws if fed out of order or
/// twice, so a caller bug surfaces immediately rather than silently corrupting the running
/// percentile distributions. A process restart mid-day cannot resume a bare in-memory instance
/// (the whole point of session-rank state is that it depends on every prior bar this session ever
/// saw) -- use <see cref="RebuildAsync"/> instead, which replays every already-scored bar (read back
/// from <see cref="LiveOptionsScoreRow"/>'s own persisted rows' identity, re-deriving the trackers
/// deterministically from the same durable Phase-A tables) before returning a session whose state is
/// indistinguishable from one that never restarted -- the identical idempotent-replay philosophy
/// <see cref="LiveVolumeBarPopulator"/> already established for bar-writing, applied here to
/// tracker state instead of row identity.
/// </summary>
public sealed class TradingDaySession
{
    /// <summary>The locked target's own entry gate (docs/LIVE_PARITY_PLAN.md's "Locked target score" section) -- not a parameter, since this class trades exactly one locked configuration.</summary>
    public const double EntryPercentileThreshold = 90.0;

    /// <summary>The locked target's own depth band for the Open leg (ATM+/-2) -- matches <c>TradeSimulator.cs</c>'s own <c>optionDepthWideByBarIndex</c> load, which is NOT <see cref="OptionDepthPopulator.DefaultBandWidth"/> (3, ATM+/-1) -- see this class's own Phase C report for why the live writer needed a second <c>LiveOptionDepthPopulator</c> call added for this band.</summary>
    public const int DepthBandWidth = 5;

    static readonly TimeSpan EntryWindowStart = new(9, 30, 0);
    static readonly TimeSpan EntryWindowEnd = new(15, 0, 0);
    static readonly TimeSpan ForceCloseAt = new(15, 15, 0);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public DateOnly AsOfDate { get; }

    public long BarVolumeThreshold { get; }

    /// <summary>-1 until the first bar is processed -- the next call to <see cref="ProcessBar"/> must carry BarIndex 0.</summary>
    public int LastProcessedBarIndex { get; private set; } = -1;

    public bool HasOpenSignal => _open is not null;

    readonly SessionRankTracker _depthRank = new();
    readonly SessionRankTracker _ivMidRank = new();
    readonly SessionRankTracker _ivCloseRank = new();
    readonly SessionRankTracker _maxPainConfirmRank = new();

    double? _previousAtmIv;
    decimal? _previousClose;
    OpenSignal? _open;

    public TradingDaySession(DateOnly asOfDate, long barVolumeThreshold)
    {
        AsOfDate = asOfDate;
        BarVolumeThreshold = barVolumeThreshold;
    }

    /// <summary>
    /// Rebuilds a session's tracker state for <paramref name="asOfDate"/> by replaying every bar
    /// already scored (per <see cref="LiveOptionsScoreRow"/>'s own persisted rows, the "how far did
    /// we already get" bookmark) back through a fresh instance -- see this class's own doc comment
    /// for why. Returns a brand-new, never-scored session (equivalent to <c>new
    /// TradingDaySession(asOfDate, barVolumeThreshold)</c>) if nothing has been scored yet today.
    /// The replay itself calls <see cref="ProcessBar"/> and discards its output (those rows already
    /// exist) -- deliberately reusing the exact same code path live scoring uses, not a parallel
    /// "just rebuild the trackers" shortcut that could drift from it.
    /// </summary>
    public static async Task<TradingDaySession> RebuildAsync(VolumeBarDbContext db, DateOnly asOfDate, long barVolumeThreshold, CancellationToken ct)
    {
        var maxScored = await db.LiveOptionsScoreBars
            .Where(r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == barVolumeThreshold)
            .Select(r => (int?)r.BarIndex)
            .MaxAsync(ct) ?? -1;

        var session = new TradingDaySession(asOfDate, barVolumeThreshold);
        if (maxScored < 0)
        {
            return session;
        }

        var bars = await db.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BarIndex <= maxScored)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(ct);
        var atmByIndex = await db.OptionAtmBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BarIndex <= maxScored)
            .ToDictionaryAsync(b => b.BarIndex, ct);
        var depthByIndex = await db.OptionDepthBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == DepthBandWidth && b.BarIndex <= maxScored)
            .ToDictionaryAsync(b => b.BarIndex, ct);
        var maxPainByIndex = await db.OptionMaxPainBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BarIndex <= maxScored)
            .ToDictionaryAsync(b => b.BarIndex, ct);

        // Also replay which signals were open/closed so far, so _open ends up in the exact state it
        // would have been in without the restart -- ProcessBar recomputes this from the score/entry
        // rules as a side effect of replaying, but the OPEN SIGNAL ROW ITSELF (already persisted)
        // must not be duplicated; RebuildAsync's caller never re-inserts these, only the fresh rows
        // from BarIndex maxScored+1 onward.
        foreach (var bar in bars)
        {
            atmByIndex.TryGetValue(bar.BarIndex, out var atm);
            depthByIndex.TryGetValue(bar.BarIndex, out var depth);
            maxPainByIndex.TryGetValue(bar.BarIndex, out var maxPain);
            session.ProcessBar(bar, atm, depth, maxPain);
        }

        return session;
    }

    /// <summary>
    /// Scores one bar and applies the entry/exit rule set, exactly once, in order. Throws if
    /// <paramref name="bar"/>'s BarIndex isn't exactly <see cref="LastProcessedBarIndex"/> + 1 -- the
    /// same "never reprocess a bar" discipline <see cref="LiveVolumeBarPopulator"/>'s own unique
    /// index enforces at the DB level for bar WRITING, enforced here in-process for bar SCORING
    /// (there is no natural DB constraint that could catch "scored twice" the way a duplicate insert
    /// would, since re-scoring wouldn't itself violate <see cref="LiveOptionsScoreRow"/>'s own unique
    /// index unless the caller also tried to re-insert the row -- this guard exists so a caller bug
    /// is caught immediately instead of silently double-counting a bar into the running percentile
    /// distributions).
    /// </summary>
    public LiveBarScoreResult ProcessBar(VolumeBarRow bar, OptionAtmBarRow? atmBar, OptionDepthBarRow? wideDepthBar, OptionMaxPainBarRow? maxPainBar)
    {
        if (bar.BarIndex != LastProcessedBarIndex + 1)
        {
            throw new InvalidOperationException(
                $"TradingDaySession({AsOfDate:yyyy-MM-dd}, threshold={BarVolumeThreshold}) expected BarIndex {LastProcessedBarIndex + 1} next, got {bar.BarIndex} -- bars must be fed in order, exactly once.");
        }

        var timeOfDayIst = bar.EndTimestamp.ToOffset(IstOffset).TimeOfDay;

        var inputs = new OptionsThreeWayScoreInputs(
            timeOfDayIst, bar.ClosePrice, _previousClose, atmBar?.AtmIv,
            wideDepthBar?.CallBidQtyAvg, wideDepthBar?.PutBidQtyAvg, wideDepthBar?.CallAskQtyAvg, wideDepthBar?.PutAskQtyAvg);

        var rawScore = OptionsThreeWayScoreCalculator.ComputeScore(inputs, ref _previousAtmIv, _depthRank, _ivMidRank, _ivCloseRank);
        var maxPainConfirmScore = MaxPainConfirmationGate.ComputeScore(bar.ClosePrice, maxPainBar?.MaxPainStrike, _maxPainConfirmRank);

        _previousClose = bar.ClosePrice;
        LastProcessedBarIndex = bar.BarIndex;

        var scaledScore = rawScore is { } r ? 100.0 * r : (double?)null;
        // Already percentile-shaped by SignedRank construction for this metric -- see
        // LiveOptionsScoreRow.Percentile's own doc comment; no second SessionRankTracker layer.
        var percentile = scaledScore is { } sc ? Math.Abs(sc) : (double?)null;
        // MaxPainConfirmationGate.Passes signs off Math.Sign(scaledScore); pass 0.0 for a null
        // scaledScore (Math.Sign(0)==0 can only match a mp of exactly 0, which Passes' own >0-strike
        // guard makes vanishingly unlikely and never a false positive either way).
        var maxPainPasses = MaxPainConfirmationGate.Passes(maxPainConfirmScore, scaledScore ?? 0.0);

        var sessionLeg = timeOfDayIst < new TimeSpan(10, 0, 0) ? "Open"
            : timeOfDayIst < OptionsThreeWayScoreCalculator.DefaultMidCloseBoundary ? "Mid"
            : "Close";

        var scoreRow = new LiveOptionsScoreRow
        {
            AsOfDate = AsOfDate,
            BarIndex = bar.BarIndex,
            BarVolumeThreshold = BarVolumeThreshold,
            EndTimestamp = bar.EndTimestamp,
            SessionLeg = sessionLeg,
            RawScore = rawScore,
            ScaledScore = scaledScore,
            Percentile = percentile,
            MaxPainConfirmScore = maxPainConfirmScore,
            MaxPainConfirmPasses = maxPainPasses,
        };

        LiveSignalOpened? opened = null;
        LiveSignalClosed? closed = null;

        if (_open is { } open)
        {
            // Same two exit conditions TradeSimulator.cs checks for this metric (StopLoss doesn't
            // apply -- the locked 2600/90 configuration runs with no stop-loss; EndOfData doesn't
            // apply here either -- see FlushEndOfDay for this session's own analog of it).
            var timedOut = timeOfDayIst >= ForceCloseAt;
            var invalidated = scaledScore is { } liveScore && percentile is { } p && p >= EntryPercentileThreshold
                && (open.Side == OptionType.Call ? liveScore < 0 : liveScore > 0);

            if (timedOut || invalidated)
            {
                closed = new LiveSignalClosed(open.EntryBarIndex, bar.BarIndex, bar.EndTimestamp, timedOut ? "TimeCutoff" : "ScoreInvalidated");
                _open = null;
            }
        }
        else if (scaledScore is { } sc2 && percentile is { } p2 && p2 >= EntryPercentileThreshold
            && timeOfDayIst >= EntryWindowStart && timeOfDayIst <= EntryWindowEnd
            && maxPainPasses)
        {
            var side = sc2 > 0 ? OptionType.Call : OptionType.Put;
            _open = new OpenSignal(bar.BarIndex, bar.EndTimestamp, side);
            opened = new LiveSignalOpened(bar.BarIndex, bar.EndTimestamp, side, sc2, p2);
        }

        return new LiveBarScoreResult(scoreRow, opened, closed);
    }

    /// <summary>
    /// This session's own analog of <c>TradeSimulator.cs</c>'s "isLastBar" end-of-data force-close --
    /// there is no literal end of a finite dataset live, only "the trading day is over" (15:35 IST
    /// hard close, or the Host stopped polling for the day). In practice <see cref="ForceCloseAt"/>
    /// (15:15 IST) already closes any open signal well before this would ever fire on a normal day;
    /// this exists purely as a safety net (e.g. a gap in bar data that skips straight past 15:15
    /// without a bar landing in the force-close check above) so a signal can never be left open
    /// forever across a day boundary. Call once, after the day's last bar has been processed.
    /// </summary>
    public LiveSignalClosed? FlushEndOfDay(DateTimeOffset asOfTimestamp)
    {
        if (_open is not { } open)
        {
            return null;
        }

        _open = null;
        return new LiveSignalClosed(open.EntryBarIndex, LastProcessedBarIndex, asOfTimestamp, "EndOfDay");
    }

    sealed record OpenSignal(int EntryBarIndex, DateTimeOffset EntryTimestamp, OptionType Side);
}
