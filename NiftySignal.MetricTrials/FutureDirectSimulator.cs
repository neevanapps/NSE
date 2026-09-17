using NiftySignal.BacktestData;
using NiftySignal.Features;

namespace NiftySignal.MetricTrials;

/// <summary>
/// Standing thumb-rule for every exploratory metric-trial simulation, per user instruction
/// (2026-09-12) -- applies regardless of which metric or gate is under test, until a finalized
/// combined score exists, at which point real risk restrictions (MaxConcurrentPositions, daily
/// loss caps, trade-count limits) come back. None of those apply here on purpose.
/// </summary>
public static class SimulationRules
{
    public static readonly TimeOnly FirstEntryTime = new(9, 30); // 15 minutes after the 09:15 open
    public static readonly TimeOnly LastEntryTime = new(15, 0); // no NEW entries after 3:00 PM
    public static readonly TimeOnly ForceCloseTime = new(15, 15); // every open position closed here, no exceptions
}

public sealed record SimulationOptions(
    double EntryRankCutoff = 85.0,
    double ExitRankCutoff = 50.0,
    bool UseTrendEfficiencyGate = false,
    double TrendEfficiencyGateRankCutoff = 50.0,
    TimeSpan TrendEfficiencyWindow = default,
    // 2026-09-12: entries stay on the fast (5-min) signal for timing -- exits can be switched to
    // the slower (15-min) signal instead, same "fast entry, slow confirm/exit" shape already
    // validated once in this project (the F55 fast/slow momentum work). Every exit trade in the
    // first run lasted 1-8 minutes, almost all via SignalDecay -- plausibly because a 5-minute
    // rolling sum churns fast enough that its own rank crosses the exit threshold almost
    // immediately. Both FutureCvdProxyNet5Min and FutureCvdProxyNet15Min already exist and are
    // populated -- this needed zero new metric-building, just a different exit wiring.
    bool UseSlowExitSignal = false)
{
    public TimeSpan EffectiveTrendEfficiencyWindow => TrendEfficiencyWindow == default ? TimeSpan.FromMinutes(15) : TrendEfficiencyWindow;
}

public sealed record SimulatedTrade(
    DateTimeOffset EntryTime, decimal EntryPrice, int Direction,
    DateTimeOffset ExitTime, decimal ExitPrice, string ExitReason)
{
    /// <summary>Points, signed by direction -- the tracked future's own price units. No lot size, no transaction costs (spread/slippage/brokerage) modeled: this is a directional-edge test, not a P&L simulation.</summary>
    public decimal NetPnlPoints => Direction * (ExitPrice - EntryPrice);
}

public sealed record DaySimulationResult(DateOnly AsOfDate, IReadOnlyList<SimulatedTrade> Trades)
{
    public decimal NetPnlPoints => Trades.Sum(t => t.NetPnlPoints);
    public int WinCount => Trades.Count(t => t.NetPnlPoints > 0);

    /// <summary>Total time spent holding a position, summed across every trade this day -- the direct measure of the "time in market" concern that motivated the always-positioned mode.</summary>
    public TimeSpan TimeInMarket => Trades.Aggregate(TimeSpan.Zero, (sum, t) => sum + (t.ExitTime - t.EntryTime));
}

/// <summary>
/// 2026-09-12: entry/exit-threshold trading (<see cref="FutureDirectSimulator.SimulateDay"/>)
/// structurally cannot deliver high time-in-market -- it only holds a position while the signal
/// is extreme, by design. Measured directly: even the best threshold-based variant covered ~22%
/// of the session; three others sat at 12-16%. To get anywhere near continuous coverage, the
/// system has to be positioned essentially all the time and simply flip direction on a reversal,
/// rather than going flat whenever the signal isn't extreme. This is a genuinely different
/// strategy shape, not a slower version of the threshold approach -- see
/// docs/REVIEW_FINDINGS.md's 2026-09-12 "always positioned" section for the full reasoning.
/// </summary>
public sealed record AlwaysPositionedOptions(
    bool UseSlowSignal = true, // FutureCvdProxyNet15Min by default -- "longer timeframe" per the user's own stated philosophy (fewer, higher-conviction trades)
    double EmaSpanCadences = 20.0, // ~5 minutes at the 15s cadence -- smooths the direction signal so it doesn't flip on the kind of sub-minute noise already observed in the slow-exit trial
    // 2026-09-12: the un-gated version's own result named the problem directly -- 09 Sep alone
    // produced more net P&L than the entire 4-day total, and 10 Sep (the choppiest day by trend
    // efficiency, diagnosed two turns ago) was the worst performer of any day in any variant
    // tried so far. Gating flips on trend efficiency is the direct synthesis of both findings:
    // hold the current direction (don't flip) while the market isn't actually trending, only
    // follow a direction change once trend efficiency itself says a real move is underway. This
    // reuses RollingTrendEfficiency/SessionRankTracker unchanged -- the exact same gate already
    // built and tested for SimulateDay, just applied to flips instead of threshold entries.
    bool UseTrendEfficiencyGate = false,
    double TrendEfficiencyGateRankCutoff = 50.0,
    TimeSpan TrendEfficiencyWindow = default)
{
    public TimeSpan EffectiveTrendEfficiencyWindow => TrendEfficiencyWindow == default ? TimeSpan.FromMinutes(15) : TrendEfficiencyWindow;
}

