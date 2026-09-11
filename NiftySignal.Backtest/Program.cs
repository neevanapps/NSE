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

// Audit finding F32's runner. Usage: dotnet run --project NiftySignal.Backtest -- <fromDate>
// <toDate> [databaseNameOverride]
//   dotnet run --project NiftySignal.Backtest -- 2026-09-08 2026-09-11
//
// Loads the REAL, currently-live RulesetConfig and ScoreWeights from NiftySignal.Host's own
// appsettings -- the point is validating the rules actually running live against real
// historical data, not a test fixture's defaults. Reads real ticks/instruments read-only from
// the database named by databaseNameOverride (default: the local synced VM copy, same as
// NiftySignal.ScoreReplay's own default -- see scripts/sync-vm-database.ps1). Writes every
// trial ScoreSnapshot/PaperTrade this run produces to a throwaway EF InMemoryDatabase -- never
// the real database above.
if (args.Length < 2 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var toDate))
{
    Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.Backtest -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [databaseNameOverride]");
    return 1;
}

var databaseNameOverride = args.Length > 2 ? args[2] : "niftysignal_vm_copy";

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

Console.WriteLine($"Backtesting {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd} against database '{databaseNameOverride}', ruleset '{rulesetConfig.RulesetVersion}', weights '{scoreWeights.Version}'.");

await using var realTicksDb = new NiftySignalDbContext(
    new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connectionStringBuilder.ConnectionString).Options);

// Isolated, throwaway store for this run's own trial writes -- a fresh name per process so two
// backtest runs (or a run alongside anything else in-process) can never share state.
var backtestDbName = $"backtest-{Guid.NewGuid()}";
var services = new ServiceCollection();
services.AddDbContext<NiftySignalDbContext>(o => o.UseInMemoryDatabase(backtestDbName));
await using var provider = services.BuildServiceProvider();

var dashboardPush = new DashboardPushClient(Options.Create(new DashboardPushOptions()), NullLogger<DashboardPushClient>.Instance);
var tradingEngine = new LiveTradingEngine(
    provider.GetRequiredService<IServiceScopeFactory>(),
    new NoOpTelegramNotifier(),
    dashboardPush,
    new FixedOptions<RulesetConfig>(rulesetConfig),
    NullLogger<LiveTradingEngine>.Instance);

var runner = new BacktestRunner(
    realTicksDb,
    provider.GetRequiredService<IServiceScopeFactory>(),
    tradingEngine,
    new FixedOptions<ScoreWeights>(scoreWeights));

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

return 0;
