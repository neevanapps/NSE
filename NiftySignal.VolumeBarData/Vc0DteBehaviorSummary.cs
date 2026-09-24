using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, forensic-verification addendum (single-day) + 2026-09-23 multi-day behaviour
/// validation addendum (this file's current shape). Aggregates
/// <see cref="Vc0DteBehaviorRecorder.ObservationRow"/>s -- always grouped so the four
/// (OptionType, SignalDirection) combinations stay separate (per the user's own explicit
/// caution: CE CrossDown is NOT a PE buy signal, and the four are never merged into a
/// "bullish/bearish" pair). <see cref="Summarize"/> groups by (OptionType, SignalDirection) only
/// (the original single-day view); <see cref="SummarizeByDay"/>/<see cref="SummarizeBySessionBucket"/>/
/// <see cref="SummarizeBySpeedBucket"/> add a second grouping key on top of the same four, for the
/// day-by-day robustness (point 7), session-bucket (point 8), and event-speed (point 9) views.
/// </summary>
public static class Vc0DteBehaviorSummary
{
    /// <summary>
    /// One (OptionType, SignalDirection[, secondary key]) group's stats. Both a mean AND a median
    /// are reported for every forward-return/MFE/MAE metric per the user's own explicit request
    /// ("do not call a signal robust merely because pooled observations are positive" --  a mean
    /// alone can hide a single outlier day). <see cref="DayCount"/> is the number of DISTINCT
    /// trading days represented, not the observation count, so a table row can't quietly imply
    /// more independent evidence than it has.
    /// </summary>
    public sealed record SummaryRow(
        OptionType OptionType, string SignalDirection, int Count, int DayCount,
        decimal? AvgForwardReturn1, decimal? MedianForwardReturn1,
        decimal? AvgForwardReturn2, decimal? MedianForwardReturn2,
        decimal? AvgForwardReturn3, decimal? MedianForwardReturn3,
        decimal? AvgForwardReturn5, decimal? MedianForwardReturn5,
        decimal? AvgForwardReturn10, decimal? MedianForwardReturn10,
        decimal AvgMaximumFavorableMovePercent, decimal MedianMaximumFavorableMovePercent,
        decimal AvgMaximumAdverseMovePercent, decimal MedianMaximumAdverseMovePercent,
        decimal AvgMaximumFavorableMoveAbs, decimal AvgMaximumAdverseMoveAbs,
        double? AvgEventBarsToMaximumFavorable, double? AvgEventBarsToMaximumAdverse,
        decimal? AvgFutureForwardReturnPercent1, decimal? AvgFutureForwardReturnPercent3,
        decimal? AvgFutureForwardReturnPercent5, decimal? AvgFutureForwardReturnPercent10,
        decimal? AvgPreSignalOptionMovePercent3, decimal? AvgPreSignalOptionMovePercent5, decimal? AvgPreSignalOptionMovePercent10,
        decimal? AvgPreSignalFutureMovePercent3, decimal? AvgPreSignalFutureMovePercent5, decimal? AvgPreSignalFutureMovePercent10);

