using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Predeclared first-pass statistical analysis for the four 6500-bar Futures metrics. This class
/// deliberately analyzes underlying Futures response only; it does not create entry thresholds,
/// option trades, composite scores, time-of-day gates or optimized parameters.
/// </summary>
public static class VolumeBar6500MetricAnalysis
{
    public const long ExtremeSignalBarVolumeCutoff = 13_000;

    public sealed record MetricSpec(
        string Name,
        Func<VolumeBar6500Revalidation.Observation, double?> Value);

    public sealed record HorizonSpec(
        int Bars,
        Func<VolumeBar6500Revalidation.Observation, double?> ForwardPoints,
        Func<VolumeBar6500Revalidation.Observation, long?> ForwardObservedVolume,
        Func<VolumeBar6500Revalidation.Observation, double?> MaxUpPoints,
        Func<VolumeBar6500Revalidation.Observation, double?> MaxDownPoints);

    public static readonly MetricSpec[] Metrics =
    [
        new("TobDepthDivergence", o => o.TobDepthDivergence),
        new("DepthImbalance", o => o.DepthImbalance),
        new("OrderFlowImbalance", o => o.OrderFlowImbalance),
        new("BarDurationUrgency", o => o.BarDurationUrgency),
    ];

    public static readonly HorizonSpec[] Horizons =
    [
        new(1, o => o.Forward1Points, o => o.Forward1ObservedVolume, o => o.MaxUp1Points, o => o.MaxDown1Points),
        new(2, o => o.Forward2Points, o => o.Forward2ObservedVolume, o => o.MaxUp2Points, o => o.MaxDown2Points),
        new(4, o => o.Forward4Points, o => o.Forward4ObservedVolume, o => o.MaxUp4Points, o => o.MaxDown4Points),
    ];

    public sealed record MetricSummaryRow(
        string Metric,
        int HorizonBars,
        string Scope,
        int N,
        double? PooledSpearman,
        double? MetricVsSignalBarChangeSpearman,
        double? SignalBarChangeVsForwardSpearman,
        int SessionCount,
        int SessionSpearmanAvailableCount,
        double? MedianSessionSpearman,
        int PositiveSessionSpearmanCount,
        int NegativeSessionSpearmanCount,
        int ZeroSessionSpearmanCount,
        double? Q5MinusQ1MeanForwardPoints,
        long? MedianForwardObservedVolume,
        long? P95ForwardObservedVolume,
        double? LeaveOneSessionOutMinSpearman,
        double? LeaveOneSessionOutMaxSpearman,
        int LeaveOneSessionOutSameSignCount,
        int LeaveOneSessionOutCount);

    public sealed record SessionRow(
        string Metric,
        int HorizonBars,
        string Scope,
        DateOnly TradingDate,
        int Dte,
        int N,
        double? Spearman,
        int Q1N,
        int Q5N,
        double? Q5MinusQ1MeanForwardPoints);

    public sealed record QuintileRow(
        string Metric,
        int HorizonBars,
        string Scope,
        int Quintile,
        int N,
        double MetricMin,
        double MetricMax,
        double MeanMetric,
        double MeanForwardPoints,
        double MedianForwardPoints,
        double FuturePositiveRate,
        double MeanMaxUpPoints,
        double MeanMaxDownPoints);

    public sealed record DteRow(
        string Metric,
        int HorizonBars,
        string Scope,
        int Dte,
        int N,
        double? Spearman,
        double MeanForwardPoints,
        double MedianForwardPoints,
        double FuturePositiveRate);

    public sealed record LeaveOneSessionOutRow(
        string Metric,
        int HorizonBars,
        DateOnly ExcludedDate,
        int N,
        double? Spearman);

    public sealed record AnalysisResult(
        IReadOnlyList<MetricSummaryRow> Summary,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<QuintileRow> Quintiles,
        IReadOnlyList<DteRow> Dte,
        IReadOnlyList<LeaveOneSessionOutRow> LeaveOneSessionOut);

    sealed record Point(
        VolumeBar6500Revalidation.Observation Observation,
        double MetricValue,
        double ForwardPoints,
        long ForwardObservedVolume,
        double MaxUpPoints,
        double MaxDownPoints);

    sealed record Cutpoints(double Q20, double Q40, double Q60, double Q80);

