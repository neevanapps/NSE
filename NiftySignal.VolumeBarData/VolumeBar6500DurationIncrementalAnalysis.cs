using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Predeclared incremental-value test for the strongest first-pass 6500-bar result:
/// does bar duration add reversal information after matching on the signal bar's direction
/// and absolute close-minus-open move magnitude?
///
/// This is an underlying-Futures diagnostic only. It does not define entries, thresholds,
/// option trades, stop-losses, composites or production logic.
/// </summary>
public static class VolumeBar6500DurationIncrementalAnalysis
{
    public const long ExtremeSignalBarVolumeCutoff = 13_000;

    public sealed record HorizonSpec(
        int Bars,
        Func<VolumeBar6500Revalidation.Observation, double?> ForwardPoints);

    public static readonly HorizonSpec[] Horizons =
    [
        new(1, o => o.Forward1Points),
        new(2, o => o.Forward2Points),
        new(4, o => o.Forward4Points),
    ];

    public sealed record CellDefinition(
        string Direction,
        int DirectionSign,
        int MagnitudeQuintile,
        double MoveMagnitudeMinObserved,
        double MoveMagnitudeMaxObserved,
        double DurationMedianSeconds);

    public sealed record CellRow(
        int HorizonBars,
        string Scope,
        string Direction,
        int MagnitudeQuintile,
        double MoveMagnitudeMinObserved,
        double MoveMagnitudeMaxObserved,
        double DurationMedianSeconds,
        int N,
        int FastN,
        int SlowN,
        double? FastMeanReversalPoints,
        double? SlowMeanReversalPoints,
        double? FastMinusSlowMeanReversalPoints,
        double? FastMedianReversalPoints,
        double? SlowMedianReversalPoints,
        double? FastReversalHitRate,
        double? SlowReversalHitRate,
        double? DurationVsReversalSpearman);

    public sealed record SessionRow(
        int HorizonBars,
        string Scope,
        DateOnly TradingDate,
        int Dte,
        int N,
        int ValidCellCount,
        double? MeanCellFastMinusSlowReversalPoints,
        double? MedianCellFastMinusSlowReversalPoints);

    public sealed record LeaveOneSessionOutRow(
        int HorizonBars,
        DateOnly ExcludedDate,
        int N,
        int ValidCellCount,
        double? MeanCellFastMinusSlowReversalPoints,
        double? MedianCellFastMinusSlowReversalPoints);

    public sealed record SummaryRow(
        int HorizonBars,
        string Scope,
        int N,
        int ValidCellCount,
        double? MeanCellFastMinusSlowReversalPoints,
        double? MedianCellFastMinusSlowReversalPoints,
        int CellDurationSpearmanAvailableCount,
        double? MedianCellDurationVsReversalSpearman,
        int NegativeCellDurationSpearmanCount,
        int PositiveCellDurationSpearmanCount,
        int SessionCount,
        int SessionsWithValidCells,
        double? MedianSessionFastMinusSlowReversalPoints,
        int PositiveSessionFastMinusSlowCount,
        int NegativeSessionFastMinusSlowCount,
        int ZeroSessionFastMinusSlowCount,
        double? LeaveOneSessionOutMinFastMinusSlow,
        double? LeaveOneSessionOutMedianFastMinusSlow,
        double? LeaveOneSessionOutMaxFastMinusSlow,
        int LeaveOneSessionOutPositiveCount,
        int LeaveOneSessionOutCount);

    public sealed record AnalysisResult(
        IReadOnlyList<CellDefinition> Definitions,
        IReadOnlyList<SummaryRow> Summary,
        IReadOnlyList<CellRow> Cells,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<LeaveOneSessionOutRow> LeaveOneSessionOut);

    sealed record DirectionCutpoints(
        int DirectionSign,
        double Q20,
        double Q40,
        double Q60,
        double Q80);

    sealed record Point(
        VolumeBar6500Revalidation.Observation Observation,
        CellDefinition Definition,
        bool IsFast,
        double ReversalAlignedFuturePoints);

