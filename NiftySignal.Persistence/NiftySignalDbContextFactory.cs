using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NiftySignal.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add` run against this project directly, without spinning up
/// the full Host. Reads the same appsettings/appsettings.Local.json the Host uses at
/// runtime, so migration tooling and the running service never see different connection
/// strings.
/// </summary>
public sealed class NiftySignalDbContextFactory : IDesignTimeDbContextFactory<NiftySignalDbContext>
{
    public NiftySignalDbContext CreateDbContext(string[] args)
    {
        var hostProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "NiftySignal.Host");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(hostProjectPath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("NiftySignalDb")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:NiftySignalDb is not set. Add it to NiftySignal.Host/appsettings.Local.json.");

        var optionsBuilder = new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql(connectionString);

        return new NiftySignalDbContext(optionsBuilder.Options);
    }
}
