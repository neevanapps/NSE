using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum VolumeBarPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record VolumeBarPopulationResult(VolumeBarPopulationOutcome Outcome, int RowCount);

/// <summary>
/// Builds one trading day's worth of <see cref="VolumeBarRow"/>s purely from raw historical
/// future ticks -- a single forward pass over the day's ticks in timestamp order, feeding
/// <see cref="VolumeBarBuilder"/> (the same pure, source-agnostic class a future live port would
/// use). Mirrors `NiftySignal.BacktestData.CadencePopulator`'s own shape (idempotency check, tick
/// streaming, UTC boundary handling) but deliberately simpler: futures-only, one instrument per
/// day, no option chain, no synthetic-forward solve -- see the 2026-09-17 plan's own "concentrate
/// on future first" scoping.
///
/// Idempotent per (<see cref="VolumeBarRow.AsOfDate"/>, <see cref="VolumeBarRow.BarVolumeThreshold"/>):
/// a day already populated at a given threshold is skipped entirely, never recomputed. Different
/// thresholds for the same day coexist as separate row sets, letting 650 vs 1300 (or any other
/// calibration) be compared without re-running anything.
/// </summary>
public static class VolumeBarPopulator
{
    public const string VolumeBarDatabaseName = "niftysignal_volume_bars";

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    public static async Task<VolumeBarPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken)
    {
        if (await destination.VolumeBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold, cancellationToken))
        {
            return new VolumeBarPopulationResult(VolumeBarPopulationOutcome.AlreadyPopulated, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered and explicitly ordered, offline
        // follow-up to F61 (which fixed LiveVolumeBarPopulator.cs but not this offline populator).
        // See NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment: a bare
        // FirstOrDefaultAsync with no filter/order would otherwise silently pick whichever
        // underlying's future Postgres happened to return first.
        var future = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (future is null)
        {
            return new VolumeBarPopulationResult(VolumeBarPopulationOutcome.NoTradableData, 0);
        }

        // Same Npgsql-UTC-only requirement as CadencePopulator.cs's own dayStart/dayEnd -- see
        // that class's own doc comment on why .ToUniversalTime() is required here.
        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
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
                rows.Add(ToRow(bar, asOfDate, barIndex++, barVolumeThreshold));
            }
        }

        if (!sawAnyTick)
        {
            return new VolumeBarPopulationResult(VolumeBarPopulationOutcome.NoTradableData, 0);
        }

        // The day's final, necessarily-partial bar -- see VolumeBarBuilder.FlushPartial's own doc
        // comment for why this would otherwise silently vanish rather than becoming a real row.
        var partial = builder.FlushPartial(lastTimestamp);
        if (partial is not null)
        {
            rows.Add(ToRow(partial, asOfDate, barIndex, barVolumeThreshold));
        }

        destination.VolumeBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new VolumeBarPopulationResult(VolumeBarPopulationOutcome.Populated, rows.Count);
    }

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
        TickCount = bar.TickCount,
    };
}