/// <summary>
/// Tests whether <see cref="CadenceContext.FutureCvdProxyNet5Min"/> (currently the strongest
/// candidate found on this dataset -- see docs/REVIEW_FINDINGS.md's 2026-09-12 sections) has
/// real directional edge, by simulating long/short trades on the tracked future itself. Entry
/// magnitude/direction: same session-rank-based dynamic-threshold pattern already validated in
/// NiftySignal.Backtest's price-led DynamicHybrid mode -- rank |signal| against its own
/// session-so-far distribution for selectivity, sign of the raw value for direction. No
/// hardcoded score magnitude anywhere, per standing instruction.
/// </summary>
public static class FutureDirectSimulator
{
    public static DaySimulationResult SimulateDay(IReadOnlyList<CadenceContext> dayRows, SimulationOptions options)
    {
        if (dayRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayRows));
        }

        var asOfDate = dayRows[0].AsOfDate;
        // Separate trackers for the entry and exit signals -- when UseSlowExitSignal is set,
        // these are two DIFFERENT series (5-min vs 15-min net) with their own distributions;
        // ranking a 15-minute sum against a 5-minute sum's history would be meaningless.
        var entrySignalRank = new SessionRankTracker();
        var exitSignalRank = new SessionRankTracker();
        var trendRank = new SessionRankTracker();
        var trendEfficiency = new RollingTrendEfficiency(options.EffectiveTrendEfficiencyWindow);

        var trades = new List<SimulatedTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, int Direction)? open = null;

        foreach (var row in dayRows.OrderBy(r => r.Timestamp))
        {
            var localTime = TimeOnly.FromTimeSpan(row.Timestamp.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
            var entryWindowOpen = localTime >= SimulationRules.FirstEntryTime && localTime < SimulationRules.LastEntryTime;
            var mustForceClose = localTime >= SimulationRules.ForceCloseTime;

            // Trend-efficiency gate is computed every cadence with a price, regardless of
            // whether either signal is present this cadence -- it's a price-only measure.
            double? currentTrendEfficiency = null;
            if (row.FutureCloseFromLastCadence is { } priceForTrend)
            {
                currentTrendEfficiency = trendEfficiency.Observe(row.Timestamp, priceForTrend);
                if (currentTrendEfficiency is { } te)
                {
                    trendRank.Add(te);
                }
            }

            if (open is { } position && mustForceClose)
            {
                var closePrice = row.FutureCloseFromLastCadence ?? position.EntryPrice;
                trades.Add(new SimulatedTrade(position.EntryTime, position.EntryPrice, position.Direction, row.Timestamp, closePrice, "ForceClose"));
                open = null;
                continue;
            }

            if (row.FutureCloseFromLastCadence is not { } price)
            {
                continue;
            }

            // Entry always reads the fast (5-min) signal, regardless of which series exits use.
            if (row.FutureCvdProxyNet5Min is { } entrySignal)
            {
                entrySignalRank.Add(Math.Abs((double)entrySignal));
            }

            var exitSignalRaw = options.UseSlowExitSignal ? row.FutureCvdProxyNet15Min : row.FutureCvdProxyNet5Min;
            if (exitSignalRaw is { } exitSignalForRank)
            {
                exitSignalRank.Add(Math.Abs((double)exitSignalForRank));
            }

            if (open is { } openPosition)
            {
                // A cadence where the chosen exit series happens to be null makes no exit
                // decision at all this cadence -- the position simply stays open, same "skip,
                // don't guess" discipline as everywhere else in this dataset.
                if (exitSignalRaw is { } exitSignal)
                {
                    var absExit = Math.Abs((double)exitSignal);
                    var exitThreshold = exitSignalRank.ValueAtPercentile(options.ExitRankCutoff);
                    var signalDecayed = exitThreshold is { } et && absExit < et;
                    var signFlipped = Math.Sign(exitSignal) != openPosition.Direction && Math.Sign(exitSignal) != 0;

                    if (signalDecayed || signFlipped)
                    {
                        trades.Add(new SimulatedTrade(openPosition.EntryTime, openPosition.EntryPrice, openPosition.Direction, row.Timestamp, price, signFlipped ? "SignFlip" : "SignalDecay"));
                        open = null;
                    }
                }
            }
            else if (entryWindowOpen && row.FutureCvdProxyNet5Min is { } signalForEntry)
            {
                var absSignal = Math.Abs((double)signalForEntry);
                var entryThreshold = entrySignalRank.ValueAtPercentile(options.EntryRankCutoff);
                var qualifies = entryThreshold is { } th && absSignal >= th && Math.Sign(signalForEntry) != 0;

                if (qualifies && options.UseTrendEfficiencyGate)
                {
                    qualifies = currentTrendEfficiency is { } te && trendRank.Rank(te) >= options.TrendEfficiencyGateRankCutoff;
                }

                if (qualifies)
                {
                    open = (row.Timestamp, price, Math.Sign(signalForEntry));
                }
            }
        }

        // Should never fire given the ForceCloseTime check above runs every cadence including
        // the last one of the day -- kept as a defensive close, not a silent dropped position.
        if (open is { } stillOpen)
        {
            var lastRow = dayRows[^1];
            var closePrice = lastRow.FutureCloseFromLastCadence ?? stillOpen.EntryPrice;
            trades.Add(new SimulatedTrade(stillOpen.EntryTime, stillOpen.EntryPrice, stillOpen.Direction, lastRow.Timestamp, closePrice, "EndOfData"));
        }

        return new DaySimulationResult(asOfDate, trades);
    }

    /// <summary>
    /// Continuously positioned: long or short from the first eligible cadence through to
    /// force-close, flipping direction on every sign change of an EMA-smoothed signal instead of
    /// entering/exiting on rank thresholds. Guarantees near-total time-in-market by construction
    /// -- there is no "flat while waiting for an extreme reading" state once the first position
    /// opens. A direction flip is NOT treated as a "new entry" for the no-new-entries-after-3pm
    /// rule -- that rule exists to stop opening fresh exposure from flat late in the day; a flip
    /// is a direction change on exposure that's already open, a different thing. The very first
    /// position of the day still can't open before 09:30 or after 15:00, same as everywhere else.
    /// </summary>
    public static DaySimulationResult SimulateDayAlwaysPositioned(IReadOnlyList<CadenceContext> dayRows, AlwaysPositionedOptions options)
    {
        if (dayRows.Count == 0)
        {
            throw new ArgumentException("A day's rows must be non-empty.", nameof(dayRows));
        }

        var asOfDate = dayRows[0].AsOfDate;
        var ema = new EmaSmoother(options.EmaSpanCadences);
        var trendRank = new SessionRankTracker();
        var trendEfficiency = new RollingTrendEfficiency(options.EffectiveTrendEfficiencyWindow);
        var trades = new List<SimulatedTrade>();
        (DateTimeOffset EntryTime, decimal EntryPrice, int Direction)? open = null;

        foreach (var row in dayRows.OrderBy(r => r.Timestamp))
        {
            var localTime = TimeOnly.FromTimeSpan(row.Timestamp.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay);
            var firstEntryWindowOpen = localTime >= SimulationRules.FirstEntryTime && localTime < SimulationRules.LastEntryTime;
            var mustForceClose = localTime >= SimulationRules.ForceCloseTime;

            // Trend efficiency is computed every cadence with a price, regardless of the
            // direction signal's own null/warm-up state -- it's a price-only measure, same as in
            // SimulateDay.
            double? currentTrendEfficiency = null;
            if (row.FutureCloseFromLastCadence is { } priceForTrend)
            {
                currentTrendEfficiency = trendEfficiency.Observe(row.Timestamp, priceForTrend);
                if (currentTrendEfficiency is { } te)
                {
                    trendRank.Add(te);
                }
            }

            if (open is { } position && mustForceClose)
            {
                var closePrice = row.FutureCloseFromLastCadence ?? position.EntryPrice;
                trades.Add(new SimulatedTrade(position.EntryTime, position.EntryPrice, position.Direction, row.Timestamp, closePrice, "ForceClose"));
                open = null;
                continue;
            }

            if (row.FutureCloseFromLastCadence is not { } price)
            {
                continue;
            }

            var rawSignal = options.UseSlowSignal ? row.FutureCvdProxyNet15Min : row.FutureCvdProxyNet5Min;
            if (rawSignal is not { } signal)
            {
                // No reading this cadence -- hold whatever direction is already open rather than
                // guessing; the EMA itself is also simply not updated (skip, don't zero-fill).
                continue;
            }

            var smoothed = ema.Observe((double)signal);
            var direction = Math.Sign(smoothed);
            if (direction == 0)
            {
                continue; // a perfectly neutral EMA reading is rare and gives no direction to act on
            }

            // Gate applies to BOTH the first entry and every later flip -- the ungated run's own
            // result is what motivated this: nearly all of it came from one trending day, and the
            // worst day (10 Sep) was the choppiest one by this exact measure. When the gate fails,
            // an already-open position simply keeps holding its current direction rather than
            // flipping on a reversal the market itself isn't confirming.
            var trendGatePasses = !options.UseTrendEfficiencyGate
                || (currentTrendEfficiency is { } gateTe && trendRank.Rank(gateTe) >= options.TrendEfficiencyGateRankCutoff);

            if (open is null)
            {
                if (!firstEntryWindowOpen || !trendGatePasses)
                {
                    continue; // the day's first-ever position still can't open before 09:30/after 15:00, or while the market isn't trending
                }

                open = (row.Timestamp, price, direction);
            }
            else if (direction != open.Value.Direction && trendGatePasses)
            {
                trades.Add(new SimulatedTrade(open.Value.EntryTime, open.Value.EntryPrice, open.Value.Direction, row.Timestamp, price, "Flip"));
                open = (row.Timestamp, price, direction);
            }
        }

        if (open is { } stillOpen)
        {
            var lastRow = dayRows[^1];
            var closePrice = lastRow.FutureCloseFromLastCadence ?? stillOpen.EntryPrice;
            trades.Add(new SimulatedTrade(stillOpen.EntryTime, stillOpen.EntryPrice, stillOpen.Direction, lastRow.Timestamp, closePrice, "EndOfData"));
        }

        return new DaySimulationResult(asOfDate, trades);
    }
}

