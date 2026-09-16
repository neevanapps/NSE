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
//   [--core-score-smoothing-cadences=N] [--crossover-fast-min=N] [--crossover-slow-min=N] [--next-week]
//   [--stop-loss-pct=N] [--max-consecutive-losses=N] (both Hysteresis only) [--correlation] [--date=yyyy-MM-dd]
//
// Reads every populated day from niftysignal_backtest_analysis (read-only). Entry picks whichever
// strike is priced near [entryPriceRangeLow, entryPriceRangeHigh] (not ATM) for the signaled side
// (long call on a bullish score, long put on bearish), one position at a time, no SL/TP in either
// strategy. MAE/MFE are diagnostics only, never drive an exit.
double ParseOverride(string flag, double fallback) =>
    args.FirstOrDefault(a => a.StartsWith(flag, StringComparison.OrdinalIgnoreCase)) is { } arg
        ? double.Parse(arg[flag.Length..])
        : fallback;

bool HasFlag(string name) =>
    args.Contains($"--{name}", StringComparer.OrdinalIgnoreCase);

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

// --date=yyyy-MM-dd (2026-09-16): restrict the whole run to one day, e.g. to isolate --next-week's
// effect on just today without the other 4 days' totals diluting/obscuring it.
var dateFilterArg = args.FirstOrDefault(a => a.StartsWith("--date=", StringComparison.OrdinalIgnoreCase));
if (dateFilterArg is not null)
{
    if (!DateOnly.TryParseExact(dateFilterArg["--date=".Length..], "yyyy-MM-dd", out var dateFilter))
    {
        Console.Error.WriteLine("Usage: --date=yyyy-MM-dd");
        return 1;
    }

    asOfDates = asOfDates.Where(d => d == dateFilter).ToList();
    if (asOfDates.Count == 0)
    {
        Console.Error.WriteLine($"{dateFilter:yyyy-MM-dd}: not populated in niftysignal_backtest_analysis.");
        return 1;
    }
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

void PrintDayResult(DateOnly asOfDate, DateOnly scoredExpiry, string expiryLabel, CoreScoreDayResult result)
{
    var dayWinRate = result.Trades.Count > 0 ? 100.0 * result.WinCount / result.Trades.Count : 0;
    Console.WriteLine($"{asOfDate:yyyy-MM-dd} ({expiryLabel}={scoredExpiry:yyyy-MM-dd}): {result.Trades.Count} trades, {dayWinRate:F1}% win rate, net {result.NetPnlPoints:F2} pts ({result.TotalPnlPercent:F1}%, avgPerTrade={result.AvgPnlPercent:F1}%), avgMFE={result.AvgMfe:F2} avgMAE={result.AvgMae:F2}");
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
// --next-week (2026-09-15, experimental -- prompted by the 09-15 Hysteresis blowup: a 0-DTE
// ThisWeek chain's theta cliff turned an already-known "stuck score" gap (first seen 09-11) into a
// -58%/-72% loss on two trades held 135/172 minutes. NextWeek's chain is never 0-DTE, so this asks
// whether scoring+trading OFF THAT CHAIN INSTEAD removes the decay tail risk. Every raw metric
// computation, weight, and rule inside CoreScoreOptionSimulator is completely unchanged -- it never
// hardcodes "ThisWeek", it just scores+trades whichever expiry's rows it's handed. Only which
// expiry we hand it changes here. Days with no second (NextWeek) expiry present that trading day
// are skipped for this mode -- StrikeCadenceSnapshot persists both chains side by side (see its own
// doc comment), but a day right at rollover could still be missing one.
var useNextWeek = HasFlag("next-week");
var dayData = new List<(DateOnly AsOfDate, DateOnly ScoredExpiry, IReadOnlyList<StrikeCadenceSnapshot> StrikeRows, IReadOnlyList<CadenceContext> CadenceContexts)>();
foreach (var asOfDate in asOfDates)
{
    var strikeRows = await db.StrikeCadenceSnapshots.Where(s => s.AsOfDate == asOfDate).ToListAsync();
    if (strikeRows.Count == 0)
    {
        continue;
    }

    var cadenceContexts = await db.CadenceContexts.Where(c => c.AsOfDate == asOfDate).ToListAsync();
    // ThisWeek = nearest ExpiryDate, NextWeek = the one after -- per the 2026-09-13 standing rule
    // NextWeek is dropped for the DEFAULT run below; --next-week opts into scoring+trading it
    // instead, not in addition.
    var distinctExpiries = strikeRows.Select(s => s.ExpiryDate).Distinct().OrderBy(e => e).ToList();
    if (useNextWeek)
    {
        if (distinctExpiries.Count < 2)
        {
            Console.WriteLine($"{asOfDate:yyyy-MM-dd}: no NextWeek expiry chain present -- skipping for --next-week.");
            continue;
        }

        dayData.Add((asOfDate, distinctExpiries[1], strikeRows, cadenceContexts));
    }
    else
    {
        dayData.Add((asOfDate, distinctExpiries[0], strikeRows, cadenceContexts));
    }
}

var expiryLabel = useNextWeek ? "NextWeek" : "ThisWeek";
Console.WriteLine(useNextWeek
    ? "*** Scoring and trading the NEXT-WEEK expiry chain instead of ThisWeek (experimental, --next-week) ***"
    : "*** Scoring and trading the ThisWeek (near) expiry chain (default) ***");
Console.WriteLine();

// --correlation (2026-09-16): user watched live today and reported the score reading bullish
// (and the fast/slow crossover reading bullish) WHILE Nifty was actually falling -- i.e. possibly
// the wrong sign, not just noise. Checks that directly and quantitatively rather than by eyeballing
// trade logs: for every cadence with a CoreScore, does its sign (and separately the fast-vs-slow
// crossover's sign) actually predict the FORWARD price move over the next 5/15 real minutes? Uses
// CadenceContext.FutureChangeForDay (cumulative since day open, leakage-safe as a per-cadence
// value -- see that class's own doc comment) so the "forward move" is just a difference of two
// already-computed cumulative values, no new lookahead-unsafe field needed. This is diagnostic only
// -- it doesn't feed into or change SimulateDay/SimulateDayCrossover/SimulateDayCombined at all.
if (HasFlag("correlation"))
{
    foreach (var day in dayData)
    {
        var diagnostics = CoreScoreOptionSimulator.BuildDiagnostics(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
            fastWindowMinutes: (int)ParseOverride("--crossover-fast-min=", 5.0), slowWindowMinutes: (int)ParseOverride("--crossover-slow-min=", 15.0));

        var contextsByTime = day.CadenceContexts.Where(c => c.FutureChangeForDay is not null).OrderBy(c => c.Timestamp).ToList();

        decimal? ForwardMove(DateTimeOffset from, TimeSpan horizon)
        {
            var atOrBefore = contextsByTime.LastOrDefault(c => c.Timestamp <= from);
            var atOrAfter = contextsByTime.FirstOrDefault(c => c.Timestamp >= from + horizon);
            return atOrBefore is not null && atOrAfter is not null
                ? atOrAfter.FutureChangeForDay - atOrBefore.FutureChangeForDay
                : null;
        }

        void ReportBucket(string label, IEnumerable<(double Signal, decimal Move)> pairs)
        {
            var list = pairs.ToList();
            if (list.Count == 0)
            {
                Console.WriteLine($"    {label}: no observations.");
                return;
            }

            var bullishRight = list.Count(p => p.Signal > 0 && p.Move > 0);
            var bullishWrong = list.Count(p => p.Signal > 0 && p.Move < 0);
            var bearishRight = list.Count(p => p.Signal < 0 && p.Move < 0);
            var bearishWrong = list.Count(p => p.Signal < 0 && p.Move > 0);
            var signalCount = bullishRight + bullishWrong + bearishRight + bearishWrong;
            var hitRate = signalCount > 0 ? 100.0 * (bullishRight + bearishRight) / signalCount : 0;

            var meanS = list.Average(p => p.Signal);
            var meanM = list.Average(p => (double)p.Move);
            var cov = list.Sum(p => (p.Signal - meanS) * ((double)p.Move - meanM));
            var sdS = Math.Sqrt(list.Sum(p => Math.Pow(p.Signal - meanS, 2)));
            var sdM = Math.Sqrt(list.Sum(p => Math.Pow((double)p.Move - meanM, 2)));
            var correlation = sdS > 0 && sdM > 0 ? cov / (sdS * sdM) : double.NaN;

            Console.WriteLine($"    {label}: n={list.Count}, correlation={correlation:F3}, directional hit rate={hitRate:F1}% over {signalCount} signed cadences (bullish-right={bullishRight}, bullish-wrong={bullishWrong}, bearish-right={bearishRight}, bearish-wrong={bearishWrong})");
        }

        Console.WriteLine($"=== Correlation check: {day.AsOfDate:yyyy-MM-dd} ({expiryLabel}={day.ScoredExpiry:yyyy-MM-dd}) ===");
        ReportBucket("CoreScore sign vs forward 5-min move", diagnostics
            .Where(d => d.CoreScore is not null)
            .Select(d => (d.CoreScore!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("CoreScore sign vs forward 15-min move", diagnostics
            .Where(d => d.CoreScore is not null)
            .Select(d => (d.CoreScore!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("Fast-minus-slow sign vs forward 5-min move", diagnostics
            .Where(d => d.CoreScoreFast is not null && d.CoreScoreSlow is not null)
            .Select(d => (d.CoreScoreFast!.Value - d.CoreScoreSlow!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(5))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));
        ReportBucket("Fast-minus-slow sign vs forward 15-min move", diagnostics
            .Where(d => d.CoreScoreFast is not null && d.CoreScoreSlow is not null)
            .Select(d => (d.CoreScoreFast!.Value - d.CoreScoreSlow!.Value, ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
            .Where(p => p.Item2 is not null)
            .Select(p => (p.Item1, p.Item2!.Value)));

        // Per-component breakdown (15-min horizon only, to keep this readable) -- pinpoints WHICH
        // of the 8 terms is driving an aggregate CoreScore breakdown like today's, rather than
        // leaving it as one unexplained composite number.
        (string Name, Func<CoreScoreCadenceDiagnostics, double?> Signed)[] components =
        [
            ("DepthImbalance", d => d.DepthImbalanceSigned),
            ("ItmSkew", d => d.ItmSkewSigned),
            ("FutureCvdNet5Min", d => d.FutureCvdNet5MinSigned),
            ("NotionalVolumeRatio", d => d.NotionalVolumeRatioSigned),
            ("GammaExposure", d => d.GammaExposureSigned),
            ("TrendReversion15m", d => d.TrendReversion15mSigned),
            ("BasisChange", d => d.BasisChangeSigned),
            ("OiChangeDiff15m", d => d.OiChangeDiff15mSigned),
        ];
        foreach (var (name, signed) in components)
        {
            ReportBucket($"  component {name} vs forward 15-min move", diagnostics
                .Select(d => (Signed: signed(d), Move: ForwardMove(d.Timestamp, TimeSpan.FromMinutes(15))))
                .Where(p => p.Signed is not null && p.Move is not null)
                .Select(p => (p.Signed!.Value, p.Move!.Value)));
        }

        Console.WriteLine();
    }

    return 0;
}

// --- Strategy 1: hysteresis threshold ---
var entryScoreThreshold = ParseOverride("--core-entry-score=", 30.0);
var entryPriceRangeLow = (decimal)ParseOverride("--core-entry-price-low=", 100.0);
var entryPriceRangeHigh = (decimal)ParseOverride("--core-entry-price-high=", 150.0);
var scoreSmoothingCadences = (int)ParseOverride("--core-score-smoothing-cadences=", 1.0);
var stopLossArg = args.FirstOrDefault(a => a.StartsWith("--stop-loss-pct=", StringComparison.OrdinalIgnoreCase));
var stopLossPct = stopLossArg is not null ? (decimal?)double.Parse(stopLossArg["--stop-loss-pct=".Length..]) : null;
var maxConsecutiveLossesArg = args.FirstOrDefault(a => a.StartsWith("--max-consecutive-losses=", StringComparison.OrdinalIgnoreCase));
var maxConsecutiveLosses = maxConsecutiveLossesArg is not null ? (int?)int.Parse(maxConsecutiveLossesArg["--max-consecutive-losses=".Length..]) : null;

Console.WriteLine($"=== Core score option simulation - HYSTERESIS THRESHOLD (entry-score>={entryScoreThreshold}, smoothing={scoreSmoothingCadences} cadence(s), strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}], stop-loss={(stopLossPct is { } sl ? $"{sl}%" : "none")}, max-consecutive-losses={(maxConsecutiveLosses is { } mcl ? mcl.ToString() : "none")}) ===");
var hysteresisResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDay(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
        new CoreScoreSimulationOptions(EntryScoreThreshold: entryScoreThreshold,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh,
            ScoreSmoothingCadences: scoreSmoothingCadences, StopLossPct: stopLossPct, MaxConsecutiveLosses: maxConsecutiveLosses));
    hysteresisResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
}
PrintOverall("Hysteresis threshold", hysteresisResults);

// --- Strategy 2: fast/slow crossover (experimental) ---
var crossoverFastMinutes = (int)ParseOverride("--crossover-fast-min=", 5.0);
var crossoverSlowMinutes = (int)ParseOverride("--crossover-slow-min=", 15.0);

Console.WriteLine($"=== Core score option simulation - FAST/SLOW CROSSOVER EXPERIMENT (fast={crossoverFastMinutes}min, slow={crossoverSlowMinutes}min, strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
var crossoverResults = new List<CoreScoreDayResult>();
foreach (var day in dayData)
{
    var result = CoreScoreOptionSimulator.SimulateDayCrossover(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
        new CoreScoreCrossoverOptions(FastWindowMinutes: crossoverFastMinutes, SlowWindowMinutes: crossoverSlowMinutes,
            EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh));
    crossoverResults.Add(result);
    PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
}
PrintOverall("Fast/slow crossover", crossoverResults);

// --- Strategy 3: combined (A=Hysteresis AND B=Crossover must agree to enter, experimental) ---
Console.WriteLine($"=== Core score option simulation - COMBINED A+B EXPERIMENT (entry-score>={entryScoreThreshold}, fast={crossoverFastMinutes}min, slow={crossoverSlowMinutes}min, strike price in [{entryPriceRangeLow},{entryPriceRangeHigh}]) ===");
foreach (var exitOnEither in new[] { true, false })
{
    var label = exitOnEither ? "exit on EITHER opposite" : "exit only when BOTH opposite";
    Console.WriteLine($"--- Combined, {label} ---");
    var combinedResults = new List<CoreScoreDayResult>();
    foreach (var day in dayData)
    {
        var result = CoreScoreOptionSimulator.SimulateDayCombined(day.StrikeRows, day.CadenceContexts, day.ScoredExpiry,
            new CoreScoreCombinedOptions(EntryScoreThreshold: entryScoreThreshold, FastWindowMinutes: crossoverFastMinutes, SlowWindowMinutes: crossoverSlowMinutes,
                EntryPriceRangeLow: entryPriceRangeLow, EntryPriceRangeHigh: entryPriceRangeHigh, ExitOnEitherOpposite: exitOnEither));
        combinedResults.Add(result);
        PrintDayResult(day.AsOfDate, day.ScoredExpiry, expiryLabel, result);
    }
    PrintOverall($"Combined ({label})", combinedResults);
}

return 0;
