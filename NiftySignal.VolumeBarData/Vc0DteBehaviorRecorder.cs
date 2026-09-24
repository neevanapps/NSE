using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (sections 17-23, 40). The PRIMARY research
/// output for Experiment 1 (spec section 47): behaviour observations, not trade P&amp;L. For every
/// fast/slow crossover on the DYNAMIC-ATM Call or Put price, records what that specific
/// SIGNALLING CONTRACT's own price does next -- pinned to that contract even if a later bar's ATM
/// strike moves elsewhere.
///
/// IMPORTANT (see the plan's own "important distinction" note): this class studies the
/// dynamically-selected ATM contract at each event, then follows THAT SPECIFIC CONTRACT forward.
/// <see cref="Vc0DteTradeSimulator"/> instead selects and holds whichever contract's live premium
/// falls in [100,150] at signal time -- often a DIFFERENT contract for the same signal event. The
/// two are separate, non-comparable views by design; do not conflate a behaviour finding here with
/// a claim about what the simulator would have traded.
/// </summary>
public static class Vc0DteBehaviorRecorder
{
    static readonly int[] ForwardHorizons = [1, 2, 3, 5, 10];

    /// <summary>
    /// 2026-09-23, multi-day behaviour validation addendum (user's own explicit request). The
    /// first 17 fields (through <see cref="TimeToMaximumAdverseMs"/>) are UNCHANGED from the
    /// original single-day recorder -- same formulas, same pinning-to-signalling-contract
    /// discipline, nothing replaced. Everything from <see cref="FutureCloseAtSignal"/> onward is
    /// ADDITIVE, answering the user's new points 4-9 without touching what already existed:
    /// <list type="bullet">
    /// <item>Percent-based <see cref="ForwardReturn1"/> etc. can be extreme on a cheap option
    /// premium (spec point 4's own worked example, signal=Rs2.00/low=Rs0.02 => -99% but only
    /// -Rs1.98) -- <see cref="AbsoluteForwardReturn1"/> etc. and
    /// <see cref="MaximumFavorableMoveAbs"/>/<see cref="MaximumAdverseMoveAbs"/> report the SAME
    /// underlying move in raw Rs, using the exact same formula/pinning, just not divided by
    /// <see cref="PriceAtSignal"/>.</item>
    /// <item><see cref="FutureForwardReturnPercent1"/> etc. -- the Nifty FUTURE's own percent move
    /// over the same event-bar horizons, for the underlying-vs-option comparison (point 5) --
    /// never contract-pinned (there is only one future), null only when the horizon runs past the
    /// day's last bar.</item>
    /// <item><see cref="PreSignalOptionMovePercent3"/> etc. / <see cref="PreSignalFutureMovePercent3"/>
    /// etc. -- the move BEFORE the signal bar, same pinned-contract convention for the option leg,
    /// for the lead/lag question (point 6). Null when eventId-N is before the start of the day.</item>
    /// <item><see cref="EventBarsToMaximumFavorable"/>/<see cref="EventBarsToMaximumAdverse"/> --
    /// the SAME extreme-tick timestamps <see cref="TimeToMaximumFavorableMs"/>/
    /// <see cref="TimeToMaximumAdverseMs"/> already located, expressed as an event-bar OFFSET from
    /// the signal bar (point 3's "time/event-bars to MFE/MAE") by finding which future event bar's
    /// own interval contains that tick -- not a new measurement, a second unit on the same one.</item>
    /// <item><see cref="FastWindowDurationMs"/>/<see cref="SlowWindowDurationMs"/> -- the real
    /// wall-clock span the fast/slow window actually covered for THIS observation (point 9), so
    /// "fast vs slow event-time activity" can be read directly per observation rather than assumed
    /// from the pooled distribution alone.</item>
    /// </list>
    /// </summary>
    public sealed record ObservationRow(
        int ObservationId, DateOnly TradingDate, int Dte, int EventId, DateTimeOffset SignalTimestamp,
        decimal Strike, OptionType OptionType, double FastValue, double SlowValue, string SignalDirection,
        decimal PriceAtSignal,
        decimal? ForwardReturn1, decimal? ForwardReturn2, decimal? ForwardReturn3, decimal? ForwardReturn5, decimal? ForwardReturn10,
        decimal MaximumFavorableMovePercent, decimal MaximumAdverseMovePercent,
        double? TimeToMaximumFavorableMs, double? TimeToMaximumAdverseMs,
        decimal FutureCloseAtSignal,
        decimal? FutureForwardReturnPercent1, decimal? FutureForwardReturnPercent2, decimal? FutureForwardReturnPercent3,
        decimal? FutureForwardReturnPercent5, decimal? FutureForwardReturnPercent10,
        decimal? AbsoluteForwardReturn1, decimal? AbsoluteForwardReturn2, decimal? AbsoluteForwardReturn3,
        decimal? AbsoluteForwardReturn5, decimal? AbsoluteForwardReturn10,
        decimal MaximumFavorableMoveAbs, decimal MaximumAdverseMoveAbs,
        decimal? PreSignalOptionMovePercent3, decimal? PreSignalOptionMovePercent5, decimal? PreSignalOptionMovePercent10,
        decimal? PreSignalFutureMovePercent3, decimal? PreSignalFutureMovePercent5, decimal? PreSignalFutureMovePercent10,
        int? EventBarsToMaximumFavorable, int? EventBarsToMaximumAdverse,
        double FastWindowDurationMs, double SlowWindowDurationMs);

