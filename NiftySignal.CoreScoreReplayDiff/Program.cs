using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.Backtest;
using NiftySignal.BacktestData;
using NiftySignal.Domain.Entities;
using NiftySignal.Host;
using NiftySignal.MetricTrials;
using NiftySignal.Persistence;

// Batch 3 (docs/replication_plan.md, 2026-09-13) -- the mandatory pre-Batch-5 gate. Proves live's
// CoreScoreSnapshot computation (LiveFeatureEngine.ComputeCadence) is the SAME as
// CoreScoreOptionSimulator's backtest math, not "a cousin of it" (external review's own words),
// by diffing every one of the 19 published fields (8 raw, 8 signed, CoreScore, CoreScoreFast,
// CoreScoreSlow) across the known populated days, joined by exact cadence timestamp.
//
// Critical detail this tool gets right that a naive replay would not: CadencePopulator (the tool
// that built niftysignal_backtest_analysis's StrikeCadenceSnapshot/CadenceContext rows) anchors
// its 15s cadence grid to a FIXED market-open clock (dayStart = 09:15 IST, boundary = dayStart +
// n*15s), not to the first tick's own timestamp the way NiftySignal.ScoreReplay's unrelated
// replay tool does. Anchoring this tool's live replay to ticks[0].ExchangeTimestamp instead would
// silently misalign every cadence timestamp between the two series and make every row fail to
// join at all -- so this tool uses the identical market-open-anchored grid CadencePopulator uses,
// for both the 15s cadence timer AND the 3s sample timer LiveFeatureEngine.Sample needs to
// populate its own DepthImbalance/ItmSkew running averages.
//
// Usage: dotnet run --project NiftySignal.CoreScoreReplayDiff -- [epsilon] [sourceDatabaseNameOverride]
//   epsilon: max allowed |backtest - live| before a cadence/field counts as a mismatch (default 1e-6).
//   sourceDatabaseNameOverride: live-schema database to replay ticks/instruments from (default
//     niftysignal_vm_copy -- matches NiftySignal.BacktestData/Program.cs's own default, since that
//     is the source CadencePopulator itself read from when it built the StrikeCadenceSnapshot/
//     CadenceContext rows being diffed against here).
//
// Read-only against both databases. Per standing instruction, this is a tool the user runs
// themselves.

var epsilon = args.Length > 0 ? double.Parse(args[0]) : 1e-6;
var sourceDatabaseNameOverride = args.Length > 1 ? args[1] : "niftysignal_vm_copy";
// Ad-hoc diagnostic: dotnet run --project NiftySignal.CoreScoreReplayDiff -- 1e-6 niftysignal_vm_copy GammaExposureRaw 2026-09-11
// Prints the first 15 mismatching (timestamp, backtest, live) triples for one field/day instead
// of the summary table, so a real root-cause dig can see the actual numbers, not just counts.
var dumpField = args.Length > 2 ? args[2] : null;
var dumpDate = args.Length > 3 ? DateOnly.ParseExact(args[3], "yyyy-MM-dd") : (DateOnly?)null;
var dumpMinDiff = args.Length > 4 ? double.Parse(args[4]) : 0.0;

var istOffset = TimeSpan.FromHours(5.5);
var marketOpen = new TimeOnly(9, 15);
var marketClose = new TimeOnly(15, 30);
var cadenceLength = TimeSpan.FromSeconds(15);
var sampleInterval = TimeSpan.FromSeconds(3);

var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");

var liveConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = sourceDatabaseNameOverride }.ConnectionString;
var analysisConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = CadencePopulator.BacktestAnalysisDatabaseName }.ConnectionString;

await using var liveDb = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(liveConnectionString).Options);
await using var analysisDb = new BacktestAnalysisDbContext(new DbContextOptionsBuilder<BacktestAnalysisDbContext>().UseNpgsql(analysisConnectionString).Options);

