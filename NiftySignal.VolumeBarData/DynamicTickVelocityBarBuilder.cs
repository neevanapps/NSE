using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, dynamic tick-velocity bar-sizing experiment (docs/Price_Based_Findings.md). Builds
/// ONE trading day's future volume bars entirely IN-MEMORY (never persisted -- no new dataset, per
/// this project's own "new backtest datasets need approval" rule; this is a fresh per-run
/// computation, same as the existing `--rolling=` sub-bar mechanism elsewhere in this project) with
/// a bar-by-bar threshold that adapts to recent tick velocity, instead of one fixed qty threshold
/// for the whole day.
///
/// **The rule, stated plainly (a first, simple rule -- not finely tuned)**: at the moment each new
/// bar starts, compare the RECENT tick rate (ticks/sec over the trailing 60 seconds) against a
/// ROLLING BASELINE tick rate (ticks/sec over the trailing 20 minutes, or however much of the
/// session has elapsed if less) -- both computed ONLY from ticks already observed by that point, no
/// look-ahead. If recent/baseline >= 1.3, the market is unusually busy right now -> use a SMALLER
/// threshold (finer, faster-closing bars). If recent/baseline &lt;= 0.7, unusually quiet -> use a
/// LARGER threshold (coarser bars). Otherwise, the default (middle) threshold. The three threshold
/// values map onto this project's own three most-used bar sizes (650/1300/2600) rather than
/// inventing new numbers, keeping results comparable to everything already recorded in this file.
///
/// **Why this specific rule**: motivated directly by `entry-bar-quality`'s own finding (same file,
/// prior section) -- entries following FAST-filling bars (high recent tick velocity) showed a
/// meaningfully better win rate than entries following slow-filling ones, across two independently-
/// found candidates. This experiment tests whether PROACTIVELY sizing the bar smaller during those
/// fast periods (instead of just observing the correlation after the fact on a fixed-size bar)
/// improves results further.
/// </summary>
public static class DynamicTickVelocityBarBuilder
{
    const int RecentWindowSeconds = 60;
    const int BaselineWindowSeconds = 20 * 60;
    const double HighVelocityRatio = 1.3;
    const double LowVelocityRatio = 0.7;

    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketClose = new(15, 30);

    public enum VelocityRegime { Low, Medium, High }

    public sealed record DynamicBarBuildResult(List<VolumeBarRow> Bars, int LowCount, int MediumCount, int HighCount);