    /// <summary>Average taken over the non-null readings only for each horizon -- a horizon with no observations that survived to it (spec section 21's forward window running past the session) is never treated as a zero.</summary>
    public static List<SummaryRow> Summarize(IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> rows)
        => rows.GroupBy(r => (r.OptionType, r.SignalDirection))
            .OrderBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection)
            .Select(g => Aggregate(g.Key.OptionType, g.Key.SignalDirection, g.ToList()))
            .ToList();

    /// <summary>Point 7 -- day-by-day robustness. One row per (TradingDate, OptionType, SignalDirection).</summary>
    public static List<SummaryRow> SummarizeByDay(IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> rows)
        => rows.GroupBy(r => (r.TradingDate, r.OptionType, r.SignalDirection))
            .OrderBy(g => g.Key.TradingDate).ThenBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection)
            .Select(g => Aggregate(g.Key.OptionType, g.Key.SignalDirection, g.ToList()))
            .ToList();

    /// <summary>Point 8 -- descriptive session-bucket analysis. One row per (bucket label, OptionType, SignalDirection).</summary>
    public static List<(string Bucket, SummaryRow Stats)> SummarizeBySessionBucket(IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> rows, TimeSpan istOffset)
        => rows.GroupBy(r => (Bucket: SessionBucket(r.SignalTimestamp, istOffset), r.OptionType, r.SignalDirection))
            .OrderBy(g => g.Key.Bucket).ThenBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection)
            .Select(g => (g.Key.Bucket, Aggregate(g.Key.OptionType, g.Key.SignalDirection, g.ToList())))
            .ToList();

    /// <summary>
    /// Point 9 -- event-speed dependence. Buckets each observation by where its OWN
    /// <see cref="Vc0DteBehaviorRecorder.ObservationRow.SlowWindowDurationMs"/> falls relative to
    /// the POOLED dataset's own quartiles (<paramref name="q1Ms"/>/<paramref name="q3Ms"/>, from
    /// <see cref="EventBarDurationStats"/> or computed directly from these rows) -- descriptive
    /// quantiles from the actual data, never an invented fixed threshold.
    /// </summary>
    public static List<(string Bucket, SummaryRow Stats)> SummarizeBySpeedBucket(
        IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> rows, double q1Ms, double q3Ms)
        => rows.GroupBy(r => (Bucket: SpeedBucket(r.SlowWindowDurationMs, q1Ms, q3Ms), r.OptionType, r.SignalDirection))
            .OrderBy(g => g.Key.Bucket).ThenBy(g => g.Key.OptionType).ThenBy(g => g.Key.SignalDirection)
            .Select(g => (g.Key.Bucket, Aggregate(g.Key.OptionType, g.Key.SignalDirection, g.ToList())))
            .ToList();

    public static string SessionBucket(DateTimeOffset signalTimestamp, TimeSpan istOffset)
    {
        var t = TimeOnly.FromDateTime(signalTimestamp.ToOffset(istOffset).DateTime);
        return t switch
        {
            _ when t < new TimeOnly(10, 0) => "09:15-10:00",
            _ when t < new TimeOnly(11, 0) => "10:00-11:00",
            _ when t < new TimeOnly(12, 0) => "11:00-12:00",
            _ when t < new TimeOnly(13, 0) => "12:00-13:00",
            _ when t < new TimeOnly(14, 0) => "13:00-14:00",
            _ when t < new TimeOnly(15, 0) => "14:00-15:00",
            _ => "15:00-15:30",
        };
    }

    /// <summary>Bottom quartile / middle 50% / top quartile of the SAME dataset's own slow-window duration -- descriptive, not an optimized cutoff (spec point 9's own explicit instruction).</summary>
    public static string SpeedBucket(double slowWindowDurationMs, double q1Ms, double q3Ms) => slowWindowDurationMs switch
    {
        var ms when ms <= q1Ms => "Fast (bottom quartile)",
        var ms when ms >= q3Ms => "Slow (top quartile)",
        _ => "Normal (middle 50%)",
    };

    /// <summary>Quartile thresholds of <see cref="Vc0DteBehaviorRecorder.ObservationRow.SlowWindowDurationMs"/> across the given observations -- nearest-rank method, same convention <see cref="EventBarDurationStats"/> already uses.</summary>
    public static (double Q1Ms, double Q3Ms) ComputeSlowWindowQuartiles(IReadOnlyList<Vc0DteBehaviorRecorder.ObservationRow> rows)
    {
        var durations = rows.Select(r => r.SlowWindowDurationMs).Order().ToList();
        if (durations.Count == 0)
        {
            return (0, 0);
        }

        double Percentile(double p)
        {
            var rank = (int)Math.Ceiling(p / 100.0 * durations.Count) - 1;
            return durations[Math.Clamp(rank, 0, durations.Count - 1)];
        }

        return (Percentile(25), Percentile(75));
    }

    static SummaryRow Aggregate(OptionType optionType, string signalDirection, List<Vc0DteBehaviorRecorder.ObservationRow> group)
    {
        static decimal? AvgOrNull(IEnumerable<decimal?> values)
        {
            var real = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return real.Count > 0 ? real.Average() : null;
        }
        static decimal? MedianOrNull(IEnumerable<decimal?> values)
        {
            var real = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
            if (real.Count == 0)
            {
                return null;
            }
            var mid = real.Count / 2;
            return real.Count % 2 == 1 ? real[mid] : (real[mid - 1] + real[mid]) / 2m;
        }
        static double? AvgOrNullD(IEnumerable<int?> values)
        {
            var real = values.Where(v => v is not null).Select(v => (double)v!.Value).ToList();
            return real.Count > 0 ? real.Average() : null;
        }
        static decimal Median(IEnumerable<decimal> values) => MedianOrNull(values.Select(v => (decimal?)v))!.Value;

        return new SummaryRow(
            optionType, signalDirection, group.Count, group.Select(r => r.TradingDate).Distinct().Count(),
            AvgOrNull(group.Select(r => r.ForwardReturn1)), MedianOrNull(group.Select(r => r.ForwardReturn1)),
            AvgOrNull(group.Select(r => r.ForwardReturn2)), MedianOrNull(group.Select(r => r.ForwardReturn2)),
            AvgOrNull(group.Select(r => r.ForwardReturn3)), MedianOrNull(group.Select(r => r.ForwardReturn3)),
            AvgOrNull(group.Select(r => r.ForwardReturn5)), MedianOrNull(group.Select(r => r.ForwardReturn5)),
            AvgOrNull(group.Select(r => r.ForwardReturn10)), MedianOrNull(group.Select(r => r.ForwardReturn10)),
            group.Average(r => r.MaximumFavorableMovePercent), Median(group.Select(r => r.MaximumFavorableMovePercent)),
            group.Average(r => r.MaximumAdverseMovePercent), Median(group.Select(r => r.MaximumAdverseMovePercent)),
            group.Average(r => r.MaximumFavorableMoveAbs), group.Average(r => r.MaximumAdverseMoveAbs),
            AvgOrNullD(group.Select(r => r.EventBarsToMaximumFavorable)), AvgOrNullD(group.Select(r => r.EventBarsToMaximumAdverse)),
            AvgOrNull(group.Select(r => r.FutureForwardReturnPercent1)), AvgOrNull(group.Select(r => r.FutureForwardReturnPercent3)),
            AvgOrNull(group.Select(r => r.FutureForwardReturnPercent5)), AvgOrNull(group.Select(r => r.FutureForwardReturnPercent10)),
            AvgOrNull(group.Select(r => r.PreSignalOptionMovePercent3)), AvgOrNull(group.Select(r => r.PreSignalOptionMovePercent5)), AvgOrNull(group.Select(r => r.PreSignalOptionMovePercent10)),
            AvgOrNull(group.Select(r => r.PreSignalFutureMovePercent3)), AvgOrNull(group.Select(r => r.PreSignalFutureMovePercent5)), AvgOrNull(group.Select(r => r.PreSignalFutureMovePercent10)));
    }
}
