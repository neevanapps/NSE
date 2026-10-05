using System.Globalization;
using System.Text;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Futures-only execution economics for the episode/state mechanisms in
/// VolumeBar6500EpisodeStateAnalysis. Every option-translation phase so far (weekly-option
/// translation, magnitude dose-response, episode/state-entry) showed negative net option returns
/// on every candidate and every quintile/mechanism even where the underlying directional hit rate
/// was well above 50% -- option premium decay/spread/timing masked the underlying result regardless
/// of how good it was. This phase instead prices a direct NIFTY Futures round trip -- one lot
/// entered at the episode's first bar, exited at the forward horizon, using the same
/// direction-aligned forward points already computed for the state-entry comparison -- against a
/// swept grid of round-trip cost assumptions, so the underlying edge can be judged on its own
/// economics before any option-execution question is asked again.
///
/// No stop, target, position sizing beyond one lot, or sequential compounding is introduced. Each
/// state-entry observation remains an independent single round trip, exactly as in the frozen
/// episode/state-entry comparison.
/// </summary>
public static class VolumeBar6500FuturesExecutionAnalysis
{
    /// <summary>
    /// Round-trip cost grid in NIFTY Futures index points. 0 is the frictionless upper bound;
    /// the remaining values span realistic-to-pessimistic NIFTY Futures round-trip cost
    /// (exchange/broker charges, bid/ask spread and market-order slippage on size).
    /// </summary>
    public static readonly double[] CostGridPoints = [0.0, 0.5, 1.0, 1.5, 2.0, 3.0];

    public const double GatePrimaryCostPoints = 1.0;
    public const double GateMinNetPositiveLeaveOneOutCount = 7;

    public sealed record SummaryRow(
        string Mechanism,
        int HorizonBars,
        double CostPoints,
        int N,
        double MeanGrossPoints,
        double? MeanNetPoints,
        double? MedianNetPoints,
        double? NetPositiveRate,
        int SessionCount,
        double? MedianSessionNetPoints,
        int PositiveSessionCount,
        int NegativeSessionCount);

    public sealed record BreakevenRow(
        string Mechanism,
        int HorizonBars,
        int N,
        double MeanGrossPoints,
        double BreakevenCostPoints);

    public sealed record SessionRow(
        DateOnly TradingDate,
        string Mechanism,
        int HorizonBars,
        double CostPoints,
        int N,
        double MeanGrossPoints,
        double MeanNetPoints);

    public sealed record LeaveOneOutRow(
        string Mechanism,
        int HorizonBars,
        double CostPoints,
        double LeaveOneSessionOutMinMeanNetPoints,
        double LeaveOneSessionOutMedianMeanNetPoints,
        double LeaveOneSessionOutMaxMeanNetPoints,
        int LeaveOneSessionOutSameSignCount,
        int LeaveOneSessionOutCount);

    public sealed record AnalysisResult(
        IReadOnlyList<SummaryRow> Summary,
        IReadOnlyList<BreakevenRow> Breakeven,
        IReadOnlyList<SessionRow> Sessions,
        IReadOnlyList<LeaveOneOutRow> LeaveOneOut);