    public static AnalysisResult Analyze(IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var summaryRows = new List<MetricSummaryRow>();
        var sessionRows = new List<SessionRow>();
        var quintileRows = new List<QuintileRow>();
        var dteRows = new List<DteRow>();
        var looRows = new List<LeaveOneSessionOutRow>();

        foreach (var metric in Metrics)
        {
            // Cutpoints depend only on the metric's own raw values, never on any future outcome.
            var metricValues = observations
                .Select(metric.Value)
                .Where(v => v is not null && double.IsFinite(v.Value))
                .Select(v => v!.Value)
                .OrderBy(v => v)
                .ToArray();

            if (metricValues.Length == 0)
            {
                continue;
            }

            var cutpoints = new Cutpoints(
                Percentile(metricValues, 0.20),
                Percentile(metricValues, 0.40),
                Percentile(metricValues, 0.60),
                Percentile(metricValues, 0.80));

            foreach (var horizon in Horizons)
            {
                var primary = BuildPoints(observations, metric, horizon, extremeVolumeFilter: false);
                var sensitivity = BuildPoints(observations, metric, horizon, extremeVolumeFilter: true);

                foreach (var scope in new[]
                {
                    (Name: "AllValid", Points: primary),
                    (Name: "SignalBarVolumeLt13000", Points: sensitivity),
                })
                {
                    var sessions = BuildSessionRows(metric.Name, horizon.Bars, scope.Name, scope.Points, cutpoints);
                    sessionRows.AddRange(sessions);

                    var quintiles = BuildQuintileRows(metric.Name, horizon.Bars, scope.Name, scope.Points, cutpoints);
                    quintileRows.AddRange(quintiles);

                    var dte = BuildDteRows(metric.Name, horizon.Bars, scope.Name, scope.Points);
                    dteRows.AddRange(dte);

                    var pooledSpearman = Spearman(
                        scope.Points.Select(p => p.MetricValue).ToArray(),
                        scope.Points.Select(p => p.ForwardPoints).ToArray());

                    var metricVsSignal = Spearman(
                        scope.Points.Select(p => p.MetricValue).ToArray(),
                        scope.Points.Select(p => p.Observation.SignalBarChangePoints).ToArray());

                    var signalVsForward = Spearman(
                        scope.Points.Select(p => p.Observation.SignalBarChangePoints).ToArray(),
                        scope.Points.Select(p => p.ForwardPoints).ToArray());

                    var sessionRhos = sessions
                        .Select(s => s.Spearman)
                        .Where(x => x is not null)
                        .Select(x => x!.Value)
                        .ToArray();

                    var q1 = quintiles.FirstOrDefault(q => q.Quintile == 1);
                    var q5 = quintiles.FirstOrDefault(q => q.Quintile == 5);
                    var q5MinusQ1 = q1 is not null && q5 is not null
                        ? q5.MeanForwardPoints - q1.MeanForwardPoints
                        : (double?)null;

                    var forwardVolumes = scope.Points
                        .Select(p => p.ForwardObservedVolume)
                        .OrderBy(x => x)
                        .ToArray();

                    double? looMin = null;
                    double? looMax = null;
                    var sameSign = 0;
                    var looCount = 0;

                    if (scope.Name == "AllValid")
                    {
                        var loo = BuildLeaveOneSessionOutRows(metric.Name, horizon.Bars, scope.Points);
                        looRows.AddRange(loo);

                        var looRhos = loo
                            .Select(x => x.Spearman)
                            .Where(x => x is not null)
                            .Select(x => x!.Value)
                            .ToArray();

                        if (looRhos.Length > 0)
                        {
                            looMin = looRhos.Min();
                            looMax = looRhos.Max();
                            looCount = looRhos.Length;
                            sameSign = pooledSpearman is { } pooled
                                ? looRhos.Count(x => SameNonZeroSign(pooled, x))
                                : 0;
                        }
                    }

                    summaryRows.Add(new MetricSummaryRow(
                        metric.Name,
                        horizon.Bars,
                        scope.Name,
                        scope.Points.Count,
                        pooledSpearman,
                        metricVsSignal,
                        signalVsForward,
                        sessions.Count,
                        sessionRhos.Length,
                        sessionRhos.Length > 0 ? Median(sessionRhos) : null,
                        sessionRhos.Count(x => x > 0),
                        sessionRhos.Count(x => x < 0),
                        sessionRhos.Count(x => x == 0),
                        q5MinusQ1,
                        forwardVolumes.Length > 0 ? NearestRank(forwardVolumes, 0.50) : null,
                        forwardVolumes.Length > 0 ? NearestRank(forwardVolumes, 0.95) : null,
                        looMin,
                        looMax,
                        sameSign,
                        looCount));
                }
            }
        }

        return new AnalysisResult(summaryRows, sessionRows, quintileRows, dteRows, looRows);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        string outputDirectory)
    {
        var result = Analyze(observations);
        Directory.CreateDirectory(outputDirectory);

        await WriteSummaryCsvAsync(Path.Combine(outputDirectory, "metric-summary.csv"), result.Summary);
        await WriteSessionsCsvAsync(Path.Combine(outputDirectory, "metric-sessions.csv"), result.Sessions);
        await WriteQuintilesCsvAsync(Path.Combine(outputDirectory, "metric-quintiles.csv"), result.Quintiles);
        await WriteDteCsvAsync(Path.Combine(outputDirectory, "metric-dte.csv"), result.Dte);
        await WriteLeaveOneOutCsvAsync(
            Path.Combine(outputDirectory, "metric-leave-one-session-out.csv"),
            result.LeaveOneSessionOut);

        PrintPrimarySummary(result.Summary);
        return result;
    }

