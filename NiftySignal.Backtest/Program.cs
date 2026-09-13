using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NiftySignal.Backtest;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;

// Audit finding F32's runner. Usage: dotnet run --project NiftySignal.Backtest --
// <fromDate> <toDate> [databaseNameOverride] [--score-mode=composite|ratio-level|ratio-momentum]
//   dotnet run --project NiftySignal.Backtest -- 2026-09-08 2026-09-11
//   dotnet run --project NiftySignal.Backtest -- 2026-09-11 2026-09-11 niftysignal_vm_copy --score-mode=ratio-momentum
//
// Loads the REAL, currently-live RulesetConfig and ScoreWeights from NiftySignal.Host's own
// appsettings -- the point is validating the rules actually running live against real
// historical data, not a test fixture's defaults. Reads real ticks/instruments read-only from
// the database named by databaseNameOverride (default: the local synced VM copy, same as
// NiftySignal.ScoreReplay's own default -- see scripts/sync-vm-database.ps1). Writes every
// trial ScoreSnapshot/PaperTrade this run produces to a throwaway EF InMemoryDatabase -- never
// the real database above.
//
// --score-mode (2026-09-11) opts into BacktestRunner's explicit, backtest-only score
// substitution -- see BacktestScoreSource's own doc comment for why this can never affect the
// live system. Omit it (the default, "composite") for the original composite, exactly as live
// trades on today. "ratio-level" substitutes RatioCompositeScore; "ratio-momentum" (audit
// finding F55) substitutes RatioMomentum (fast-minus-slow ratio composite).
//
// --min-abs-score=N / --exit-below-abs=N (2026-09-11) override the loaded RulesetConfig's
// Entry.MinAbsScore / Exit.ExitOnScoreBelowAbs for this run only -- a deliberately narrow,
// explicit override for testing recalibrated thresholds against real data (e.g. the ratio
// composite's own distribution is nothing like the original composite's, so its thresholds need
// their own derivation, not a reused constant -- see docs/REVIEW_FINDINGS.md's 2026-09-11
// section). Never writes back to appsettings; the real live config is untouched.
var scoreSource = args.FirstOrDefault(a => a.StartsWith("--score-mode=", StringComparison.OrdinalIgnoreCase)) is { } scoreModeArg
    ? scoreModeArg["--score-mode=".Length..].ToLowerInvariant() switch
    {
        "composite" => BacktestScoreSource.OriginalComposite,
        "ratio-level" => BacktestScoreSource.RatioLevel,
        "ratio-momentum" => BacktestScoreSource.RatioMomentum,
        "dynamic-hybrid" => BacktestScoreSource.DynamicHybrid,
        var other => throw new ArgumentException($"Unknown --score-mode '{other}' -- expected composite, ratio-level, ratio-momentum, or dynamic-hybrid."),
    }
    : BacktestScoreSource.OriginalComposite;
// --entry-rank-cutoff=N / --exit-rank-cutoff=N (2026-09-11, dynamic-hybrid only): the one
// remaining parameter BacktestScoreSource.DynamicHybrid has -- percentile cutoffs (0-100) against
// the ratio level's own session-so-far distribution, not score magnitudes. Defaults live on
// BacktestRunner itself (85/50); overridable here for the same reason every other threshold in
// this tool is.
var entryRankCutoffOverride = args.FirstOrDefault(a => a.StartsWith("--entry-rank-cutoff=", StringComparison.OrdinalIgnoreCase)) is { } entryRankArg
    ? double.Parse(entryRankArg["--entry-rank-cutoff=".Length..])
    : (double?)null;
var exitRankCutoffOverride = args.FirstOrDefault(a => a.StartsWith("--exit-rank-cutoff=", StringComparison.OrdinalIgnoreCase)) is { } exitRankArg
    ? double.Parse(exitRankArg["--exit-rank-cutoff=".Length..])
    : (double?)null;
var minAbsScoreOverride = args.FirstOrDefault(a => a.StartsWith("--min-abs-score=", StringComparison.OrdinalIgnoreCase)) is { } minAbsArg
    ? double.Parse(minAbsArg["--min-abs-score=".Length..])
    : (double?)null;
var exitBelowAbsOverride = args.FirstOrDefault(a => a.StartsWith("--exit-below-abs=", StringComparison.OrdinalIgnoreCase)) is { } exitBelowArg
    ? double.Parse(exitBelowArg["--exit-below-abs=".Length..])
    : (double?)null;
