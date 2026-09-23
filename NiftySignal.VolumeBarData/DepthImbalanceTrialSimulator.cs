using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-22, "Depth Imbalance, transparent research" track -- user's own exact spec (see chat): a
/// sliding N-bar rolling sum of <see cref="DepthImbalanceSumBarRow.RawQtyDiffSum"/> or
/// <see cref="DepthImbalanceSumBarRow.NotionalDiffSum"/>, turned into a single-side (Call-only OR
/// Put-only, per <see cref="DepthImbalanceSumBarRow.Side"/>) long/flat decision by SIGN alone -- no
/// percentile gate, no stop-loss, no confirmation filter. This is a deliberate choice, not an
/// oversight: CLAUDE.md's "No gating during single-metric evaluation" rule says a single metric's
/// own diagnostic trial must not grow conditional logic bolted on to rescue its results, so this
/// simulator is intentionally the simplest possible reading of the metric's own sign, stated
/// explicitly rather than silently assumed. Distinct from <see cref="TradeSimulator"/>'s
/// SignedRank/percentile-gated dispatch, which is the production-style calibration harness this
/// task's methodology is deliberately NOT using for this first pass.
///
/// Rule: flat -&gt; long (Call or Put, per <paramref name="side"/> in <see cref="SimulateDayAsync"/>)
/// the bar the rolling value first turns positive (enter at that bar's own close timestamp, using
/// the real tick price at-or-before that instant). Long -&gt; flat the bar the rolling value turns
/// &lt;= 0, or forced flat at the day's last bar. AS-BUILT, not pre-negated for either side -- the
/// 2026-09-22 Put-side run added alongside the original Call-only run is read literally the same way
/// (positive rolling = buy that side), not flipped by assumption, per CLAUDE.md's "a metric's sign is
/// a hypothesis until tested, not a textbook default" rule. Held strike/token is fixed at entry (not
/// re-picked bar to bar), matching every other simulator in this file.
///
/// Strike selection (added 2026-09-22, user's own explicit instruction -- Days 1-3 did NOT do this,
/// they traded the pure ATM strike whatever its premium happened to be, which is why Day 1's 0-DTE
/// entries ranged down to a few paise): when <paramref name="minEntryPrice"/>/<paramref
/// name="maxEntryPrice"/> are set, this does NOT default to the pure-ATM strike's premium -- it
/// walks strikes outward from ATM by distance (same ordering <c>AtmStrikeSelector.PickAtm</c> uses)
/// and takes the FIRST one (closest to ATM) whose live premium at the entry instant actually falls
/// inside the band. Byte-identical logic to <see cref="TradeSimulator"/>'s own <c>PickStrikeInBandAsync</c>
/// (duplicated, not shared, for the same closure reasons that method's own doc comment gives) so the
/// two simulators can't silently drift on this. With both bounds null, falls back to the bar's own
/// stored <see cref="DepthImbalanceSumBarRow.AtmToken"/> (original Days 1-3 behavior).
/// </summary>
public static class DepthImbalanceTrialSimulator
{
    public sealed record Trial(
        VolumeBarTrade Trade, int WindowBars, bool UseNotional, int DaysToExpiry,
        decimal MaePoints, decimal MfePoints, decimal MaePercent, decimal MfePercent, double RollingValueAtEntry);

    public static async Task<List<Trial>> SimulateDayAsync(
        NiftySignalDbContext source, VolumeBarDbContext volumeBarDb, DateOnly asOfDate, long barVolumeThreshold,
        int bandWidth, OptionType side, int windowBars, bool useNotional, CancellationToken cancellationToken,
        Dictionary<(DateOnly, string), OptionPriceSeries>? sharedPriceCache = null,
        decimal? minEntryPrice = null, decimal? maxEntryPrice = null)
    {
        var bars = await volumeBarDb.DepthImbalanceSumBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold && b.BandWidth == bandWidth && b.Side == side)
            .OrderBy(b => b.BarIndex)
            .ToListAsync(cancellationToken);

        if (bars.Count == 0)
        {
            return [];
        }

        var priceCache = sharedPriceCache ?? new Dictionary<(DateOnly, string), OptionPriceSeries>();
        async Task<OptionPriceSeries> GetSeriesAsync(string token)
        {
            if (priceCache.TryGetValue((asOfDate, token), out var cached))
            {
                return cached;
            }

            var series = await OptionPriceSeries.LoadAsync(source, token, bars[0].EndTimestamp.AddHours(-1), bars[^1].EndTimestamp, cancellationToken);
            priceCache[(asOfDate, token)] = series;
            return series;
        }

