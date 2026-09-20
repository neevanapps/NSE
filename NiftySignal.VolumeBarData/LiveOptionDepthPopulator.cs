using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum LiveOptionDepthWriteOutcome
{
    Written,
    NoNewBars,
    NoTradableData,
}

public sealed record LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome Outcome, int RowCount);

/// <summary>Live-path sibling of <see cref="OptionDepthPopulator"/> -- see <see cref="LiveVolumeBarPopulator"/>'s own doc comment for the shared idempotent-replay shape and <see cref="LiveOptionAtmPopulator"/>'s for the live-data-breadth feasibility note.</summary>
public static class LiveOptionDepthPopulator
{
    public const int DefaultBandWidth = OptionDepthPopulator.DefaultBandWidth;

    public static async Task<LiveOptionDepthWriteResult> WriteNewBarsAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);
        if (futureBars.Count == 0)
        {
            return new LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome.NoTradableData, 0);
        }

        var existingMaxIndex = await destination.OptionDepthBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth)
            .Select(b => (int?)b.BarIndex)
            .MaxAsync(cancellationToken) ?? -1;

        if (futureBars[^1].BarIndex <= existingMaxIndex)
        {
            return new LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome.NoNewBars, 0);
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(cancellationToken);
        if (allOptions.Count == 0)
        {
            return new LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        // Only the PENDING bars need a band/row at all -- computed against every futureBar in the
        // dictionary comprehension sense would be wasted work for bars whose OptionDepthBarRow
        // already exists.
        var pendingBars = futureBars.Where(b => b.BarIndex > existingMaxIndex).ToList();
        var bandByBarIndex = pendingBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(bandWidth).ToHashSet());

        var touchedStrikes = bandByBarIndex.Values.SelectMany(s => s).Distinct().ToList();

        // Each future bar's own depth accumulator RESETS at that bar's boundary (see
        // OptionDepthBarRow's own doc comment: "average across THIS bar's tick window," not
        // cumulative) -- so ticks belonging to an already-persisted bar can never affect a pending
        // bar's own row. Scoping the query to start right after the last already-written bar's own
        // EndTimestamp (dayStart if nothing persisted yet) turns this from an O(whole day so far)
        // rescan on every poll into an O(new ticks since the last poll) one -- the genuinely
        // incremental shape the rest of this file's own doc comments describe, not just idempotent.
        // The offline populator's own tick range is INCLUSIVE of dayStart (`>= dayStart`), since the
        // very first tick of the day IS the first bar's own first sample -- but each subsequent
        // bar's ticks must start strictly AFTER the previous bar's own EndTimestamp (`>`), since a
        // tick landing exactly on a bar boundary already got counted as that closing bar's own last
        // sample (see the offline populator's own `tick.ExchangeTimestamp > futureBars[barIndex].EndTimestamp`
        // walk). Getting this backwards for bar 0 specifically (excluding dayStart itself) was a
        // real off-by-one caught by the replay-parity harness -- see docs/LIVE_PARITY_PLAN.md's
        // Phase A section.
        var scanStart = existingMaxIndex >= 0 ? futureBars[existingMaxIndex].EndTimestamp : futureBars[0].StartTimestamp;
        var scanStartInclusive = existingMaxIndex < 0;
        var scanEnd = pendingBars[^1].EndTimestamp;

        async Task<Dictionary<int, (double? BidAvg, double? AskAvg, double? TobBidAvg, double? TobAskAvg)>> ComputeDepthByBarAsync(string token)
        {
            var ticks = await source.Ticks
                .AsNoTracking()
                .Where(t => t.Token == token && (scanStartInclusive ? t.ExchangeTimestamp >= scanStart : t.ExchangeTimestamp > scanStart) && t.ExchangeTimestamp <= scanEnd && t.Depth != null)
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
                result[pendingBars[barIndex].BarIndex] = (
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
                while (barIndex < pendingBars.Count && tick.ExchangeTimestamp > pendingBars[barIndex].EndTimestamp)
                {
                    FlushBar();
                    barIndex++;
                }

                if (barIndex >= pendingBars.Count)
                {
                    break;
                }

                accumulator.ApplyTick(tick.Depth!);
                tobBidSum += tick.Depth!.Bid1Qty;
                tobAskSum += tick.Depth!.Ask1Qty;
                tobCount++;
            }

            if (barIndex < pendingBars.Count)
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
        foreach (var bar in pendingBars)
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

        if (rows.Count == 0)
        {
            return new LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome.NoNewBars, 0);
        }

        destination.OptionDepthBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);
        return new LiveOptionDepthWriteResult(LiveOptionDepthWriteOutcome.Written, rows.Count);
    }
}
