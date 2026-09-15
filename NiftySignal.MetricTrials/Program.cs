using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.BacktestData;
using NiftySignal.MetricTrials;

// 2026-09-13: Core score option trade simulation -- the current combined-score candidate (8
// terms: DepthImbalance/ItmSkew/FutureCvdNet5Min/NotionalVolumeRatio/GammaExposure/
// TrendReversion15m/BasisChange/OiChangeDiff15m, see CoreScoreOptionSimulator's own doc comment
// for weights, signs, and the 11 Sep investigation that led to ItmSkew/GammaExposure being cut).
// Runs the SAME composite score through two different trading strategies for comparison:
// 1. Hysteresis threshold (SimulateDay) -- enters at +/-threshold, exits only once the score
//    crosses to the OPPOSITE threshold.
// 2. Fast/slow crossover (SimulateDayCrossover) -- EXPERIMENTAL (2026-09-13): smooths the same
//    score over two real-time trailing windows and trades the crossover between them, always
//    positioned once the first cross fires. See CoreScoreCrossoverOptions' own doc comment.
// The six earlier FutureCvdProxyNet5Min future-direct diagnostic variants that used to run here
// (2026-09-12, entry/exit-rank threshold trading and always-positioned EMA variants) are removed
// from this console tool's output -- that investigation concluded and fed into the Core score
// design; FutureDirectSimulator.cs itself is untouched and still covered by its own tests
// (NiftySignal.Tests/MetricTrials/FutureDirectSimulatorTests.cs), just no longer run/logged here.
//
// Usage: dotnet run --project NiftySignal.MetricTrials --
//   [--core-entry-score=N] [--core-entry-price-low=N] [--core-entry-price-high=N]
//   [--core-score-smoothing-cadences=N] [--crossover-fast-min=N] [--crossover-slow-min=N]
//
// Reads every populated day from niftysignal_backtest_analysis (read-only). Entry picks whichever
// strike is priced near [entryPriceRangeLow, entryPriceRangeHigh] (not ATM) for the signaled side
// (long call on a bullish score, long put on bearish), one position at a time, no SL/TP in either
// strategy. MAE/MFE are diagnostics only, never drive an exit.
double ParseOverride(string flag, double fallback) =>
    args.FirstOrDefault(a => a.StartsWith(flag, StringComparison.OrdinalIgnoreCase)) is { } arg
        ? double.Parse(arg[flag.Length..])
        : fallback;

var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");
var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = CadencePopulator.BacktestAnalysisDatabaseName }.ConnectionString;

var dbOptions = new DbContextOptionsBuilder<BacktestAnalysisDbContext>().UseNpgsql(connectionString).Options;
await using var db = new BacktestAnalysisDbContext(dbOptions);

