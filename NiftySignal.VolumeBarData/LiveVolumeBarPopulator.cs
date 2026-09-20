using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum LiveVolumeBarWriteOutcome
{
    Written,
    NoNewBars,
    NoTradableData,
}

public sealed record LiveVolumeBarWriteResult(LiveVolumeBarWriteOutcome Outcome, int RowCount);

/// <summary>
/// Live-path sibling of <see cref="VolumeBarPopulator"/> -- Phase A of docs/LIVE_PARITY_PLAN.md.
/// Deliberately NOT a stateful in-process tick subscriber: instead of hooking into
/// <c>MarketDataIngestionWorker</c>'s own tick loop (which already holds a serialization lock
/// around <c>LiveFeatureEngine</c> and has its own well-tested resilience/backpressure shape),
/// this REPLAYS today's future ticks from <see cref="NiftySignal.Persistence.NiftySignalDbContext.Ticks"/>
/// -- the same source table <see cref="VolumeBarPopulator"/> already reads offline, and the same
/// table <c>MarketDataIngestionWorker.FlushAsync</c> already writes ticks into live, batched every
/// ~1s/200 ticks -- through a FRESH <see cref="VolumeBarBuilder"/> on every call.
///
/// Why replay-from-scratch instead of an incrementally-updated builder: idempotency falls out for
/// free. A completed bar's own identity is fully determined by (AsOfDate, BarVolumeThreshold,
/// BarIndex) and the tick sequence up to that point, both of which are already durable in Postgres
/// -- there is no in-memory accumulator state to snapshot/restore across a Host restart. Restarting
/// mid-day simply re-derives the exact same bars 0..N from the same ticks and skips re-inserting
/// any BarIndex already present (see <paramref name="destination"/>'s own unique index on
/// (AsOfDate, BarVolumeThreshold, BarIndex), which backs this as a hard guarantee, not just a
/// best-effort check). The cost is re-scanning the day's ticks-so-far on every poll (a few thousand
/// to ~30k rows for the future alone by market close) -- cheap relative to the polling cadence this
/// is meant to run at (seconds, not sub-second), and deliberately traded for correctness simplicity
/// over a hand-rolled incremental-resume protocol.
/// </summary>
public static class LiveVolumeBarPopulator
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    /// <summary>UTC market-open boundary for <paramref name="asOfDate"/> -- same construction as <see cref="VolumeBarPopulator"/>'s own dayStart, exposed here so the Host caller and the option-side live populators can compute matching futureBars windows without re-deriving the IST/UTC conversion independently.</summary>
    public static DateTimeOffset DayStartUtc(DateOnly asOfDate) => new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();

    /// <summary>UTC market-close boundary for <paramref name="asOfDate"/> -- see <see cref="DayStartUtc"/>.</summary>
    public static DateTimeOffset DayEndUtc(DateOnly asOfDate) => new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();

    /// <summary>
    /// Replays every future tick received so far today and writes any bar not already persisted.
    /// Call repeatedly on a cadence (e.g. every few seconds) while the day is live; call once more
    /// with <paramref name="finalizeDay"/> = true after market close to flush the day's necessarily-
    /// partial final bar (see <see cref="VolumeBarBuilder.FlushPartial"/>) -- omitted on every other
    /// call, since flushing early would wrongly freeze a bar that real ticks were still going to
    /// extend.
    /// </summary>
    public static async Task<LiveVolumeBarWriteResult> WriteNewBarsAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold,
        DateTimeOffset nowUtc, bool finalizeDay, CancellationToken cancellationToken)
    {
        var future = await source.Instruments
            .FirstOrDefaultAsync(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future, cancellationToken);
        if (future is null)
        {
            return new LiveVolumeBarWriteResult(LiveVolumeBarWriteOutcome.NoTradableData, 0);
        }

        var dayStart = DayStartUtc(asOfDate);
        var dayEnd = DayEndUtc(asOfDate);
        var cutoff = finalizeDay ? dayEnd : (nowUtc < dayEnd ? nowUtc : dayEnd);

        // -1 when nothing persisted yet for this (day, threshold) -- every replayed bar's index
        // (0-based) is then > -1, so all of them get written.
        var existingMaxIndex = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .Select(b => (int?)b.BarIndex)
            .MaxAsync(cancellationToken) ?? -1;

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= cutoff)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var builder = new VolumeBarBuilder(barVolumeThreshold);
        var rows = new List<VolumeBarRow>();
        var barIndex = 0;
        var sawAnyTick = false;
        var lastTimestamp = dayStart;

        await foreach (var tick in ticks.WithCancellation(cancellationToken))
        {
            sawAnyTick = true;
            lastTimestamp = tick.ExchangeTimestamp;

            var bar = builder.ApplyTick(tick.ExchangeTimestamp, tick.LastPrice, tick.Volume, tick.Depth, tick.OpenInterest);
            if (bar is not null)
            {
                if (barIndex > existingMaxIndex)
                {
                    rows.Add(ToRow(bar, asOfDate, barIndex, barVolumeThreshold));
                }

                barIndex++;
            }
        }

        if (!sawAnyTick)
        {
            return new LiveVolumeBarWriteResult(LiveVolumeBarWriteOutcome.NoTradableData, 0);
        }

        if (finalizeDay)
        {
            var partial = builder.FlushPartial(lastTimestamp);
            if (partial is not null && barIndex > existingMaxIndex)
            {
                rows.Add(ToRow(partial, asOfDate, barIndex, barVolumeThreshold));
            }
        }

        if (rows.Count == 0)
        {
            return new LiveVolumeBarWriteResult(LiveVolumeBarWriteOutcome.NoNewBars, 0);
        }

        destination.VolumeBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);
        return new LiveVolumeBarWriteResult(LiveVolumeBarWriteOutcome.Written, rows.Count);
    }

    // Field-for-field identical to VolumeBarPopulator.ToRow -- kept as a separate copy rather than
    // shared so this file has no compile-time dependency on VolumeBarPopulator's own internals
    // (that class exposes no public mapping method), not because the mapping itself differs.
    static VolumeBarRow ToRow(VolumeBar bar, DateOnly asOfDate, int barIndex, long barVolumeThreshold) => new()
    {
        AsOfDate = asOfDate,
        BarIndex = barIndex,
        BarVolumeThreshold = barVolumeThreshold,
        StartTimestamp = bar.StartTimestamp,
        EndTimestamp = bar.EndTimestamp,
        DurationSeconds = bar.Duration.TotalSeconds,
        OpenPrice = bar.OpenPrice,
        HighPrice = bar.HighPrice,
        LowPrice = bar.LowPrice,
        ClosePrice = bar.ClosePrice,
        Volume = bar.Volume,
        OpenInterestAtClose = bar.OpenInterestAtClose,
        VwapAtClose = bar.VwapAtClose,
        FutureCvdNet = bar.FutureCvdNet,
        FutureDepthImbalance = bar.DepthImbalance,
        OrderFlowImbalance = bar.OrderFlowImbalance,
        TopOfBookImbalance = bar.TopOfBookImbalance,
    };
}
