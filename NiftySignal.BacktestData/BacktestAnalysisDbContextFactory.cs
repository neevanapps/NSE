using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NiftySignal.BacktestData;

/// <summary>
/// Lets `dotnet ef migrations add` run against this project directly, mirroring
/// NiftySignal.Persistence's own NiftySignalDbContextFactory. Reads the same
/// appsettings/appsettings.Local.json Host uses (for the base Postgres server/credentials),
/// but always targets the "niftysignal_backtest_analysis" database name -- never whatever
/// database NiftySignalDb's connection string happens to name.
/// </summary>
public sealed class BacktestAnalysisDbContextFactory : IDesignTimeDbContextFactory<BacktestAnalysisDbContext>
{
    public BacktestAnalysisDbContext CreateDbContext(string[] args)
    {
        var hostProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "NiftySignal.Host");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(hostProjectPath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var baseConnectionString = configuration.GetConnectionString("NiftySignalDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:NiftySignalDb is not set. Add it to NiftySignal.Host/appsettings.Local.json.");

        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = CadencePopulator.BacktestAnalysisDatabaseName,
        }.ConnectionString;

        var optionsBuilder = new DbContextOptionsBuilder<BacktestAnalysisDbContext>()
            .UseNpgsql(connectionString);

        return new BacktestAnalysisDbContext(optionsBuilder.Options);
    }
}