// --sustained-seconds=N / --sustained-cadences=N (2026-09-11): same override pattern as the two
// above, for EntryConfig.MinScoreSustainedSeconds/MinScoreSustainedCadences -- testing whether
// momentum mode's entries are being filtered out by the sustain requirement (kept unchanged from
// the original composite's own 45s/3-cadence calibration when F55 was built) rather than by
// MinAbsScore itself.
var sustainedSecondsOverride = args.FirstOrDefault(a => a.StartsWith("--sustained-seconds=", StringComparison.OrdinalIgnoreCase)) is { } sustainSecArg
    ? int.Parse(sustainSecArg["--sustained-seconds=".Length..])
    : (int?)null;
var sustainedCadencesOverride = args.FirstOrDefault(a => a.StartsWith("--sustained-cadences=", StringComparison.OrdinalIgnoreCase)) is { } sustainCadArg
    ? int.Parse(sustainCadArg["--sustained-cadences=".Length..])
    : (int?)null;
// --max-concurrent-positions=N (2026-09-11): same override pattern as everything above, for
// Capital.MaxConcurrentPositions. Real live config runs 3 (sized for one strategy trading alone);
// user's ask is 1 for backtests going forward, reserving >1 for a future multi-strategy paper
// trading design running several strategies in parallel, not this single strategy stacking
// several of its own overlapping positions.
var maxConcurrentPositionsOverride = args.FirstOrDefault(a => a.StartsWith("--max-concurrent-positions=", StringComparison.OrdinalIgnoreCase)) is { } maxConcurrentArg
    ? int.Parse(maxConcurrentArg["--max-concurrent-positions=".Length..])
    : (int?)null;
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