    public static AnalysisResult Analyze(IReadOnlyList<VolumeBar6500EpisodeStateAnalysis.EntryObservation> entries)
    {
        var summary = new List<SummaryRow>();
        var breakeven = new List<BreakevenRow>();
        var sessions = new List<SessionRow>();
        var leaveOneOut = new List<LeaveOneOutRow>();

        foreach (var mechanism in VolumeBar6500EpisodeStateAnalysis.Mechanisms)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var group = entries
                    .Where(x => x.Mechanism == mechanism && x.HorizonBars == horizon.Bars && x.AlignedForwardPoints is not null)
                    .ToArray();

                if (group.Length == 0) { continue; }

                var gross = group.Select(x => x.AlignedForwardPoints!.Value).ToArray();
                var meanGross = gross.Average();
                breakeven.Add(new BreakevenRow(mechanism, horizon.Bars, group.Length, meanGross, meanGross));

                var bySession = group
                    .GroupBy(x => x.TradingDate)
                    .OrderBy(g => g.Key)
                    .Select(g => g.Select(x => x.AlignedForwardPoints!.Value).ToArray())
                    .ToArray();
                var sessionDates = group.GroupBy(x => x.TradingDate).OrderBy(g => g.Key).Select(g => g.Key).ToArray();

                foreach (var cost in CostGridPoints)
                {
                    var net = gross.Select(g => g - cost).ToArray();
                    var sessionMeans = bySession.Select(s => s.Average() - cost).ToArray();

                    for (var i = 0; i < bySession.Length; i++)
                    {
                        sessions.Add(new SessionRow(
                            sessionDates[i],
                            mechanism,
                            horizon.Bars,
                            cost,
                            bySession[i].Length,
                            bySession[i].Average(),
                            sessionMeans[i]));
                    }

                    var meanNet = MeanOrNull(net);
                    summary.Add(new SummaryRow(
                        mechanism,
                        horizon.Bars,
                        cost,
                        group.Length,
                        meanGross,
                        meanNet,
                        MedianOrNull(net),
                        RateOrNull(net.Select(x => x > 0).ToArray()),
                        bySession.Length,
                        MedianOrNull(sessionMeans),
                        sessionMeans.Count(x => x > 0),
                        sessionMeans.Count(x => x < 0)));

                    if (bySession.Length > 1)
                    {
                        var overallSign = Math.Sign(meanNet ?? 0);
                        var looMeans = new List<double>();
                        for (var i = 0; i < bySession.Length; i++)
                        {
                            var excludedTotal = 0.0;
                            var excludedCount = 0;
                            for (var j = 0; j < bySession.Length; j++)
                            {
                                if (j == i) { continue; }
                                excludedTotal += bySession[j].Sum() - cost * bySession[j].Length;
                                excludedCount += bySession[j].Length;
                            }
                            looMeans.Add(excludedTotal / excludedCount);
                        }

                        leaveOneOut.Add(new LeaveOneOutRow(
                            mechanism,
                            horizon.Bars,
                            cost,
                            looMeans.Min(),
                            MedianOrNull(looMeans)!.Value,
                            looMeans.Max(),
                            overallSign == 0 ? 0 : looMeans.Count(x => Math.Sign(x) == overallSign),
                            looMeans.Count));
                    }
                }
            }
        }

        return new AnalysisResult(summary, breakeven, sessions, leaveOneOut);
    }

    public static async Task<AnalysisResult> WriteReportsAsync(
        IReadOnlyList<VolumeBar6500EpisodeStateAnalysis.EntryObservation> entries,
        string outputDirectory)
    {
        var result = Analyze(entries);
        Directory.CreateDirectory(outputDirectory);

        await WriteSummaryAsync(Path.Combine(outputDirectory, "futures-execution-summary.csv"), result.Summary);
        await WriteBreakevenAsync(Path.Combine(outputDirectory, "futures-execution-breakeven.csv"), result.Breakeven);
        await WriteSessionsAsync(Path.Combine(outputDirectory, "futures-execution-sessions.csv"), result.Sessions);
        await WriteLeaveOneOutAsync(Path.Combine(outputDirectory, "futures-execution-leave-one-session-out.csv"), result.LeaveOneOut);

        PrintPrimarySummary(result);
        return result;
    }

    static void PrintPrimarySummary(AnalysisResult result)
    {
        Console.WriteLine(
            $"6500 futures-execution analysis -- direct Futures round trip per state-entry, no option translation. " +
            $"Gate: mean net points > 0 at {GatePrimaryCostPoints:0.0}pt round-trip cost, " +
            $"same-sign in >= {GateMinNetPositiveLeaveOneOutCount:0}/9 leave-one-session-out folds.");

        foreach (var row in result.Breakeven.OrderBy(x => x.Mechanism).ThenBy(x => x.HorizonBars))
        {
            Console.WriteLine(
                $"  {row.Mechanism,-23} +{row.HorizonBars}: N={row.N:N0}, meanGrossPoints={row.MeanGrossPoints:0.0000}, " +
                $"breakevenCostPoints={row.BreakevenCostPoints:0.0000}");
        }

        foreach (var mechanism in VolumeBar6500EpisodeStateAnalysis.Mechanisms)
        {
            foreach (var horizon in VolumeBar6500OptionTranslationAnalysis.Horizons)
            {
                var primary = result.Summary.FirstOrDefault(x =>
                    x.Mechanism == mechanism && x.HorizonBars == horizon.Bars && x.CostPoints == GatePrimaryCostPoints);
                if (primary is null) { continue; }

                var loo = result.LeaveOneOut.FirstOrDefault(x =>
                    x.Mechanism == mechanism && x.HorizonBars == horizon.Bars && x.CostPoints == GatePrimaryCostPoints);

                var netPositive = primary.MeanNetPoints is > 0;
                var looConsistent = loo is not null && loo.LeaveOneSessionOutSameSignCount >= GateMinNetPositiveLeaveOneOutCount;
                var verdict = netPositive && looConsistent ? "PASS" : "FAIL";

                Console.WriteLine(
                    $"  {mechanism,-23} +{horizon.Bars} @{GatePrimaryCostPoints:0.0}pt: " +
                    $"meanNet={Format(primary.MeanNetPoints)}, net+rate={FormatRate(primary.NetPositiveRate)}, " +
                    $"sessions+/-={primary.PositiveSessionCount}/{primary.NegativeSessionCount}, " +
                    $"looSameSign={(loo is null ? "NA" : $"{loo.LeaveOneSessionOutSameSignCount}/{loo.LeaveOneSessionOutCount}")}, " +
                    $"gate={verdict}");
            }
        }
    }

    static string Format(double? value) => value is null ? "NA" : value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
    static string FormatRate(double? value) => value is null ? "NA" : (value.Value * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    static double? MeanOrNull(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : values.Average();

    static double? MedianOrNull(IReadOnlyList<double> values)
    {
        if (values.Count == 0) { return null; }
        var sorted = values.OrderBy(x => x).ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    static double? RateOrNull(IReadOnlyList<bool> values) =>
        values.Count == 0 ? null : values.Count(x => x) / (double)values.Count;

    static async Task WriteSummaryAsync(string path, IReadOnlyList<SummaryRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,HorizonBars,CostPoints,N,MeanGrossPoints,MeanNetPoints,MedianNetPoints,NetPositiveRate,SessionCount,MedianSessionNetPoints,PositiveSessionCount,NegativeSessionCount");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.HorizonBars, r.CostPoints, r.N, r.MeanGrossPoints, r.MeanNetPoints, r.MedianNetPoints, r.NetPositiveRate, r.SessionCount, r.MedianSessionNetPoints, r.PositiveSessionCount, r.NegativeSessionCount));
    }

    static async Task WriteBreakevenAsync(string path, IReadOnlyList<BreakevenRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,HorizonBars,N,MeanGrossPoints,BreakevenCostPoints");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.HorizonBars, r.N, r.MeanGrossPoints, r.BreakevenCostPoints));
    }

    static async Task WriteSessionsAsync(string path, IReadOnlyList<SessionRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("TradingDate,Mechanism,HorizonBars,CostPoints,N,MeanGrossPoints,MeanNetPoints");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.TradingDate.ToString("yyyy-MM-dd"), r.Mechanism, r.HorizonBars, r.CostPoints, r.N, r.MeanGrossPoints, r.MeanNetPoints));
    }

    static async Task WriteLeaveOneOutAsync(string path, IReadOnlyList<LeaveOneOutRow> rows)
    {
        await using var writer = Writer(path);
        await writer.WriteLineAsync("Mechanism,HorizonBars,CostPoints,LeaveOneSessionOutMinMeanNetPoints,LeaveOneSessionOutMedianMeanNetPoints,LeaveOneSessionOutMaxMeanNetPoints,LeaveOneSessionOutSameSignCount,LeaveOneSessionOutCount");
        foreach (var r in rows)
            await writer.WriteLineAsync(Csv(r.Mechanism, r.HorizonBars, r.CostPoints, r.LeaveOneSessionOutMinMeanNetPoints, r.LeaveOneSessionOutMedianMeanNetPoints, r.LeaveOneSessionOutMaxMeanNetPoints, r.LeaveOneSessionOutSameSignCount, r.LeaveOneSessionOutCount));
    }

    static StreamWriter Writer(string path) => new(path, false, new UTF8Encoding(false));
    static string Csv(params object?[] values) => string.Join(",", values.Select(CsvField));
    static string CsvField(object? value)
    {
        if (value is null) return "";
        var field = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        return field.IndexOfAny([',', '"', '\r', '\n']) < 0 ? field : "\"" + field.Replace("\"", "\"\"") + "\"";
    }
}
