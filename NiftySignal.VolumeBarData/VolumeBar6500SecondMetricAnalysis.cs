using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Second correctness-first 6500-bar metric batch. Every metric is evaluated independently against
/// underlying NIFTY Futures response before any option translation. The simple signal-bar reversal
/// relationship remains the benchmark, and each metric also gets a direction + move-magnitude
/// controlled incremental-information test using the exact frozen cells from the duration study.
/// </summary>
public static class VolumeBar6500SecondMetricAnalysis
{
    public const long ExtremeSignalBarVolumeCutoff = 13_000;

    public enum MoveControlFrame
    {
        DirectionalFuture,
        ReversalAlignedFuture,
    }

    public sealed record MetricSpec(
        string Name,
        Func<VolumeBar6500Revalidation.Observation, double?> Value,
        MoveControlFrame MoveControlFrame,
        bool DiagnosticOnly = false);

    public sealed record HorizonSpec(
        int Bars,
        Func<VolumeBar6500Revalidation.Observation, double?> ForwardPoints,
        Func<VolumeBar6500Revalidation.Observation, long?> ForwardObservedVolume,
        Func<VolumeBar6500Revalidation.Observation, double?> MaxUpPoints,
        Func<VolumeBar6500Revalidation.Observation, double?> MaxDownPoints);

    public static readonly MetricSpec[] Metrics =
    [
        new("TopOfBookImbalance", o => o.TopOfBookImbalance, MoveControlFrame.DirectionalFuture),
        new("FutureCvdProxyNet", o => o.FutureCvdProxyNet is { } x ? (double)x : null, MoveControlFrame.DirectionalFuture),
        new("PriceImpact", o => o.PriceImpact, MoveControlFrame.DirectionalFuture),
        new("FutureOiBuildupSignedMagnitude", o => o.FutureOiBuildupSignedMagnitude, MoveControlFrame.DirectionalFuture),
        new("TrendReversion15", o => o.TrendReversion15, MoveControlFrame.DirectionalFuture),
        new("TrendPersistence15Raw", o => o.TrendPersistence15Raw, MoveControlFrame.DirectionalFuture, DiagnosticOnly: true),
        new("TickDensity", o => o.TickDensity, MoveControlFrame.ReversalAlignedFuture),
        new("TickVelocity", o => o.TickVelocity, MoveControlFrame.ReversalAlignedFuture),
        new("PriceEfficiency", o => o.PriceEfficiency, MoveControlFrame.ReversalAlignedFuture),
        new("Churn", o => o.Churn, MoveControlFrame.ReversalAlignedFuture),
        new("VwapDeviation", o => o.VwapDeviation, MoveControlFrame.DirectionalFuture),
    ];

    public static readonly HorizonSpec[] Horizons =
    [
        new(1, o => o.Forward1Points, o => o.Forward1ObservedVolume, o => o.MaxUp1Points, o => o.MaxDown1Points),
        new(2, o => o.Forward2Points, o => o.Forward2ObservedVolume, o => o.MaxUp2Points, o => o.MaxDown2Points),
        new(4, o => o.Forward4Points, o => o.Forward4ObservedVolume, o => o.MaxUp4Points, o => o.MaxDown4Points),
    ];

    public sealed record BaselineRow(
        int HorizonBars,
        string Scope,
        int N,
        double? SignalBarChangeVsForwardSpearman,
        int SessionCount,
        double? MedianSessionSpearman,
        int PositiveSessionSpearmanCount,
        int NegativeSessionSpearmanCount);

    public sealed record SummaryRow(
        string Metric,
        bool DiagnosticOnly,
        string MoveControlFrame,
        int HorizonBars,
        string Scope,
        int N,
        double? PooledSpearman,
        double? MetricVsSignalBarChangeSpearman,
        double? SignalBarChangeVsForwardSpearman,
        int SessionCount,
        double? MedianSessionSpearman,
        int PositiveSessionSpearmanCount,
        int NegativeSessionSpearmanCount,
        double? Q5MinusQ1MeanForwardPoints,
        long? MedianForwardObservedVolume,
        int MoveControlledValidCellCount,
        double? MedianMoveControlledCellSpearman,
        int PositiveMoveControlledCellCount,
        int NegativeMoveControlledCellCount,
        int MoveControlledSessionCount,
        double? MedianMoveControlledSessionSpearman,
        int PositiveMoveControlledSessionCount,
        int NegativeMoveControlledSessionCount,
        double? RawLeaveOneOutMinSpearman,
        double? RawLeaveOneOutMedianSpearman,
        double? RawLeaveOneOutMaxSpearman,
        int RawLeaveOneOutSameSignCount,
        int RawLeaveOneOutCount,
        double? MoveLeaveOneOutMinMedianCellSpearman,
        double? MoveLeaveOneOutMedianMedianCellSpearman,
        double? MoveLeaveOneOutMaxMedianCellSpearman,
        int MoveLeaveOneOutSameSignCount,
        int MoveLeaveOneOutCount);