    /// <summary>
    /// Tie-aware Spearman rank correlation. Returns null when there are fewer than three valid
    /// pairs or either ranked series is constant.
    /// </summary>
    public static double? Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count)
        {
            throw new ArgumentException("Spearman inputs must have equal length.");
        }

        var pairs = Enumerable.Range(0, x.Count)
            .Where(i => double.IsFinite(x[i]) && double.IsFinite(y[i]))
            .Select(i => (X: x[i], Y: y[i]))
            .ToArray();

        if (pairs.Length < 3)
        {
            return null;
        }

        var rx = Ranks(pairs.Select(p => p.X).ToArray());
        var ry = Ranks(pairs.Select(p => p.Y).ToArray());
        return Pearson(rx, ry);
    }

    static List<Point> BuildPoints(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        MetricSpec metric,
        HorizonSpec horizon,
        bool extremeVolumeFilter)
    {
        var points = new List<Point>();

        foreach (var observation in observations)
        {
            if (extremeVolumeFilter && observation.ObservedVolume >= ExtremeSignalBarVolumeCutoff)
            {
                continue;
            }

            var metricValue = metric.Value(observation);
            var forward = horizon.ForwardPoints(observation);
            var forwardVolume = horizon.ForwardObservedVolume(observation);
            var maxUp = horizon.MaxUpPoints(observation);
            var maxDown = horizon.MaxDownPoints(observation);

            if (metricValue is null || forward is null || forwardVolume is null || maxUp is null || maxDown is null)
            {
                continue;
            }

            if (!double.IsFinite(metricValue.Value) ||
                !double.IsFinite(forward.Value) ||
                !double.IsFinite(maxUp.Value) ||
                !double.IsFinite(maxDown.Value))
            {
                continue;
            }

            points.Add(new Point(
                observation,
                metricValue.Value,
                forward.Value,
                forwardVolume.Value,
                maxUp.Value,
                maxDown.Value));
        }

        return points;
    }

    static List<SessionRow> BuildSessionRows(
        string metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        Cutpoints cutpoints)
    {
        var rows = new List<SessionRow>();

        foreach (var group in points.GroupBy(p => p.Observation.TradingDate).OrderBy(g => g.Key))
        {
            var items = group.ToArray();
            var q1 = items.Where(p => Quintile(p.MetricValue, cutpoints) == 1).ToArray();
            var q5 = items.Where(p => Quintile(p.MetricValue, cutpoints) == 5).ToArray();

            rows.Add(new SessionRow(
                metric,
                horizon,
                scope,
                group.Key,
                items[0].Observation.Dte,
                items.Length,
                Spearman(items.Select(p => p.MetricValue).ToArray(), items.Select(p => p.ForwardPoints).ToArray()),
                q1.Length,
                q5.Length,
                q1.Length > 0 && q5.Length > 0
                    ? q5.Average(p => p.ForwardPoints) - q1.Average(p => p.ForwardPoints)
                    : null));
        }

        return rows;
    }

    static List<QuintileRow> BuildQuintileRows(
        string metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        Cutpoints cutpoints)
    {
        var rows = new List<QuintileRow>();

        for (var quintile = 1; quintile <= 5; quintile++)
        {
            var group = points.Where(p => Quintile(p.MetricValue, cutpoints) == quintile).ToArray();
            if (group.Length == 0)
            {
                continue;
            }

            rows.Add(new QuintileRow(
                metric,
                horizon,
                scope,
                quintile,
                group.Length,
                group.Min(p => p.MetricValue),
                group.Max(p => p.MetricValue),
                group.Average(p => p.MetricValue),
                group.Average(p => p.ForwardPoints),
                Median(group.Select(p => p.ForwardPoints).ToArray()),
                group.Count(p => p.ForwardPoints > 0) / (double)group.Length,
                group.Average(p => p.MaxUpPoints),
                group.Average(p => p.MaxDownPoints)));
        }

        return rows;
    }

    static List<DteRow> BuildDteRows(
        string metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points)
    {
        var rows = new List<DteRow>();

        foreach (var group in points.GroupBy(p => p.Observation.Dte).OrderBy(g => g.Key))
        {
            var items = group.ToArray();
            rows.Add(new DteRow(
                metric,
                horizon,
                scope,
                group.Key,
                items.Length,
                Spearman(items.Select(p => p.MetricValue).ToArray(), items.Select(p => p.ForwardPoints).ToArray()),
                items.Average(p => p.ForwardPoints),
                Median(items.Select(p => p.ForwardPoints).ToArray()),
                items.Count(p => p.ForwardPoints > 0) / (double)items.Length));
        }

        return rows;
    }

    static List<LeaveOneSessionOutRow> BuildLeaveOneSessionOutRows(
        string metric,
        int horizon,
        IReadOnlyList<Point> points)
    {
        var rows = new List<LeaveOneSessionOutRow>();

        foreach (var excluded in points.Select(p => p.Observation.TradingDate).Distinct().OrderBy(x => x))
        {
            var remaining = points.Where(p => p.Observation.TradingDate != excluded).ToArray();

            rows.Add(new LeaveOneSessionOutRow(
                metric,
                horizon,
                excluded,
                remaining.Length,
                Spearman(
                    remaining.Select(p => p.MetricValue).ToArray(),
                    remaining.Select(p => p.ForwardPoints).ToArray())));
        }

        return rows;
    }

    static int Quintile(double value, Cutpoints c)
    {
        if (value <= c.Q20) { return 1; }
        if (value <= c.Q40) { return 2; }
        if (value <= c.Q60) { return 3; }
        if (value <= c.Q80) { return 4; }
        return 5;
    }

    static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            throw new ArgumentException("Percentile input must not be empty.");
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Count - 1) * p;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);

        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }

    static double[] Ranks(IReadOnlyList<double> values)
    {
        var indexed = values
            .Select((value, index) => (Value: value, Index: index))
            .OrderBy(x => x.Value)
            .ToArray();

        var ranks = new double[values.Count];
        var i = 0;

        while (i < indexed.Length)
        {
            var j = i + 1;
            while (j < indexed.Length && indexed[j].Value.Equals(indexed[i].Value))
            {
                j++;
            }

            // Ranks are 1-based; tied observations receive the average occupied rank.
            var averageRank = ((i + 1) + j) / 2.0;
            for (var k = i; k < j; k++)
            {
                ranks[indexed[k].Index] = averageRank;
            }

            i = j;
        }

        return ranks;
    }

    static double? Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var meanX = x.Average();
        var meanY = y.Average();

        double covariance = 0;
        double varianceX = 0;
        double varianceY = 0;

        for (var i = 0; i < x.Count; i++)
        {
            var dx = x[i] - meanX;
            var dy = y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        if (varianceX <= 0 || varianceY <= 0)
        {
            return null;
        }

        return covariance / Math.Sqrt(varianceX * varianceY);
    }

    static bool SameNonZeroSign(double left, double right) =>
        (left > 0 && right > 0) || (left < 0 && right < 0);

    static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("Median input must not be empty.");
        }

        var sorted = values.OrderBy(x => x).ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static long NearestRank(IReadOnlyList<long> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    static void PrintPrimarySummary(IReadOnlyList<MetricSummaryRow> summary)
    {
        Console.WriteLine("6500 metric analysis — underlying Futures only; no trading-rule selection:");

        foreach (var row in summary
            .Where(r => r.Scope == "AllValid")
            .OrderBy(r => r.Metric)
            .ThenBy(r => r.HorizonBars))
        {
            Console.WriteLine(
                $"  {row.Metric,-22} +{row.HorizonBars}: n={row.N:N0}, " +
                $"rho={Format(row.PooledSpearman)}, medianSessionRho={Format(row.MedianSessionSpearman)}, " +
                $"sessionSigns=+{row.PositiveSessionSpearmanCount}/-{row.NegativeSessionSpearmanCount}/0{row.ZeroSessionSpearmanCount}, " +
                $"Q5-Q1={Format(row.Q5MinusQ1MeanForwardPoints)} pts, " +
                $"rho(metric,currentBarMove)={Format(row.MetricVsSignalBarChangeSpearman)}, " +
                $"LOO sameSign={row.LeaveOneSessionOutSameSignCount}/{row.LeaveOneSessionOutCount}");
        }

        Console.WriteLine(
            $"Sensitivity is separately exported after removing signal bars with ObservedVolume >= {ExtremeSignalBarVolumeCutoff:N0}.");
    }

    static string Format(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    static async Task WriteSummaryCsvAsync(string path, IReadOnlyList<MetricSummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,Scope,N,PooledSpearman,MetricVsSignalBarChangeSpearman," +
            "SignalBarChangeVsForwardSpearman,SessionCount,SessionSpearmanAvailableCount,MedianSessionSpearman," +
            "PositiveSessionSpearmanCount,NegativeSessionSpearmanCount,ZeroSessionSpearmanCount," +
            "Q5MinusQ1MeanForwardPoints,MedianForwardObservedVolume,P95ForwardObservedVolume," +
            "LeaveOneSessionOutMinSpearman,LeaveOneSessionOutMaxSpearman," +
            "LeaveOneSessionOutSameSignCount,LeaveOneSessionOutCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope, r.N, r.PooledSpearman,
                r.MetricVsSignalBarChangeSpearman, r.SignalBarChangeVsForwardSpearman,
                r.SessionCount, r.SessionSpearmanAvailableCount, r.MedianSessionSpearman,
                r.PositiveSessionSpearmanCount, r.NegativeSessionSpearmanCount, r.ZeroSessionSpearmanCount,
                r.Q5MinusQ1MeanForwardPoints, r.MedianForwardObservedVolume, r.P95ForwardObservedVolume,
                r.LeaveOneSessionOutMinSpearman, r.LeaveOneSessionOutMaxSpearman,
                r.LeaveOneSessionOutSameSignCount, r.LeaveOneSessionOutCount));
        }
    }

    static async Task WriteSessionsCsvAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,Scope,TradingDate,Dte,N,Spearman,Q1N,Q5N,Q5MinusQ1MeanForwardPoints");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope,
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.N, r.Spearman, r.Q1N, r.Q5N, r.Q5MinusQ1MeanForwardPoints));
        }
    }

    static async Task WriteQuintilesCsvAsync(string path, IReadOnlyList<QuintileRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,Scope,Quintile,N,MetricMin,MetricMax,MeanMetric,MeanForwardPoints," +
            "MedianForwardPoints,FuturePositiveRate,MeanMaxUpPoints,MeanMaxDownPoints");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope, r.Quintile, r.N,
                r.MetricMin, r.MetricMax, r.MeanMetric,
                r.MeanForwardPoints, r.MedianForwardPoints, r.FuturePositiveRate,
                r.MeanMaxUpPoints, r.MeanMaxDownPoints));
        }
    }

    static async Task WriteDteCsvAsync(string path, IReadOnlyList<DteRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,Scope,Dte,N,Spearman,MeanForwardPoints,MedianForwardPoints,FuturePositiveRate");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope, r.Dte, r.N, r.Spearman,
                r.MeanForwardPoints, r.MedianForwardPoints, r.FuturePositiveRate));
        }
    }

    static async Task WriteLeaveOneOutCsvAsync(string path, IReadOnlyList<LeaveOneSessionOutRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Metric,HorizonBars,ExcludedDate,N,Spearman");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars,
                r.ExcludedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.N, r.Spearman));
        }
    }

    static StreamWriter Writer(string path) =>
        new(path, false, new UTF8Encoding(false));

    static string Csv(params object?[] values) =>
        string.Join(",", values.Select(CsvField));

    static string CsvField(object? value)
    {
        if (value is null) { return ""; }

        var field = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
