using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum DepthImbalanceSumPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record DepthImbalanceSumPopulationResult(DepthImbalanceSumPopulationOutcome Outcome, int RowCount);

/// <summary>Builds one <see cref="DepthImbalanceSumBarRow"/> per already-populated future <see cref="VolumeBarRow"/>, for ONE side (Call or Put) -- sum-accumulated (not averaged), band selection mirrors <see cref="OptionDepthPopulator"/> (nearest-<see cref="BandWidth"/> strikes to THIS bar's own future close).</summary>
public static class DepthImbalanceSumPopulator
{
    public const int DefaultBandWidth = 3;

    public static async Task<DepthImbalanceSumPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, OptionType side, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        if (await destination.DepthImbalanceSumBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == side, cancellationToken))
        {
            return new DepthImbalanceSumPopulationResult(DepthImbalanceSumPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new DepthImbalanceSumPopulationResult(DepthImbalanceSumPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.OptionType == side && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new DepthImbalanceSumPopulationResult(DepthImbalanceSumPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();
        var daysToExpiry = (nearestExpiry.ToDateTime(TimeOnly.MinValue).Date - asOfDate.ToDateTime(TimeOnly.MinValue).Date).Days;

        var bandByBarIndex = futureBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(bandWidth).ToHashSet());

        var atmStrikeByBarIndex = futureBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).First());

        var touchedStrikes = bandByBarIndex.Values.SelectMany(s => s).Distinct().ToList();

        async Task<Dictionary<int, (double RawDiffSum, double NotionalDiffSum, bool AnyTick)>> ComputeByBarAsync(string token)
        {
            var dayStart = futureBars[0].StartTimestamp;
            var dayEnd = futureBars[^1].EndTimestamp;

            var ticks = await source.Ticks
                .AsNoTracking()
                .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd && t.Depth != null)
                .OrderBy(t => t.ExchangeTimestamp)
                .Select(t => new { t.ExchangeTimestamp, t.Depth })
                .ToListAsync(cancellationToken);

            var result = new Dictionary<int, (double, double, bool)>();
            var accumulator = new DepthImbalanceSumAccumulator();
            var anyTick = false;
            var barIndex = 0;

            void FlushBar()
            {
                result[futureBars[barIndex].BarIndex] = (accumulator.RawQtyDiffSum, accumulator.NotionalDiffSum, anyTick);
                accumulator = new DepthImbalanceSumAccumulator();
                anyTick = false;
            }

            foreach (var tick in ticks)
            {
                while (barIndex < futureBars.Count && tick.ExchangeTimestamp > futureBars[barIndex].EndTimestamp)
                {
                    FlushBar();
                    barIndex++;
                }

                if (barIndex >= futureBars.Count)
                {
                    break;
                }

                accumulator.ApplyTick(tick.Depth!);
                anyTick = true;
            }

            if (barIndex < futureBars.Count)
            {
                FlushBar();
            }

            return result;
        }

        var byStrike = new Dictionary<decimal, Dictionary<int, (double, double, bool)>>();
        foreach (var strike in touchedStrikes)
        {
            var opt = chain.FirstOrDefault(o => o.StrikePrice == strike);
            if (opt is not null)
            {
                byStrike[strike] = await ComputeByBarAsync(opt.Token);
            }
        }

        var rows = new List<DepthImbalanceSumBarRow>();
        foreach (var bar in futureBars)
        {
            var band = bandByBarIndex[bar.BarIndex];
            var atmStrike = atmStrikeByBarIndex[bar.BarIndex];
            var atmOption = chain.First(o => o.StrikePrice == atmStrike);

            double rawSum = 0, notionalSum = 0;
            var any = false;
            foreach (var strike in band)
            {
                if (byStrike.TryGetValue(strike, out var byBar) && byBar.TryGetValue(bar.BarIndex, out var v) && v.Item3)
                {
                    rawSum += v.Item1;
                    notionalSum += v.Item2;
                    any = true;
                }
            }

            rows.Add(new DepthImbalanceSumBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                BandWidth = bandWidth,
                Side = side,
                EndTimestamp = bar.EndTimestamp,
                FutureClosePrice = bar.ClosePrice,
                RawQtyDiffSum = any ? rawSum : null,
                NotionalDiffSum = any ? notionalSum : null,
                AtmToken = atmOption.Token,
                AtmStrike = atmStrike,
                DaysToExpiry = daysToExpiry,
            });
        }

        destination.DepthImbalanceSumBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new DepthImbalanceSumPopulationResult(DepthImbalanceSumPopulationOutcome.Populated, rows.Count);
    }
}