        // Chain only loaded (and only walked strike-by-strike) when a price band is actually set --
        // with both bounds null this is never touched, same "free when unused" shape as
        // TradeSimulator's own PickStrikeInBandAsync.
        List<Domain.Entities.Instrument>? chain = null;
        async Task<(string Token, decimal Strike, decimal Price)?> PickStrikeInBandAsync(decimal futurePrice, DateTimeOffset atTime)
        {
            if (minEntryPrice is null && maxEntryPrice is null)
            {
                return null;
            }

            if (chain is null)
            {
                // Audit finding F62 (2026-09-22) -- NIFTY-filtered, offline follow-up to F61. See
                // NiftySignal.Host.LiveFeatureEngine.NiftyUnderlying's own doc comment.
                var nearestExpiry = await source.Instruments
                    .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate != null && i.Underlying == "NIFTY")
                    .Select(i => i.ExpiryDate!.Value)
                    .MinAsync(cancellationToken);
                chain = await source.Instruments
                    .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Option && i.OptionType == side && i.ExpiryDate == nearestExpiry && i.Underlying == "NIFTY")
                    .ToListAsync(cancellationToken);
            }

            foreach (var candidate in chain.OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice)))
            {
                var series = await GetSeriesAsync(candidate.Token);
                var price = series.PriceAtOrBefore(atTime);
                if (price is { } p && p > 0
                    && (minEntryPrice is null || p >= minEntryPrice)
                    && (maxEntryPrice is null || p <= maxEntryPrice))
                {
                    return (candidate.Token, candidate.StrikePrice!.Value, p);
                }
            }

            return null;
        }

        // Sliding sum over the last windowBars values (nulls treated as 0 contribution -- a bar
        // with literally no depth-bearing tick contributes nothing to the accumulated diff, which
        // is the natural reading of "accumulate the difference over the bar" when there's nothing
        // to accumulate).
        var values = bars.Select(b => useNotional ? b.NotionalDiffSum ?? 0.0 : b.RawQtyDiffSum ?? 0.0).ToArray();

        var trials = new List<Trial>();
        var inPosition = false;
        DateTimeOffset entryTime = default;
        decimal entryPrice = 0;
        string entryToken = "";
        decimal entryStrike = 0;
        int entryDte = 0;
        double entryRolling = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            if (i < windowBars - 1)
            {
                continue;
            }

            double rolling = 0;
            for (var j = i - windowBars + 1; j <= i; j++)
            {
                rolling += values[j];
            }

            var bar = bars[i];
            var isLastBar = i == bars.Count - 1;

            if (!inPosition && rolling > 0 && !isLastBar)
            {
                var picked = await PickStrikeInBandAsync(bar.FutureClosePrice, bar.EndTimestamp);
                string? token = picked?.Token;
                decimal? price = picked?.Price;
                decimal? strike = picked?.Strike;

                if (picked is null && minEntryPrice is null && maxEntryPrice is null)
                {
                    var series = await GetSeriesAsync(bar.AtmToken);
                    price = series.PriceAtOrBefore(bar.EndTimestamp);
                    token = bar.AtmToken;
                    strike = bar.AtmStrike;
                }

                if (price is { } p && p > 0 && token is not null && strike is not null)
                {
                    inPosition = true;
                    entryTime = bar.EndTimestamp;
                    entryPrice = p;
                    entryToken = token;
                    entryStrike = strike.Value;
                    entryDte = bar.DaysToExpiry;
                    entryRolling = rolling;
                }
            }
            else if (inPosition && (rolling <= 0 || isLastBar))
            {
                var series = await GetSeriesAsync(entryToken);
                var exitPrice = series.PriceAtOrBefore(bar.EndTimestamp);
                if (exitPrice is { } ep && ep > 0)
                {
                    var pricesDuringHold = series.AllPrices
                        .Where(t => t.Timestamp >= entryTime && t.Timestamp <= bar.EndTimestamp)
                        .Select(t => t.Price)
                        .ToList();
                    var maeMfe = MaeMfeCalculator.Compute(entryPrice, pricesDuringHold);

                    var trade = new VolumeBarTrade(
                        entryTime, entryPrice, side, entryStrike,
                        bar.EndTimestamp, ep, isLastBar ? "EndOfDay" : "SignFlip", entryRolling);

                    trials.Add(new Trial(trade, windowBars, useNotional, entryDte,
                        maeMfe.MaePoints, maeMfe.MfePoints, maeMfe.MaePercent, maeMfe.MfePercent, entryRolling));
                }

                inPosition = false;
            }
        }

        return trials;
    }
}
