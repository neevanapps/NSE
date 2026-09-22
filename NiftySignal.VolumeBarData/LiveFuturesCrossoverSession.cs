using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>One bar's worth of <see cref="LiveFuturesCrossoverSession.ProcessBar"/> output -- same shape <see cref="LiveBarScoreResult"/> plays for the options side.</summary>
public sealed record LiveCrossoverBarResult(LiveFuturesCrossoverScoreRow ScoreRow, LiveSignalOpened? Opened, LiveSignalClosed? Closed);

/// <summary>
/// The day-scoped owner of the live futures SMA-crossover strategy's own rolling-window/rank-tracker
/// state -- 2026-09-21, futures-crossover live-wiring task, mirroring <see cref="TradingDaySession"/>'s
/// own role/shape for the options side exactly (same "one instance per trading day, discard and
/// rebuild at the next day boundary" lifecycle, same restart-safety contract via
/// <see cref="RebuildAsync"/>, same strict-BarIndex-order/no-reprocess guard in
/// <see cref="ProcessBar"/>).
///
/// Trades the LOCKED, adopted-for-continued-out-of-sample-tracking configuration (docs/LIVE_PARITY_PLAN.md's
/// "Adopted for continued out-of-sample tracking" note): 8-bar fast / 40-bar slow SIMPLE MOVING
/// AVERAGE of <see cref="FuturesSessionGatedScoreCalculator.ComputeScore"/>'s own scaled ([-100,100])
/// per-bar reading, a qualifying crossing needs the fast/slow gap at the crossing bar to be at least
/// <see cref="ThresholdPoints"/> (5.0), entry additionally gated by the same Open-window TOB
/// confirmation <c>SessionGatedDepthDurationConfirmed</c> itself requires
/// (<see cref="FuturesSessionGatedScoreCalculator.PassesTobConfirmation"/>), exit on
/// <c>CrossoverReversed</c> (an opposite qualifying crossing) or the standard 15:15 IST
/// <see cref="ForceCloseAt"/> force-close -- exactly <c>TradeSimulator.SimulateCrossoverDayAsync</c>'s
/// own futures branch (<c>scoreMetric: SessionGatedDepthDurationConfirmed</c>), just driven bar by bar
/// live instead of over an already-populated day's rows in one pass. The 1300/8/30/8 config found
/// promising in a later sweep is deliberately NOT used here -- this class trades the config actually
/// being tracked for continued out-of-sample validation, per the live-wiring task's own explicit
/// instruction.
/// </summary>
public sealed class LiveFuturesCrossoverSession
{
    /// <summary>Locked config -- see this class's own doc comment. Not a parameter: this class trades exactly one locked configuration, same "not a general-purpose live simulator" design <see cref="TradingDaySession"/> itself documents.</summary>
    public const int FastBars = 8;

    public const int SlowBars = 40;

    public const double ThresholdPoints = 5.0;

    static readonly TimeSpan EntryWindowStart = new(9, 30, 0);
    static readonly TimeSpan EntryWindowEnd = new(15, 0, 0);
    static readonly TimeSpan ForceCloseAt = new(15, 15, 0);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public DateOnly AsOfDate { get; }

    public long BarVolumeThreshold { get; }

    /// <summary>-1 until the first bar is processed -- the next call to <see cref="ProcessBar"/> must carry BarIndex 0. Same contract as <see cref="TradingDaySession.LastProcessedBarIndex"/>.</summary>
    public int LastProcessedBarIndex { get; private set; } = -1;

    public bool HasOpenSignal => _open is not null;

    readonly SessionRankTracker _depthRank = new();
    readonly SessionRankTracker _durationRank = new();
    readonly SessionRankTracker _tobRank = new();

    /// <summary>The rolling window of the last (up to) <see cref="SlowBars"/> SCALED scores, oldest first -- same FIFO shape <c>TradeSimulator.SimulateCrossoverDayAsync</c>'s own <c>scoreWindow</c> uses. A bar with no reading (null score) contributes nothing, same "don't fabricate a signal that isn't there" convention every tracker in this codebase follows.</summary>
    readonly List<double> _scoreWindow = new(SlowBars);

    double? _previousDiff;
    decimal? _previousClose;
    OpenSignal? _open;

    public LiveFuturesCrossoverSession(DateOnly asOfDate, long barVolumeThreshold)
    {
        AsOfDate = asOfDate;
        BarVolumeThreshold = barVolumeThreshold;
    }

    /// <summary>
    /// Restart-safety: rebuilds this session's tracker/rolling-window/open-signal state for
    /// <paramref name="asOfDate"/> by replaying every bar already scored (per
    /// <see cref="LiveFuturesCrossoverScoreRow"/>'s own persisted rows, the "how far did we already
    /// get" bookmark) back through a fresh instance -- identical shape and reasoning to
    /// <see cref="TradingDaySession.RebuildAsync"/>. Returns a brand-new, never-scored session if
    /// nothing has been scored yet today.
    /// </summary>
    public static async Task<LiveFuturesCrossoverSession> RebuildAsync(VolumeBarDbContext db, DateOnly asOfDate, long barVolumeThreshold, CancellationToken ct)
    {
        var maxScored = await db.LiveFuturesCrossoverScoreBars
            .Where(r => r.AsOfDate == asOfDate && r.BarVolumeThreshold == barVolumeThreshold)
            .Select(r => (int?)r.BarIndex)
            .MaxAsync(ct) ?? -1;

        var session = new LiveFuturesCrossoverSession(asOfDate, barVolumeThreshold);
        if (maxScored < 0)
        {
            return session;
        }

        var bars = await db.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BarIndex <= maxScored)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(ct);

