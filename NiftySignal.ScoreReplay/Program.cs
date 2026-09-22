using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.Backtest;
using NiftySignal.Domain.Entities;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Rules;

// One-off analysis tool (2026-09-09) -- NOT audit finding F32's deferred backtest runner. F32
// needs LiveTradingEngine/PaperTradeSimulator/PerformanceReportBuilder wired together for full
// entry/exit/P&L simulation, and stays deferred exactly as planned. This only drives
// BacktestTickSource's already-built replay (see its own doc comment: "this class ... [is]
// real and tested, but nothing wires [it] ... through LiveFeatureEngine.OnTick/Sample/
// ComputeCadence on a clock keyed off tick timestamps") through the scoring engine alone, to
// recover a real score distribution from today's already-recorded ticks under the
// fully-corrected Batch 1-3 formulas -- purely so Batch 4's F13 thresholds (MinAbsScore,
// MinScoreSustainedSeconds/Cadences) can be derived today instead of waiting for tomorrow's
// live session. Read-only: never writes anything back to the database.
//
// Connection string is never a command-line argument or literal here -- same pattern as
// NiftySignalDbContextFactory (reads NiftySignal.Host's own appsettings/appsettings.Local.json),
// just with the database name swapped to the local synced VM copy by default, since that's
// where today's real market data actually lives (see scripts/sync-vm-database.ps1). Pass a
// second arg to point at a different database name if ever needed.
var istOffset = TimeSpan.FromHours(5.5);
var asOfDate = args.Length > 0
    ? DateOnly.ParseExact(args[0], "yyyy-MM-dd")
    : DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(istOffset).Date);
var databaseNameOverride = args.Length > 1 ? args[1] : "niftysignal_vm_copy";

// AppContext.BaseDirectory (the bin/Debug/net10.0 output folder), not
// Directory.GetCurrentDirectory() -- unlike `dotnet ef`, this tool is commonly launched via
// the built DLL directly, whose working directory is wherever the caller happens to be, not
// this project's own folder.
var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");

var connectionStringBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseNameOverride };
var connectionString = connectionStringBuilder.ConnectionString;

var options = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connectionString).Options;
await using var db = new NiftySignalDbContext(options);

// Audit finding F61 (2026-09-22): explicit NIFTY filter at the intake, in addition to
// LiveFeatureEngine's own internal filter -- see that class's NiftyUnderlying doc comment.
var instruments = await db.Instruments.Where(i => i.AsOfDate == asOfDate && i.Underlying == LiveFeatureEngine.NiftyUnderlying).ToListAsync();
if (instruments.Count == 0)
{
    Console.Error.WriteLine($"No resolved instruments found for {asOfDate:yyyy-MM-dd}.");
    return 1;
}

Console.WriteLine($"Replaying {asOfDate:yyyy-MM-dd} against {instruments.Count} instruments...");

var dayStartUtc = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), istOffset).ToUniversalTime();
var dayEndUtc = dayStartUtc.AddDays(1);

var tickSource = new BacktestTickSource(db, dayStartUtc, dayEndUtc);
var ticks = new List<Tick>();
await foreach (var tick in tickSource.ReadTicksAsync(CancellationToken.None))
{
    ticks.Add(tick);
}

if (ticks.Count == 0)
{
    Console.Error.WriteLine($"No ticks found for {asOfDate:yyyy-MM-dd}.");
    return 1;
}

Console.WriteLine($"Loaded {ticks.Count} ticks, {ticks[0].ExchangeTimestamp.ToOffset(istOffset):HH:mm:ss} to {ticks[^1].ExchangeTimestamp.ToOffset(istOffset):HH:mm:ss} IST.");

var engine = new LiveFeatureEngine(instruments);

// Same cadence as MarketDataIngestionWorker's real timers (SampleInterval/ScoreCadence) --
// keyed off recorded tick timestamps here instead of a live PeriodicTimer, so the replay
// reproduces the same 3s-sample/15s-cadence rhythm without needing to run in real time.
var sampleInterval = TimeSpan.FromSeconds(3);
var cadenceInterval = TimeSpan.FromSeconds(15);
var nextSample = ticks[0].ExchangeTimestamp;
var nextCadence = ticks[0].ExchangeTimestamp;
var lastTickTime = ticks[^1].ExchangeTimestamp;

