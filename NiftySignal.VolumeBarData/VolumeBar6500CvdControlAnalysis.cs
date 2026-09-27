using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Dedicated follow-up for the clean 6500 FutureCvdProxyNet finding.
///
/// The question is deliberately narrow: after the already-frozen direction + absolute move-magnitude
/// matching, does the inverse CVD/future relationship survive contemporaneous bar-state controls?
///
/// Controls are all known when the signal bar closes:
/// - CloseLocation = (Close - Low) / (High - Low)
/// - TopOfBookImbalance
/// - DepthImbalance
///
/// Partial Spearman is implemented as Pearson correlation between rank residuals after projecting
/// ranked CVD and ranked forward outcome onto the same ranked controls. No thresholds are searched.
/// </summary>
public static class VolumeBar6500CvdControlAnalysis
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

    public sealed record StageSpec(
        string Name,
        bool CompleteCaseOnly,
        string[] Controls);

    public static readonly StageSpec[] Stages =
    [
        new("BenchmarkAllCvd", false, []),
        new("CompleteCaseNoControls", true, []),
        new("ControlCloseLocation", true, ["CloseLocation"]),
        new("ControlTopOfBook", true, ["TopOfBookImbalance"]),
        new("ControlDepth", true, ["DepthImbalance"]),
        new("ControlAllThree", true, ["CloseLocation", "TopOfBookImbalance", "DepthImbalance"]),
    ];

    public sealed record DiagnosticRow(
        int HorizonBars,
        string Scope,
        int AllCvdN,
        int CompleteCaseN,
        double? CvdVsCloseLocationSpearman,
        double? CvdVsTopOfBookSpearman,
        double? CvdVsDepthSpearman,
        double? TopOfBookVsDepthSpearman);

    public sealed record SummaryRow(
        int HorizonBars,
        string Scope,
        string Stage,
        string Controls,
        int N,
        int ValidCellCount,
        double? MedianCellPartialSpearman,
        int PositiveCellPartialSpearmanCount,
        int NegativeCellPartialSpearmanCount,
        int SessionCount,
        int SessionsWithValidCells,
        double? MedianSessionCellPartialSpearman,
        int PositiveSessionCount,
        int NegativeSessionCount,
        double? LeaveOneOutMinMedianCellPartialSpearman,
        double? LeaveOneOutMedianMedianCellPartialSpearman,
        double? LeaveOneOutMaxMedianCellPartialSpearman,
        int LeaveOneOutSameSignCount,
        int LeaveOneOutCount);

    public sealed record CellRow(
        int HorizonBars,
        string Scope,
        string Stage,
        string Controls,
        string Direction,
        int MagnitudeQuintile,
        int N,
        double? PartialSpearman);

    public sealed record SessionRow(
        int HorizonBars,
        string Scope,
        string Stage,
        DateOnly TradingDate,
        int Dte,
        int N,
        int ValidCellCount,
        double? MedianCellPartialSpearman,
        int PositiveCellCount,
        int NegativeCellCount);

    public sealed record LeaveOneSessionOutRow(
        int HorizonBars,
        string Stage,
        DateOnly ExcludedDate,
        int N,
        int ValidCellCount,
        double? MedianCellPartialSpearman);

    public sealed record AnalysisResult(
        IReadOnlyList<DiagnosticRow> Diagnostics,
        IReadOnlyList<SummaryRow> Summary,
        IReadOnlyList<CellRow> Cells,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<LeaveOneSessionOutRow> LeaveOneSessionOut);

    sealed record Point(
        VolumeBar6500Revalidation.Observation Observation,
        double Cvd,
        double Forward,
        double? CloseLocation,
        double? TopOfBook,
        double? Depth)
    {
        public bool IsCompleteCase =>
            CloseLocation is not null &&
            TopOfBook is not null &&
            Depth is not null;
    }

    public static AnalysisResult Analyze(IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var eligibleForFrozenCells = observations
            .Where(IsEligibleMoveCellSignal)
            .ToArray();

        var definitions =
            VolumeBar6500DurationIncrementalAnalysis.BuildFrozenDefinitions(eligibleForFrozenCells);

        var diagnostics = new List<DiagnosticRow>();
        var summary = new List<SummaryRow>();
        var cells = new List<CellRow>();
        var sessions = new List<SessionRow>();
        var leaveOneOut = new List<LeaveOneSessionOutRow>();

        foreach (var horizon in Horizons)
        {
            foreach (var scope in ScopeObservations(observations))
            {
                var points = BuildPoints(scope.Rows, horizon);
                var complete = points.Where(p => p.IsCompleteCase).ToArray();

                diagnostics.Add(BuildDiagnosticRow(
                    horizon.Bars,
                    scope.Name,
                    points,
                    complete));

                foreach (var stage in Stages)
                {
                    var stagePoints = stage.CompleteCaseOnly ? complete : points.ToArray();

                    var stageCells = BuildCellRows(
                        horizon.Bars,
                        scope.Name,
                        stage,
                        stagePoints,
                        definitions);

                    var stageSessions = BuildSessionRows(
                        horizon.Bars,
                        scope.Name,
                        stage,
                        stagePoints,
                        definitions);

                    cells.AddRange(stageCells);
                    sessions.AddRange(stageSessions);

                    IReadOnlyList<LeaveOneSessionOutRow> looRows = Array.Empty<LeaveOneSessionOutRow>();
                    if (scope.Name == "AllValid")
                    {
                        looRows = BuildLeaveOneOutRows(
                            horizon.Bars,
                            stage,
                            stagePoints,
                            definitions);

                        leaveOneOut.AddRange(looRows);
                    }

                    summary.Add(BuildSummary(
                        horizon.Bars,
                        scope.Name,
                        stage,
                        stagePoints,
                        stageCells,
                        stageSessions,
                        looRows));
                }
            }
        }

        return new AnalysisResult(
            diagnostics,
            summary,
            cells,
            sessions,
            leaveOneOut);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        string outputDirectory)
    {
        var result = Analyze(observations);
        Directory.CreateDirectory(outputDirectory);

        await WriteDiagnosticsCsvAsync(
            Path.Combine(outputDirectory, "cvd-control-diagnostics.csv"),
            result.Diagnostics);

        await WriteSummaryCsvAsync(
            Path.Combine(outputDirectory, "cvd-control-summary.csv"),
            result.Summary);

        await WriteCellsCsvAsync(
            Path.Combine(outputDirectory, "cvd-control-cells.csv"),
            result.Cells);

        await WriteSessionsCsvAsync(
            Path.Combine(outputDirectory, "cvd-control-sessions.csv"),
            result.Sessions);

        await WriteLeaveOneOutCsvAsync(
            Path.Combine(outputDirectory, "cvd-control-leave-one-session-out.csv"),
            result.LeaveOneSessionOut);

        PrintPrimarySummary(result);
        return result;
    }

    static IEnumerable<(string Name, IReadOnlyList<VolumeBar6500Revalidation.Observation> Rows)> ScopeObservations(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        yield return ("AllValid", observations);
        yield return (
            "SignalBarVolumeLt13000",
            observations.Where(o => o.ObservedVolume < ExtremeSignalBarVolumeCutoff).ToArray());
    }

    static List<Point> BuildPoints(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        HorizonSpec horizon)
    {
        var points = new List<Point>();

        foreach (var observation in observations)
        {
            if (!IsEligibleMoveCellSignal(observation) ||
                observation.FutureCvdProxyNet is not { } cvd)
            {
                continue;
            }

            var forward = horizon.ForwardPoints(observation);
            if (forward is null || !double.IsFinite(forward.Value))
            {
                continue;
            }

            var closeLocation = CloseLocation(observation);

            points.Add(new Point(
                observation,
                cvd,
                forward.Value,
                closeLocation,
                observation.TopOfBookImbalance,
                observation.DepthImbalance));
        }

        return points;
    }

    static DiagnosticRow BuildDiagnosticRow(
        int horizon,
        string scope,
        IReadOnlyList<Point> allCvd,
        IReadOnlyList<Point> complete)
    {
        return new DiagnosticRow(
            horizon,
            scope,
            allCvd.Count,
            complete.Count,
            PartialSpearman(
                complete.Select(x => x.Cvd).ToArray(),
                complete.Select(x => x.CloseLocation!.Value).ToArray()),
            PartialSpearman(
                complete.Select(x => x.Cvd).ToArray(),
                complete.Select(x => x.TopOfBook!.Value).ToArray()),
            PartialSpearman(
                complete.Select(x => x.Cvd).ToArray(),
                complete.Select(x => x.Depth!.Value).ToArray()),
            PartialSpearman(
                complete.Select(x => x.TopOfBook!.Value).ToArray(),
                complete.Select(x => x.Depth!.Value).ToArray()));
    }

    static List<CellRow> BuildCellRows(
        int horizon,
        string scope,
        StageSpec stage,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rows = new List<CellRow>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p => MatchesDefinition(p.Observation, definition, definitions))
                .ToArray();

            if (group.Length == 0)
            {
                continue;
            }

            rows.Add(new CellRow(
                horizon,
                scope,
                stage.Name,
                string.Join("+", stage.Controls),
                definition.Direction,
                definition.MagnitudeQuintile,
                group.Length,
                StagePartialSpearman(group, stage)));
        }

        return rows;
    }

    static List<SessionRow> BuildSessionRows(
        int horizon,
        string scope,
        StageSpec stage,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rows = new List<SessionRow>();

        foreach (var session in points
            .GroupBy(p => p.Observation.TradingDate)
            .OrderBy(g => g.Key))
        {
            var items = session.ToArray();
            var cellRhos = CellRhos(items, stage, definitions);

            rows.Add(new SessionRow(
                horizon,
                scope,
                stage.Name,
                session.Key,
                items[0].Observation.Dte,
                items.Length,
                cellRhos.Length,
                MedianOrNull(cellRhos),
                cellRhos.Count(x => x > 0),
                cellRhos.Count(x => x < 0)));
        }

        return rows;
    }

    static List<LeaveOneSessionOutRow> BuildLeaveOneOutRows(
        int horizon,
        StageSpec stage,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
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

            var cellRhos = CellRhos(remaining, stage, definitions);

            rows.Add(new LeaveOneSessionOutRow(
                horizon,
                stage.Name,
                excluded,
                remaining.Length,
                cellRhos.Length,
                MedianOrNull(cellRhos)));
        }

        return rows;
    }

    static SummaryRow BuildSummary(
        int horizon,
        string scope,
        StageSpec stage,
        IReadOnlyList<Point> points,
        IReadOnlyList<CellRow> cells,
        IReadOnlyList<SessionRow> sessions,
        IReadOnlyList<LeaveOneSessionOutRow> leaveOneOut)
    {
        var cellRhos = cells
            .Select(x => x.PartialSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var sessionRhos = sessions
            .Select(x => x.MedianCellPartialSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var looRhos = leaveOneOut
            .Select(x => x.MedianCellPartialSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var fullMedian = MedianOrNull(cellRhos);

        return new SummaryRow(
            horizon,
            scope,
            stage.Name,
            string.Join("+", stage.Controls),
            points.Count,
            cellRhos.Length,
            fullMedian,
            cellRhos.Count(x => x > 0),
            cellRhos.Count(x => x < 0),
            sessions.Count,
            sessionRhos.Length,
            MedianOrNull(sessionRhos),
            sessionRhos.Count(x => x > 0),
            sessionRhos.Count(x => x < 0),
            looRhos.Length > 0 ? looRhos.Min() : null,
            MedianOrNull(looRhos),
            looRhos.Length > 0 ? looRhos.Max() : null,
            fullMedian is { } baseline
                ? looRhos.Count(x => SameNonZeroSign(baseline, x))
                : 0,
            looRhos.Length);
    }

    static double[] CellRhos(
        IReadOnlyList<Point> points,
        StageSpec stage,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rhos = new List<double>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p => MatchesDefinition(p.Observation, definition, definitions))
                .ToArray();

            var rho = StagePartialSpearman(group, stage);
            if (rho is { } value)
            {
                rhos.Add(value);
            }
        }

        return rhos.ToArray();
    }

    static double? StagePartialSpearman(
        IReadOnlyList<Point> group,
        StageSpec stage)
    {
        if (group.Count == 0)
        {
            return null;
        }

        var x = group.Select(p => p.Cvd).ToArray();
        var y = group.Select(p => p.Forward).ToArray();

        var controls = new List<IReadOnlyList<double>>();

        foreach (var control in stage.Controls)
        {
            switch (control)
            {
                case "CloseLocation":
                    if (group.Any(p => p.CloseLocation is null)) { return null; }
                    controls.Add(group.Select(p => p.CloseLocation!.Value).ToArray());
                    break;

                case "TopOfBookImbalance":
                    if (group.Any(p => p.TopOfBook is null)) { return null; }
                    controls.Add(group.Select(p => p.TopOfBook!.Value).ToArray());
                    break;

                case "DepthImbalance":
                    if (group.Any(p => p.Depth is null)) { return null; }
                    controls.Add(group.Select(p => p.Depth!.Value).ToArray());
                    break;

                default:
                    throw new InvalidOperationException($"Unknown CVD control: {control}");
            }
        }

        return PartialSpearman(x, y, controls);
    }

    /// <summary>
    /// Partial Spearman correlation. Inputs are average-ranked with ties, centered, then the same
    /// ranked controls are removed from both X and Y using modified Gram-Schmidt projection.
    /// Pearson correlation of the two residual vectors is returned.
    ///
    /// With zero controls this reduces to ordinary tie-aware Spearman.
    /// </summary>
    public static double? PartialSpearman(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        params IReadOnlyList<double>[] controls) =>
        PartialSpearman(x, y, (IReadOnlyList<IReadOnlyList<double>>)controls);

    static double? PartialSpearman(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        IReadOnlyList<IReadOnlyList<double>> controls)
    {
        if (x.Count != y.Count ||
            controls.Any(c => c.Count != x.Count))
        {
            throw new ArgumentException("Partial Spearman inputs must have equal lengths.");
        }

        if (x.Count < controls.Count + 3 ||
            x.Any(v => !double.IsFinite(v)) ||
            y.Any(v => !double.IsFinite(v)) ||
            controls.Any(c => c.Any(v => !double.IsFinite(v))))
        {
            return null;
        }

        var xRank = Center(AverageRanks(x));
        var yRank = Center(AverageRanks(y));

        var basis = new List<double[]>();

        foreach (var control in controls)
        {
            var vector = Center(AverageRanks(control));

            foreach (var q in basis)
            {
                SubtractProjection(vector, q);
            }

            var norm = Math.Sqrt(vector.Sum(v => v * v));
            if (norm <= 1e-12)
            {
                continue;
            }

            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }

            basis.Add(vector);
        }

        foreach (var q in basis)
        {
            SubtractProjection(xRank, q);
            SubtractProjection(yRank, q);
        }

        return PearsonCentered(xRank, yRank);
    }

    static double[] AverageRanks(IReadOnlyList<double> values)
    {
        var indexed = values
            .Select((value, index) => (value, index))
            .OrderBy(x => x.value)
            .ThenBy(x => x.index)
            .ToArray();

        var ranks = new double[values.Count];
        var i = 0;

        while (i < indexed.Length)
        {
            var j = i + 1;
            while (j < indexed.Length && indexed[j].value == indexed[i].value)
            {
                j++;
            }

            var averageRank = ((i + 1) + j) / 2.0;
            for (var k = i; k < j; k++)
            {
                ranks[indexed[k].index] = averageRank;
            }

            i = j;
        }

        return ranks;
    }

    static double[] Center(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return values.Select(v => v - mean).ToArray();
    }

    static void SubtractProjection(double[] target, IReadOnlyList<double> unitVector)
    {
        var coefficient = 0.0;
        for (var i = 0; i < target.Length; i++)
        {
            coefficient += target[i] * unitVector[i];
        }

        for (var i = 0; i < target.Length; i++)
        {
            target[i] -= coefficient * unitVector[i];
        }
    }

    static double? PearsonCentered(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y)
    {
        var xx = 0.0;
        var yy = 0.0;
        var xy = 0.0;

        for (var i = 0; i < x.Count; i++)
        {
            xx += x[i] * x[i];
            yy += y[i] * y[i];
            xy += x[i] * y[i];
        }

        if (xx <= 1e-20 || yy <= 1e-20)
        {
            return null;
        }

        return xy / Math.Sqrt(xx * yy);
    }

    static double? CloseLocation(VolumeBar6500Revalidation.Observation observation)
    {
        var range = observation.SignalBarHigh - observation.SignalBarLow;
        if (range <= 0)
        {
            return null;
        }

        var value =
            ((double)observation.FuturesClose - (double)observation.SignalBarLow) /
            (double)range;

        return double.IsFinite(value) ? value : null;
    }

    static bool IsEligibleMoveCellSignal(
        VolumeBar6500Revalidation.Observation observation) =>
        double.IsFinite(observation.SignalBarChangePoints) &&
        observation.SignalBarChangePoints != 0 &&
        double.IsFinite(observation.ExchangeDurationSeconds) &&
        observation.ExchangeDurationSeconds > 0;

    static bool MatchesDefinition(
        VolumeBar6500Revalidation.Observation observation,
        VolumeBar6500DurationIncrementalAnalysis.CellDefinition definition,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        if (!IsEligibleMoveCellSignal(observation))
        {
            return false;
        }

        var directionSign = Math.Sign(observation.SignalBarChangePoints);
        if (directionSign != definition.DirectionSign)
        {
            return false;
        }

        var magnitude = Math.Abs(observation.SignalBarChangePoints);
        var directionDefinitions = definitions
            .Where(d => d.DirectionSign == directionSign)
            .OrderBy(d => d.MagnitudeQuintile)
            .ToArray();

        if (directionDefinitions.Length == 0)
        {
            return false;
        }

        var quintile = directionDefinitions[^1].MagnitudeQuintile;

        for (var i = 0; i < directionDefinitions.Length - 1; i++)
        {
            if (magnitude <= directionDefinitions[i].MoveMagnitudeMaxObserved)
            {
                quintile = directionDefinitions[i].MagnitudeQuintile;
                break;
            }
        }

        return quintile == definition.MagnitudeQuintile;
    }

    static bool SameNonZeroSign(double left, double right) =>
        (left > 0 && right > 0) ||
        (left < 0 && right < 0);

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

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine(
            "6500 CVD control test — move matched, then contemporaneous bar-state controls:");

        foreach (var diagnostic in result.Diagnostics
            .Where(x => x.Scope == "AllValid")
            .OrderBy(x => x.HorizonBars))
        {
            Console.WriteLine(
                $"  +{diagnostic.HorizonBars} diagnostics: allCvdN={diagnostic.AllCvdN:N0}, " +
                $"completeN={diagnostic.CompleteCaseN:N0}, " +
                $"rho(CVD,closeLocation)={Format(diagnostic.CvdVsCloseLocationSpearman)}, " +
                $"rho(CVD,TOB)={Format(diagnostic.CvdVsTopOfBookSpearman)}, " +
                $"rho(CVD,depth)={Format(diagnostic.CvdVsDepthSpearman)}, " +
                $"rho(TOB,depth)={Format(diagnostic.TopOfBookVsDepthSpearman)}");
        }

        foreach (var horizon in Horizons.Select(h => h.Bars))
        {
            Console.WriteLine($"  +{horizon}:");

            foreach (var row in result.Summary
                .Where(x => x.Scope == "AllValid" && x.HorizonBars == horizon)
                .OrderBy(x => Array.FindIndex(Stages, s => s.Name == x.Stage)))
            {
                Console.WriteLine(
                    $"    {row.Stage,-24} n={row.N:N0}, " +
                    $"cellMedian={Format(row.MedianCellPartialSpearman)}, " +
                    $"cellSigns=+{row.PositiveCellPartialSpearmanCount}/-{row.NegativeCellPartialSpearmanCount}, " +
                    $"sessionMedian={Format(row.MedianSessionCellPartialSpearman)}, " +
                    $"sessionSigns=+{row.PositiveSessionCount}/-{row.NegativeSessionCount}, " +
                    $"LOO sameSign={row.LeaveOneOutSameSignCount}/{row.LeaveOneOutCount}");
            }
        }

        Console.WriteLine(
            $"Sensitivity is separately exported after removing signal bars with ObservedVolume >= {ExtremeSignalBarVolumeCutoff:N0}.");
    }

    static string Format(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    static async Task WriteDiagnosticsCsvAsync(
        string path,
        IReadOnlyList<DiagnosticRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,AllCvdN,CompleteCaseN,CvdVsCloseLocationSpearman," +
            "CvdVsTopOfBookSpearman,CvdVsDepthSpearman,TopOfBookVsDepthSpearman");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.AllCvdN, r.CompleteCaseN,
                r.CvdVsCloseLocationSpearman, r.CvdVsTopOfBookSpearman,
                r.CvdVsDepthSpearman, r.TopOfBookVsDepthSpearman));
        }
    }

    static async Task WriteSummaryCsvAsync(
        string path,
        IReadOnlyList<SummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,Stage,Controls,N,ValidCellCount,MedianCellPartialSpearman," +
            "PositiveCellPartialSpearmanCount,NegativeCellPartialSpearmanCount,SessionCount," +
            "SessionsWithValidCells,MedianSessionCellPartialSpearman,PositiveSessionCount," +
            "NegativeSessionCount,LeaveOneOutMinMedianCellPartialSpearman," +
            "LeaveOneOutMedianMedianCellPartialSpearman,LeaveOneOutMaxMedianCellPartialSpearman," +
            "LeaveOneOutSameSignCount,LeaveOneOutCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.Stage, r.Controls, r.N, r.ValidCellCount,
                r.MedianCellPartialSpearman, r.PositiveCellPartialSpearmanCount,
                r.NegativeCellPartialSpearmanCount, r.SessionCount,
                r.SessionsWithValidCells, r.MedianSessionCellPartialSpearman,
                r.PositiveSessionCount, r.NegativeSessionCount,
                r.LeaveOneOutMinMedianCellPartialSpearman,
                r.LeaveOneOutMedianMedianCellPartialSpearman,
                r.LeaveOneOutMaxMedianCellPartialSpearman,
                r.LeaveOneOutSameSignCount, r.LeaveOneOutCount));
        }
    }

    static async Task WriteCellsCsvAsync(
        string path,
        IReadOnlyList<CellRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,Stage,Controls,Direction,MagnitudeQuintile,N,PartialSpearman");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.Stage, r.Controls, r.Direction,
                r.MagnitudeQuintile, r.N, r.PartialSpearman));
        }
    }

    static async Task WriteSessionsCsvAsync(
        string path,
        IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,Stage,TradingDate,Dte,N,ValidCellCount," +
            "MedianCellPartialSpearman,PositiveCellCount,NegativeCellCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.Stage,
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.N, r.ValidCellCount, r.MedianCellPartialSpearman,
                r.PositiveCellCount, r.NegativeCellCount));
        }
    }

    static async Task WriteLeaveOneOutCsvAsync(
        string path,
        IReadOnlyList<LeaveOneSessionOutRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Stage,ExcludedDate,N,ValidCellCount,MedianCellPartialSpearman");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Stage,
                r.ExcludedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.N, r.ValidCellCount, r.MedianCellPartialSpearman));
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