    public sealed record SessionRow(
        string Metric,
        int HorizonBars,
        string Scope,
        DateOnly TradingDate,
        int Dte,
        int N,
        double? Spearman);

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

    public sealed record MoveControlledCellRow(
        string Metric,
        string OutcomeFrame,
        int HorizonBars,
        string Scope,
        string Direction,
        int MagnitudeQuintile,
        int N,
        double? Spearman,
        double MeanOutcomePoints,
        double MedianOutcomePoints,
        double OutcomePositiveRate);

    public sealed record MoveControlledSessionRow(
        string Metric,
        int HorizonBars,
        string Scope,
        DateOnly TradingDate,
        int Dte,
        int ValidCellCount,
        double? MedianCellSpearman,
        int PositiveCellSpearmanCount,
        int NegativeCellSpearmanCount);

    public sealed record LeaveOneSessionOutRow(
        string Metric,
        int HorizonBars,
        DateOnly ExcludedDate,
        int N,
        double? RawSpearman,
        int MoveControlledValidCellCount,
        double? MoveControlledMedianCellSpearman);

    public sealed record OiStateRow(
        int HorizonBars,
        string Scope,
        string State,
        int ImpliedDirectionSign,
        int N,
        double MeanForwardPoints,
        double MedianForwardPoints,
        double FuturePositiveRate,
        double? MeanDirectionalAlignedPoints,
        double? DirectionalHitRate);

    public sealed record AnalysisResult(
        IReadOnlyList<BaselineRow> Baseline,
        IReadOnlyList<SummaryRow> Summary,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<QuintileRow> Quintiles,
        IReadOnlyList<DteRow> Dte,
        IReadOnlyList<MoveControlledCellRow> MoveControlledCells,
        IReadOnlyList<MoveControlledSessionRow> MoveControlledSessions,
        IReadOnlyList<LeaveOneSessionOutRow> LeaveOneSessionOut,
        IReadOnlyList<OiStateRow> OiStates);

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
        var baseline = new List<BaselineRow>();
        var summary = new List<SummaryRow>();
        var sessions = new List<SessionRow>();
        var quintiles = new List<QuintileRow>();
        var dte = new List<DteRow>();
        var moveCells = new List<MoveControlledCellRow>();
        var moveSessions = new List<MoveControlledSessionRow>();
        var leaveOneOut = new List<LeaveOneSessionOutRow>();
        var oiStates = new List<OiStateRow>();

        var moveDefinitionSignals = observations
            .Where(o =>
                double.IsFinite(o.SignalBarChangePoints) &&
                o.SignalBarChangePoints != 0 &&
                double.IsFinite(o.ExchangeDurationSeconds) &&
                o.ExchangeDurationSeconds > 0)
            .ToArray();

        var moveDefinitions =
            VolumeBar6500DurationIncrementalAnalysis.BuildFrozenDefinitions(moveDefinitionSignals);

        foreach (var horizon in Horizons)
        {
            foreach (var scope in ScopeObservations(observations))
            {
                baseline.Add(BuildBaselineRow(horizon, scope.Name, scope.Rows));
                oiStates.AddRange(BuildOiStateRows(horizon, scope.Name, scope.Rows));
            }
        }