    /// <summary>
    /// <paramref name="lowThreshold"/>/<paramref name="mediumThreshold"/>/<paramref name="highVelocityThreshold"/>
    /// are the three qty thresholds used for Low/Medium/High recent-vs-baseline tick-velocity
    /// regimes respectively (defaults 2600/1300/650 -- coarser bars when quiet, finer when busy,
    /// matching the direction `entry-bar-quality` found).
    /// </summary>
    public static async Task<DynamicBarBuildResult> BuildDayAsync(
        NiftySignalDbContext source, DateOnly asOfDate, CancellationToken cancellationToken,
        long lowThreshold = 2600, long mediumThreshold = 1300, long highVelocityThreshold = 650)
    {
        // Same future-token resolution and market-hours window as VolumeBarPopulator.PopulateDayAsync
        // -- deliberately identical so this experiment's bars are comparable to the fixed-threshold
        // ones already recorded, not an independently-diverging tick source.
        var future = await source.Instruments
            .Where(i => i.AsOfDate == asOfDate && i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (future is null)
        {
            return new DynamicBarBuildResult([], 0, 0, 0);
        }

        var dayStart = new DateTimeOffset(asOfDate.ToDateTime(MarketOpen), IstOffset).ToUniversalTime();
        var dayEnd = new DateTimeOffset(asOfDate.ToDateTime(MarketClose), IstOffset).ToUniversalTime();

        var ticks = source.Ticks
            .Where(t => t.Token == future.Token && t.ExchangeTimestamp >= dayStart && t.ExchangeTimestamp <= dayEnd)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        // Rolling tick-timestamp history, bounded to the baseline window -- evicted from the front
        // as ticks age out, so both the recent-60s and baseline-20min counts are always computed
        // causally from only what's already been observed.
        var recentTimestamps = new List<DateTimeOffset>();
        var regimeCounts = (Low: 0, Medium: 0, High: 0);
        long lastRegimeThreshold = mediumThreshold;

        long? ThresholdProvider()
        {
            if (recentTimestamps.Count == 0)
            {
                return null; // no history yet -- VolumeBarBuilder falls back to its own default.
            }

            var now = recentTimestamps[^1];
            var recentCutoff = now.AddSeconds(-RecentWindowSeconds);
            var baselineCutoff = now.AddSeconds(-BaselineWindowSeconds);

            var recentCount = 0;
            var baselineCount = 0;
            for (var i = recentTimestamps.Count - 1; i >= 0; i--)
            {
                if (recentTimestamps[i] < baselineCutoff)
                {
                    break;
                }
                baselineCount++;
                if (recentTimestamps[i] >= recentCutoff)
                {
                    recentCount++;
                }
            }

            var baselineSpanSeconds = Math.Min(BaselineWindowSeconds, (now - recentTimestamps[0]).TotalSeconds);
            if (baselineSpanSeconds < RecentWindowSeconds)
            {
                return null; // not enough history to trust a baseline rate yet -- use the default.
            }

            var recentRate = recentCount / (double)RecentWindowSeconds;
            var baselineRate = baselineCount / baselineSpanSeconds;
            if (baselineRate <= 0)
            {
                return null;
            }

            var ratio = recentRate / baselineRate;
            long threshold;
            if (ratio >= HighVelocityRatio)
            {
                threshold = highVelocityThreshold;
                regimeCounts.High++;
            }
            else if (ratio <= LowVelocityRatio)
            {
                threshold = lowThreshold;
                regimeCounts.Low++;
            }
            else
            {
                threshold = mediumThreshold;
                regimeCounts.Medium++;
            }

            lastRegimeThreshold = threshold;
            return threshold;
        }

        var builder = new VolumeBarBuilder(mediumThreshold, ThresholdProvider);
        var rows = new List<VolumeBarRow>();
        var barIndex = 0;
        var lastTimestamp = dayStart;
        var sawAnyTick = false;

        await foreach (var tick in ticks.WithCancellation(cancellationToken))
        {
            sawAnyTick = true;
            lastTimestamp = tick.ExchangeTimestamp;

            recentTimestamps.Add(tick.ExchangeTimestamp);
            var evictBefore = tick.ExchangeTimestamp.AddSeconds(-BaselineWindowSeconds);
            var evictCount = 0;
            while (evictCount < recentTimestamps.Count && recentTimestamps[evictCount] < evictBefore)
            {
                evictCount++;
            }
            if (evictCount > 0)
            {
                recentTimestamps.RemoveRange(0, evictCount);
            }

            var bar = builder.ApplyTick(tick.ExchangeTimestamp, tick.LastPrice, tick.Volume, tick.Depth, tick.OpenInterest);
            if (bar is not null)
            {
                rows.Add(ToRow(bar, asOfDate, barIndex++, builder.LastCompletedBarThreshold));
            }
        }

        if (sawAnyTick)
        {
            var partial = builder.FlushPartial(lastTimestamp);
            if (partial is not null)
            {
                rows.Add(ToRow(partial, asOfDate, barIndex, builder.LastCompletedBarThreshold));
            }
        }

        return new DynamicBarBuildResult(rows, regimeCounts.Low, regimeCounts.Medium, regimeCounts.High);
    }

    static VolumeBarRow ToRow(VolumeBar bar, DateOnly asOfDate, int barIndex, long actualThreshold) => new()
    {
        AsOfDate = asOfDate,
        BarIndex = barIndex,
        // 2026-09-23: a dynamic bar's own THIS-BAR threshold, not a single fixed value for the
        // whole day -- callers that need the sizing decision per bar should read this field
        // directly rather than assume one constant threshold applies to every row.
        BarVolumeThreshold = actualThreshold,
        StartTimestamp = bar.StartTimestamp,
        EndTimestamp = bar.EndTimestamp,
        DurationSeconds = bar.Duration.TotalSeconds,
        OpenPrice = bar.OpenPrice,
        HighPrice = bar.HighPrice,
        LowPrice = bar.LowPrice,
        ClosePrice = bar.ClosePrice,
        Volume = bar.Volume,
        OpenInterestAtClose = bar.OpenInterestAtClose,
        VwapAtClose = bar.VwapAtClose,
        FutureCvdNet = bar.FutureCvdNet,
        FutureDepthImbalance = bar.DepthImbalance,
        OrderFlowImbalance = bar.OrderFlowImbalance,
        TopOfBookImbalance = bar.TopOfBookImbalance,
        TickCount = bar.TickCount,
    };
}