    /// <param name="fastBars">Baseline 3 (spec section 17/52).</param>
    /// <param name="slowBars">Baseline 10 (spec section 17/52).</param>
    /// <param name="thresholdFraction">0 (baseline) -- any real sign flip on the AverageLtp fast/slow SMA qualifies; a nonzero value can be swept later without touching this method.</param>
    public static async Task<List<ObservationRow>> RecordAsync(
        NiftySignalDbContext source, DateOnly asOfDate,
        IReadOnlyList<Domain.Entities.Instrument> chain,
        IReadOnlyList<FutureEventBar> futureBars, IReadOnlyList<SynchronizedOptionEventBar> optionBars,
        CancellationToken cancellationToken, int fastBars = 3, int slowBars = 10, double thresholdFraction = 0.0)
    {
        var rows = new List<ObservationRow>();
        if (futureBars.Count == 0)
        {
            return rows;
        }

        var barsByToken = optionBars.GroupBy(b => b.Token).ToDictionary(g => g.Key, g => g.OrderBy(b => b.EventId).ToList());

        var observationId = 0;
        foreach (var side in new[] { OptionType.Call, OptionType.Put })
        {
            var engine = new PriceCrossoverEngine(fastBars, slowBars);
            string? lastAtmToken = null;
            var sideChain = chain.Where(c => c.OptionType == side).ToList();

            for (var eventId = 0; eventId < futureBars.Count; eventId++)
            {
                var futureBar = futureBars[eventId];
                var atm = EventBarStrikeSelector.PickDynamicAtm(sideChain, side, futureBar.Close);
                if (atm is null)
                {
                    continue;
                }

                if (lastAtmToken is not null && lastAtmToken != atm.Token)
                {
                    // Correctness fix already established for the unrelated time-based track
                    // (docs/Price_Based_Findings.md's "Correctness review" section): never let the
                    // rolling window mix two different contracts' premiums.
                    engine.Reset();
                }
                lastAtmToken = atm.Token;

                if (!barsByToken.TryGetValue(atm.Token, out var atmBars))
                {
                    continue;
                }
                var atmBar = atmBars.FirstOrDefault(b => b.EventId == eventId);
                if (atmBar is null)
                {
                    continue;
                }

                var step = engine.Observe(atmBar.AverageLtp is { } ltp ? (double)ltp : null, thresholdFraction);
                if (!step.CrossedUp && !step.CrossedDown)
                {
                    continue;
                }

                var priceAtSignal = atmBar.AverageLtp!.Value;
                var direction = step.CrossedUp ? "Bullish" : "Bearish";

                decimal? ForwardReturn(int horizon)
                {
                    var future = atmBars.FirstOrDefault(b => b.EventId == eventId + horizon);
                    if (future?.AverageLtp is not { } futurePrice || priceAtSignal == 0)
                    {
                        return null;
                    }
                    return (futurePrice - priceAtSignal) / priceAtSignal * 100m;
                }

                // Absolute (Rs) counterpart -- same pinned-contract lookup, no percent division.
                // Point 4's own worked example: a cheap contract's percent MAE can look extreme
                // (signal=Rs2.00, low=Rs0.02 => -99%) while the actual Rs loss is small (-Rs1.98)
                // -- both views are kept, neither replaces the other.
                decimal? AbsoluteForwardReturn(int horizon)
                {
                    var future = atmBars.FirstOrDefault(b => b.EventId == eventId + horizon);
                    return future?.AverageLtp is { } futurePrice ? futurePrice - priceAtSignal : null;
                }

                // The Nifty FUTURE's own move over the same horizons -- never contract-pinned
                // (there is only one future instrument), null only past the day's last bar. For
                // point 5 (does the crossover track the underlying, the option premium, or
                // neither).
                var futureCloseAtSignal = futureBar.Close;
                decimal? FutureForwardReturn(int horizon)
                {
                    var idx = eventId + horizon;
                    if (idx < 0 || idx >= futureBars.Count || futureCloseAtSignal == 0)
                    {
                        return null;
                    }
                    return (futureBars[idx].Close - futureCloseAtSignal) / futureCloseAtSignal * 100m;
                }

                // The move BEFORE the signal (point 6, lead/lag) -- same pinned-contract
                // convention for the option leg (this SAME token's own earlier bars, which exist
                // regardless of the engine's own warm-up/reset history, since the bar builder
                // covers the whole day for every strike).
                decimal? PreSignalOptionMove(int horizon)
                {
                    var pastIdx = eventId - horizon;
                    if (pastIdx < 0)
                    {
                        return null;
                    }
                    var past = atmBars.FirstOrDefault(b => b.EventId == pastIdx);
                    return past?.AverageLtp is { } pastPrice && pastPrice != 0 ? (priceAtSignal - pastPrice) / pastPrice * 100m : null;
                }
                decimal? PreSignalFutureMove(int horizon)
                {
                    var pastIdx = eventId - horizon;
                    if (pastIdx < 0 || futureCloseAtSignal == 0)
                    {
                        return null;
                    }
                    return (futureCloseAtSignal - futureBars[pastIdx].Close) / futureBars[pastIdx].Close * 100m;
                }

                // Real wall-clock span the fast/slow window covered for THIS observation (point
                // 9) -- from the window's own first bar's start to the signal bar's own end. The
                // engine only ever signals once fully warmed, so eventId-slowBars+1 is always
                // >= 0 here (never needs a null check).
                var fastWindowDurationMs = (futureBar.EndTimestamp - futureBars[eventId - fastBars + 1].StartTimestamp).TotalMilliseconds;
                var slowWindowDurationMs = (futureBar.EndTimestamp - futureBars[eventId - slowBars + 1].StartTimestamp).TotalMilliseconds;

                var (mfePercent, maePercent, mfeAbs, maeAbs, timeToMfeMs, timeToMaeMs) = await ComputeMfeMaeAsync(
                    source, atm.Token, atmBar.EndTimestamp, priceAtSignal, asOfDate, cancellationToken);

                // Same extreme-tick timestamps, expressed as an event-bar offset from the signal
                // (point 3's "time/event-bars to MFE/MAE") -- find which future bar's own interval
                // contains that tick, never a re-measurement.
                int? EventBarsTo(double? afterMs) => afterMs is { } ms
                    ? FindEventBarContaining(futureBars, atmBar.EndTimestamp + TimeSpan.FromMilliseconds(ms)) - eventId
                    : null;

                rows.Add(new ObservationRow(
                    observationId++, asOfDate, atmBar.Dte, eventId, atmBar.EndTimestamp,
                    atm.StrikePrice!.Value, side, step.FastMa!.Value, step.SlowMa!.Value, direction,
                    priceAtSignal,
                    ForwardReturn(1), ForwardReturn(2), ForwardReturn(3), ForwardReturn(5), ForwardReturn(10),
                    mfePercent, maePercent, timeToMfeMs, timeToMaeMs,
                    futureCloseAtSignal,
                    FutureForwardReturn(1), FutureForwardReturn(2), FutureForwardReturn(3), FutureForwardReturn(5), FutureForwardReturn(10),
                    AbsoluteForwardReturn(1), AbsoluteForwardReturn(2), AbsoluteForwardReturn(3), AbsoluteForwardReturn(5), AbsoluteForwardReturn(10),
                    mfeAbs, maeAbs,
                    PreSignalOptionMove(3), PreSignalOptionMove(5), PreSignalOptionMove(10),
                    PreSignalFutureMove(3), PreSignalFutureMove(5), PreSignalFutureMove(10),
                    EventBarsTo(timeToMfeMs), EventBarsTo(timeToMaeMs),
                    fastWindowDurationMs, slowWindowDurationMs));
            }
        }

        return rows;
    }