var asOfDates = await db.CadenceContexts.Select(c => c.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
if (asOfDates.Count == 0)
{
    Console.Error.WriteLine("No populated days found in niftysignal_backtest_analysis.CadenceContexts.");
    return 1;
}

// .ToOffset(IST) -- CadenceContext.Timestamp is stored UTC (Npgsql's own requirement, see
// CadencePopulator's own comment on this), so printing EntryTime/ExitTime directly would show
// UTC wall-clock time, not the IST everyone reading this output actually thinks in.
string FormatIst(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(5.5)).ToString("HH:mm:ss");

void PrintCoreTrade(CoreScoreTrade trade)
{
    var side = trade.Side == NiftySignal.Domain.Enums.OptionType.Call ? "Call" : "Put";
    var duration = trade.ExitTime - trade.EntryTime;
    Console.WriteLine($"    {FormatIst(trade.EntryTime)} Long {side}@{trade.StrikePrice} entry={trade.EntryPrice:F2} score={trade.EntryScore:F1} -> {FormatIst(trade.ExitTime)} exit={trade.ExitPrice:F2} ({trade.ExitReason}, {duration.TotalMinutes:F1}min) MFE={trade.Mfe:F2} MAE={trade.Mae:F2} netPnl={trade.NetPnlPoints:F2} ({trade.NetPnlPercent:F1}%)");
}

void PrintDayResult(DateOnly asOfDate, DateOnly thisWeekExpiry, CoreScoreDayResult result)
{
    var dayWinRate = result.Trades.Count > 0 ? 100.0 * result.WinCount / result.Trades.Count : 0;
    Console.WriteLine($"{asOfDate:yyyy-MM-dd} (ThisWeek={thisWeekExpiry:yyyy-MM-dd}): {result.Trades.Count} trades, {dayWinRate:F1}% win rate, net {result.NetPnlPoints:F2} pts ({result.TotalPnlPercent:F1}%, avgPerTrade={result.AvgPnlPercent:F1}%), avgMFE={result.AvgMfe:F2} avgMAE={result.AvgMae:F2}");
    foreach (var trade in result.Trades)
    {
        PrintCoreTrade(trade);
    }
}

void PrintOverall(string label, IReadOnlyList<CoreScoreDayResult> dayResults)
{
    var allTrades = dayResults.SelectMany(r => r.Trades).ToList();
    var totalPnl = allTrades.Sum(t => t.NetPnlPoints);
    var totalPnlPercent = allTrades.Sum(t => t.NetPnlPercent);
    var winRate = allTrades.Count > 0 ? 100.0 * allTrades.Count(t => t.NetPnlPoints > 0) / allTrades.Count : 0;
    var avgMfe = allTrades.Count > 0 ? allTrades.Average(t => t.Mfe) : 0;
    var avgMae = allTrades.Count > 0 ? allTrades.Average(t => t.Mae) : 0;
    Console.WriteLine($"--- {label} total: {allTrades.Count} trades, {winRate:F1}% win rate, net {totalPnl:F2} pts ({totalPnlPercent:F1}%), avgMFE={avgMfe:F2} avgMAE={avgMae:F2} ---");
    Console.WriteLine();
}

// Load each day's raw rows ONCE, reused by both strategies below -- avoids two round-trips per day.
var dayData = new List<(DateOnly AsOfDate, DateOnly ThisWeekExpiry, IReadOnlyList<StrikeCadenceSnapshot> StrikeRows, IReadOnlyList<CadenceContext> CadenceContexts)>();
foreach (var asOfDate in asOfDates)
{
    var strikeRows = await db.StrikeCadenceSnapshots.Where(s => s.AsOfDate == asOfDate).ToListAsync();
    if (strikeRows.Count == 0)
    {
        continue;
    }

    var cadenceContexts = await db.CadenceContexts.Where(c => c.AsOfDate == asOfDate).ToListAsync();
    // ThisWeek = nearest ExpiryDate -- NextWeek is dropped per the 2026-09-13 standing rule, not
    // scored at all here.
    var thisWeekExpiry = strikeRows.Select(s => s.ExpiryDate).Distinct().OrderBy(e => e).First();
    dayData.Add((asOfDate, thisWeekExpiry, strikeRows, cadenceContexts));
}

// --- Strategy 1: hysteresis threshold ---
var entryScoreThreshold = ParseOverride("--core-entry-score=", 30.0);
var entryPriceRangeLow = (decimal)ParseOverride("--core-entry-price-low=", 100.0);
var entryPriceRangeHigh = (decimal)ParseOverride("--core-entry-price-high=", 150.0);
var scoreSmoothingCadences = (int)ParseOverride("--core-score-smoothing-cadences=", 1.0);

Console.WriteLine($"=== Core score option simulation - HYSTERESIS THRESHOLD (entry-score>={entryScoreThreshold}, smoothing={scoreSmoothingCadences} cadence(s), strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
var hysteresisResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDay(day.StrikeRows, day.CadenceContexts, day.ThisWeekExpiry,
        new CoreScoreSimulationOptions(EntryScoreThreshold: entryScoreThreshold,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh,
            ScoreSmoothingCadences: scoreSmoothingCadences));
    hysteresisResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ThisWeekExpiry, result);
}
PrintOverall("Hysteresis threshold", hysteresisResults);

// --- Strategy 2: fast/slow crossover (experimental) ---
var crossoverFastMinutes = (int)ParseOverride("--crossover-fast-min=", 5.0);
var crossoverSlowMinutes = (int)ParseOverride("--crossover-slow-min=", 15.0);

Console.WriteLine($"=== Core score option simulation - FAST/SLOW CROSSOVER EXPERIMENT (fast={crossoverFastMinutes}min, slow={crossoverSlowMinutes}min, strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
var crossoverResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDayCrossover(day.StrikeRows, day.CadenceContexts, day.ThisWeekExpiry,
        new CoreScoreCrossoverOptions(FastWindowMinutes: crossoverFastMinutes, SlowWindowMinutes: crossoverSlowMinutes,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh));
    crossoverResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ThisWeekExpiry, result);
}
PrintOverall("Fast/slow crossover", crossoverResults);

return 0;
