using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum OptionDepthPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionDepthPopulationResult(OptionDepthPopulationOutcome Outcome, int RowCount);

/// <summary>Builds one <see cref="OptionDepthBarRow"/> per already-populated future <see cref="VolumeBarRow"/> -- see that entity's own doc comment for the band and the tick-accumulation shape.</summary>
public static class OptionDepthPopulator
{
    /// <summary>Default matches every other narrow-band options table (ATM±1 = 3 strikes) -- pass a wider value (e.g. 5 = ATM±2) explicitly to compare. See <see cref="OptionDepthBarRow"/>'s own doc comment for why this is a real parameter here, not a constant.</summary>
    public const int DefaultBandWidth = 3;

    public static async Task<OptionDepthPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        if (await destination.OptionDepthBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth, cancellationToken))
        {
            return new OptionDepthPopulationResult(OptionDepthPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionDepthPopulationResult(OptionDepthPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionDepthPopulationResult(OptionDepthPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        // Same per-bar band as OptionBandFlowPopulator/OptionOiPopulator: 3 strikes nearest THIS
        // bar's future close.
        var bandByBarIndex = futureBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(bandWidth).ToHashSet());

        var touchedStrikes = bandByBarIndex.Values.SelectMany(s => s).Distinct().ToList();

        async Task<Dictionary<int, (double? BidAvg, double? AskAvg, double? TobBidAvg, double? TobAskAvg)>> ComputeDepthByBarAsync(string token)
        {
            var dayStart = futureBars[0].StartTimestamp;
            var dayEnd = futureBars[^1].EndTimestamp;

            var ticks = await source.Ticks
                .AsNoTracking()
                .Where(t => t.Token == token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd && t.Depth != null)
                .OrderBy(t => t.ExchangeTimestamp)
                .Select(t => new { t.ExchangeTimestamp, t.Depth })
                .ToListAsync(cancellationToken);

            var result = new Dictionary<int, (double?, double?, double?, double?)>();
            var accumulator = new DepthImbalanceAccumulator();
            long tobBidSum = 0, tobAskSum = 0;
            var tobCount = 0;
            var barIndex = 0;

            void FlushBar()
            {
                result[futureBars[barIndex].BarIndex] = (
                    accumulator.AverageBidQty, accumulator.AverageAskQty,
                    tobCount > 0 ? (double)tobBidSum / tobCount : null,
                    tobCount > 0 ? (double)tobAskSum / tobCount : null);
                accumulator = new DepthImbalanceAccumulator();
                tobBidSum = 0;
                tobAskSum = 0;
                tobCount = 0;
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
                tobBidSum += tick.Depth!.Bid1Qty;
                tobAskSum += tick.Depth!.Ask1Qty;
                tobCount++;
            }

            if (barIndex < futureBars.Count)
            {
                FlushBar();
            }

            return result;
        }

        var callDepthByStrike = new Dictionary<decimal, Dictionary<int, (double?, double?, double?, double?)>>();
        var putDepthByStrike = new Dictionary<decimal, Dictionary<int, (double?, double?, double?, double?)>>();

        foreach (var strike in touchedStrikes)
        {
            var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            if (call is not null)
            {
                callDepthByStrike[strike] = await ComputeDepthByBarAsync(call.Token);
            }

            var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (put is not null)
            {
                putDepthByStrike[strike] = await ComputeDepthByBarAsync(put.Token);
            }
        }

        var rows = new List<OptionDepthBarRow>();
        foreach (var bar in futureBars)
        {
            var band = bandByBarIndex[bar.BarIndex];

            (double? Sum, bool Any) SumSide(Dictionary<decimal, Dictionary<int, (double? BidAvg, double? AskAvg, double? TobBidAvg, double? TobAskAvg)>> bySide, Func<(double? BidAvg, double? AskAvg, double? TobBidAvg, double? TobAskAvg), double?> pick)
            {
                double sum = 0;
                var any = false;
                foreach (var strike in band)
                {
                    if (bySide.TryGetValue(strike, out var byBar) && byBar.TryGetValue(bar.BarIndex, out var v) && pick(v) is { } value)
                    {
                        sum += value;
                        any = true;
                    }
                }

                return (any ? sum : null, any);
            }

            var (callBid, _) = SumSide(callDepthByStrike, v => v.BidAvg);
            var (callAsk, _) = SumSide(callDepthByStrike, v => v.AskAvg);
            var (putBid, _) = SumSide(putDepthByStrike, v => v.BidAvg);
            var (putAsk, _) = SumSide(putDepthByStrike, v => v.AskAvg);
            var (callTobBid, _) = SumSide(callDepthByStrike, v => v.TobBidAvg);
            var (callTobAsk, _) = SumSide(callDepthByStrike, v => v.TobAskAvg);
            var (putTobBid, _) = SumSide(putDepthByStrike, v => v.TobBidAvg);
            var (putTobAsk, _) = SumSide(putDepthByStrike, v => v.TobAskAvg);

            rows.Add(new OptionDepthBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                BandWidth = bandWidth,
                EndTimestamp = bar.EndTimestamp,
                CallBidQtyAvg = callBid,
                CallAskQtyAvg = callAsk,
                PutBidQtyAvg = putBid,
                PutAskQtyAvg = putAsk,
                CallTobBidQtyAvg = callTobBid,
                CallTobAskQtyAvg = callTobAsk,
                PutTobBidQtyAvg = putTobBid,
                PutTobAskQtyAvg = putTobAsk,
            });
        }

        destination.OptionDepthBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionDepthPopulationResult(OptionDepthPopulationOutcome.Populated, rows.Count);
    }
}
