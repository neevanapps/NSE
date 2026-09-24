using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (section 4). Builds one trading day's
/// <see cref="FutureEventBar"/> series from the NIFTY future's own raw ticks, cutting a new bar
/// every time cumulative traded volume since the previous bar's close reaches
/// <paramref name="thresholdContracts"/> in <see cref="BuildDayAsync"/>. In-memory only, rebuilt
/// per run -- no new database table, same convention <see cref="TimeBasedBarBuilder"/> and
/// <see cref="PerStrikeCadenceSimulator"/> already established earlier this session.
/// </summary>
public static class FutureEventBarBuilder
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    /// <summary>
    /// 2026-09-23, forensic-verification addendum (user's own explicit request, after the first
    /// 87-trade/1591-bar result): one raw tick's own contribution to the threshold/excess-carry
    /// accounting, so the exact arithmetic behind a bar close (and the carry into the next bar)
    /// can be read off directly rather than trusted from the aggregate <see cref="FutureEventBar"/>
    /// alone. Produced by the SAME accumulation loop that builds the bars (see
    /// <see cref="BuildDayWithTraceAsync"/>) -- not a re-implementation that could silently drift
    /// from what actually produced the bars.
    /// </summary>
    public sealed record TickTraceRow(
        DateTimeOffset Timestamp, long? PreviousCumulativeVolume, long CurrentCumulativeVolume, long Delta,
        long AccumulatorBefore, long Threshold, long AccumulatorAfter, int EventId, bool ClosesBar, long? Excess);

    public static async Task<List<FutureEventBar>> BuildDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate, long thresholdContracts, CancellationToken cancellationToken)
        => (await BuildDayWithTraceAsync(source, asOfDate, thresholdContracts, cancellationToken)).Bars;

    public static Task<(List<FutureEventBar> Bars, List<TickTraceRow> Trace)> BuildDayWithTraceAsync(
        NiftySignalDbContext source, DateOnly asOfDate, long thresholdContracts, CancellationToken cancellationToken)
        => BuildDayWithTraceForUnderlyingAsync(source, asOfDate, thresholdContracts, "NIFTY", cancellationToken);

    /// <summary>
    /// 2026-09-24, cross-index validation (Sensex). Identical bar-construction logic to
    /// <see cref="BuildDayAsync"/>/<see cref="BuildDayWithTraceAsync"/> (same threshold/excess-
    /// carry/VWAP arithmetic, same final-partial-bar rule) -- the only difference is the caller
    /// supplies which index's future to build from, instead of the fixed "NIFTY" filter.
    /// <see cref="BuildDayAsync"/>/<see cref="BuildDayWithTraceAsync"/> are unchanged (they now
    /// delegate here with "NIFTY" hardcoded) and remain the sole implementation every existing
    /// 0-DTE/DTE-expansion command uses.
    /// </summary>
    public static async Task<List<FutureEventBar>> BuildDayForUnderlyingAsync(
        NiftySignalDbContext source, DateOnly asOfDate, long thresholdContracts, string underlying, CancellationToken cancellationToken)
        => (await BuildDayWithTraceForUnderlyingAsync(source, asOfDate, thresholdContracts, underlying, cancellationToken)).Bars;

    public static async Task<(List<FutureEventBar> Bars, List<TickTraceRow> Trace)> BuildDayWithTraceForUnderlyingAsync(
        NiftySignalDbContext source, DateOnly asOfDate, long thresholdContracts, string underlying, CancellationToken cancellationToken)
    {
        if (thresholdContracts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdContracts), thresholdContracts, "thresholdContracts must be > 0.");
        }

        var future = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future && i.Underlying == underlying)
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (future is null)
        {
            return ([], []);
        }

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var bars = new List<FutureEventBar>();
        var trace = new List<TickTraceRow>();
        var eventId = 0;

        // Threshold-accounting carry (spec 4.1): the excess volume of the bar that just closed,
        // fed in as this bar's own starting accumulator balance -- see FutureEventBar.Volume's own
        // doc comment for why this is accounting-only, not a split of any tick's OHLC/VWAP
        // contribution.
        long carryIn = 0;

        long? previousVolume = null;
        DateTimeOffset? barStart = null;
        decimal? open = null, high = null, low = null, close = null;
        long? lastOi = null;
        var tickCount = 0;
        long accumulated = carryIn;
        double vwapPriceVolume = 0;
        double vwapVolume = 0;

        void ResetBarState()
        {
            barStart = null;
            open = high = low = close = null;
            tickCount = 0;
            vwapPriceVolume = 0;
            vwapVolume = 0;
        }

        await foreach (var tick in ticks.WithCancellation(cancellationToken))
        {
            var previousVolumeBeforeThisTick = previousVolume;
            var delta = previousVolumeBeforeThisTick is { } prev ? Math.Max(0, tick.Volume - prev) : 0;
            var accumulatorBefore = accumulated;
            previousVolume = tick.Volume;

            barStart ??= tick.ExchangeTimestamp;
            open ??= tick.LastPrice;
            high = high is { } h ? Math.Max(h, tick.LastPrice) : tick.LastPrice;
            low = low is { } l ? Math.Min(l, tick.LastPrice) : tick.LastPrice;
            close = tick.LastPrice;
            lastOi = tick.OpenInterest ?? lastOi;
            tickCount++;

            if (delta > 0)
            {
                vwapPriceVolume += (double)tick.LastPrice * delta;
                vwapVolume += delta;
            }

            accumulated += delta;
            var closesBar = accumulated >= thresholdContracts;
            long? excess = null;

            if (closesBar)
            {
                var vwap = vwapVolume > 0 ? vwapPriceVolume / vwapVolume : (double?)null;
                bars.Add(new FutureEventBar(
                    eventId, asOfDate, barStart!.Value, tick.ExchangeTimestamp,
                    open!.Value, high!.Value, low!.Value, close!.Value,
                    accumulated, vwap, lastOi, tickCount, IsFinalPartialBar: false));

                excess = accumulated - thresholdContracts;
                carryIn = excess.Value;
                accumulated = carryIn;
                ResetBarState();
            }

            trace.Add(new TickTraceRow(
                tick.ExchangeTimestamp, previousVolumeBeforeThisTick,
                tick.Volume, delta, accumulatorBefore, thresholdContracts, accumulated, eventId, closesBar, excess));

            if (closesBar)
            {
                eventId++;
            }
        }

        // Day's final, necessarily-partial bar (spec section 5) -- explicitly marked, never
        // silently folded in as a normal threshold-crossing bar.
        if (barStart is not null)
        {
            var vwap = vwapVolume > 0 ? vwapPriceVolume / vwapVolume : (double?)null;
            bars.Add(new FutureEventBar(
                eventId, asOfDate, barStart.Value, dayEnd,
                open!.Value, high!.Value, low!.Value, close!.Value,
                accumulated, vwap, lastOi, tickCount, IsFinalPartialBar: true));
        }

        return (bars, trace);
    }
}
