using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NiftySignal.BacktestData;
using NiftySignal.Persistence;

// 2026-09-12 design (docs/REVIEW_FINDINGS.md). Usage:
//   dotnet run --project NiftySignal.BacktestData -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [sourceDatabaseNameOverride]
//   dotnet run --project NiftySignal.BacktestData -- 2026-09-08 2026-09-11
//
// Reads real historical ticks/instruments READ-ONLY from the source database (default:
// niftysignal_vm_copy, the local synced VM copy). Writes CadenceContext rows to a brand-new,
// dedicated database (niftysignal_backtest_analysis) -- never the source database, and never
// anything NiftySignal.Persistence's live schema touches.
//
// Idempotent per trading day: a day already present in the destination is skipped entirely.
// The only way to redo a day is to delete its rows first, deliberately, after a real
// calculation mistake has been found and fixed -- this tool never repopulates on its own.
//
// Per standing instruction, this is a tool the user runs themselves -- it is not run
// automatically as part of any other process.
if (args.Length < 2 || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", out var toDate))
{
    Console.Error.WriteLine("Usage: dotnet run --project NiftySignal.BacktestData -- <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [sourceDatabaseNameOverride]");
    return 1;
}

var sourceDatabaseNameOverride = args.Length > 2 ? args[2] : "niftysignal_vm_copy";

// Same sourcing convention as NiftySignal.Backtest/Program.cs and NiftySignal.ScoreReplay --
// reads the real Host connection settings rather than inventing a separate config source.
var hostProjectPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NiftySignal.Host");
var configuration = new ConfigurationBuilder()
    .SetBasePath(hostProjectPath)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
    ?? throw new InvalidOperationException("ConnectionStrings:NiftySignalDb is not set in NiftySignal.Host/appsettings.Local.json.");

var sourceConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = sourceDatabaseNameOverride }.ConnectionString;
var destinationConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = CadencePopulator.BacktestAnalysisDatabaseName }.ConnectionString;

// Plain indexer + manual parse, not GetValue<T> -- avoids pulling in the
// Microsoft.Extensions.Configuration.Binder package for one value.
var riskFreeRate = double.TryParse(configuration["Pricing:RiskFreeRate"], out var configuredRiskFreeRate)
    ? configuredRiskFreeRate
    : 0.065;

var sourceOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(sourceConnectionString).Options;
var destinationOptions = new DbContextOptionsBuilder<BacktestAnalysisDbContext>().UseNpgsql(destinationConnectionString).Options;

await using (var destination = new BacktestAnalysisDbContext(destinationOptions))
{
    // Creates the niftysignal_backtest_analysis database and applies the schema on first run --
    // a no-op on every subsequent run once it's already current.
    await destination.Database.MigrateAsync();
}

var populatedDays = 0;
var skippedDays = 0;

for (var date = fromDate; date <= toDate; date = date.AddDays(1))
{
    await using var source = new NiftySignalDbContext(sourceOptions);
    await using var destination = new BacktestAnalysisDbContext(destinationOptions);

    var result = await CadencePopulator.PopulateDayAsync(source, destination, date, riskFreeRate, CancellationToken.None);

    switch (result.Outcome)
    {
        case PopulationOutcome.Populated:
            Console.WriteLine($"{date:yyyy-MM-dd}: populated {result.RowCount} cadence rows, {result.StrikeRowCount} strike rows, {result.BandRowCount} band rows.");
            populatedDays++;
            break;
        case PopulationOutcome.AlreadyPopulated:
            Console.WriteLine($"{date:yyyy-MM-dd}: already populated -- skipped.");
            skippedDays++;
            break;
        case PopulationOutcome.NoTradableData:
            Console.WriteLine($"{date:yyyy-MM-dd}: no Spot/Future/option data for this date -- skipped.");
            skippedDays++;
            break;
    }
}

Console.WriteLine($"Done. {populatedDays} day(s) populated, {skippedDays} day(s) skipped.");
return 0;
