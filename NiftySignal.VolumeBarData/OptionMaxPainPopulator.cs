using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum OptionMaxPainPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionMaxPainPopulationResult(OptionMaxPainPopulationOutcome Outcome, int RowCount);

/// <summary>Builds one <see cref="OptionMaxPainBarRow"/> per already-populated future <see cref="VolumeBarRow"/> -- see that entity's own doc comment for why this spans the full chain rather than a windowed band.</summary>
public static class OptionMaxPainPopulator
{
    public static async Task<OptionMaxPainPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken)
    {
        if (await destination.OptionMaxPainBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold, cancellationToken))
        {
            return new OptionMaxPainPopulationResult(OptionMaxPainPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionMaxPainPopulationResult(OptionMaxPainPopulationOutcome.NoTradableData, 0);
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionMaxPainPopulationResult(OptionMaxPainPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = futureBars[^1].EndTimestamp;

        // Whole-chain OI series, loaded ONCE per instrument for the whole day (not per bar) --
        // same shared-cache shape every other populator in this file already uses.
        var callOiByStrike = new Dictionary<decimal, OptionOiSeries>();
        var putOiByStrike = new Dictionary<decimal, OptionOiSeries>();
        foreach (var strike in distinctStrikes)
        {
            var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            if (call is not null)
            {
                callOiByStrike[strike] = await OptionOiSeries.LoadAsync(source, call.Token, dayStart, dayEnd, cancellationToken);
            }

            var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (put is not null)
            {
                putOiByStrike[strike] = await OptionOiSeries.LoadAsync(source, put.Token, dayStart, dayEnd, cancellationToken);
            }
        }

        var rows = new List<OptionMaxPainBarRow>();
        foreach (var bar in futureBars)
        {
            var callOiAtBar = new Dictionary<decimal, long>();
            var putOiAtBar = new Dictionary<decimal, long>();
            foreach (var strike in distinctStrikes)
            {
                if (callOiByStrike.TryGetValue(strike, out var callSeries) && callSeries.OiAtOrBefore(bar.EndTimestamp) is { } callOi)
                {
                    callOiAtBar[strike] = callOi;
                }

                if (putOiByStrike.TryGetValue(strike, out var putSeries) && putSeries.OiAtOrBefore(bar.EndTimestamp) is { } putOi)
                {
                    putOiAtBar[strike] = putOi;
                }
            }

            decimal? maxPainStrike = null;
            decimal? highestOiStrike = null;

            if (callOiAtBar.Count > 0 || putOiAtBar.Count > 0)
            {
                var bestPayout = decimal.MaxValue;
                var bestCombinedOi = -1L;

                foreach (var candidate in distinctStrikes)
                {
                    // Total payout option WRITERS owe if the underlying settles at `candidate` --
                    // sum of each call's intrinsic value (candidate above that call's strike) times
                    // its OI, plus each put's intrinsic value (candidate below that put's strike)
                    // times its OI. Max pain minimizes this.
                    var payout = 0m;
                    foreach (var (strike, oi) in callOiAtBar)
                    {
                        if (candidate > strike)
                        {
                            payout += (candidate - strike) * oi;
                        }
                    }

                    foreach (var (strike, oi) in putOiAtBar)
                    {
                        if (candidate < strike)
                        {
                            payout += (strike - candidate) * oi;
                        }
                    }

                    if (payout < bestPayout)
                    {
                        bestPayout = payout;
                        maxPainStrike = candidate;
                    }

                    var combinedOi = callOiAtBar.GetValueOrDefault(candidate) + putOiAtBar.GetValueOrDefault(candidate);
                    if (combinedOi > bestCombinedOi)
                    {
                        bestCombinedOi = combinedOi;
                        highestOiStrike = candidate;
                    }
                }
            }

            rows.Add(new OptionMaxPainBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                EndTimestamp = bar.EndTimestamp,
                MaxPainStrike = maxPainStrike,
                HighestOiStrike = highestOiStrike,
            });
        }

        destination.OptionMaxPainBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionMaxPainPopulationResult(OptionMaxPainPopulationOutcome.Populated, rows.Count);
    }
}
