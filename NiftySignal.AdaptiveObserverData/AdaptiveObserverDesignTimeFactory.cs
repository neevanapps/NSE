using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NiftySignal.AdaptiveObserverData;

public sealed class AdaptiveObserverDesignTimeFactory : IDesignTimeDbContextFactory<AdaptiveObserverDbContext>
{
    public AdaptiveObserverDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("NiftySignal.Host/appsettings.json", optional: true)
            .AddJsonFile("NiftySignal.Host/appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var baseConnection = config.GetConnectionString("NiftySignalDb")
            ?? "Host=localhost;Database=niftysignal;Username=postgres;Password=postgres";
        var observerConnection = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = AdaptiveObserverDbContext.DatabaseName,
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseNpgsql(observerConnection)
            .Options;

        return new AdaptiveObserverDbContext(options);
    }
}