        foreach (var metric in Metrics)
        {
            var rawMetricValues = observations
                .Select(metric.Value)
                .Where(v => v is not null && double.IsFinite(v.Value))
                .Select(v => v!.Value)
                .OrderBy(v => v)
                .ToArray();

            if (rawMetricValues.Length == 0)
            {
                continue;
            }

            var cuts = new Cutpoints(
                Percentile(rawMetricValues, 0.20),
                Percentile(rawMetricValues, 0.40),
                Percentile(rawMetricValues, 0.60),
                Percentile(rawMetricValues, 0.80));

            foreach (var horizon in Horizons)
            {
                foreach (var scope in ScopeObservations(observations))
                {
                    var points = BuildPoints(scope.Rows, metric, horizon);
                    var rawSessions = BuildSessionRows(metric.Name, horizon.Bars, scope.Name, points);
                    var rawQuintiles = BuildQuintileRows(metric.Name, horizon.Bars, scope.Name, points, cuts);
                    var dteRows = BuildDteRows(metric.Name, horizon.Bars, scope.Name, points);
                    var controlledCells = BuildMoveControlledCells(
                        metric,
                        horizon.Bars,
                        scope.Name,
                        points,
                        moveDefinitions);
                    var controlledSessions = BuildMoveControlledSessions(
                        metric,
                        horizon.Bars,
                        scope.Name,
                        points,
                        moveDefinitions);

                    sessions.AddRange(rawSessions);
                    quintiles.AddRange(rawQuintiles);
                    dte.AddRange(dteRows);
                    moveCells.AddRange(controlledCells);
                    moveSessions.AddRange(controlledSessions);

                    IReadOnlyList<LeaveOneSessionOutRow> looRows = Array.Empty<LeaveOneSessionOutRow>();
                    if (scope.Name == "AllValid")
                    {
                        looRows = BuildLeaveOneSessionOutRows(metric, horizon.Bars, points, moveDefinitions);
                        leaveOneOut.AddRange(looRows);
                    }

                    summary.Add(BuildSummary(
                        metric,
                        horizon.Bars,
                        scope.Name,
                        points,
                        rawSessions,
                        rawQuintiles,
                        controlledCells,
                        controlledSessions,
                        looRows));
                }
            }
        }

