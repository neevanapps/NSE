using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum LiveOptionMaxPainWriteOutcome
{
    Written,
    NoNewBars,
    NoTradableData,
}

public sealed record LiveOptionMaxPainWriteResult(LiveOptionMaxPainWriteOutcome Outcome, int RowCount);

/// <summary>
/// Live-path sibling of <see cref="OptionMaxPainPopulator"/> -- see <see cref="LiveVolumeBarPopulator"/>'s
/// own doc comment for the shared idempotent-replay shape.
///
/// Feasibility note (2026-09-20 investigation, the one genuine open question for this table): the
/// offline populator's own doc comment claims it "spans the FULL listed chain," but a direct query
/// against `niftysignal_vm_copy` (the historical mirror every offline populator reads) shows every
/// historical day has only ever had ~20 distinct strikes per expiry -- exactly
/// `InstrumentUniverseResolver`'s live ATM+/-10-each-side window, never a wider chain. So live
/// writing this table is NOT a regression in data breadth versus what the offline populator has ever
/// actually been fed; it reproduces the same (narrower-than-the-doc-comment-implies) band. This is
/// documented as a pre-existing, unrelated data-breadth caveat in docs/LIVE_PARITY_PLAN.md's Phase A
/// section, not treated as a Phase A blocker -- Phase A's job is parity with the offline populator as
/// it actually behaves today, not fixing that populator's own chain breadth (out of scope here).
/// </summary>
public static class LiveOptionMaxPainPopulator
{
    /// <param name="seriesCache">
    /// Phase G perf fix (docs/LIVE_PARITY_PLAN.md): see <see cref="LiveOptionAtmPopulator.WriteNewBarsAsync"/>'s
    /// own identical parameter -- null (the default) reproduces the original from-scratch-every-call
    /// behavior exactly, unchanged for every existing caller.
    /// </param>
    public static async Task<LiveOptionMaxPainWriteResult> WriteNewBarsAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, LiveOptionSeriesCache? seriesCache = null)
    {
        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);
        if (futureBars.Count == 0)
        {
            return new LiveOptionMaxPainWriteResult(LiveOptionMaxPainWriteOutcome.NoTradableData, 0);
        }

        var existingMaxIndex = await destination.OptionMaxPainBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .Select(b => (int?)b.BarIndex)
            .MaxAsync(cancellationToken) ?? -1;

        var pendingBars = futureBars.Where(b => b.BarIndex > existingMaxIndex).ToList();
        if (pendingBars.Count == 0)
        {
            return new LiveOptionMaxPainWriteResult(LiveOptionMaxPainWriteOutcome.NoNewBars, 0);
        }

        // Audit finding F61 (2026-09-22) -- NIFTY-filtered; see LiveOptionAtmPopulator's identical fix
        // and NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);
        if (allOptions.Count == 0)
        {
            return new LiveOptionMaxPainWriteResult(LiveOptionMaxPainWriteOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(s => s).ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = pendingBars[^1].EndTimestamp;

        var callOiByStrike = new Dictionary<decimal, OptionOiSeries>();
        var putOiByStrike = new Dictionary<decimal, OptionOiSeries>();
        foreach (var strike in distinctStrikes)
        {
            var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            if (call is not null)
            {
                callOiByStrike[strike] = seriesCache is not null
                    ? await seriesCache.GetOiSeriesAsync(source, asOfDate, call.Token, dayStart, dayEnd, cancellationToken)
                    : await OptionOiSeries.LoadAsync(source, call.Token, dayStart, dayEnd, cancellationToken);
            }

            var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (put is not null)
            {
                putOiByStrike[strike] = seriesCache is not null
                    ? await seriesCache.GetOiSeriesAsync(source, asOfDate, put.Token, dayStart, dayEnd, cancellationToken)
                    : await OptionOiSeries.LoadAsync(source, put.Token, dayStart, dayEnd, cancellationToken);
            }
        }

        // Body below is field-for-field identical to OptionMaxPainPopulator.PopulateDayAsync's own
        // per-bar loop -- only the outer bar set (pendingBars) differs.
        var rows = new List<OptionMaxPainBarRow>();
        foreach (var bar in pendingBars)
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
        return new LiveOptionMaxPainWriteResult(LiveOptionMaxPainWriteOutcome.Written, rows.Count);
    }
}