    public static AnalysisResult Analyze(IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var eligibleSignals = observations
            .Where(IsEligibleSignal)
            .ToArray();

        var definitions = BuildFrozenDefinitions(eligibleSignals);
        var definitionMap = definitions.ToDictionary(
            d => (d.DirectionSign, d.MagnitudeQuintile));

        var cells = new List<CellRow>();
        var sessions = new List<SessionRow>();
        var leaveOneOut = new List<LeaveOneSessionOutRow>();
        var summary = new List<SummaryRow>();

        foreach (var horizon in Horizons)
        {
            var primary = BuildPoints(
                eligibleSignals,
                definitionMap,
                horizon,
                extremeVolumeFilter: false);

            var sensitivity = BuildPoints(
                eligibleSignals,
                definitionMap,
                horizon,
                extremeVolumeFilter: true);

            foreach (var scope in new[]
            {
                (Name: "AllValid", Points: primary),
                (Name: "SignalBarVolumeLt13000", Points: sensitivity),
            })
            {
                var cellRows = BuildCellRows(horizon.Bars, scope.Name, scope.Points, definitions);
                var sessionRows = BuildSessionRows(horizon.Bars, scope.Name, scope.Points, definitions);

                cells.AddRange(cellRows);
                sessions.AddRange(sessionRows);

                IReadOnlyList<LeaveOneSessionOutRow> looRows = Array.Empty<LeaveOneSessionOutRow>();
                if (scope.Name == "AllValid")
                {
                    looRows = BuildLeaveOneSessionOutRows(horizon.Bars, scope.Points, definitions);
                    leaveOneOut.AddRange(looRows);
                }

                summary.Add(BuildSummary(
                    horizon.Bars,
                    scope.Name,
                    scope.Points,
                    cellRows,
                    sessionRows,
                    looRows));
            }
        }

        return new AnalysisResult(definitions, summary, cells, sessions, leaveOneOut);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        string outputDirectory)
    {
        var result = Analyze(observations);
        Directory.CreateDirectory(outputDirectory);

        await WriteSummaryCsvAsync(
            Path.Combine(outputDirectory, "duration-incremental-summary.csv"),
            result.Summary);

        await WriteCellsCsvAsync(
            Path.Combine(outputDirectory, "duration-incremental-cells.csv"),
            result.Cells);

        await WriteSessionsCsvAsync(
            Path.Combine(outputDirectory, "duration-incremental-sessions.csv"),
            result.Sessions);

        await WriteLeaveOneOutCsvAsync(
            Path.Combine(outputDirectory, "duration-incremental-leave-one-session-out.csv"),
            result.LeaveOneSessionOut);

        PrintPrimarySummary(result.Summary);
        return result;
    }

    public static IReadOnlyList<CellDefinition> BuildFrozenDefinitions(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var definitions = new List<CellDefinition>();

        foreach (var directionSign in new[] { -1, 1 })
        {
            var directionRows = observations
                .Where(o => Math.Sign(o.SignalBarChangePoints) == directionSign)
                .ToArray();

            if (directionRows.Length == 0)
            {
                continue;
            }

            var magnitudes = directionRows
                .Select(o => Math.Abs(o.SignalBarChangePoints))
                .OrderBy(x => x)
                .ToArray();

            var cutpoints = new DirectionCutpoints(
                directionSign,
                Percentile(magnitudes, 0.20),
                Percentile(magnitudes, 0.40),
                Percentile(magnitudes, 0.60),
                Percentile(magnitudes, 0.80));

            for (var quintile = 1; quintile <= 5; quintile++)
            {
                var cell = directionRows
                    .Where(o => MagnitudeQuintile(Math.Abs(o.SignalBarChangePoints), cutpoints) == quintile)
                    .ToArray();

                if (cell.Length == 0)
                {
                    continue;
                }

                definitions.Add(new CellDefinition(
                    DirectionName(directionSign),
                    directionSign,
                    quintile,
                    cell.Min(o => Math.Abs(o.SignalBarChangePoints)),
                    cell.Max(o => Math.Abs(o.SignalBarChangePoints)),
                    Median(cell.Select(o => o.ExchangeDurationSeconds).ToArray())));
            }
        }

        return definitions
            .OrderBy(d => d.DirectionSign)
            .ThenBy(d => d.MagnitudeQuintile)
            .ToArray();
    }

    static List<Point> BuildPoints(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        IReadOnlyDictionary<(int DirectionSign, int MagnitudeQuintile), CellDefinition> definitions,
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

            var forward = horizon.ForwardPoints(observation);
            if (forward is null || !double.IsFinite(forward.Value))
            {
                continue;
            }

            var directionSign = Math.Sign(observation.SignalBarChangePoints);
            var quintile = FindDefinitionQuintile(
                Math.Abs(observation.SignalBarChangePoints),
                directionSign,
                definitions.Values);

            if (!definitions.TryGetValue((directionSign, quintile), out var definition))
            {
                continue;
            }

            var reversalAligned = -directionSign * forward.Value;
            points.Add(new Point(
                observation,
                definition,
                observation.ExchangeDurationSeconds <= definition.DurationMedianSeconds,
                reversalAligned));
        }