        foreach (var bar in bars)
        {
            session.ProcessBar(bar);
        }

        return session;
    }

    /// <summary>
    /// Scores one bar and applies the crossover entry/exit rule set, exactly once, in order. Throws
    /// if <paramref name="bar"/>'s BarIndex isn't exactly <see cref="LastProcessedBarIndex"/> + 1 --
    /// same "never reprocess a bar" discipline <see cref="TradingDaySession.ProcessBar"/> enforces.
    /// </summary>
    public LiveCrossoverBarResult ProcessBar(VolumeBarRow bar)
    {
        if (bar.BarIndex != LastProcessedBarIndex + 1)
        {
            throw new InvalidOperationException(
                $"LiveFuturesCrossoverSession({AsOfDate:yyyy-MM-dd}, threshold={BarVolumeThreshold}) expected BarIndex {LastProcessedBarIndex + 1} next, got {bar.BarIndex} -- bars must be fed in order, exactly once.");
        }

        var timeOfDayIst = bar.EndTimestamp.ToOffset(IstOffset).TimeOfDay;

        var inputs = new FuturesSessionGatedScoreInputs(timeOfDayIst, bar.ClosePrice, _previousClose, bar.DurationSeconds, bar.FutureDepthImbalance, bar.TopOfBookImbalance);
        var rawScore = FuturesSessionGatedScoreCalculator.ComputeScore(inputs, _depthRank, _durationRank);
        var tobConfirmScore = FuturesSessionGatedScoreCalculator.ComputeTobConfirmationScore(inputs, _tobRank);

        _previousClose = bar.ClosePrice;
        LastProcessedBarIndex = bar.BarIndex;

        var scaledScore = rawScore is { } r ? 100.0 * r : (double?)null;

        double? fastMa = null;
        double? slowMa = null;
        double? diff = null;
        var crossedUp = false;
        var crossedDown = false;

        if (scaledScore is { } sc)
        {
            _scoreWindow.Add(sc);
            if (_scoreWindow.Count > SlowBars)
            {
                _scoreWindow.RemoveAt(0);
            }

            if (_scoreWindow.Count >= SlowBars)
            {
                fastMa = _scoreWindow.Skip(_scoreWindow.Count - FastBars).Average();
                slowMa = _scoreWindow.Average();
                diff = fastMa - slowMa;

                if (_previousDiff is { } prevDiff && diff is { } d)
                {
                    crossedUp = prevDiff <= 0 && d > 0 && Math.Abs(d) >= ThresholdPoints;
                    crossedDown = prevDiff >= 0 && d < 0 && Math.Abs(d) >= ThresholdPoints;
                }

                _previousDiff = diff;
            }
        }

        var scoreRow = new LiveFuturesCrossoverScoreRow
        {
            AsOfDate = AsOfDate,
            BarIndex = bar.BarIndex,
            BarVolumeThreshold = BarVolumeThreshold,
            EndTimestamp = bar.EndTimestamp,
            RawScore = rawScore,
            ScaledScore = scaledScore,
            FastMa = fastMa,
            SlowMa = slowMa,
            Diff = diff,
            CrossedUp = crossedUp,
            CrossedDown = crossedDown,
            TobConfirmScore = tobConfirmScore,
        };

        LiveSignalOpened? opened = null;
        LiveSignalClosed? closed = null;

        if (_open is { } open)
        {
            // Same two exit conditions SimulateCrossoverDayAsync's own futures branch checks
            // (stop-loss doesn't apply -- the locked crossover config runs with no stop-loss;
            // EndOfData doesn't apply here either -- FlushEndOfDay is this session's own analog).
            var timedOut = timeOfDayIst >= ForceCloseAt;
            var reversed = open.Side == OptionType.Call ? crossedDown : crossedUp;

            if (timedOut || reversed)
            {
                closed = new LiveSignalClosed(open.EntryBarIndex, bar.BarIndex, bar.EndTimestamp, timedOut ? "TimeCutoff" : "CrossoverReversed");
                _open = null;
            }
        }
        else if ((crossedUp || crossedDown)
            && timeOfDayIst >= EntryWindowStart && timeOfDayIst <= EntryWindowEnd
            // Same Open-window TOB confirmation gate SessionGatedDepthDurationConfirmed's own
            // percentile-threshold entry requires -- see FuturesSessionGatedScoreCalculator
            // .PassesTobConfirmation's own doc comment. scaledScore is guaranteed non-null here
            // (crossedUp/crossedDown can only be true when the rolling window just produced a diff,
            // which requires a real scaledScore this bar).
            && FuturesSessionGatedScoreCalculator.PassesTobConfirmation(timeOfDayIst, crossedUp ? 1.0 : -1.0, tobConfirmScore))
        {
            var side = crossedUp ? OptionType.Call : OptionType.Put;
            _open = new OpenSignal(bar.BarIndex, bar.EndTimestamp, side);
            // EntryScore is the fast/slow gap at the crossing bar (diff), same convention
            // SimulateCrossoverDayAsync's own VolumeBarTrade.EntryScore uses for this metric --
            // NOT a percentile (this strategy has no percentile concept, see
            // LiveEntrySignalRow.EntryPercentile's own doc comment).
            opened = new LiveSignalOpened(bar.BarIndex, bar.EndTimestamp, side, diff ?? 0.0, EntryPercentile: 0.0);
        }

        return new LiveCrossoverBarResult(scoreRow, opened, closed);
    }

    /// <summary>Same day-boundary safety net as <see cref="TradingDaySession.FlushEndOfDay"/> -- call once, after the day's last bar has been processed.</summary>
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