var tickIndex = 0;
var cadenceCount = 0;
// Real-time gating: LiveTradingEngine.EvaluateCadenceAsync only ever feeds its sustain
// tracker (and only ever considers an entry) when CompositeScore is non-null -- matched here
// exactly, not IsWarmedUp, since that's the actual production gate.
var scoredCadences = new List<(DateTimeOffset At, double Score)>();

while (nextSample <= lastTickTime || nextCadence <= lastTickTime)
{
    var boundary = nextSample < nextCadence ? nextSample : nextCadence;

    // Apply every tick recorded up to this boundary before sampling/computing at it -- in the
    // live system, by the time a timer fires, OnTick has already applied everything that
    // arrived before that instant.
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
        var snapshot = engine.ComputeCadence(boundary);
        nextCadence = nextCadence.Add(cadenceInterval);
        if (snapshot is not null)
        {
            cadenceCount++;
            if (snapshot.CompositeScore is { } score)
            {
                scoredCadences.Add((boundary, score));
            }
        }
    }
}

Console.WriteLine($"Computed {cadenceCount} cadences, {scoredCadences.Count} produced a composite score.");

if (scoredCadences.Count == 0)
{
    Console.Error.WriteLine("No scored cadences -- nothing to analyze.");
    return 1;
}

var absScores = scoredCadences.Select(c => Math.Abs(c.Score)).OrderBy(s => s).ToList();

Console.WriteLine();
Console.WriteLine("|CompositeScore| distribution over scored cadences:");
Console.WriteLine(
    $"  p50={Percentile(absScores, 50):F1}  p75={Percentile(absScores, 75):F1}  " +
    $"p90={Percentile(absScores, 90):F1}  p92.5={Percentile(absScores, 92.5):F1}  " +
    $"p95={Percentile(absScores, 95):F1}  max={absScores[^1]:F1}");

// Audit's own suggested methodology: pick MinAbsScore from a target qualification rate
// ("top 5-10% of cadences"), then check whether real streaks at that level actually clear
// MinScoreSustainedSeconds (45s) / MinScoreSustainedCadences (3) -- a percentile alone
// doesn't say whether the score actually holds there long enough to matter.
Console.WriteLine();
Console.WriteLine("Sustain-duration check at candidate MinAbsScore thresholds:");
foreach (var candidate in new[] { Percentile(absScores, 90), Percentile(absScores, 92.5), Percentile(absScores, 95) })
{
    var tracker = new ScoreSustainTracker();
    var streaks = new List<(TimeSpan Duration, int Cadences)>();
    var previous = new SustainStatus(TimeSpan.Zero, 0);
    foreach (var (at, score) in scoredCadences)
    {
        var status = tracker.Observe(at, score, candidate);
        if (status.CadenceCount == 0 && previous.CadenceCount > 0)
        {
            streaks.Add((previous.Duration, previous.CadenceCount));
        }

        previous = status;
    }

    if (previous.CadenceCount > 0)
    {
        streaks.Add((previous.Duration, previous.CadenceCount));
    }

    var qualifying = scoredCadences.Count(c => Math.Abs(c.Score) >= candidate);
    var longestStreakSeconds = streaks.Count > 0 ? streaks.Max(s => s.Duration.TotalSeconds) : 0;
    var longestStreakCadences = streaks.Count > 0 ? streaks.Max(s => s.Cadences) : 0;
    var streaksAtOrAbove45s = streaks.Count(s => s.Duration.TotalSeconds >= 45);

    Console.WriteLine(
        $"  threshold={candidate:F1}: {qualifying} cadences qualify ({100.0 * qualifying / scoredCadences.Count:F1}%), " +
        $"{streaks.Count} streaks, longest={longestStreakSeconds:F0}s/{longestStreakCadences} cadences, " +
        $"streaks>=45s: {streaksAtOrAbove45s}");
}

return 0;

static double Percentile(List<double> sorted, double p)
{
    var rank = p / 100.0 * (sorted.Count - 1);
    var lower = (int)Math.Floor(rank);
    var upper = (int)Math.Ceiling(rank);
    if (lower == upper)
    {
        return sorted[lower];
    }

    var frac = rank - lower;
    return sorted[lower] + ((sorted[upper] - sorted[lower]) * frac);
}
