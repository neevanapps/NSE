using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Frozen magnitude dose-response analysis for the two 6500-bar Futures candidates already
/// translated to weekly options. No threshold is selected. The only buckets are fixed causal
/// expanding absolute-percentile quintiles Q1..Q5.
/// </summary>
public static class VolumeBar6500MagnitudeDoseResponseAnalysis
{
    public sealed record SignalKey(DateOnly TradingDate, string Candidate, int SignalBarIndex);

    public sealed record SignalInfo(
        SignalKey Key,
        int Dte,
        double DriverValue,
        double DriverAbsExpandingPercentile,
        int MagnitudeQuintile,
        int ExpectedDirectionSign);

    public sealed record UnderlyingOutcome(
        SignalInfo Signal,
        int HorizonBars,
        double ForwardPoints,
        double AlignedForwardPoints,
        bool? Correct);

    public sealed record BucketRow(
        string Candidate,
        int HorizonBars,
        int MagnitudeQuintile,
        double PercentileLowerExclusive,
        double PercentileUpperInclusive,
        int SessionCount,
        int SignalCount,
        double SignalsPerSession,
        int UnderlyingOutcomeCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        double? MedianAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOutcomeCount,
        double? OptionNetPositiveRate,
        double? MeanExecutableNetReturnPct,
        double? MedianExecutableNetReturnPct,
        double? MeanLtpReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        double? MeanMfeMinusMaePercent,
        double? MedianTimeToMfeSeconds,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record SessionRow(
        DateOnly TradingDate,
        int Dte,
        string Candidate,
        int HorizonBars,
        int MagnitudeQuintile,
        int SignalCount,
        int UnderlyingOutcomeCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOutcomeCount,
        double? OptionNetPositiveRate,
        double? MeanExecutableNetReturnPct,
        double? MedianExecutableNetReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record DteRow(
        int Dte,
        string Candidate,
        int HorizonBars,
        int MagnitudeQuintile,
        int SessionCount,
        int SignalCount,
        int UnderlyingOutcomeCount,
        double? UnderlyingHitRate,
        double? MeanAlignedForwardPoints,
        int EligibleEntryCount,
        int ExecutableOutcomeCount,
        double? OptionNetPositiveRate,
        double? MeanExecutableNetReturnPct,
        double? MedianExecutableNetReturnPct,
        double? MeanMfePercent,
        double? MeanMaePercent,
        int FuturesCorrectOptionProfit,
        int FuturesCorrectOptionLoss,
        int FuturesWrongOptionProfit,
        int FuturesWrongOptionLoss);

    public sealed record MonotonicityRow(
        string Candidate,
        int HorizonBars,
        string Metric,
        int ValidBucketCount,
        double? PooledBucketSpearman,
        int AdjacentImprovementCount,
        int AdjacentComparisonCount,
        int SessionCountWithMetric,
        double? MedianSessionBucketSpearman,
        int PositiveSessionSpearmanCount,
        int NegativeSessionSpearmanCount);

    public sealed record AnalysisResult(
        IReadOnlyList<BucketRow> Buckets,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<DteRow> Dte,
        IReadOnlyList<MonotonicityRow> Monotonicity);

    sealed record MetricSpec(string Name, Func<BucketRow, double?> BucketValue, Func<SessionRow, double?> SessionValue);

    static readonly MetricSpec[] MonotonicMetrics =
    [
        new("UnderlyingHitRate", r => r.UnderlyingHitRate, r => r.UnderlyingHitRate),
        new("MeanAlignedForwardPoints", r => r.MeanAlignedForwardPoints, r => r.MeanAlignedForwardPoints),
        new("OptionNetPositiveRate", r => r.OptionNetPositiveRate, r => r.OptionNetPositiveRate),
        new("MeanExecutableNetReturnPct", r => r.MeanExecutableNetReturnPct, r => r.MeanExecutableNetReturnPct),
        new("MedianExecutableNetReturnPct", r => r.MedianExecutableNetReturnPct, r => r.MedianExecutableNetReturnPct),
        new("MeanMfeMinusMaePercent", r => r.MeanMfeMinusMaePercent, r =>
            r.MeanMfePercent is { } mfe && r.MeanMaePercent is { } mae ? mfe - mae : null),
    ];

    public static AnalysisResult Analyze(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> underlyingObservations,
        VolumeBar6500OptionTranslationAnalysis.AnalysisResult translation)
    {
        var signals = BuildSignalInfo(underlyingObservations);
        var signalByKey = signals.ToDictionary(x => x.Key);
        var underlying = BuildUnderlyingOutcomes(underlyingObservations, signalByKey);

        var auditByKey = translation.SignalAudit
            .Where(a => signalByKey.ContainsKey(new SignalKey(a.TradingDate, a.Candidate, a.SignalBarIndex)))
            .GroupBy(a => new SignalKey(a.TradingDate, a.Candidate, a.SignalBarIndex))
            .ToDictionary(g => g.Key, g => g.Single());

        var optionByKeyHorizon = translation.Observations
            .Where(o => signalByKey.ContainsKey(new SignalKey(o.TradingDate, o.Candidate, o.SignalBarIndex)))
            .GroupBy(o => (
                new SignalKey(o.TradingDate, o.Candidate, o.SignalBarIndex),
                o.HorizonBars))
            .ToDictionary(g => g.Key, g => g.Single());

        var bucketRows = new List<BucketRow>();
        var sessionRows = new List<SessionRow>();
        var dteRows = new List<DteRow>();

        foreach (var candidate in VolumeBar6500OptionTranslationAnalysis.Candidates)
        {
            var candidateSignals = signals.Where(s => s.Key.Candidate == candidate.Name).ToArray();

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                for (var q = 1; q <= 5; q++)
                {
                    var bucketSignals = candidateSignals
                        .Where(s => s.MagnitudeQuintile == q)
                        .ToArray();

                    bucketRows.Add(BuildBucketRow(
                        candidate.Name,
                        horizon.Bars,
                        q,
                        bucketSignals,
                        underlying,
                        auditByKey,
                        optionByKeyHorizon));

                    foreach (var session in bucketSignals
                        .GroupBy(s => s.Key.TradingDate)
                        .OrderBy(g => g.Key))
                    {
                        sessionRows.Add(BuildSessionRow(
                            session.Key,
                            candidate.Name,
                            horizon.Bars,
                            q,
                            session.ToArray(),
                            underlying,
                            auditByKey,
                            optionByKeyHorizon));
                    }

                    foreach (var dte in bucketSignals
                        .GroupBy(s => s.Dte)
                        .OrderBy(g => g.Key))
                    {
                        dteRows.Add(BuildDteRow(
                            dte.Key,
                            candidate.Name,
                            horizon.Bars,
                            q,
                            dte.ToArray(),
                            underlying,
                            auditByKey,
                            optionByKeyHorizon));
                    }
                }
            }
        }

        var monotonicity = BuildMonotonicity(bucketRows, sessionRows);

        return new AnalysisResult(bucketRows, sessionRows, dteRows, monotonicity);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> underlyingObservations,
        VolumeBar6500OptionTranslationAnalysis.AnalysisResult translation,
        string outputDirectory)
    {
        var result = Analyze(underlyingObservations, translation);
        Directory.CreateDirectory(outputDirectory);

        await WriteBucketsCsvAsync(
            Path.Combine(outputDirectory, "magnitude-dose-response-summary.csv"),
            result.Buckets);

        await WriteSessionsCsvAsync(
            Path.Combine(outputDirectory, "magnitude-dose-response-sessions.csv"),
            result.Sessions);

        await WriteDteCsvAsync(
            Path.Combine(outputDirectory, "magnitude-dose-response-dte.csv"),
            result.Dte);

        await WriteMonotonicityCsvAsync(
            Path.Combine(outputDirectory, "magnitude-dose-response-monotonicity.csv"),
            result.Monotonicity);

        PrintPrimarySummary(result);
        return result;
    }

    public static int MagnitudeQuintile(double percentile)
    {
        if (!double.IsFinite(percentile) || percentile < 0 || percentile > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be finite and in [0,1].");
        }

        if (percentile <= 0.20) { return 1; }
        if (percentile <= 0.40) { return 2; }
        if (percentile <= 0.60) { return 3; }
        if (percentile <= 0.80) { return 4; }
        return 5;
    }

    public static double? SpearmanAcrossQuintiles(IReadOnlyList<double?> values)
    {
        if (values.Count != 5)
        {
            throw new ArgumentException("Exactly five quintile values are required.", nameof(values));
        }

        var pairs = values
            .Select((value, index) => (Quintile: index + 1, Value: value))
            .Where(x => x.Value is not null && double.IsFinite(x.Value.Value))
            .Select(x => (X: (double)x.Quintile, Y: x.Value!.Value))
            .ToArray();

        return pairs.Length < 3
            ? null
            : VolumeBar6500MetricAnalysis.Spearman(
                pairs.Select(x => x.X).ToArray(),
                pairs.Select(x => x.Y).ToArray());
    }

    public static (int Improvements, int Comparisons) CountAdjacentImprovements(IReadOnlyList<double?> values)
    {
        if (values.Count != 5)
        {
            throw new ArgumentException("Exactly five quintile values are required.", nameof(values));
        }

        var improvements = 0;
        var comparisons = 0;

        for (var i = 1; i < values.Count; i++)
        {
            if (values[i - 1] is not { } previous ||
                values[i] is not { } current ||
                !double.IsFinite(previous) ||
                !double.IsFinite(current))
            {
                continue;
            }

            comparisons++;
            if (current > previous)
            {
                improvements++;
            }
        }

        return (improvements, comparisons);
    }

    static List<SignalInfo> BuildSignalInfo(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations)
    {
        var result = new List<SignalInfo>();

        foreach (var day in observations
            .GroupBy(o => o.TradingDate)
            .OrderBy(g => g.Key))
        {
            var rows = day.OrderBy(o => o.BarIndex).ToArray();

            foreach (var candidate in VolumeBar6500OptionTranslationAnalysis.Candidates)
            {
                var driver = rows.Select(candidate.Driver).ToArray();
                var ranks = VolumeBar6500OptionTranslationAnalysis.ExpandingAbsolutePercentiles(driver);

                for (var i = 0; i < rows.Length; i++)
                {
                    if (driver[i] is not { } value ||
                        !double.IsFinite(value) ||
                        value == 0 ||
                        ranks[i] is not { } rank)
                    {
                        continue;
                    }

                    result.Add(new SignalInfo(
                        new SignalKey(rows[i].TradingDate, candidate.Name, rows[i].BarIndex),
                        rows[i].Dte,
                        value,
                        rank,
                        MagnitudeQuintile(rank),
                        -Math.Sign(value)));
                }
            }
        }

        return result;
    }

    static List<UnderlyingOutcome> BuildUnderlyingOutcomes(
        IReadOnlyList<VolumeBar6500Revalidation.Observation> observations,
        IReadOnlyDictionary<SignalKey, SignalInfo> signalByKey)
    {
        var observationByDateBar = observations
            .ToDictionary(o => (o.TradingDate, o.BarIndex));

        var result = new List<UnderlyingOutcome>();

        foreach (var signal in signalByKey.Values)
        {
            if (!observationByDateBar.TryGetValue(
                (signal.Key.TradingDate, signal.Key.SignalBarIndex),
                out var observation))
            {
                continue;
            }

            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var forward = horizon.ForwardPoints(observation);
                if (forward is null || !double.IsFinite(forward.Value))
                {
                    continue;
                }

                result.Add(new UnderlyingOutcome(
                    signal,
                    horizon.Bars,
                    forward.Value,
                    signal.ExpectedDirectionSign * forward.Value,
                    forward.Value == 0
                        ? null
                        : signal.ExpectedDirectionSign * forward.Value > 0));
            }
        }

        return result;
    }