if (positional.Length < 2 || !DateOnly.TryParseExact(positional[0], "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(positional[1], "yyyy-MM-dd", out var toDate))
{
    Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.Backtest -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [databaseNameOverride] [--score-mode=composite|ratio-level|ratio-momentum|dynamic-hybrid] [--min-abs-score=N] [--exit-below-abs=N] [--sustained-seconds=N] [--sustained-cadences=N] [--entry-rank-cutoff=N] [--exit-rank-cutoff=N] [--max-concurrent-positions=N] [--dump-scores=HH:mm-HH:mm] [--print-percentiles]");
    return 1;
}

var databaseNameOverride = positional.Length > 2 ? positional[2] : "niftysignal_vm_copy";

// Same sourcing as NiftySignal.ScoreReplay/Program.cs: AppContext.BaseDirectory, not
// Directory.GetCurrentDirectory() -- this tool is commonly launched via the built DLL directly.
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

var rulesetOptions = configuration.GetSection(RulesetConfigOptions.SectionName).Get<RulesetConfigOptions>() ?? new RulesetConfigOptions();
var rulesetConfig = rulesetOptions.ToRulesetConfig();
var rulesetErrors = RulesetConfigValidator.Validate(rulesetConfig);
if (rulesetErrors.Count > 0)
{
    Console.Error.WriteLine($"Real RulesetConfig failed validation -- refusing to backtest against an invalid config: {string.Join("; ", rulesetErrors)}");
    return 1;
}

var scoreWeightsOptionsRaw = configuration.GetSection(ScoreWeightsOptions.SectionName).Get<ScoreWeightsOptions>() ?? new ScoreWeightsOptions();
var scoreWeights = scoreWeightsOptionsRaw.ToScoreWeights();
var scoreWeightsErrors = ScoreWeightsValidator.Validate(scoreWeights);
if (scoreWeightsErrors.Count > 0)
{
    Console.Error.WriteLine($"Real ScoreWeights failed validation -- refusing to backtest against invalid weights: {string.Join("; ", scoreWeightsErrors)}");
    return 1;
}

if (minAbsScoreOverride is { } minAbsOverride)
{
    rulesetConfig = rulesetConfig with { Entry = rulesetConfig.Entry with { MinAbsScore = minAbsOverride } };
}

if (exitBelowAbsOverride is { } exitBelowOverride)
{
    rulesetConfig = rulesetConfig with { Exit = rulesetConfig.Exit with { ExitOnScoreBelowAbs = exitBelowOverride } };
}

if (sustainedSecondsOverride is { } sustainedSecondsValue)
{
    rulesetConfig = rulesetConfig with { Entry = rulesetConfig.Entry with { MinScoreSustainedSeconds = sustainedSecondsValue } };
}

if (sustainedCadencesOverride is { } sustainedCadencesValue)
{
    rulesetConfig = rulesetConfig with { Entry = rulesetConfig.Entry with { MinScoreSustainedCadences = sustainedCadencesValue } };
}

if (maxConcurrentPositionsOverride is { } maxConcurrentValue)
{
    rulesetConfig = rulesetConfig with { Capital = rulesetConfig.Capital with { MaxConcurrentPositions = maxConcurrentValue } };
}

var thresholdSummary = scoreSource == BacktestScoreSource.DynamicHybrid
    ? $"MinAbsScore/ExitOnScoreBelowAbs computed fresh per cadence (entryRankCutoff={entryRankCutoffOverride ?? 85.0}, exitRankCutoff={exitRankCutoffOverride ?? 50.0}) -- see --dump-scores to inspect"
    : $"MinAbsScore={rulesetConfig.Entry.MinAbsScore}, ExitOnScoreBelowAbs={rulesetConfig.Exit.ExitOnScoreBelowAbs}";
Console.WriteLine($"Backtesting {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd} against database '{databaseNameOverride}', ruleset '{rulesetConfig.RulesetVersion}', weights '{scoreWeights.Version}', score={scoreSource}, {thresholdSummary}, MinScoreSustainedSeconds={rulesetConfig.Entry.MinScoreSustainedSeconds}, MinScoreSustainedCadences={rulesetConfig.Entry.MinScoreSustainedCadences}, MaxConcurrentPositions={rulesetConfig.Capital.MaxConcurrentPositions}.");

await using var realTicksDb = new NiftySignalDbContext(
    new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connectionStringBuilder.ConnectionString).Options);

// Isolated, throwaway store for this run's own trial writes -- a fresh name per process so two
// backtest runs (or a run alongside anything else in-process) can never share state.
var backtestDbName = $"backtest-{Guid.NewGuid()}";
var services = new ServiceCollection();
services.AddDbContext<NiftySignalDbContext>(o => o.UseInMemoryDatabase(backtestDbName));
await using var provider = services.BuildServiceProvider();

var dashboardPush = new DashboardPushClient(Options.Create(new DashboardPushOptions()), NullLogger<DashboardPushClient>.Instance);
// MutableRulesetOptions always, not just for dynamic-hybrid -- BacktestRunner only ever writes to
// it in DynamicHybrid mode, so every other mode behaves exactly as FixedOptions would, but this
// way LiveTradingEngine is constructed identically regardless of mode.
var mutableRulesetOptions = new MutableRulesetOptions(rulesetConfig);
var tradingEngine = new LiveTradingEngine(
    provider.GetRequiredService<IServiceScopeFactory>(),
    new NoOpTelegramNotifier(),
    dashboardPush,
    mutableRulesetOptions,
    NullLogger<LiveTradingEngine>.Instance);

var runner = new BacktestRunner(
    realTicksDb,
    provider.GetRequiredService<IServiceScopeFactory>(),
    tradingEngine,
    new FixedOptions<ScoreWeights>(scoreWeights),
    scoreSource: scoreSource,
    mutableRulesetOptions: mutableRulesetOptions,
    dynamicEntryRankCutoff: entryRankCutoffOverride ?? 85.0,
    dynamicExitRankCutoff: exitRankCutoffOverride ?? 50.0);

var result = await runner.RunAsync(fromDate, toDate, CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Computed {result.TotalCadences} cadences, {result.ScoredCadences} produced a composite score.");
Console.WriteLine();

var perf = result.Performance;
Console.WriteLine("Performance report:");
Console.WriteLine($"  Total trades: {perf.TotalTrades}");
Console.WriteLine($"  Win rate: {perf.WinRatePct:F1}%  (rolling 20: {perf.RollingWinRate20Pct?.ToString("F1") ?? "n/a"}%, rolling 50: {perf.RollingWinRate50Pct?.ToString("F1") ?? "n/a"}%)");
Console.WriteLine($"  Avg win: {perf.AverageWin:F2}  Avg loss: {perf.AverageLoss:F2}  Profit factor: {perf.ProfitFactor:F2}");
Console.WriteLine($"  Net P&L: {perf.NetPnl:F2}  Max drawdown: {perf.MaxDrawdown:F2} ({perf.MaxDrawdownPct:F1}%)");
Console.WriteLine($"  Sharpe-like ratio: {perf.SharpeRatio?.ToString("F2") ?? "n/a"}  Trades/day: {perf.TradesPerDay:F1}");

if (result.Trades.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Per-trade breakdown:");
    var istOffset = TimeSpan.FromHours(5.5);
    foreach (var trade in result.Trades)
    {
        var entryIst = trade.EntryTime.ToOffset(istOffset);
        var exitDesc = trade.ExitTime is { } exitTime
            ? $"exit {exitTime.ToOffset(istOffset):MM-dd HH:mm:ss} @ {trade.ExitPrice:F2} ({trade.ExitReason}) netPnl={trade.NetPnl:F2}"
            : "still open at end of range";
        Console.WriteLine($"  {entryIst:MM-dd HH:mm:ss} {trade.TradingSymbol} {trade.Direction} entry @ {trade.EntryPrice:F2} -> {exitDesc}");
    }
}

// --dump-scores=HH:mm-HH:mm (2026-09-11, audit finding F55): prints every cadence's fast/slow/
// momentum ratio-composite reading in the given IST time window -- for exactly this kind of "what
// did the score actually do around this moment" question, since none of this (unlike the real
// ticks database) lands anywhere queryable after the process exits.
if (args.FirstOrDefault(a => a.StartsWith("--dump-scores=", StringComparison.OrdinalIgnoreCase)) is { } dumpArg)
{
    var range = dumpArg["--dump-scores=".Length..].Split('-');
    var dumpFrom = TimeOnly.ParseExact(range[0], "HH:mm");
    var dumpTo = TimeOnly.ParseExact(range[1], "HH:mm");
    var istOffsetForDump = TimeSpan.FromHours(5.5);

    Console.WriteLine();
    Console.WriteLine($"Score dump, {range[0]}-{range[1]} IST:");
    Console.WriteLine("  time     fast    slow    momentum");
    foreach (var s in result.Snapshots)
    {
        var atIst = TimeOnly.FromDateTime(s.ComputedAt.ToOffset(istOffsetForDump).DateTime);
        if (atIst < dumpFrom || atIst > dumpTo)
        {
            continue;
        }

        Console.WriteLine($"  {atIst:HH:mm:ss}  {s.RatioCompositeScoreFast?.ToString("F1") ?? "-",6}  {s.RatioCompositeScore?.ToString("F1") ?? "-",6}  {s.RatioMomentum?.ToString("F1") ?? "-",8}");
    }
}

// --print-percentiles (2026-09-11, audit finding F55): derives a candidate MinAbsScore from the
// active score's OWN real distribution this run just produced, instead of guessing one -- same
// p90/p92.5-ish methodology F13's original MinAbsScore was derived with. Reads directly from
// result.Snapshots (the in-memory backtest store), since that data never lands anywhere durable
// once this process exits.
if (args.Any(a => a.Equals("--print-percentiles", StringComparison.OrdinalIgnoreCase)))
{
    Func<NiftySignal.Domain.Entities.ScoreSnapshot, double?> selector = scoreSource switch
    {
        BacktestScoreSource.RatioLevel => s => s.RatioCompositeScore,
        BacktestScoreSource.RatioMomentum => s => s.RatioMomentum,
        _ => s => s.CompositeScore,
    };
    var values = result.Snapshots.Select(selector).Where(v => v is not null).Select(v => Math.Abs(v!.Value)).OrderBy(v => v).ToList();

    Console.WriteLine();
    if (values.Count == 0)
    {
        Console.WriteLine("Percentiles: no non-null values for the active score -- nothing to derive from.");
    }
    else
    {
        double Percentile(double p)
        {
            var rank = p / 100.0 * (values.Count - 1);
            var lower = (int)Math.Floor(rank);
            var upper = (int)Math.Ceiling(rank);
            return lower == upper ? values[lower] : values[lower] + ((values[upper] - values[lower]) * (rank - lower));
        }

        Console.WriteLine($"|{scoreSource}| percentiles over {values.Count} warmed-up cadences:");
        Console.WriteLine($"  p50={Percentile(50):F2}  p75={Percentile(75):F2}  p90={Percentile(90):F2}  p92.5={Percentile(92.5):F2}  p95={Percentile(95):F2}  max={values[^1]:F2}");
    }
}

return 0;