/// <summary>
/// |net price change| / total price distance traveled over the trailing <paramref name="window"/>
/// -- near 1 means a clean trend, near 0 means constant back-and-forth with little net progress.
/// This is the exact quantity that told apart 11 Sep (0.060 for the whole day, strongly trending)
/// from 10 Sep (0.022, the choppiest day -- see docs/REVIEW_FINDINGS.md's 2026-09-12 diagnosis),
/// computed here as a live rolling window rather than a whole-day retrospective number, since a
/// gate has to decide in real time, not with hindsight of how the day turned out.
/// </summary>
public sealed class RollingTrendEfficiency(TimeSpan window)
{
    readonly Queue<(DateTimeOffset Timestamp, decimal Price)> _points = new();

    /// <summary>Feeds one more price observation and returns the current trend efficiency -- null until at least two points exist in the window, or if the window's own path length is zero (a perfectly flat price).</summary>
    public double? Observe(DateTimeOffset timestamp, decimal price)
    {
        _points.Enqueue((timestamp, price));
        while (_points.Count > 0 && timestamp - _points.Peek().Timestamp > window)
        {
            _points.Dequeue();
        }

        if (_points.Count < 2)
        {
            return null;
        }

        var ordered = _points.ToArray();
        var netChange = Math.Abs((double)(ordered[^1].Price - ordered[0].Price));

        var totalPath = 0.0;
        for (var i = 1; i < ordered.Length; i++)
        {
            totalPath += Math.Abs((double)(ordered[i].Price - ordered[i - 1].Price));
        }

        return totalPath > 0 ? netChange / totalPath : null;
    }
}

/// <summary>
/// Standard exponential moving average, span-parameterized (alpha = 2/(span+1), the usual
/// span-to-alpha conversion) so the smoothing strength is expressed in "roughly how many cadences
/// of memory" rather than a raw alpha most readers would have to convert in their head. Used to
/// smooth the direction-determining signal in <see cref="FutureDirectSimulator.SimulateDayAlwaysPositioned"/>
/// -- without an entry threshold filtering out noise, every sign change becomes a real trade, so
/// the signal needs to be smooth enough that it doesn't flip on the kind of sub-minute noise
/// already observed when the slow-exit trial's sign-flip check reacted to the raw (unsmoothed)
/// FutureCvdProxyNet15Min directly.
/// </summary>
public sealed class EmaSmoother(double spanCadences)
{
    readonly double _alpha = 2.0 / (spanCadences + 1.0);
    double? _current;

    public double? Current => _current;

    public double Observe(double value)
    {
        _current = _current is { } previous ? (_alpha * value) + ((1 - _alpha) * previous) : value;
        return _current.Value;
    }
}
