using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum CvdProxySumPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record CvdProxySumPopulationResult(CvdProxySumPopulationOutcome Outcome, int RowCount);

/// <summary>Builds one <see cref="CvdProxySumBarRow"/> per already-populated future <see cref="VolumeBarRow"/>, for ONE side (Call or Put) -- sum-accumulated quote-rule CVD proxy, band selection mirrors <see cref="DepthImbalanceSumPopulator"/> (nearest-<see cref="BandWidth"/> strikes to THIS bar's own future close).</summary>
public static class CvdProxySumPopulator
{
    public const int DefaultBandWidth = 3;

    public static async Task<CvdProxySumPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, OptionType side, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        if (await destination.CvdProxySumBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == side, cancellationToken))
        {
            return new CvdProxySumPopulationResult(CvdProxySumPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new CvdProxySumPopulationResult(CvdProxySumPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.OptionType == side && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new CvdProxySumPopulationResult(CvdProxySumPopulationOutcome.NoTradableData, 0);
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

        async Task<Dictionary<int, (double RawSum, double NotionalSum, bool AnyTick)>> ComputeByBarAsync(string token)
        {
            var dayStart = futureBars[0].StartTimestamp;
            var dayEnd = futureBars[^1].EndTimestamp;

            var ticks = await source.Ticks
                .AsNoTracking()
                .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
                .OrderBy(t => t.ExchangeTimestamp)
                .Select(t => new { t.ExchangeTimestamp, t.LastPrice, t.Volume, t.Depth })
                .ToListAsync(cancellationToken);

            var result = new Dictionary<int, (double, double, bool)>();
            var accumulator = new CvdProxySumAccumulator();
            var anyTick = false;
            var barIndex = 0;
            long? previousVolume = null;

            void FlushBar()
            {
                result[futureBars[barIndex].BarIndex] = (accumulator.RawCvdSum, accumulator.NotionalCvdSum, anyTick);
                accumulator = new CvdProxySumAccumulator();
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

                // Volume is cumulative for the whole day -- the delta (and hence the classification
                // baseline) spans bar boundaries, same "day-scoped, not bar-scoped" convention
                // FutureCvdProxyAccumulator's own live caller already uses. First tick of the day
                // for this token has no prior baseline, so it contributes nothing (not a guessed 0).
                if (previousVolume is { } prev)
                {
                    var volumeDelta = tick.Volume - prev;
                    if (volumeDelta > 0)
                    {
                        accumulator.ApplyTick(tick.LastPrice, tick.Depth, volumeDelta);
                        anyTick = true;
                    }
                }

                previousVolume = tick.Volume;
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

        var rows = new List<CvdProxySumBarRow>();
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

            rows.Add(new CvdProxySumBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                BandWidth = bandWidth,
                Side = side,
                EndTimestamp = bar.EndTimestamp,
                FutureClosePrice = bar.ClosePrice,
                RawCvdSum = any ? rawSum : null,
                NotionalCvdSum = any ? notionalSum : null,
                AtmToken = atmOption.Token,
                AtmStrike = atmStrike,
                DaysToExpiry = daysToExpiry,
            });
        }

        destination.CvdProxySumBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new CvdProxySumPopulationResult(CvdProxySumPopulationOutcome.Populated, rows.Count);
    }
}