var asOfDates = await analysisDb.CadenceContexts.Select(c => c.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
if (asOfDates.Count == 0)
{
    Console.Error.WriteLine("No populated days found in niftysignal_backtest_analysis.CadenceContexts.");
    return 1;
}

// The 19 fields Batch 3 must prove tight -- same order the plan's own Verification section
// lists them in (8 raw, 8 signed, CoreScore, CoreScoreFast, CoreScoreSlow).
string[] fieldNames =
[
    "DepthImbalanceRaw", "DepthImbalanceSigned",
    "ItmSkewRaw", "ItmSkewSigned",
    "FutureCvdNet5MinRaw", "FutureCvdNet5MinSigned",
    "NotionalVolumeRatioRaw", "NotionalVolumeRatioSigned",
    "GammaExposureRaw", "GammaExposureSigned",
    "TrendReversion15mRaw", "TrendReversion15mSigned",
    "BasisChangeRaw", "BasisChangeSigned",
    "OiChangeDiff15mRaw", "OiChangeDiff15mSigned",
    "CoreScore", "CoreScoreFast", "CoreScoreSlow",
];

double?[] ExtractLive(CoreScoreSnapshot s) =>
[
    s.DepthImbalanceRaw, s.DepthImbalanceSigned,
    s.ItmSkewRaw, s.ItmSkewSigned,
    s.FutureCvdNet5MinRaw, s.FutureCvdNet5MinSigned,
    s.NotionalVolumeRatioRaw, s.NotionalVolumeRatioSigned,
    s.GammaExposureRaw, s.GammaExposureSigned,
    s.TrendReversion15mRaw, s.TrendReversion15mSigned,
    s.BasisChangeRaw, s.BasisChangeSigned,
    s.OiChangeDiff15mRaw, s.OiChangeDiff15mSigned,
    s.CoreScore, s.CoreScoreFast, s.CoreScoreSlow,
];

double?[] ExtractBacktest(CoreScoreCadenceDiagnostics d) =>
[
    d.DepthImbalanceRaw, d.DepthImbalanceSigned,
    d.ItmSkewRaw, d.ItmSkewSigned,
    d.FutureCvdNet5MinRaw, d.FutureCvdNet5MinSigned,
    d.NotionalVolumeRatioRaw, d.NotionalVolumeRatioSigned,
    d.GammaExposureRaw, d.GammaExposureSigned,
    d.TrendReversion15mRaw, d.TrendReversion15mSigned,
    d.BasisChangeRaw, d.BasisChangeSigned,
    d.OiChangeDiff15mRaw, d.OiChangeDiff15mSigned,
    d.CoreScore, d.CoreScoreFast, d.CoreScoreSlow,
];

void PrintTable(string label, double[] maxAbsDiff, int[] mismatchCount, int[] bothNonNullCount, int[] nullMismatchCount)
{
    Console.WriteLine($"  {label}");
    Console.WriteLine($"  {"Field",-26} {"BothNonNull",12} {"NullMismatch",13} {"MaxAbsDiff",14} {"Mismatches(>eps)",17}");
    for (var i = 0; i < fieldNames.Length; i++)
    {
        Console.WriteLine($"  {fieldNames[i],-26} {bothNonNullCount[i],12} {nullMismatchCount[i],13} {maxAbsDiff[i],14:F6} {mismatchCount[i],17}");
    }
}

var overallMaxAbsDiff = new double[fieldNames.Length];
var overallMismatchCount = new int[fieldNames.Length];
var overallBothNonNullCount = new int[fieldNames.Length];
var overallNullMismatchCount = new int[fieldNames.Length];

foreach (var asOfDate in asOfDates)
{
    Console.WriteLine($"=== {asOfDate:yyyy-MM-dd} ===");

    var instruments = await liveDb.Instruments.Where(i => i.AsOfDate == asOfDate).ToListAsync();
    if (instruments.Count == 0)
    {
        Console.WriteLine($"  SKIPPED: no resolved instruments in '{sourceDatabaseNameOverride}' for this date.");
        Console.WriteLine();
        continue;
    }

    var strikeRows = await analysisDb.StrikeCadenceSnapshots.Where(s => s.AsOfDate == asOfDate).ToListAsync();
    if (strikeRows.Count == 0)
    {
        Console.WriteLine("  SKIPPED: no StrikeCadenceSnapshot rows for this date.");
        Console.WriteLine();
        continue;
    }

    var cadenceContexts = await analysisDb.CadenceContexts.Where(c => c.AsOfDate == asOfDate).ToListAsync();
    // ThisWeek = nearest ExpiryDate, same convention as NiftySignal.MetricTrials/Program.cs.
    var thisWeekExpiry = strikeRows.Select(s => s.ExpiryDate).Distinct().OrderBy(e => e).First();

    // --- Backtest side: exactly CoreScoreOptionSimulator's own math, no re-derivation here. ---
    var backtestDiagnostics = CoreScoreOptionSimulator.BuildDiagnostics(strikeRows, cadenceContexts, thisWeekExpiry);
    var backtestByTimestamp = backtestDiagnostics.ToDictionary(d => d.Timestamp);

    // --- Live side: replay real ticks through LiveFeatureEngine on CadencePopulator's own
    // market-open-anchored clock (see the file-header comment for why this anchoring matters). ---
    var dayStartUtc = new DateTimeOffset(asOfDate.ToDateTime(marketOpen), istOffset).ToUniversalTime();
    var dayEndUtc = new DateTimeOffset(asOfDate.ToDateTime(marketClose), istOffset).ToUniversalTime();

    var tickSource = new BacktestTickSource(liveDb, dayStartUtc, dayEndUtc.AddSeconds(1));
    var ticks = new List<Tick>();
    await foreach (var tick in tickSource.ReadTicksAsync(CancellationToken.None))
    {
        ticks.Add(tick);
    }

    if (ticks.Count == 0)
    {
        Console.WriteLine($"  SKIPPED: no ticks found in '{sourceDatabaseNameOverride}' for this date.");
        Console.WriteLine();
        continue;
    }

    var engine = new LiveFeatureEngine(instruments);
    var liveByTimestamp = new Dictionary<DateTimeOffset, CoreScoreSnapshot>();

    var tickIndex = 0;
    var nextSample = dayStartUtc;
    var nextCadence = dayStartUtc + cadenceLength;

    while (nextSample <= dayEndUtc || nextCadence <= dayEndUtc)
    {
        var boundary = nextSample < nextCadence ? nextSample : nextCadence;

        // Apply every tick recorded up to this boundary before sampling/computing at it -- same
        // discipline NiftySignal.ScoreReplay's own replay loop uses.
        while (tickIndex < ticks.Count && ticks[tickIndex].ExchangeTimestamp <= boundary)
        {
            engine.OnTick(ticks[tickIndex]);
            tickIndex++;
        }

        if (nextSample <= boundary)
        {
            engine.Sample(boundary);
            nextSample = nextSample.Add(sampleInterval);
        }

        if (nextCadence <= boundary)
        {
            engine.ComputeCadence(boundary);
            nextCadence = nextCadence.Add(cadenceLength);
            if (engine.LastCoreScoreSnapshot is { } snapshot)
            {
                liveByTimestamp[boundary] = snapshot;
            }
        }
    }

    Console.WriteLine($"  {ticks.Count} ticks replayed, {backtestDiagnostics.Count} backtest cadences, {liveByTimestamp.Count} live cadences with a CoreScoreSnapshot.");

    var dayMaxAbsDiff = new double[fieldNames.Length];
    var dayMismatchCount = new int[fieldNames.Length];
    var dayBothNonNullCount = new int[fieldNames.Length];
    var dayNullMismatchCount = new int[fieldNames.Length];
    var matchedCount = 0;
    var dumpFieldIndex = dumpField is null ? -1 : Array.IndexOf(fieldNames, dumpField);
    var dumpedCount = 0;

    foreach (var (timestamp, backtestDiag) in backtestByTimestamp.OrderBy(kv => kv.Key))
    {
        if (!liveByTimestamp.TryGetValue(timestamp, out var liveSnapshot))
        {
            continue;
        }

        matchedCount++;
        var backtestValues = ExtractBacktest(backtestDiag);
        var liveValues = ExtractLive(liveSnapshot);

        for (var i = 0; i < fieldNames.Length; i++)
        {
            var b = backtestValues[i];
            var l = liveValues[i];

            if (b is null && l is null)
            {
                continue;
            }

            if (b is null || l is null)
            {
                dayNullMismatchCount[i]++;
                overallNullMismatchCount[i]++;

                if (i == dumpFieldIndex && (dumpDate is null || dumpDate == asOfDate) && dumpedCount < 25)
                {
                    Console.WriteLine($"    NULLMISMATCH {timestamp:HH:mm:ss} backtest={(b is null ? "null" : b.Value.ToString("F6"))} live={(l is null ? "null" : l.Value.ToString("F6"))}");
                    dumpedCount++;
                }

                continue;
            }

            dayBothNonNullCount[i]++;
            overallBothNonNullCount[i]++;
            var diff = Math.Abs(b.Value - l.Value);
            if (diff > dayMaxAbsDiff[i])
            {
                dayMaxAbsDiff[i] = diff;
            }

            if (diff > overallMaxAbsDiff[i])
            {
                overallMaxAbsDiff[i] = diff;
            }

            if (diff > epsilon)
            {
                dayMismatchCount[i]++;
                overallMismatchCount[i]++;

                if (i == dumpFieldIndex && (dumpDate is null || dumpDate == asOfDate) && diff > dumpMinDiff && dumpedCount < 25)
                {
                    Console.WriteLine($"    MISMATCH {timestamp:HH:mm:ss} backtest={b.Value:F6} live={l.Value:F6} diff={diff:F6}");
                    dumpedCount++;
                }
            }
        }
    }

    Console.WriteLine($"  {matchedCount} cadences matched by exact timestamp (of {backtestByTimestamp.Count} backtest / {liveByTimestamp.Count} live).");
    PrintTable($"epsilon={epsilon}", dayMaxAbsDiff, dayMismatchCount, dayBothNonNullCount, dayNullMismatchCount);
    Console.WriteLine();
}

Console.WriteLine("=== OVERALL (all days) ===");
PrintTable($"epsilon={epsilon}", overallMaxAbsDiff, overallMismatchCount, overallBothNonNullCount, overallNullMismatchCount);

return 0;