    /// <summary>The EventId of the future bar whose own [Start,End] interval contains <paramref name="timestamp"/> -- falls back to the last bar if the timestamp is at/after the day's final close (can happen for a tick exactly at session end).</summary>
    static int FindEventBarContaining(IReadOnlyList<FutureEventBar> futureBars, DateTimeOffset timestamp)
    {
        for (var i = 0; i < futureBars.Count; i++)
        {
            if (timestamp <= futureBars[i].EndTimestamp)
            {
                return futureBars[i].EventId;
            }
        }
        return futureBars[^1].EventId;
    }

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketClose = new(15, 30);

    static async Task<(decimal MfePercent, decimal MaePercent, decimal MfeAbs, decimal MaeAbs, double? TimeToMfeMs, double? TimeToMaeMs)> ComputeMfeMaeAsync(
        NiftySignalDbContext source, string token, DateTimeOffset signalTimestamp, decimal entryPrice, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();
        var series = await OptionTickSeries.LoadAsync(source, token, signalTimestamp, dayEnd, cancellationToken);

        decimal mfePoints = 0, maePoints = 0;
        double? timeToMfeMs = null, timeToMaeMs = null;

        foreach (var entry in series.AllEntries)
        {
            var move = entry.LastPrice - entryPrice;
            if (move > mfePoints)
            {
                mfePoints = move;
                timeToMfeMs = (entry.Timestamp - signalTimestamp).TotalMilliseconds;
            }
            if (-move > maePoints)
            {
                maePoints = -move;
                timeToMaeMs = (entry.Timestamp - signalTimestamp).TotalMilliseconds;
            }
        }

        var mfePercent = entryPrice != 0 ? mfePoints / entryPrice * 100m : 0m;
        var maePercent = entryPrice != 0 ? maePoints / entryPrice * 100m : 0m;
        return (mfePercent, maePercent, mfePoints, maePoints, timeToMfeMs, timeToMaeMs);
    }

