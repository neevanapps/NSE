using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NiftySignal.Trading.Data;

/// <summary>
/// Design-time factory used ONLY by <c>dotnet ef migrations add</c> / <c>migrations script</c>. Those commands build the model and write files; they do
/// not open a connection. To make that checkable, the connection string below points at a host in the reserved <c>.invalid</c> top-level domain (RFC 6761:
/// it can never resolve) and carries no user name or password: if a design-time command ever tried to connect, it would fail instead of reaching a
/// database. Real connections are supplied explicitly by a store (CP6.3), from an environment variable, and are never read here.
/// </summary>
public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public const string PlaceholderConnection = "Host=design-time.invalid;Database=niftysignal_trading_design;Timeout=1;Command Timeout=1";

    public TradingDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(PlaceholderConnection, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .Options);
}
