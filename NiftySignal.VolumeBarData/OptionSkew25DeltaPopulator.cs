using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.VolumeBarData;

public enum OptionSkew25DeltaPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionSkew25DeltaPopulationResult(OptionSkew25DeltaPopulationOutcome Outcome, int RowCount);

/// <summary>Builds one <see cref="OptionSkew25DeltaBarRow"/> per already-populated future <see cref="VolumeBarRow"/> -- see that entity's own doc comment for the band and 25-delta selection method.</summary>
public static class OptionSkew25DeltaPopulator
{
    const int SyntheticForwardStrikeCount = 5; // Matches OptionAtmPopulator exactly.
    const int WideBandStrikeCount = 21; // ATM +/- 10 strikes -- see this row's own doc comment.
    const double RiskFreeRate = 0.065; // Matches OptionAtmPopulator exactly.
    const double TargetCallDelta = 0.25;
    const double TargetPutDelta = -0.25;

    public static async Task<OptionSkew25DeltaPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken)
    {
        if (await destination.OptionSkew25DeltaBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold, cancellationToken))
        {
            return new OptionSkew25DeltaPopulationResult(OptionSkew25DeltaPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionSkew25DeltaPopulationResult(OptionSkew25DeltaPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionSkew25DeltaPopulationResult(OptionSkew25DeltaPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = futureBars[^1].EndTimestamp;

        var quoteCache = new Dictionary<string, OptionQuoteSeries>();
        async Task<OptionQuoteSeries> GetQuotesAsync(string token)
        {
            if (!quoteCache.TryGetValue(token, out var cached))
            {
                cached = await OptionQuoteSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
                quoteCache[token] = cached;
            }

            return cached;
        }

        var rows = new List<OptionSkew25DeltaBarRow>();
        foreach (var bar in futureBars)
        {
            var t = TimeToExpiry.YearsUntilExpiry(nearestExpiry, bar.EndTimestamp);

            // Forward via the usual 5-nearest-strike put-call parity, same as OptionAtmPopulator.
            var forwardPairs = new List<(double Strike, double CallMid, double PutMid)>();
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
                if (callMid is { } cm && putMid is { } pm)
                {
                    forwardPairs.Add(((double)strike, (double)cm, (double)pm));
                }
            }

            var forward = SyntheticForward.Compute(forwardPairs, t, RiskFreeRate);

            decimal? call25Strike = null;
            double? call25Iv = null;
            decimal? put25Strike = null;
            double? put25Iv = null;

            if (forward is { } forwardValue)
            {
                var bestCallDeltaGap = double.MaxValue;
                var bestPutDeltaGap = double.MaxValue;

                foreach (var strike in distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(WideBandStrikeCount))
                {
                    var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
                    if (call is not null)
                    {
                        var callMid = (await GetQuotesAsync(call.Token)).MidAtOrBefore(bar.EndTimestamp);
                        if (callMid is { } cm)
                        {
                            var iv = ImpliedVolatilitySolver.Solve(OptionType.Call, (double)cm, forwardValue, (double)strike, t, RiskFreeRate);
                            if (iv is { } vol)
                            {
                                var delta = BlackScholes.Calculate(OptionType.Call, forwardValue, (double)strike, t, RiskFreeRate, vol).Greeks.Delta;
                                var gap = Math.Abs(delta - TargetCallDelta);
                                if (gap < bestCallDeltaGap)
                                {
                                    bestCallDeltaGap = gap;
                                    call25Strike = strike;
                                    call25Iv = vol;
                                }
                            }
                        }
                    }

                    var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
                    if (put is not null)
                    {
                        var putMid = (await GetQuotesAsync(put.Token)).MidAtOrBefore(bar.EndTimestamp);
                        if (putMid is { } pm)
                        {
                            var iv = ImpliedVolatilitySolver.Solve(OptionType.Put, (double)pm, forwardValue, (double)strike, t, RiskFreeRate);
                            if (iv is { } vol)
                            {
                                var delta = BlackScholes.Calculate(OptionType.Put, forwardValue, (double)strike, t, RiskFreeRate, vol).Greeks.Delta;
                                var gap = Math.Abs(delta - TargetPutDelta);
                                if (gap < bestPutDeltaGap)
                                {
                                    bestPutDeltaGap = gap;
                                    put25Strike = strike;
                                    put25Iv = vol;
                                }
                            }
                        }
                    }
                }
            }

            double? skewRatio = call25Iv is { } c && put25Iv is { } p && c > 0 ? p / c : null;

            rows.Add(new OptionSkew25DeltaBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                EndTimestamp = bar.EndTimestamp,
                SyntheticForward = forward is { } fwd ? (decimal)fwd : null,
                Call25DeltaStrike = call25Strike,
                Call25DeltaIv = call25Iv,
                Put25DeltaStrike = put25Strike,
                Put25DeltaIv = put25Iv,
                SkewRatio = skewRatio,
            });
        }

        destination.OptionSkew25DeltaBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionSkew25DeltaPopulationResult(OptionSkew25DeltaPopulationOutcome.Populated, rows.Count);
    }
}
