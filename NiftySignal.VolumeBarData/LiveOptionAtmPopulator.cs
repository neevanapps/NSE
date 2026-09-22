using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.VolumeBarData;

public enum LiveOptionAtmWriteOutcome
{
    Written,
    NoNewBars,
    NoTradableData,
}

public sealed record LiveOptionAtmWriteResult(LiveOptionAtmWriteOutcome Outcome, int RowCount);

/// <summary>
/// Live-path sibling of <see cref="OptionAtmPopulator"/> -- same idempotent-replay shape as
/// <see cref="LiveVolumeBarPopulator"/> (see that class's own doc comment for why replay-from-
/// scratch, not an incrementally-updated in-memory tracker). Runs against whatever future
/// <see cref="VolumeBarRow"/>s <see cref="LiveVolumeBarPopulator"/> has already written for the day
/// -- one <see cref="OptionAtmBarRow"/> per future bar, only for bars not already populated, per the
/// exact same (AsOfDate, BarVolumeThreshold, BarIndex) identity the future table uses.
///
/// Feasibility note (2026-09-20 investigation): the live instrument universe
/// (<c>InstrumentUniverseResolver</c>, ATM +/- 10 strikes each side, nearest 2 weekly expiries) is
/// the SAME breadth every historical day's <c>Instruments</c> table has ever had -- confirmed via
/// direct query against `niftysignal_vm_copy` across all 9 available historical days (~20 distinct
/// strikes per expiry, 40 instruments, every single day). So this isn't a narrower live dataset than
/// what the offline populator itself has ever validated against -- see the plan doc's Phase A
/// section for the full writeup.
/// </summary>
public static class LiveOptionAtmPopulator
{
    const int SyntheticForwardStrikeCount = 5;
    const double RiskFreeRate = 0.065;

    /// <param name="seriesCache">
    /// Phase G perf fix (docs/LIVE_PARITY_PLAN.md): when supplied, each touched token's quote series is
    /// loaded/extended incrementally across polls instead of re-scanning the whole day-so-far range every
    /// call -- see <see cref="LiveOptionSeriesCache"/>'s own doc comment. Null (the default) reproduces the
    /// original from-scratch-every-call behavior exactly, unchanged for every existing caller (offline
    /// tools, tests, replay harnesses).
    /// </param>
    public static async Task<LiveOptionAtmWriteResult> WriteNewBarsAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, LiveOptionSeriesCache? seriesCache = null)
    {
        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);
        if (futureBars.Count == 0)
        {
            return new LiveOptionAtmWriteResult(LiveOptionAtmWriteOutcome.NoTradableData, 0);
        }

        var existingMaxIndex = await destination.OptionAtmBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .Select(b => (int?)b.BarIndex)
            .MaxAsync(cancellationToken) ?? -1;

        var pendingBars = futureBars.Where(b => b.BarIndex > existingMaxIndex).ToList();
        if (pendingBars.Count == 0)
        {
            return new LiveOptionAtmWriteResult(LiveOptionAtmWriteOutcome.NoNewBars, 0);
        }

        // Audit finding F61 (2026-09-22) -- NIFTY-filtered; see NiftySignal.Host.LiveFeatureEngine.
        // NiftyUnderlying's own doc comment. Without this, a Sensex/Bank Nifty option with an
        // earlier expiry than NIFTY's own nearest weekly would silently win the Min() below and mix
        // a different underlying's strikes into NIFTY's ATM band.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);
        if (allOptions.Count == 0)
        {
            return new LiveOptionAtmWriteResult(LiveOptionAtmWriteOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = pendingBars[^1].EndTimestamp;

        var quoteCache = new Dictionary<string, OptionQuoteSeries>();
        async Task<OptionQuoteSeries> GetQuotesAsync(string token)
        {
            if (quoteCache.TryGetValue(token, out var cached))
            {
                return cached;
            }

            var series = seriesCache is not null
                ? await seriesCache.GetQuoteSeriesAsync(source, asOfDate, token, dayStart, dayEnd, cancellationToken)
                : await OptionQuoteSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
            quoteCache[token] = series;
            return series;
        }

        // Body below is field-for-field identical to OptionAtmPopulator.PopulateDayAsync's own
        // per-bar loop -- only the outer bar set (pendingBars, not every futureBar) differs.
        var rows = new List<OptionAtmBarRow>();
        foreach (var bar in pendingBars)
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
        return new LiveOptionAtmWriteResult(LiveOptionAtmWriteOutcome.Written, rows.Count);
    }

    static decimal? NearestStrike(List<decimal> distinctStrikes, decimal price) =>
        distinctStrikes.Count == 0 ? null : distinctStrikes.OrderBy(s => Math.Abs(s - price)).First();
}
