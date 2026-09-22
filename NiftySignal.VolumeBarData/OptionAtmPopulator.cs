using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.VolumeBarData;

public enum OptionAtmPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionAtmPopulationResult(OptionAtmPopulationOutcome Outcome, int RowCount);

/// <summary>
/// Builds one <see cref="OptionAtmBarRow"/> per already-populated future <see cref="VolumeBarRow"/>
/// -- the futures bars stay the clock (locked 2026-09-18 decision), this just reads the ATM
/// options complex's own state as of each one's close. Idempotent per (AsOfDate,
/// BarVolumeThreshold), same convention as <see cref="VolumeBarPopulator"/>.
/// </summary>
public static class OptionAtmPopulator
{
    // Matches CadencePopulator's own SyntheticForwardStrikeCount exactly -- same established,
    // already-validated convention, not reinvented.
    const int SyntheticForwardStrikeCount = 5;

    // Matches LiveFeatureEngine's own DefaultRiskFreeRate -- a fixed constant, not derived per
    // tick, so no data-availability concern here.
    const double RiskFreeRate = 0.065;

    public static async Task<OptionAtmPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken)
    {
        if (await destination.OptionAtmBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold, cancellationToken))
        {
            return new OptionAtmPopulationResult(OptionAtmPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionAtmPopulationResult(OptionAtmPopulationOutcome.NoTradableData, 0);
        }

        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null)
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionAtmPopulationResult(OptionAtmPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = futureBars[^1].EndTimestamp;

        // Loaded once per token regardless of how many bars/strikes reference it -- the "5
        // nearest strikes" set drifts as price moves through the day, but each individual token
        // is still only ever fetched from the DB once (same shared-cache shape TradeSimulator's
        // own sharedPriceCache already uses for option fills).
        var quoteCache = new Dictionary<string, OptionQuoteSeries>();
        async Task<OptionQuoteSeries> GetQuotesAsync(string token)
        {
            if (quoteCache.TryGetValue(token, out var cached))
            {
                return cached;
            }

            var series = await OptionQuoteSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            quoteCache[token] = series;
            return series;
        }

        var rows = new List<OptionAtmBarRow>();
        foreach (var bar in futureBars)
        {
            var t = TimeToExpiry.YearsUntilExpiry(nearestExpiry, bar.EndTimestamp);

            var pairs = new List<(double Strike, double CallMid, double PutMid)>();
            var quotesByStrike = new Dictionary<decimal, (decimal? CallMid, decimal? PutMid)>();
            foreach (var strike in distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(SyntheticForwardStrikeCount))
            {
                var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
                var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
                if (call is null || put is null)
                {
                    continue;
                }

                var callMid = (await GetQuotesAsync(call.Token)).MidAtOrBefore(bar.EndTimestamp);
                var putMid = (await GetQuotesAsync(put.Token)).MidAtOrBefore(bar.EndTimestamp);
                quotesByStrike[strike] = (callMid, putMid);

                if (callMid is { } cm && putMid is { } pm)
                {
                    pairs.Add(((double)strike, (double)cm, (double)pm));
                }
            }

            var forward = SyntheticForward.Compute(pairs, t, RiskFreeRate);
            var atmStrike = forward is { } f ? NearestStrike(distinctStrikes, (decimal)f) : null;

            double? callIv = null;
            double? putIv = null;
            if (atmStrike is { } strikeValue && forward is { } forwardValue)
            {
                if (!quotesByStrike.TryGetValue(strikeValue, out var atmQuotes))
                {
                    // ATM-by-forward wasn't among the 5 nearest-to-spot strikes already fetched
                    // (only happens right at a strike-interval boundary) -- fetch it directly.
                    var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strikeValue);
                    var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strikeValue);
                    var callMid = call is not null ? (await GetQuotesAsync(call.Token)).MidAtOrBefore(bar.EndTimestamp) : null;
                    var putMid = put is not null ? (await GetQuotesAsync(put.Token)).MidAtOrBefore(bar.EndTimestamp) : null;
                    atmQuotes = (callMid, putMid);
                }

                if (atmQuotes.CallMid is { } cm)
                {
                    callIv = ImpliedVolatilitySolver.Solve(OptionType.Call, (double)cm, forwardValue, (double)strikeValue, t, RiskFreeRate);
                }

                if (atmQuotes.PutMid is { } pm)
                {
                    putIv = ImpliedVolatilitySolver.Solve(OptionType.Put, (double)pm, forwardValue, (double)strikeValue, t, RiskFreeRate);
                }
            }

            // Average when both legs solve; falls back to whichever one succeeded if only one
            // does (both should agree closely via put-call parity through the synthetic forward
            // -- that agreement is the whole reason SyntheticForward exists). Null only if both fail.
            double? atmIv = (callIv, putIv) switch
            {
                ({ } c, { } p) => (c + p) / 2.0,
                ({ } c, null) => c,
                (null, { } p) => p,
                _ => null,
            };

            rows.Add(new OptionAtmBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                EndTimestamp = bar.EndTimestamp,
                SyntheticForward = forward is { } fwd ? (decimal)fwd : null,
                AtmStrike = atmStrike,
                AtmCallIv = callIv,
                AtmPutIv = putIv,
                AtmIv = atmIv,
            });
        }

        destination.OptionAtmBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionAtmPopulationResult(OptionAtmPopulationOutcome.Populated, rows.Count);
    }

    static decimal? NearestStrike(List<decimal> distinctStrikes, decimal price) =>
        distinctStrikes.Count == 0 ? null : distinctStrikes.OrderBy(s => Math.Abs(s - price)).First();
}
