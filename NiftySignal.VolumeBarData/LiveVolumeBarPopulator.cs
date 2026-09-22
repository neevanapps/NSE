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
/// ~1s/200 ticks.
///
/// Idempotency comes from <paramref name="destination"/>'s own unique index on (AsOfDate,
/// BarVolumeThreshold, BarIndex): a completed bar's identity is fully determined by that triple and
/// the tick sequence up to that point, both durable in Postgres, so re-deriving bars 0..N from the
/// same ticks and skipping any BarIndex already present is always safe, restart or not.
///
/// Live performance incident (docs/LIVE_PARITY_PLAN.md, dated entry): the ORIGINAL version of this
/// method always replayed every future tick since market open through a brand-new
/// <see cref="VolumeBarBuilder"/> on every poll, deliberately traded for correctness simplicity over
/// a hand-rolled incremental-resume protocol -- proven live to be the wrong trade-off: bar-write-to-
/// score lag grew from ~1.7s to ~9-10s over the first 20 minutes of a real trading day as the
/// ticks-so-far replay cost grew unboundedly with the day. Fixed by <paramref name="builderCache"/>:
/// when supplied and warm, only ticks NEWER than the last one already fed into the cached builder are
/// queried and applied, continuing to accumulate on the SAME <see cref="VolumeBarBuilder"/> instance
/// -- same <c>existing</c>-parameter-based incremental/fallback-to-full-reload shape already proven
/// for the option-side series by <see cref="LiveOptionSeriesCache"/>. <paramref name="builderCache"/>
/// null (the default) reproduces the original from-scratch-every-call behavior exactly, unchanged for
/// every existing caller that doesn't pass one (offline tools, tests).
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
    /// Replays every future tick received so far today (or, with a warm <paramref name="builderCache"/>,
    /// only the ticks newer than the last poll already processed) and writes any bar not already
    /// persisted. Call repeatedly on a cadence (e.g. every few seconds) while the day is live; call
    /// once more with <paramref name="finalizeDay"/> = true after market close to flush the day's
    /// necessarily-partial final bar (see <see cref="VolumeBarBuilder.FlushPartial"/>) -- omitted on
    /// every other call, since flushing early would wrongly freeze a bar that real ticks were still
    /// going to extend.
    /// </summary>
    /// <param name="builderCache">
    /// Live performance fix (docs/LIVE_PARITY_PLAN.md): when supplied, the in-progress
    /// <see cref="VolumeBarBuilder"/> for (asOfDate, barVolumeThreshold) is kept warm across polls --
    /// this call then only queries and applies ticks strictly newer than the last one already fed
    /// into it, instead of re-replaying the whole day-so-far. Null (the default) reproduces the
    /// original from-scratch-every-call behavior exactly, unchanged for every existing caller (offline
    /// tools, tests) that doesn't pass one. See <see cref="LiveVolumeBarBuilderCache"/>'s own doc
    /// comment for the full incremental-resume/restart-safety shape.
    /// </param>
    public static async Task<LiveVolumeBarWriteResult> WriteNewBarsAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold,
        DateTimeOffset nowUtc, bool finalizeDay, CancellationToken cancellationToken, LiveVolumeBarBuilderCache? builderCache = null)
    {
        // Audit finding F61 (2026-09-22): NIFTY-filtered and explicitly ordered -- see
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment for the ambiguity
        // this closes now that the Instruments table can also hold Sensex/Bank Nifty rows for the
        // same AsOfDate. A bare FirstOrDefaultAsync with no filter/order (the previous code here)
        // would otherwise silently pick whichever underlying's future Postgres happened to return
        // first, corrupting this exact live-parity future-bar pipeline with no error.
        var future = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);
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

        var cached = builderCache?.Get(asOfDate, barVolumeThreshold);
        var builder = cached?.Builder ?? new VolumeBarBuilder(barVolumeThreshold);
        var barIndex = cached?.NextBarIndex ?? 0;
        var queryStart = cached?.LastTickTimestamp ?? dayStart;
        var queryStartTickId = cached?.LastTickId ?? long.MinValue;
        var resuming = cached is not null;

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token
                && (resuming
                    ? (t.ExchangeTimestamp > queryStart || (t.ExchangeTimestamp == queryStart && t.Id > queryStartTickId))
                    : t.ExchangeTimestamp >= queryStart)
                && t.ExchangeTimestamp <= cutoff)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var rows = new List<VolumeBarRow>();
        var sawNewTick = false;
        var lastTimestamp = queryStart;
        var lastTickId = queryStartTickId;

        await foreach (var tick in ticks.WithCancellation(cancellationToken))
        {
            sawNewTick = true;
            lastTimestamp = tick.ExchangeTimestamp;
            lastTickId = tick.Id;

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

        // Cold start (no cache, or nothing cached yet for today): "no ticks at all" genuinely means
        // no tradable data yet. Resuming from a warm cache: a poll with zero NEW ticks is completely
        // normal (nothing traded between polls) -- it was already proven tradable by a previous poll.
        if (!resuming && !sawNewTick)
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

            // The day is over -- this builder must never be fed another tick (FlushPartial has
            // already reset its internal accumulator for a bar that will never be completed).
            // Discarding here also means a Host that somehow polls again the same day starts a
            // correct fresh full replay rather than resuming a finalized builder.
            builderCache?.Clear(asOfDate, barVolumeThreshold);
        }
        else if (builderCache is not null)
        {
            builderCache.Set(asOfDate, barVolumeThreshold, new LiveVolumeBarBuilderCache.State
            {
                Builder = builder,
                NextBarIndex = barIndex,
                LastTickTimestamp = lastTimestamp,
                LastTickId = lastTickId,
            });
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