        return new AnalysisResult(
            baseline,
            summary,
            sessions,
            quintiles,
            dte,
            moveCells,
            moveSessions,
            leaveOneOut,
            oiStates);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        string outputDirectory)
    {
        var result = Analyze(observations);
        Directory.CreateDirectory(outputDirectory);

        await WriteBaselineCsvAsync(Path.Combine(outputDirectory, "second-metric-baseline.csv"), result.Baseline);
        await WriteSummaryCsvAsync(Path.Combine(outputDirectory, "second-metric-summary.csv"), result.Summary);
        await WriteSessionsCsvAsync(Path.Combine(outputDirectory, "second-metric-sessions.csv"), result.Sessions);
        await WriteQuintilesCsvAsync(Path.Combine(outputDirectory, "second-metric-quintiles.csv"), result.Quintiles);
        await WriteDteCsvAsync(Path.Combine(outputDirectory, "second-metric-dte.csv"), result.Dte);
        await WriteMoveCellsCsvAsync(
            Path.Combine(outputDirectory, "second-metric-move-controlled-cells.csv"),
            result.MoveControlledCells);
        await WriteMoveSessionsCsvAsync(
            Path.Combine(outputDirectory, "second-metric-move-controlled-sessions.csv"),
            result.MoveControlledSessions);
        await WriteLeaveOneOutCsvAsync(
            Path.Combine(outputDirectory, "second-metric-leave-one-session-out.csv"),
            result.LeaveOneSessionOut);
        await WriteOiStatesCsvAsync(
            Path.Combine(outputDirectory, "second-metric-oi-states.csv"),
            result.OiStates);

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

    static BaselineRow BuildBaselineRow(
        HorizonSpec horizon,
        string scope,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var valid = observations
            .Select(o => (Observation: o, Forward: horizon.ForwardPoints(o)))
            .Where(x => x.Forward is not null && double.IsFinite(x.Forward.Value))
            .Select(x => (x.Observation, Forward: x.Forward!.Value))
            .ToArray();

        var sessionRhos = valid
            .GroupBy(x => x.Observation.TradingDate)
            .Select(g => VolumeBar6500MetricAnalysis.Spearman(
                g.Select(x => x.Observation.SignalBarChangePoints).ToArray(),
                g.Select(x => x.Forward).ToArray()))
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        return new BaselineRow(
            horizon.Bars,
            scope,
            valid.Length,
            VolumeBar6500MetricAnalysis.Spearman(
                valid.Select(x => x.Observation.SignalBarChangePoints).ToArray(),
                valid.Select(x => x.Forward).ToArray()),
            valid.Select(x => x.Observation.TradingDate).Distinct().Count(),
            MedianOrNull(sessionRhos),
            sessionRhos.Count(x => x > 0),
            sessionRhos.Count(x => x < 0));
    }

    static List<Point> BuildPoints(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        MetricSpec metric,
        HorizonSpec horizon)
    {
        var points = new List<Point>();

        foreach (var observation in observations)
        {
            var value = metric.Value(observation);
            var forward = horizon.ForwardPoints(observation);
            var forwardVolume = horizon.ForwardObservedVolume(observation);
            var maxUp = horizon.MaxUpPoints(observation);
            var maxDown = horizon.MaxDownPoints(observation);

            if (value is null || forward is null || forwardVolume is null || maxUp is null || maxDown is null)
            {
                continue;
            }

            if (!double.IsFinite(value.Value) ||
                !double.IsFinite(forward.Value) ||
                !double.IsFinite(maxUp.Value) ||
                !double.IsFinite(maxDown.Value))
            {
                continue;
            }

            points.Add(new Point(
                observation,
                value.Value,
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
        IReadOnlyList<Point> points) =>
        points
            .GroupBy(p => p.Observation.TradingDate)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var items = g.ToArray();
                return new SessionRow(
                    metric,
                    horizon,
                    scope,
                    g.Key,
                    items[0].Observation.Dte,
                    items.Length,
                    VolumeBar6500MetricAnalysis.Spearman(
                        items.Select(x => x.MetricValue).ToArray(),
                        items.Select(x => x.ForwardPoints).ToArray()));
            })
            .ToList();

    static List<QuintileRow> BuildQuintileRows(
        string metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        Cutpoints cuts)
    {
        var rows = new List<QuintileRow>();

        for (var q = 1; q <= 5; q++)
        {
            var group = points.Where(p => Quintile(p.MetricValue, cuts) == q).ToArray();
            if (group.Length == 0)
            {
                continue;
            }

            rows.Add(new QuintileRow(
                metric,
                horizon,
                scope,
                q,
                group.Length,
                group.Min(x => x.MetricValue),
                group.Max(x => x.MetricValue),
                group.Average(x => x.MetricValue),
                group.Average(x => x.ForwardPoints),
                Median(group.Select(x => x.ForwardPoints).ToArray()),
                group.Count(x => x.ForwardPoints > 0) / (double)group.Length,
                group.Average(x => x.MaxUpPoints),
                group.Average(x => x.MaxDownPoints)));
        }

        return rows;
    }

    static List<DteRow> BuildDteRows(
        string metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points) =>
        points
            .GroupBy(p => p.Observation.Dte)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var items = g.ToArray();
                return new DteRow(
                    metric,
                    horizon,
                    scope,
                    g.Key,
                    items.Length,
                    VolumeBar6500MetricAnalysis.Spearman(
                        items.Select(x => x.MetricValue).ToArray(),
                        items.Select(x => x.ForwardPoints).ToArray()),
                    items.Average(x => x.ForwardPoints),
                    Median(items.Select(x => x.ForwardPoints).ToArray()),
                    items.Count(x => x.ForwardPoints > 0) / (double)items.Length);
            })
            .ToList();

    static List<MoveControlledCellRow> BuildMoveControlledCells(
        MetricSpec metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rows = new List<MoveControlledCellRow>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p => MatchesDefinition(p.Observation, definition, definitions))
                .Select(p => (Point: p, Outcome: ControlledOutcome(metric, p)))
                .ToArray();

            if (group.Length == 0)
            {
                continue;
            }

            rows.Add(new MoveControlledCellRow(
                metric.Name,
                metric.MoveControlFrame.ToString(),
                horizon,
                scope,
                definition.Direction,
                definition.MagnitudeQuintile,
                group.Length,
                VolumeBar6500MetricAnalysis.Spearman(
                    group.Select(x => x.Point.MetricValue).ToArray(),
                    group.Select(x => x.Outcome).ToArray()),
                group.Average(x => x.Outcome),
                Median(group.Select(x => x.Outcome).ToArray()),
                group.Count(x => x.Outcome > 0) / (double)group.Length));
        }

        return rows;
    }

    static List<MoveControlledSessionRow> BuildMoveControlledSessions(
        MetricSpec metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rows = new List<MoveControlledSessionRow>();

        foreach (var session in points.GroupBy(p => p.Observation.TradingDate).OrderBy(g => g.Key))
        {
            var items = session.ToArray();
            var rhos = CellRhos(metric, items, definitions);

            rows.Add(new MoveControlledSessionRow(
                metric.Name,
                horizon,
                scope,
                session.Key,
                items[0].Observation.Dte,
                rhos.Length,
                MedianOrNull(rhos),
                rhos.Count(x => x > 0),
                rhos.Count(x => x < 0)));
        }

        return rows;
    }

    static List<LeaveOneSessionOutRow> BuildLeaveOneSessionOutRows(
        MetricSpec metric,
        int horizon,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rows = new List<LeaveOneSessionOutRow>();

        foreach (var excluded in points.Select(p => p.Observation.TradingDate).Distinct().OrderBy(x => x))
        {
            var remaining = points.Where(p => p.Observation.TradingDate != excluded).ToArray();
            var moveRhos = CellRhos(metric, remaining, definitions);

            rows.Add(new LeaveOneSessionOutRow(
                metric.Name,
                horizon,
                excluded,
                remaining.Length,
                VolumeBar6500MetricAnalysis.Spearman(
                    remaining.Select(x => x.MetricValue).ToArray(),
                    remaining.Select(x => x.ForwardPoints).ToArray()),
                moveRhos.Length,
                MedianOrNull(moveRhos)));
        }

        return rows;
    }

    static SummaryRow BuildSummary(
        MetricSpec metric,
        int horizon,
        string scope,
        IReadOnlyList<Point> points,
        IReadOnlyList<SessionRow> sessions,
        IReadOnlyList<QuintileRow> quintiles,
        IReadOnlyList<MoveControlledCellRow> controlledCells,
        IReadOnlyList<MoveControlledSessionRow> controlledSessions,
        IReadOnlyList<LeaveOneSessionOutRow> leaveOneOut)
    {
        var pooled = VolumeBar6500MetricAnalysis.Spearman(
            points.Select(x => x.MetricValue).ToArray(),
            points.Select(x => x.ForwardPoints).ToArray());

        var metricVsMove = VolumeBar6500MetricAnalysis.Spearman(
            points.Select(x => x.MetricValue).ToArray(),
            points.Select(x => x.Observation.SignalBarChangePoints).ToArray());

        var moveVsForward = VolumeBar6500MetricAnalysis.Spearman(
            points.Select(x => x.Observation.SignalBarChangePoints).ToArray(),
            points.Select(x => x.ForwardPoints).ToArray());

        var sessionRhos = sessions
            .Select(x => x.Spearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var q1 = quintiles.FirstOrDefault(x => x.Quintile == 1);
        var q5 = quintiles.FirstOrDefault(x => x.Quintile == 5);

        var cellRhos = controlledCells
            .Select(x => x.Spearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var moveSessionRhos = controlledSessions
            .Select(x => x.MedianCellSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var rawLoo = leaveOneOut
            .Select(x => x.RawSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var moveLoo = leaveOneOut
            .Select(x => x.MoveControlledMedianCellSpearman)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        var primaryMove = MedianOrNull(cellRhos);

        return new SummaryRow(
            metric.Name,
            metric.DiagnosticOnly,
            metric.MoveControlFrame.ToString(),
            horizon,
            scope,
            points.Count,
            pooled,
            metricVsMove,
            moveVsForward,
            sessions.Count,
            MedianOrNull(sessionRhos),
            sessionRhos.Count(x => x > 0),
            sessionRhos.Count(x => x < 0),
            q1 is not null && q5 is not null ? q5.MeanForwardPoints - q1.MeanForwardPoints : null,
            points.Count > 0
                ? NearestRank(points.Select(x => x.ForwardObservedVolume).OrderBy(x => x).ToArray(), 0.50)
                : null,
            cellRhos.Length,
            primaryMove,
            cellRhos.Count(x => x > 0),
            cellRhos.Count(x => x < 0),
            controlledSessions.Count,
            MedianOrNull(moveSessionRhos),
            moveSessionRhos.Count(x => x > 0),
            moveSessionRhos.Count(x => x < 0),
            rawLoo.Length > 0 ? rawLoo.Min() : null,
            MedianOrNull(rawLoo),
            rawLoo.Length > 0 ? rawLoo.Max() : null,
            pooled is { } rawBase ? rawLoo.Count(x => SameNonZeroSign(rawBase, x)) : 0,
            rawLoo.Length,
            moveLoo.Length > 0 ? moveLoo.Min() : null,
            MedianOrNull(moveLoo),
            moveLoo.Length > 0 ? moveLoo.Max() : null,
            primaryMove is { } moveBase ? moveLoo.Count(x => SameNonZeroSign(moveBase, x)) : 0,
            moveLoo.Length);
    }

    static List<OiStateRow> BuildOiStateRows(
        HorizonSpec horizon,
        string scope,
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var rows = new List<OiStateRow>();

        var valid = observations
            .Select(o => (Observation: o, Forward: horizon.ForwardPoints(o)))
            .Where(x =>
                x.Forward is not null &&
                double.IsFinite(x.Forward.Value) &&
                !string.IsNullOrWhiteSpace(x.Observation.FutureOiBuildupState))
            .Select(x => (x.Observation, Forward: x.Forward!.Value))
            .ToArray();

        foreach (var group in valid.GroupBy(x => x.Observation.FutureOiBuildupState!).OrderBy(g => g.Key))
        {
            var direction = OiDirection(group.Key);
            var values = group.Select(x => x.Forward).ToArray();
            var aligned = direction == 0
                ? Array.Empty<double>()
                : values.Select(x => direction * x).ToArray();

            rows.Add(new OiStateRow(
                horizon.Bars,
                scope,
                group.Key,
                direction,
                values.Length,
                values.Average(),
                Median(values),
                values.Count(x => x > 0) / (double)values.Length,
                aligned.Length > 0 ? aligned.Average() : null,
                aligned.Length > 0 ? aligned.Count(x => x > 0) / (double)aligned.Length : null));
        }

        return rows;
    }

    static double[] CellRhos(
        MetricSpec metric,
        IReadOnlyList<Point> points,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var rhos = new List<double>();

        foreach (var definition in definitions)
        {
            var group = points
                .Where(p => MatchesDefinition(p.Observation, definition, definitions))
                .Select(p => (Point: p, Outcome: ControlledOutcome(metric, p)))
                .ToArray();

            var rho = VolumeBar6500MetricAnalysis.Spearman(
                group.Select(x => x.Point.MetricValue).ToArray(),
                group.Select(x => x.Outcome).ToArray());

            if (rho is { } value)
            {
                rhos.Add(value);
            }
        }

        return rhos.ToArray();
    }

    static bool MatchesDefinition(
        VolumeBar6500Revalidation.Observation observation,
        VolumeBar6500DurationIncrementalAnalysis.CellDefinition definition,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        if (observation.SignalBarChangePoints == 0 ||
            !double.IsFinite(observation.SignalBarChangePoints) ||
            observation.ExchangeDurationSeconds <= 0 ||
            !double.IsFinite(observation.ExchangeDurationSeconds))
        {
            return false;
        }

        var directionSign = Math.Sign(observation.SignalBarChangePoints);
        if (directionSign != definition.DirectionSign)
        {
            return false;
        }

        var q = FindMagnitudeQuintile(
            Math.Abs(observation.SignalBarChangePoints),
            directionSign,
            definitions);

        return q == definition.MagnitudeQuintile;
    }

    static int FindMagnitudeQuintile(
        double magnitude,
        int directionSign,
        IReadOnlyList<VolumeBar6500DurationIncrementalAnalysis.CellDefinition> definitions)
    {
        var directionDefinitions = definitions
            .Where(d => d.DirectionSign == directionSign)
            .OrderBy(d => d.MagnitudeQuintile)
            .ToArray();

        if (directionDefinitions.Length == 0)
        {
            return 0;
        }

        for (var i = 0; i < directionDefinitions.Length - 1; i++)
        {
            if (magnitude <= directionDefinitions[i].MoveMagnitudeMaxObserved)
            {
                return directionDefinitions[i].MagnitudeQuintile;
            }
        }

        return directionDefinitions[^1].MagnitudeQuintile;
    }

    static double ControlledOutcome(MetricSpec metric, Point point) =>
        metric.MoveControlFrame switch
        {
            MoveControlFrame.DirectionalFuture => point.ForwardPoints,
            MoveControlFrame.ReversalAlignedFuture =>
                -Math.Sign(point.Observation.SignalBarChangePoints) * point.ForwardPoints,
            _ => throw new ArgumentOutOfRangeException(),
        };

    static int OiDirection(string state) =>
        state switch
        {
            "LongBuildup" or "ShortCovering" => 1,
            "ShortBuildup" or "LongUnwinding" => -1,
            _ => 0,
        };

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

    static bool SameNonZeroSign(double left, double right) =>
        (left > 0 && right > 0) || (left < 0 && right < 0);

    static double Median(IReadOnlyList<double> values) =>
        MedianOrNull(values) ?? throw new ArgumentException("Median input must not be empty.");

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

    static long NearestRank(IReadOnlyList<long> sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile * sorted.Count);
        var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine("6500 second metric batch — simple current-bar reversal benchmark:");
        foreach (var row in result.Baseline.Where(x => x.Scope == "AllValid").OrderBy(x => x.HorizonBars))
        {
            Console.WriteLine(
                $"  +{row.HorizonBars}: n={row.N:N0}, rho(currentMove,future)={Format(row.SignalBarChangeVsForwardSpearman)}, " +
                $"medianSessionRho={Format(row.MedianSessionSpearman)}, " +
                $"sessionSigns=+{row.PositiveSessionSpearmanCount}/-{row.NegativeSessionSpearmanCount}");
        }

        Console.WriteLine("6500 second metric batch — raw + move-controlled Futures relationships:");
        foreach (var row in result.Summary
            .Where(x => x.Scope == "AllValid")
            .OrderBy(x => x.Metric)
            .ThenBy(x => x.HorizonBars))
        {
            var diagnostic = row.DiagnosticOnly ? " [diagnostic]" : "";
            Console.WriteLine(
                $"  {row.Metric,-31} +{row.HorizonBars}:{diagnostic} " +
                $"rawRho={Format(row.PooledSpearman)}, sessionMedian={Format(row.MedianSessionSpearman)}, " +
                $"rho(metric,currentMove)={Format(row.MetricVsSignalBarChangeSpearman)}, " +
                $"moveCellMedian={Format(row.MedianMoveControlledCellSpearman)}, " +
                $"moveSessionMedian={Format(row.MedianMoveControlledSessionSpearman)}, " +
                $"rawLOO={row.RawLeaveOneOutSameSignCount}/{row.RawLeaveOneOutCount}, " +
                $"moveLOO={row.MoveLeaveOneOutSameSignCount}/{row.MoveLeaveOneOutCount}");
        }

        Console.WriteLine(
            $"Sensitivity is separately exported after removing signal bars with ObservedVolume >= {ExtremeSignalBarVolumeCutoff:N0}.");
    }

    static string Format(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    static async Task WriteBaselineCsvAsync(string path, IReadOnlyList<BaselineRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,N,SignalBarChangeVsForwardSpearman,SessionCount,MedianSessionSpearman," +
            "PositiveSessionSpearmanCount,NegativeSessionSpearmanCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.N, r.SignalBarChangeVsForwardSpearman, r.SessionCount,
                r.MedianSessionSpearman, r.PositiveSessionSpearmanCount, r.NegativeSessionSpearmanCount));
        }
    }

    static async Task WriteSummaryCsvAsync(string path, IReadOnlyList<SummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,DiagnosticOnly,MoveControlFrame,HorizonBars,Scope,N,PooledSpearman," +
            "MetricVsSignalBarChangeSpearman,SignalBarChangeVsForwardSpearman,SessionCount," +
            "MedianSessionSpearman,PositiveSessionSpearmanCount,NegativeSessionSpearmanCount," +
            "Q5MinusQ1MeanForwardPoints,MedianForwardObservedVolume,MoveControlledValidCellCount," +
            "MedianMoveControlledCellSpearman,PositiveMoveControlledCellCount,NegativeMoveControlledCellCount," +
            "MoveControlledSessionCount,MedianMoveControlledSessionSpearman,PositiveMoveControlledSessionCount," +
            "NegativeMoveControlledSessionCount,RawLeaveOneOutMinSpearman,RawLeaveOneOutMedianSpearman," +
            "RawLeaveOneOutMaxSpearman,RawLeaveOneOutSameSignCount,RawLeaveOneOutCount," +
            "MoveLeaveOneOutMinMedianCellSpearman,MoveLeaveOneOutMedianMedianCellSpearman," +
            "MoveLeaveOneOutMaxMedianCellSpearman,MoveLeaveOneOutSameSignCount,MoveLeaveOneOutCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.DiagnosticOnly, r.MoveControlFrame, r.HorizonBars, r.Scope, r.N,
                r.PooledSpearman, r.MetricVsSignalBarChangeSpearman, r.SignalBarChangeVsForwardSpearman,
                r.SessionCount, r.MedianSessionSpearman, r.PositiveSessionSpearmanCount,
                r.NegativeSessionSpearmanCount, r.Q5MinusQ1MeanForwardPoints,
                r.MedianForwardObservedVolume, r.MoveControlledValidCellCount,
                r.MedianMoveControlledCellSpearman, r.PositiveMoveControlledCellCount,
                r.NegativeMoveControlledCellCount, r.MoveControlledSessionCount,
                r.MedianMoveControlledSessionSpearman, r.PositiveMoveControlledSessionCount,
                r.NegativeMoveControlledSessionCount, r.RawLeaveOneOutMinSpearman,
                r.RawLeaveOneOutMedianSpearman, r.RawLeaveOneOutMaxSpearman,
                r.RawLeaveOneOutSameSignCount, r.RawLeaveOneOutCount,
                r.MoveLeaveOneOutMinMedianCellSpearman, r.MoveLeaveOneOutMedianMedianCellSpearman,
                r.MoveLeaveOneOutMaxMedianCellSpearman, r.MoveLeaveOneOutSameSignCount,
                r.MoveLeaveOneOutCount));
        }
    }

    static async Task WriteSessionsCsvAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Metric,HorizonBars,Scope,TradingDate,Dte,N,Spearman");
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope,
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.N, r.Spearman));
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
                r.MetricMin, r.MetricMax, r.MeanMetric, r.MeanForwardPoints,
                r.MedianForwardPoints, r.FuturePositiveRate, r.MeanMaxUpPoints, r.MeanMaxDownPoints));
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

    static async Task WriteMoveCellsCsvAsync(string path, IReadOnlyList<MoveControlledCellRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,OutcomeFrame,HorizonBars,Scope,Direction,MagnitudeQuintile,N,Spearman," +
            "MeanOutcomePoints,MedianOutcomePoints,OutcomePositiveRate");
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.OutcomeFrame, r.HorizonBars, r.Scope, r.Direction,
                r.MagnitudeQuintile, r.N, r.Spearman, r.MeanOutcomePoints,
                r.MedianOutcomePoints, r.OutcomePositiveRate));
        }
    }

    static async Task WriteMoveSessionsCsvAsync(string path, IReadOnlyList<MoveControlledSessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,Scope,TradingDate,Dte,ValidCellCount,MedianCellSpearman," +
            "PositiveCellSpearmanCount,NegativeCellSpearmanCount");
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars, r.Scope,
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.ValidCellCount, r.MedianCellSpearman,
                r.PositiveCellSpearmanCount, r.NegativeCellSpearmanCount));
        }
    }

    static async Task WriteLeaveOneOutCsvAsync(string path, IReadOnlyList<LeaveOneSessionOutRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Metric,HorizonBars,ExcludedDate,N,RawSpearman,MoveControlledValidCellCount," +
            "MoveControlledMedianCellSpearman");
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Metric, r.HorizonBars,
                r.ExcludedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.N, r.RawSpearman, r.MoveControlledValidCellCount,
                r.MoveControlledMedianCellSpearman));
        }
    }

    static async Task WriteOiStatesCsvAsync(string path, IReadOnlyList<OiStateRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "HorizonBars,Scope,State,ImpliedDirectionSign,N,MeanForwardPoints,MedianForwardPoints," +
            "FuturePositiveRate,MeanDirectionalAlignedPoints,DirectionalHitRate");
        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.HorizonBars, r.Scope, r.State, r.ImpliedDirectionSign, r.N,
                r.MeanForwardPoints, r.MedianForwardPoints, r.FuturePositiveRate,
                r.MeanDirectionalAlignedPoints, r.DirectionalHitRate));
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
