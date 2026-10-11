using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Trading.Data;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>
/// The local PostgreSQL test database. The connection string comes ONLY from the environment variable <see cref="Variable"/> (never from the repository, never
/// committed). A test is skipped when it is absent, and the database is refused unless it is on the loopback interface and is named <c>niftysignal_trading_test</c>:
/// these tests drop and recreate the whole trading schema, so they can never point at a tick, observer, VM or production database.
/// </summary>
public static class PostgresTestDatabase
{
    public const string Variable = "NSE_TRADING_TEST_DB";
    public const string RequiredDatabase = "niftysignal_trading_test";

    public static string? ConnectionString => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } text ? Guard(text) : null;

    public static bool Available => ConnectionString is not null;

    public static string Guard(string text)
    {
        var builder = new NpgsqlConnectionStringBuilder(text);
        if (builder.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException($"{Variable} must point at the local machine; refusing host '{builder.Host}'.");
        }

        if (!string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{Variable} must name the database '{RequiredDatabase}'; refusing '{builder.Database}'.");
        }

        return builder.ConnectionString;
    }

    public static DbContextOptions<TradingDbContext> Options()
        => new DbContextOptionsBuilder<TradingDbContext>()
            .UseNpgsql(ConnectionString ?? throw new InvalidOperationException($"{Variable} is not set."), npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", TradingDbContext.SchemaName))
            .Options;

    public static TradingDbContext NewContext() => new(Options());

    /// <summary>Drops the whole trading schema and applies the reviewed migrations to a fresh one (test code only: production code never migrates implicitly).</summary>
    public static async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {TradingDbContext.SchemaName} CASCADE", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }
}

/// <summary>A fact that is skipped (visibly) when the local test database is not configured.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestDatabase.Available)
        {
            Skip = $"{PostgresTestDatabase.Variable} is not set: the local PostgreSQL test database tests are skipped.";
        }
    }
}
