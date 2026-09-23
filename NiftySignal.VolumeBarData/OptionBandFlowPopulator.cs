using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum OptionBandFlowPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionBandFlowPopulationResult(OptionBandFlowPopulationOutcome Outcome, int RowCount);

/// <summary>
/// Builds one <see cref="OptionBandFlowBarRow"/> per already-populated future
/// <see cref="VolumeBarRow"/> -- see that entity's own doc comment for the band definition and
/// why this needs a tick-level flow accumulation rather than a point-in-time snapshot.
/// </summary>
public static class OptionBandFlowPopulator
{
    /// <summary>Default matches the original locked ATM±1 -- pass 5 (ATM±2) explicitly to compare. See <see cref="OptionBandFlowBarRow"/>'s own doc comment.</summary>
    public const int DefaultBandWidth = 3;

    public static async Task<OptionBandFlowPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        if (await destination.OptionBandFlowBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth, cancellationToken))
        {
            return new OptionBandFlowPopulationResult(OptionBandFlowPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionBandFlowPopulationResult(OptionBandFlowPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionBandFlowPopulationResult(OptionBandFlowPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        // Per-bar band: the 3 strikes nearest the FUTURE's own close at THAT bar (ATM +/- 1, per
        // the plan's own definition) -- different bars can reference different bands as price
        // drifts through the day.
        var bandByBarIndex = futureBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(bandWidth).ToHashSet());

        var touchedStrikes = bandByBarIndex.Values.SelectMany(s => s).Distinct().ToList();

        var callNotionalByStrike = new Dictionary<decimal, Dictionary<int, decimal>>();
        var putNotionalByStrike = new Dictionary<decimal, Dictionary<int, decimal>>();

        foreach (var strike in touchedStrikes)
        {
            var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            if (call is not null)
            {
                callNotionalByStrike[strike] = await ComputeNotionalByBarAsync(source, call.Token, futureBars, cancellationToken);
            }

            var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (put is not null)
            {
                putNotionalByStrike[strike] = await ComputeNotionalByBarAsync(source, put.Token, futureBars, cancellationToken);
            }
        }

        var rows = new List<OptionBandFlowBarRow>();
        foreach (var bar in futureBars)
        {
            var band = bandByBarIndex[bar.BarIndex];
            var callNotional = band.Sum(s => callNotionalByStrike.TryGetValue(s, out var d) && d.TryGetValue(bar.BarIndex, out var v) ? v : 0m);
            var putNotional = band.Sum(s => putNotionalByStrike.TryGetValue(s, out var d) && d.TryGetValue(bar.BarIndex, out var v) ? v : 0m);

            rows.Add(new OptionBandFlowBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                BandWidth = bandWidth,
                EndTimestamp = bar.EndTimestamp,
                CallNotionalVolume = callNotional,
                PutNotionalVolume = putNotional,
            });
        }

        destination.OptionBandFlowBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionBandFlowPopulationResult(OptionBandFlowPopulationOutcome.Populated, rows.Count);
    }

    /// <summary>
    /// One option instrument's own notional volume (tick volume-delta x tick price, same
    /// cumulative-volume-diffing <see cref="FutureFlowAccumulator"/> already uses for the future
    /// itself, reused here rather than reimplemented) bucketed into whichever future bar's time
    /// window each tick falls into -- a single ordered pass merging this instrument's own ticks
    /// against the future's already-fixed bar boundaries.
    /// </summary>
    static async Task<Dictionary<int, decimal>> ComputeNotionalByBarAsync(
        NiftySignalDbContext source, string token, List<VolumeBarRow> futureBars, CancellationToken cancellationToken)
    {
        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = futureBars[futureBars.Count - 1].EndTimestamp;

        var ticks = await source.Ticks
            .AsNoTracking()
            .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .Select(t => new { t.ExchangeTimestamp, t.LastPrice, t.Volume })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, decimal>();
        var flow = new FutureFlowAccumulator();
        var barIndex = 0;
        var barNotional = 0m;

        foreach (var tick in ticks)
        {
            while (barIndex < futureBars.Count && tick.ExchangeTimestamp > futureBars[barIndex].EndTimestamp)
            {
                result[futureBars[barIndex].BarIndex] = barNotional;
                barNotional = 0m;
                barIndex++;
            }

            if (barIndex >= futureBars.Count)
            {
                break;
            }

            flow.ApplyTick(tick.LastPrice, tick.Volume);
            barNotional += flow.LastVolumeDelta * tick.LastPrice;
        }

        if (barIndex < futureBars.Count)
        {
            result[futureBars[barIndex].BarIndex] = barNotional;
        }

        return result;
    }
}
