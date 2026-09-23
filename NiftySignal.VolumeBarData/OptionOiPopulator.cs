using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

public enum OptionOiPopulationOutcome
{
    Populated,
    AlreadyPopulated,
    NoTradableData,
}

public sealed record OptionOiPopulationResult(OptionOiPopulationOutcome Outcome, int RowCount);

/// <summary>
/// Builds one <see cref="OptionOiBarRow"/> per already-populated future <see cref="VolumeBarRow"/>
/// -- see that entity's own doc comment for the two sub-metrics this feeds and the band definition.
/// </summary>
public static class OptionOiPopulator
{
    /// <summary>Default matches the original locked ATM±1 -- pass 5 (ATM±2) explicitly to compare.</summary>
    public const int DefaultBandWidth = 3;

    public static async Task<OptionOiPopulationResult> PopulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext destination, DateOnly asOfDate, long barVolumeThreshold, CancellationToken cancellationToken, int bandWidth = DefaultBandWidth)
    {
        if (await destination.OptionOiBars.AnyAsync(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth, cancellationToken))
        {
            return new OptionOiPopulationResult(OptionOiPopulationOutcome.AlreadyPopulated, 0);
        }

        var futureBars = await destination.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (futureBars.Count == 0)
        {
            return new OptionOiPopulationResult(OptionOiPopulationOutcome.NoTradableData, 0);
        }

        // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
        // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
        var allOptions = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.Underlying == "NIFTY")
            .ToListAsync(cancellationToken);

        if (allOptions.Count == 0)
        {
            return new OptionOiPopulationResult(OptionOiPopulationOutcome.NoTradableData, 0);
        }

        var nearestExpiry = allOptions.Select(o => o.ExpiryDate!.Value).Min();
        var chain = allOptions.Where(o => o.ExpiryDate == nearestExpiry).ToList();
        var distinctStrikes = chain.Select(o => o.StrikePrice!.Value).Distinct().ToList();

        var dayStart = futureBars[0].StartTimestamp;
        var dayEnd = futureBars[^1].EndTimestamp;

        // Same per-bar band as OptionBandFlowPopulator: 3 strikes nearest THIS bar's future close.
        var bandByBarIndex = futureBars.ToDictionary(
            bar => bar.BarIndex,
            bar => distinctStrikes.OrderBy(s => Math.Abs(s - bar.ClosePrice)).Take(bandWidth).ToHashSet());

        var touchedStrikes = bandByBarIndex.Values.SelectMany(s => s).Distinct().ToList();

        var quoteCache = new Dictionary<string, OptionQuoteSeries>();
        var oiCache = new Dictionary<string, OptionOiSeries>();
        async Task<OptionQuoteSeries> GetQuotesAsync(string token)
        {
            if (!quoteCache.TryGetValue(token, out var cached))
            {
                cached = await OptionQuoteSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
                quoteCache[token] = cached;
            }

            return cached;
        }

        async Task<OptionOiSeries> GetOiAsync(string token)
        {
            if (!oiCache.TryGetValue(token, out var cached))
            {
                cached = await OptionOiSeries.LoadAsync(source, token, dayStart, dayEnd, cancellationToken);
                oiCache[token] = cached;
            }

            return cached;
        }

        var tokenByStrikeSide = new Dictionary<(decimal Strike, OptionType Side), string>();
        foreach (var strike in touchedStrikes)
        {
            var call = chain.FirstOrDefault(o => o.OptionType == OptionType.Call && o.StrikePrice == strike);
            var put = chain.FirstOrDefault(o => o.OptionType == OptionType.Put && o.StrikePrice == strike);
            if (call is not null)
            {
                tokenByStrikeSide[(strike, OptionType.Call)] = call.Token;
            }

            if (put is not null)
            {
                tokenByStrikeSide[(strike, OptionType.Put)] = put.Token;
            }
        }

        // Bar-over-bar previous state per (strike, side), tracked across the WHOLE day regardless
        // of current band membership -- a strike leaving and re-entering the band must still diff
        // against its own last real reading, not against a stale "band changed" gap.
        var previousMid = new Dictionary<(decimal, OptionType), decimal>();
        var previousOi = new Dictionary<(decimal, OptionType), long>();

        var rows = new List<OptionOiBarRow>();
        foreach (var bar in futureBars)
        {
            var band = bandByBarIndex[bar.BarIndex];
            decimal callOiChangeNotional = 0m;
            decimal putOiChangeNotional = 0m;
            double buildupNet = 0.0;
            var buildupHasSignal = false;

            foreach (var strike in touchedStrikes)
            {
                foreach (var side in new[] { OptionType.Call, OptionType.Put })
                {
                    if (!tokenByStrikeSide.TryGetValue((strike, side), out var token))
                    {
                        continue;
                    }

                    var mid = (await GetQuotesAsync(token)).MidAtOrBefore(bar.EndTimestamp);
                    var oi = (await GetOiAsync(token)).OiAtOrBefore(bar.EndTimestamp);

                    var inBand = band.Contains(strike);

                    if (inBand
                        && mid is { } curMid && oi is { } curOi
                        && previousMid.TryGetValue((strike, side), out var prevMid)
                        && previousOi.TryGetValue((strike, side), out var prevOi))
                    {
                        var priceChange = curMid - prevMid;
                        var oiChange = curOi - prevOi;

                        // Raw ΔOI x the price AT THE MOMENT of that change -- deliberately NOT a
                        // re-priced level differenced later (see this row's own doc comment for
                        // why that was wrong: it conflates real OI change with the option's own
                        // noisier price movement). A zero ΔOI contributes exactly zero here, which
                        // is correct and common given OI's own ~once/minute update cadence vs. this
                        // bar clock's much faster ticks.
                        var changeNotional = oiChange * curMid;
                        if (side == OptionType.Call)
                        {
                            callOiChangeNotional += changeNotional;
                        }
                        else
                        {
                            putOiChangeNotional += changeNotional;
                        }

                        var classification = OiBuildupClassifier.Classify(priceChange, oiChange);
                        var direction = classification switch
                        {
                            OiBuildupClassification.LongBuildup or OiBuildupClassification.ShortCovering => 1.0,
                            OiBuildupClassification.ShortBuildup or OiBuildupClassification.LongUnwinding => -1.0,
                            _ => 0.0,
                        };

                        // Put-side sign flip: "LongBuildup" read on the PUT contract itself
                        // (put price + put OI both rising) is bearish for the underlying, not
                        // bullish -- same flip the original time-cadence OiBuildupNet documented.
                        if (side == OptionType.Put)
                        {
                            direction = -direction;
                        }

                        if (direction != 0.0)
                        {
                            buildupNet += direction * Math.Abs(oiChange) * (double)curMid;
                        }

                        // A real classification outcome (even Neutral, even a zero-direction
                        // result) is still evidence this bar had usable previous-bar state to
                        // compare against -- distinct from no-previous-state-at-all, which stays
                        // null below.
                        buildupHasSignal = true;
                    }

                    if (mid is { } midForNext)
                    {
                        previousMid[(strike, side)] = midForNext;
                    }

                    if (oi is { } oiForNext)
                    {
                        previousOi[(strike, side)] = oiForNext;
                    }
                }
            }

            rows.Add(new OptionOiBarRow
            {
                AsOfDate = asOfDate,
                BarIndex = bar.BarIndex,
                BarVolumeThreshold = barVolumeThreshold,
                BandWidth = bandWidth,
                EndTimestamp = bar.EndTimestamp,
                CallOiChangeNotional = callOiChangeNotional,
                PutOiChangeNotional = putOiChangeNotional,
                BuildupNet = buildupHasSignal ? buildupNet : null,
            });
        }

        destination.OptionOiBars.AddRange(rows);
        await destination.SaveChangesAsync(cancellationToken);

        return new OptionOiPopulationResult(OptionOiPopulationOutcome.Populated, rows.Count);
    }
}