    static BucketRow BuildBucketRow(
        string candidate,
        int horizon,
        int quintile,
        IReadOnlyList<SignalInfo> signals,
        IReadOnlyList<UnderlyingOutcome> underlying,
        IReadOnlyDictionary<SignalKey, VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> auditByKey,
        IReadOnlyDictionary<(SignalKey, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionByKeyHorizon)
    {
        var under = underlying
            .Where(x =>
                x.Signal.Key.Candidate == candidate &&
                x.HorizonBars == horizon &&
                x.Signal.MagnitudeQuintile == quintile)
            .ToArray();

        var audit = signals
            .Select(s => auditByKey.GetValueOrDefault(s.Key))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var options = signals
            .Select(s => optionByKeyHorizon.GetValueOrDefault((s.Key, horizon)))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var executable = options
            .Where(x => x.OutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable)
            .ToArray();

        var sessionCount = signals.Select(s => s.Key.TradingDate).Distinct().Count();

        return new BucketRow(
            candidate,
            horizon,
            quintile,
            (quintile - 1) * 0.20,
            quintile * 0.20,
            sessionCount,
            signals.Count,
            sessionCount > 0 ? signals.Count / (double)sessionCount : 0,
            under.Length,
            RateOrNull(under.Where(x => x.Correct is not null).Select(x => x.Correct!.Value).ToArray()),
            MeanOrNull(under.Select(x => x.AlignedForwardPoints).ToArray()),
            MedianOrNull(under.Select(x => x.AlignedForwardPoints).ToArray()),
            audit.Count(x => x.Status == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
            executable.Length,
            RateOrNull(executable.Where(x => x.OptionProfitable is not null).Select(x => x.OptionProfitable!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.LtpReturnPct is not null).Select(x => x.LtpReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Select(x => x.MfePercent).ToArray()),
            MeanOrNull(executable.Select(x => x.MaePercent).ToArray()),
            MeanOrNull(executable.Select(x => x.MfePercent - x.MaePercent).ToArray()),
            MedianOrNull(executable.Where(x => x.TimeToMfeSeconds is not null).Select(x => x.TimeToMfeSeconds!.Value).ToArray()),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss"));
    }

    static SessionRow BuildSessionRow(
        DateOnly date,
        string candidate,
        int horizon,
        int quintile,
        IReadOnlyList<SignalInfo> signals,
        IReadOnlyList<UnderlyingOutcome> underlying,
        IReadOnlyDictionary<SignalKey, VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> auditByKey,
        IReadOnlyDictionary<(SignalKey, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionByKeyHorizon)
    {
        var under = underlying
            .Where(x =>
                x.Signal.Key.TradingDate == date &&
                x.Signal.Key.Candidate == candidate &&
                x.HorizonBars == horizon &&
                x.Signal.MagnitudeQuintile == quintile)
            .ToArray();

        var audit = signals
            .Select(s => auditByKey.GetValueOrDefault(s.Key))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var options = signals
            .Select(s => optionByKeyHorizon.GetValueOrDefault((s.Key, horizon)))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var executable = options
            .Where(x => x.OutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable)
            .ToArray();

        return new SessionRow(
            date,
            signals.Count > 0 ? signals[0].Dte : 0,
            candidate,
            horizon,
            quintile,
            signals.Count,
            under.Length,
            RateOrNull(under.Where(x => x.Correct is not null).Select(x => x.Correct!.Value).ToArray()),
            MeanOrNull(under.Select(x => x.AlignedForwardPoints).ToArray()),
            audit.Count(x => x.Status == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
            executable.Length,
            RateOrNull(executable.Where(x => x.OptionProfitable is not null).Select(x => x.OptionProfitable!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Select(x => x.MfePercent).ToArray()),
            MeanOrNull(executable.Select(x => x.MaePercent).ToArray()),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss"));
    }

    static DteRow BuildDteRow(
        int dte,
        string candidate,
        int horizon,
        int quintile,
        IReadOnlyList<SignalInfo> signals,
        IReadOnlyList<UnderlyingOutcome> underlying,
        IReadOnlyDictionary<SignalKey, VolumeBar6500OptionTranslationAnalysis.SignalAuditRow> auditByKey,
        IReadOnlyDictionary<(SignalKey, int), VolumeBar6500OptionTranslationAnalysis.ObservationRow> optionByKeyHorizon)
    {
        var signalKeys = signals.Select(s => s.Key).ToHashSet();

        var under = underlying
            .Where(x =>
                x.Signal.Key.Candidate == candidate &&
                x.HorizonBars == horizon &&
                x.Signal.MagnitudeQuintile == quintile &&
                x.Signal.Dte == dte)
            .ToArray();

        var audit = signals
            .Select(s => auditByKey.GetValueOrDefault(s.Key))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var options = signalKeys
            .Select(key => optionByKeyHorizon.GetValueOrDefault((key, horizon)))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();

        var executable = options
            .Where(x => x.OutcomeStatus == VolumeBar6500OptionTranslationAnalysis.OutcomeStatus.Executable)
            .ToArray();

        return new DteRow(
            dte,
            candidate,
            horizon,
            quintile,
            signals.Select(s => s.Key.TradingDate).Distinct().Count(),
            signals.Count,
            under.Length,
            RateOrNull(under.Where(x => x.Correct is not null).Select(x => x.Correct!.Value).ToArray()),
            MeanOrNull(under.Select(x => x.AlignedForwardPoints).ToArray()),
            audit.Count(x => x.Status == VolumeBar6500OptionTranslationAnalysis.SignalStatus.Eligible),
            executable.Length,
            RateOrNull(executable.Where(x => x.OptionProfitable is not null).Select(x => x.OptionProfitable!.Value).ToArray()),
            MeanOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MedianOrNull(executable.Where(x => x.ExecutableNetReturnPct is not null).Select(x => x.ExecutableNetReturnPct!.Value).ToArray()),
            MeanOrNull(executable.Select(x => x.MfePercent).ToArray()),
            MeanOrNull(executable.Select(x => x.MaePercent).ToArray()),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesCorrectOptionLoss"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionProfit"),
            options.Count(x => x.OutcomeCategory == "FuturesWrongOptionLoss"));
    }

    static List<MonotonicityRow> BuildMonotonicity(
        IReadOnlyList<BucketRow> buckets,
        IReadOnlyList<SessionRow> sessions)
    {
        var rows = new List<MonotonicityRow>();

        foreach (var candidate in VolumeBar6500OptionTranslationAnalysis.Candidates)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var bucketSet = buckets
                    .Where(x => x.Candidate == candidate.Name && x.HorizonBars == horizon.Bars)
                    .OrderBy(x => x.MagnitudeQuintile)
                    .ToArray();

                foreach (var metric in MonotonicMetrics)
                {
                    var values = Enumerable.Range(1, 5)
                        .Select(q => bucketSet.FirstOrDefault(x => x.MagnitudeQuintile == q))
                        .Select(x => x is null ? null : metric.BucketValue(x))
                        .ToArray();

                    var pooledRho = SpearmanAcrossQuintiles(values);
                    var adjacent = CountAdjacentImprovements(values);

                    var sessionRhos = new List<double>();

                    foreach (var date in sessions
                        .Where(x => x.Candidate == candidate.Name && x.HorizonBars == horizon.Bars)
                        .Select(x => x.TradingDate)
                        .Distinct()
                        .OrderBy(x => x))
                    {
                        var sessionSet = sessions
                            .Where(x =>
                                x.TradingDate == date &&
                                x.Candidate == candidate.Name &&
                                x.HorizonBars == horizon.Bars)
                            .OrderBy(x => x.MagnitudeQuintile)
                            .ToArray();

                        var sessionValues = Enumerable.Range(1, 5)
                            .Select(q => sessionSet.FirstOrDefault(x => x.MagnitudeQuintile == q))
                            .Select(x => x is null ? null : metric.SessionValue(x))
                            .ToArray();

                        var rho = SpearmanAcrossQuintiles(sessionValues);
                        if (rho is { } value)
                        {
                            sessionRhos.Add(value);
                        }
                    }

                    rows.Add(new MonotonicityRow(
                        candidate.Name,
                        horizon.Bars,
                        metric.Name,
                        values.Count(x => x is not null),
                        pooledRho,
                        adjacent.Improvements,
                        adjacent.Comparisons,
                        sessionRhos.Count,
                        MedianOrNull(sessionRhos),
                        sessionRhos.Count(x => x > 0),
                        sessionRhos.Count(x => x < 0)));
                }
            }
        }

        return rows;
    }

    static double? RateOrNull(IReadOnlyList<bool> values) =>
        values.Count == 0 ? null : values.Count(x => x) / (double)values.Count;

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

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine(
            "6500 magnitude dose-response — fixed causal expanding-percentile quintiles; no threshold selection:");

        foreach (var candidate in VolumeBar6500OptionTranslationAnalysis.Candidates)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                Console.WriteLine($"  {candidate.Name} +{horizon.Bars}:");

                foreach (var row in result.Buckets
                    .Where(x => x.Candidate == candidate.Name && x.HorizonBars == horizon.Bars)
                    .OrderBy(x => x.MagnitudeQuintile))
                {
                    Console.WriteLine(
                        $"    Q{row.MagnitudeQuintile}: signals={row.SignalCount:N0}, " +
                        $"signals/day={row.SignalsPerSession:0.0}, futureHit={FormatRate(row.UnderlyingHitRate)}, " +
                        $"alignedPts={Format(row.MeanAlignedForwardPoints)}, option+={FormatRate(row.OptionNetPositiveRate)}, " +
                        $"meanNet={FormatPct(row.MeanExecutableNetReturnPct)}, medianNet={FormatPct(row.MedianExecutableNetReturnPct)}, " +
                        $"MFE={FormatPct(row.MeanMfePercent)}, MAE={FormatPct(row.MeanMaePercent)}");
                }

                foreach (var metric in result.Monotonicity
                    .Where(x =>
                        x.Candidate == candidate.Name &&
                        x.HorizonBars == horizon.Bars &&
                        (x.Metric == "UnderlyingHitRate" ||
                         x.Metric == "MeanAlignedForwardPoints" ||
                         x.Metric == "MeanExecutableNetReturnPct")))
                {
                    Console.WriteLine(
                        $"    monotonic {metric.Metric}: bucketRho={Format(metric.PooledBucketSpearman)}, " +
                        $"adjacentUp={metric.AdjacentImprovementCount}/{metric.AdjacentComparisonCount}, " +
                        $"sessionMedianRho={Format(metric.MedianSessionBucketSpearman)}, " +
                        $"sessionSigns=+{metric.PositiveSessionSpearmanCount}/-{metric.NegativeSessionSpearmanCount}");
                }
            }
        }

        Console.WriteLine(
            "Q1..Q5 are descriptive intensity buckets only. No quintile is promoted to an entry rule by this run.");
    }

    static string Format(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);

    static string FormatRate(double? value) =>
        value is null ? "NA" : (value.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    static string FormatPct(double? value) =>
        value is null ? "NA" : value.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";

    static async Task WriteBucketsCsvAsync(string path, IReadOnlyList<BucketRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Candidate,HorizonBars,MagnitudeQuintile,PercentileLowerExclusive,PercentileUpperInclusive," +
            "SessionCount,SignalCount,SignalsPerSession,UnderlyingOutcomeCount,UnderlyingHitRate," +
            "MeanAlignedForwardPoints,MedianAlignedForwardPoints,EligibleEntryCount,ExecutableOutcomeCount," +
            "OptionNetPositiveRate,MeanExecutableNetReturnPct,MedianExecutableNetReturnPct,MeanLtpReturnPct," +
            "MeanMfePercent,MeanMaePercent,MeanMfeMinusMaePercent,MedianTimeToMfeSeconds," +
            "FuturesCorrectOptionProfit,FuturesCorrectOptionLoss,FuturesWrongOptionProfit,FuturesWrongOptionLoss");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Candidate, r.HorizonBars, r.MagnitudeQuintile,
                r.PercentileLowerExclusive, r.PercentileUpperInclusive,
                r.SessionCount, r.SignalCount, r.SignalsPerSession,
                r.UnderlyingOutcomeCount, r.UnderlyingHitRate,
                r.MeanAlignedForwardPoints, r.MedianAlignedForwardPoints,
                r.EligibleEntryCount, r.ExecutableOutcomeCount, r.OptionNetPositiveRate,
                r.MeanExecutableNetReturnPct, r.MedianExecutableNetReturnPct, r.MeanLtpReturnPct,
                r.MeanMfePercent, r.MeanMaePercent, r.MeanMfeMinusMaePercent,
                r.MedianTimeToMfeSeconds, r.FuturesCorrectOptionProfit,
                r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
        }
    }

    static async Task WriteSessionsCsvAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "TradingDate,Dte,Candidate,HorizonBars,MagnitudeQuintile,SignalCount,UnderlyingOutcomeCount," +
            "UnderlyingHitRate,MeanAlignedForwardPoints,EligibleEntryCount,ExecutableOutcomeCount," +
            "OptionNetPositiveRate,MeanExecutableNetReturnPct,MedianExecutableNetReturnPct," +
            "MeanMfePercent,MeanMaePercent,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss," +
            "FuturesWrongOptionProfit,FuturesWrongOptionLoss");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.TradingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Dte, r.Candidate, r.HorizonBars, r.MagnitudeQuintile, r.SignalCount,
                r.UnderlyingOutcomeCount, r.UnderlyingHitRate, r.MeanAlignedForwardPoints,
                r.EligibleEntryCount, r.ExecutableOutcomeCount, r.OptionNetPositiveRate,
                r.MeanExecutableNetReturnPct, r.MedianExecutableNetReturnPct,
                r.MeanMfePercent, r.MeanMaePercent, r.FuturesCorrectOptionProfit,
                r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
        }
    }

    static async Task WriteDteCsvAsync(string path, IReadOnlyList<DteRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Dte,Candidate,HorizonBars,MagnitudeQuintile,SessionCount,SignalCount,UnderlyingOutcomeCount," +
            "UnderlyingHitRate,MeanAlignedForwardPoints,EligibleEntryCount,ExecutableOutcomeCount," +
            "OptionNetPositiveRate,MeanExecutableNetReturnPct,MedianExecutableNetReturnPct," +
            "MeanMfePercent,MeanMaePercent,FuturesCorrectOptionProfit,FuturesCorrectOptionLoss," +
            "FuturesWrongOptionProfit,FuturesWrongOptionLoss");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Dte, r.Candidate, r.HorizonBars, r.MagnitudeQuintile, r.SessionCount,
                r.SignalCount, r.UnderlyingOutcomeCount, r.UnderlyingHitRate,
                r.MeanAlignedForwardPoints, r.EligibleEntryCount, r.ExecutableOutcomeCount,
                r.OptionNetPositiveRate, r.MeanExecutableNetReturnPct, r.MedianExecutableNetReturnPct,
                r.MeanMfePercent, r.MeanMaePercent, r.FuturesCorrectOptionProfit,
                r.FuturesCorrectOptionLoss, r.FuturesWrongOptionProfit, r.FuturesWrongOptionLoss));
        }
    }

    static async Task WriteMonotonicityCsvAsync(string path, IReadOnlyList<MonotonicityRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync(
            "Candidate,HorizonBars,Metric,ValidBucketCount,PooledBucketSpearman," +
            "AdjacentImprovementCount,AdjacentComparisonCount,SessionCountWithMetric," +
            "MedianSessionBucketSpearman,PositiveSessionSpearmanCount,NegativeSessionSpearmanCount");

        foreach (var r in rows)
        {
            await writer.WriteLineAsync(Csv(
                r.Candidate, r.HorizonBars, r.Metric, r.ValidBucketCount,
                r.PooledBucketSpearman, r.AdjacentImprovementCount, r.AdjacentComparisonCount,
                r.SessionCountWithMetric, r.MedianSessionBucketSpearman,
                r.PositiveSessionSpearmanCount, r.NegativeSessionSpearmanCount));
        }
    }

    static StreamWriter Writer(string path) =>
        new(path, false, new UTF8Encoding(false));

    static string Csv(params object?[] values) =>
        string.Join(",", values.Select(CsvField));

    static string CsvField(object? value)
    {
        if (value is null)
        {
            return "";
        }

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