        return points;
    }

    static List<CellRow> BuildCellRows(
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<CellDefinition> definitions)
    {
        var rows = new List<CellRow>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p =>
                    p.Definition.DirectionSign == definition.DirectionSign &&
                    p.Definition.MagnitudeQuintile == definition.MagnitudeQuintile)
                .ToArray();

            if (group.Length == 0)
            {
                continue;
            }

            var fast = group.Where(p => p.IsFast).ToArray();
            var slow = group.Where(p => !p.IsFast).ToArray();

            rows.Add(new CellRow(
                horizon,
                scope,
                definition.Direction,
                definition.MagnitudeQuintile,
                definition.MoveMagnitudeMinObserved,
                definition.MoveMagnitudeMaxObserved,
                definition.DurationMedianSeconds,
                group.Length,
                fast.Length,
                slow.Length,
                MeanOrNull(fast.Select(p => p.ReversalAlignedFuturePoints).ToArray()),
                MeanOrNull(slow.Select(p => p.ReversalAlignedFuturePoints).ToArray()),
                FastMinusSlow(fast, slow),
                MedianOrNull(fast.Select(p => p.ReversalAlignedFuturePoints).ToArray()),
                MedianOrNull(slow.Select(p => p.ReversalAlignedFuturePoints).ToArray()),
                RateOrNull(fast.Select(p => p.ReversalAlignedFuturePoints > 0).ToArray()),
                RateOrNull(slow.Select(p => p.ReversalAlignedFuturePoints > 0).ToArray()),
                VolumeBar6500MetricAnalysis.Spearman(
                    group.Select(p => p.Observation.ExchangeDurationSeconds).ToArray(),
                    group.Select(p => p.ReversalAlignedFuturePoints).ToArray())));
        }

        return rows;
    }

    static List<SessionRow> BuildSessionRows(
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<CellDefinition> definitions)
    {
        var rows = new List<SessionRow>();

        foreach (var group in points.GroupBy(p => p.Observation.TradingDate).OrderBy(g => g.Key))
        {
            var items = group.ToArray();
            var cellDiffs = MatchedCellDifferences(items, definitions);

            rows.Add(new SessionRow(
                horizon,
                scope,
                group.Key,
                items[0].Observation.Dte,
                items.Length,
                cellDiffs.Length,
                MeanOrNull(cellDiffs),
                MedianOrNull(cellDiffs)));
        }

        return rows;
    }

    static List<LeaveOneSessionOutRow> BuildLeaveOneSessionOutRows(
        int horizon,
        IReadOnlyList<Point> points,
        IReadOnlyList<CellDefinition> definitions)
    {
        var rows = new List<LeaveOneSessionOutRow>();

        foreach (var excluded in points
            .Select(p => p.Observation.TradingDate)
            .Distinct()
            .OrderBy(x => x))
        {
            var remaining = points
                .Where(p => p.Observation.TradingDate != excluded)
                .ToArray();

            var cellDiffs = MatchedCellDifferences(remaining, definitions);

            rows.Add(new LeaveOneSessionOutRow(
                horizon,
                excluded,
                remaining.Length,
                cellDiffs.Length,
                MeanOrNull(cellDiffs),
                MedianOrNull(cellDiffs)));
        }

        return rows;
    }

    static SummaryRow BuildSummary(
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<CellRow> cells,
        IReadOnlyList<SessionRow> sessions,
        IReadOnlyList<LeaveOneSessionOutRow> leaveOneOut)
    {
        var cellDiffs = cells
            .Select(c => c.FastMinusSlowMeanReversalPoints)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var cellRhos = cells
            .Select(c => c.DurationVsReversalSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var sessionDiffs = sessions
            .Select(s => s.MeanCellFastMinusSlowReversalPoints)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var looDiffs = leaveOneOut
            .Select(x => x.MeanCellFastMinusSlowReversalPoints)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        return new SummaryRow(
            horizon,
            scope,
            points.Count,
            cellDiffs.Length,
            MeanOrNull(cellDiffs),
            MedianOrNull(cellDiffs),
            cellRhos.Length,
            MedianOrNull(cellRhos),
            cellRhos.Count(x => x < 0),
            cellRhos.Count(x => x > 0),
            sessions.Count,
            sessionDiffs.Length,
            MedianOrNull(sessionDiffs),
            sessionDiffs.Count(x => x > 0),
            sessionDiffs.Count(x => x < 0),
            sessionDiffs.Count(x => x == 0),
            looDiffs.Length > 0 ? looDiffs.Min() : null,
            MedianOrNull(looDiffs),
            looDiffs.Length > 0 ? looDiffs.Max() : null,
            looDiffs.Count(x => x > 0),
            looDiffs.Length);
    }

    static double[] MatchedCellDifferences(
        IReadOnlyList<Point> points,
        IReadOnlyList<CellDefinition> definitions)
    {
        var differences = new List<double>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p =>
                    p.Definition.DirectionSign == definition.DirectionSign &&
                    p.Definition.MagnitudeQuintile == definition.MagnitudeQuintile)
                .ToArray();

            var fast = group.Where(p => p.IsFast).ToArray();
            var slow = group.Where(p => !p.IsFast).ToArray();

            if (fast.Length == 0 || slow.Length == 0)
            {
                continue;
            }

            differences.Add(
                fast.Average(p => p.ReversalAlignedFuturePoints) -
                slow.Average(p => p.ReversalAlignedFuturePoints));
        }

        return differences.ToArray();
    }

    static bool IsEligibleSignal(VolumeBar6500Revalidation.Observation o) =>
        double.IsFinite(o.SignalBarChangePoints) &&
        o.SignalBarChangePoints != 0 &&
        double.IsFinite(o.ExchangeDurationSeconds) &&
        o.ExchangeDurationSeconds > 0;

    static int FindDefinitionQuintile(
        double magnitude,
        int directionSign,
        IEnumerable<CellDefinition> definitions)
    {
        var directionDefinitions = definitions
            .Where(d => d.DirectionSign == directionSign)
            .OrderBy(d => d.MagnitudeQuintile)
            .ToArray();

        if (directionDefinitions.Length == 0)
        {
            return 0;
        }

        // Definitions are frozen from the full eligible signal-bar population. Using the observed
        // max of each predeclared cell preserves those frozen boundaries for every horizon/scope.
        for (var i = 0; i < directionDefinitions.Length - 1; i++)
        {
            if (magnitude <= directionDefinitions[i].MoveMagnitudeMaxObserved)
            {
                return directionDefinitions[i].MagnitudeQuintile;
            }
        }

        return directionDefinitions[^1].MagnitudeQuintile;
    }

    static int MagnitudeQuintile(double magnitude, DirectionCutpoints cutpoints)
    {
        if (magnitude <= cutpoints.Q20) { return 1; }
        if (magnitude <= cutpoints.Q40) { return 2; }
        if (magnitude <= cutpoints.Q60) { return 3; }
        if (magnitude <= cutpoints.Q80) { return 4; }
        return 5;
    }

    static string DirectionName(int sign) =>
        sign > 0 ? "UP" : "DOWN";

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

    static double? FastMinusSlow(IReadOnlyList<Point> fast, IReadOnlyList<Point> slow)
    {
        if (fast.Count == 0 || slow.Count == 0)
        {
            return null;
        }

        return fast.Average(p => p.ReversalAlignedFuturePoints) -
               slow.Average(p => p.ReversalAlignedFuturePoints);
    }

    static double? MeanOrNull(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values.Average();

    static double? MedianOrNull(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(x => x).ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static double Median(IReadOnlyList<double> values) =>
        MedianOrNull(values)
        ?? throw new ArgumentException("Median input must not be empty.");

    static double? RateOrNull(IReadOnlyList<bool> values) =>
        values.Count == 0 ? null : values.Count(x => x) / (double)values.Count;

    static void PrintPrimarySummary(IReadOnlyList<SummaryRow> summary)
    {
        Console.WriteLine(
            "6500 duration incremental test — reversal after matching direction + move-magnitude quintile:");

        foreach (var row in summary
            .Where(r => r.Scope == "AllValid")
            .OrderBy(r => r.HorizonBars))
        {
            Console.WriteLine(
                $"  +{row.HorizonBars}: n={row.N:N0}, cells={row.ValidCellCount}, " +
                $"meanCell(Fast-Slow)={Format(row.MeanCellFastMinusSlowReversalPoints)} pts, " +
                $"medianCell(Fast-Slow)={Format(row.MedianCellFastMinusSlowReversalPoints)} pts, " +
                $"medianCellRho(duration,reversal)={Format(row.MedianCellDurationVsReversalSpearman)}, " +
                $"sessionMedian={Format(row.MedianSessionFastMinusSlowReversalPoints)} pts, " +
                $"sessionSigns=+{row.PositiveSessionFastMinusSlowCount}/-{row.NegativeSessionFastMinusSlowCount}/0{row.ZeroSessionFastMinusSlowCount}, " +
                $"LOO positive={row.LeaveOneSessionOutPositiveCount}/{row.LeaveOneSessionOutCount}");
        }

        Console.WriteLine(
            $"Sensitivity is separately exported after removing signal bars with ObservedVolume >= {ExtremeSignalBarVolumeCutoff:N0}.");
    }

    static string Format(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    static async Task WriteSummaryCsvAsync(string path, IReadOnlyList<SummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,N,ValidCellCount,MeanCellFastMinusSlowReversalPoints," +
            "MedianCellFastMinusSlowReversalPoints,CellDurationSpearmanAvailableCount," +
            "MedianCellDurationVsReversalSpearman,NegativeCellDurationSpearmanCount," +
            "PositiveCellDurationSpearmanCount,SessionCount,SessionsWithValidCells," +
            "MedianSessionFastMinusSlowReversalPoints,PositiveSessionFastMinusSlowCount," +
            "NegativeSessionFastMinusSlowCount,ZeroSessionFastMinusSlowCount," +
            "LeaveOneSessionOutMinFastMinusSlow,LeaveOneSessionOutMedianFastMinusSlow," +
            "LeaveOneSessionOutMaxFastMinusSlow,LeaveOneSessionOutPositiveCount," +
            "LeaveOneSessionOutCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars,
                r.Scope,
                r.N,
                r.ValidCellCount,
                r.MeanCellFastMinusSlowReversalPoints,
                r.MedianCellFastMinusSlowReversalPoints,
                r.CellDurationSpearmanAvailableCount,
                r.MedianCellDurationVsReversalSpearman,
                r.NegativeCellDurationSpearmanCount,
                r.PositiveCellDurationSpearmanCount,
                r.SessionCount,
                r.SessionsWithValidCells,
                r.MedianSessionFastMinusSlowReversalPoints,
                r.PositiveSessionFastMinusSlowCount,
                r.NegativeSessionFastMinusSlowCount,
                r.ZeroSessionFastMinusSlowCount,
                r.LeaveOneSessionOutMinFastMinusSlow,
                r.LeaveOneSessionOutMedianFastMinusSlow,
                r.LeaveOneSessionOutMaxFastMinusSlow,
                r.LeaveOneSessionOutPositiveCount,
                r.LeaveOneSessionOutCount));
        }
    }

    static async Task WriteCellsCsvAsync(string path, IReadOnlyList<CellRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,Direction,MagnitudeQuintile,MoveMagnitudeMinObserved," +
            "MoveMagnitudeMaxObserved,DurationMedianSeconds,N,FastN,SlowN," +
            "FastMeanReversalPoints,SlowMeanReversalPoints,FastMinusSlowMeanReversalPoints," +
            "FastMedianReversalPoints,SlowMedianReversalPoints,FastReversalHitRate," +
            "SlowReversalHitRate,DurationVsReversalSpearman");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars,
                r.Scope,
                r.Direction,
                r.MagnitudeQuintile,
                r.MoveMagnitudeMinObserved,
                r.MoveMagnitudeMaxObserved,
                r.DurationMedianSeconds,
                r.N,
                r.FastN,
                r.SlowN,
                r.FastMeanReversalPoints,
                r.SlowMeanReversalPoints,
                r.FastMinusSlowMeanReversalPoints,
                r.FastMedianReversalPoints,
                r.SlowMedianReversalPoints,
                r.FastReversalHitRate,
                r.SlowReversalHitRate,
                r.DurationVsReversalSpearman));
        }
    }

    static async Task WriteSessionsCsvAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,TradingDate,Dte,N,ValidCellCount," +
            "MeanCellFastMinusSlowReversalPoints,MedianCellFastMinusSlowReversalPoints");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars,
                r.Scope,
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte,
                r.N,
                r.ValidCellCount,
                r.MeanCellFastMinusSlowReversalPoints,
                r.MedianCellFastMinusSlowReversalPoints));
        }
    }

    static async Task WriteLeaveOneOutCsvAsync(
        string path,
        IReadOnlyList<LeaveOneSessionOutRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,ExcludedDate,N,ValidCellCount," +
            "MeanCellFastMinusSlowReversalPoints,MedianCellFastMinusSlowReversalPoints");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars,
                r.ExcludedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.N,
                r.ValidCellCount,
                r.MeanCellFastMinusSlowReversalPoints,
                r.MedianCellFastMinusSlowReversalPoints));
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