    public static void WriteCsv(IReadOnlyList<ObservationRow> rows, string path, TimeSpan istOffset)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("ObservationId,TradingDate,Dte,EventId,SignalTimeIST,Strike,OptionType,FastValue,SlowValue,SignalDirection,PriceAtSignal,"
            + "ForwardReturn1,ForwardReturn2,ForwardReturn3,ForwardReturn5,ForwardReturn10,MaximumFavorableMovePercent,MaximumAdverseMovePercent,TimeToMaximumFavorableMs,TimeToMaximumAdverseMs,"
            + "FutureCloseAtSignal,FutureForwardReturnPercent1,FutureForwardReturnPercent2,FutureForwardReturnPercent3,FutureForwardReturnPercent5,FutureForwardReturnPercent10,"
            + "AbsoluteForwardReturn1,AbsoluteForwardReturn2,AbsoluteForwardReturn3,AbsoluteForwardReturn5,AbsoluteForwardReturn10,MaximumFavorableMoveAbs,MaximumAdverseMoveAbs,"
            + "PreSignalOptionMovePercent3,PreSignalOptionMovePercent5,PreSignalOptionMovePercent10,PreSignalFutureMovePercent3,PreSignalFutureMovePercent5,PreSignalFutureMovePercent10,"
            + "EventBarsToMaximumFavorable,EventBarsToMaximumAdverse,FastWindowDurationMs,SlowWindowDurationMs");
        foreach (var r in rows)
        {
            var ist = r.SignalTimestamp.ToOffset(istOffset);
            writer.WriteLine(string.Join(',',
                r.ObservationId, r.TradingDate.ToString("yyyy-MM-dd"), r.Dte, r.EventId, ist.ToString("HH:mm:ss.fff"), r.Strike, r.OptionType,
                r.FastValue.ToString("F4"), r.SlowValue.ToString("F4"), r.SignalDirection, r.PriceAtSignal,
                r.ForwardReturn1, r.ForwardReturn2, r.ForwardReturn3, r.ForwardReturn5, r.ForwardReturn10,
                r.MaximumFavorableMovePercent.ToString("F4"), r.MaximumAdverseMovePercent.ToString("F4"), r.TimeToMaximumFavorableMs, r.TimeToMaximumAdverseMs,
                r.FutureCloseAtSignal, r.FutureForwardReturnPercent1, r.FutureForwardReturnPercent2, r.FutureForwardReturnPercent3, r.FutureForwardReturnPercent5, r.FutureForwardReturnPercent10,
                r.AbsoluteForwardReturn1, r.AbsoluteForwardReturn2, r.AbsoluteForwardReturn3, r.AbsoluteForwardReturn5, r.AbsoluteForwardReturn10,
                r.MaximumFavorableMoveAbs, r.MaximumAdverseMoveAbs,
                r.PreSignalOptionMovePercent3, r.PreSignalOptionMovePercent5, r.PreSignalOptionMovePercent10,
                r.PreSignalFutureMovePercent3, r.PreSignalFutureMovePercent5, r.PreSignalFutureMovePercent10,
                r.EventBarsToMaximumFavorable, r.EventBarsToMaximumAdverse, r.FastWindowDurationMs, r.SlowWindowDurationMs));
        }
    }
}
