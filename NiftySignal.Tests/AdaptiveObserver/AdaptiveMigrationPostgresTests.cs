using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// The adaptive observer migration <c>20261028130033_AddSpotAtmRuleAndStrikeGreeks</c> on a REAL PostgreSQL server. The server connection comes ONLY from the environment variable
/// <see cref="AdaptivePg.Variable"/> (never from the repository) and is refused unless it is on the loopback interface and names the maintenance database <c>postgres</c>. Every test creates its OWN uniquely named
/// disposable database (<c>niftysignal_adaptive_mig_test_*</c>), and drops it afterwards; no other database is ever opened, so it can never reach a tick, observer, VM or production database.
/// The tests are skipped (visibly) when the variable is absent.
/// </summary>
public static class AdaptivePg
{
    public const string Variable = "NSE_ADAPTIVE_MIGRATION_TEST_DB";
    public const string Previous = "20261014045007_AddStrikeBookMetricsAndFuturesGrossOfi";
    public const string Latest = "20261028130033_AddSpotAtmRuleAndStrikeGreeks";

    public static string? Maintenance => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } text ? Guard(text) : null;

    public static bool Available => Maintenance is not null;

    static string Guard(string text)
    {
        var b = new NpgsqlConnectionStringBuilder(text);
        if (b.Host is not ("localhost" or "127.0.0.1" or "::1"))
        {
            throw new InvalidOperationException($"{Variable} must point at the local machine; refusing host '{b.Host}'.");
        }

        if (!string.Equals(b.Database, "postgres", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{Variable} must name the maintenance database 'postgres'; refusing '{b.Database}'.");
        }

        return b.ConnectionString;
    }
}

public sealed class PgMigrationFactAttribute : FactAttribute
{
    public PgMigrationFactAttribute()
    {
        if (!AdaptivePg.Available)
        {
            Skip = $"{AdaptivePg.Variable} is not set: the PostgreSQL adaptive migration tests are skipped.";
        }
    }
}

/// <summary>A disposable database created on the local server and dropped on dispose.</summary>
public sealed class TempAdaptiveDatabase : IAsyncDisposable
{
    public string Name { get; }

    public string ConnectionString { get; }

    TempAdaptiveDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }

    public static async Task<TempAdaptiveDatabase> CreateAsync()
    {
        var name = "niftysignal_adaptive_mig_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(AdaptivePg.Maintenance))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(AdaptivePg.Maintenance!) { Database = name, Pooling = false }.ConnectionString;
        return new TempAdaptiveDatabase(name, cs);
    }

    public AdaptiveObserverDbContext Context() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task MigrateAsync(string? target = null)
    {
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>INSERTs one row filling every NOT NULL column without a default by its type (so a historical row can be written against the OLD schema without the current entity model).</summary>
    public async Task InsertFilledAsync(string table, IReadOnlyDictionary<string, object> values)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        var cols = new List<(string Name, string Type, bool Required)>();
        await using (var q = new NpgsqlCommand("SELECT column_name, data_type, (is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO') FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t", c))
        {
            q.Parameters.AddWithValue("t", table);
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                cols.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2)));
            }
        }

        var names = new List<string>();
        var parameters = new List<NpgsqlParameter>();
        foreach (var (name, type, required) in cols)
        {
            if (!values.TryGetValue(name, out var value))
            {
                if (!required)
                {
                    continue;
                }

                value = type switch
                {
                    "bigint" => 0L,
                    "integer" => 0,
                    "smallint" => (short)0,
                    "boolean" => false,
                    "double precision" => 0d,
                    "date" => new DateOnly(2026, 10, 1),
                    "timestamp with time zone" => new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero),
                    "jsonb" => "{}",
                    _ => "x",
                };
            }

            names.Add("\"" + name + "\"");
            var p = new NpgsqlParameter("p" + parameters.Count, value);
            if (type == "jsonb")
            {
                p.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
            }

            parameters.Add(p);
        }

        await using var insert = new NpgsqlCommand($"INSERT INTO {table} ({string.Join(",", names)}) VALUES ({string.Join(",", parameters.Select(p => "@" + p.ParameterName))})", c);
        insert.Parameters.AddRange(parameters.ToArray());
        await insert.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdaptivePg.Maintenance);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class AdaptiveMigrationPostgresTests
{
    static readonly string[] NewStrikeColumns = ["DeltaStart", "DeltaEnd", "DeltaChange", "VegaStart", "VegaEnd", "VegaChange"];

    static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "NiftySignal.AdaptiveObserverData", "Sql", "adaptive-spot-atm-and-strike-greeks.idempotent.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("adaptive-spot-atm-and-strike-greeks.idempotent.sql not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The previous schema with representative HISTORICAL rows: an unstamped adaptive-v1 session and v1 / v2 strike rows of a CE / PE pair across two bars.</summary>
    static async Task<TempAdaptiveDatabase> HistoricalDatabaseAsync()
    {
        var temp = await TempAdaptiveDatabase.CreateAsync();
        await temp.MigrateAsync(AdaptivePg.Previous);
        await temp.InsertFilledAsync("adaptive_sessions", new Dictionary<string, object>
        {
            ["Id"] = 1L, ["ModelVersion"] = "adaptive-v1", ["TradeDate"] = new DateOnly(2026, 10, 14), ["IsHistoricalSeed"] = false, ["FutureToken"] = "FUT", ["EstimatorName"] = "OpeningVolumeProjectionV1",
        });
        foreach (var (version, token, side, bar) in new[]
        {
            ("option-strike-v1", "C23000", 1, 1), ("option-strike-v2", "C23000", 1, 1), ("option-strike-v2", "P23000", 2, 1),
            ("option-strike-v2", "C23000", 1, 2), ("option-strike-v2", "P23000", 2, 2),
        })
        {
            await temp.InsertFilledAsync("adaptive_option_strike_bars", new Dictionary<string, object>
            {
                ["SessionId"] = 1L, ["BarSeq"] = bar, ["MetricsVersion"] = version, ["Strike"] = 23000d, ["Side"] = side, ["Token"] = token, ["TradingSymbol"] = "OPT",
                ["Expiry"] = new DateOnly(2026, 10, 12), ["IvStart"] = 20.5d, ["IvEnd"] = 21.75d, ["IvChange"] = 1.25d, ["PriceStart"] = 100.25d, ["TotalQuantity"] = 12345L,
            });
        }

        // the explicit Id above did not advance the identity sequence: do it, so rows added through EF get fresh ids
        await temp.ExecAsync("SELECT setval(pg_get_serial_sequence('adaptive_sessions', 'Id'), 1)");
        return temp;
    }

    // ------------------------------------------------------------------ 1. empty database -> latest

    [PgMigrationFact]
    public async Task EmptyDatabase_UpgradedToLatest_HasTheExactColumnsTypesAndIndexes_AndNoPendingModelChanges()
    {
        await using var temp = await TempAdaptiveDatabase.CreateAsync();
        await temp.MigrateAsync();

        await using var db = temp.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());                          // the EF model and the migration snapshot agree
        Assert.Equal(1, await temp.ScalarAsync($"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{AdaptivePg.Latest}'"));

        foreach (var column in NewStrikeColumns)
        {
            Assert.Equal(1, await temp.ScalarAsync($"SELECT count(*) FROM information_schema.columns WHERE table_name = 'adaptive_option_strike_bars' AND column_name = '{column}' AND data_type = 'double precision' AND is_nullable = 'YES'"));
        }

        Assert.Equal(1, await temp.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'adaptive_sessions' AND column_name = 'AtmRuleVersion' AND data_type = 'character varying' AND character_maximum_length = 32 AND is_nullable = 'YES'"));
        // the existing uniqueness constraints and the fixed-strike index are still there
        Assert.Equal(1, await temp.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE tablename = 'adaptive_option_strike_bars' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexdef LIKE '%(\"SessionId\", \"BarSeq\", \"Token\", \"MetricsVersion\")%'"));
        Assert.Equal(1, await temp.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE tablename = 'adaptive_option_strike_bars' AND indexdef LIKE '%(\"SessionId\", \"Expiry\", \"Strike\", \"Side\", \"BarSeq\")%'"));
        Assert.Equal(1, await temp.ScalarAsync("SELECT count(*) FROM pg_indexes WHERE tablename = 'adaptive_sessions' AND indexdef LIKE 'CREATE UNIQUE INDEX%' AND indexdef LIKE '%(\"TradeDate\", \"ModelVersion\")%'"));
    }

    // ------------------------------------------------------------------ 2. previous schema with history -> latest

    [PgMigrationFact]
    public async Task HistoricalDatabase_UpgradedToLatest_KeepsEveryRowAndValue_AndReadsThemThroughTheCurrentModel()
    {
        await using var temp = await HistoricalDatabaseAsync();
        Assert.Equal(5, await temp.ScalarAsync("SELECT count(*) FROM adaptive_option_strike_bars"));
        var checksumBefore = await temp.ScalarAsync("SELECT sum(\"TotalQuantity\") + count(*) * 1000 FROM adaptive_option_strike_bars");

        await temp.MigrateAsync();

        await using var db = temp.Context();
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal("adaptive-v1", session.ModelVersion);
        Assert.Null(session.AtmRuleVersion);                                         // NOT relabelled: a historical session stays on the legacy rule
        Assert.Equal(AtmCenterRule.SyntheticForwardLegacy, AtmCenterRules.FromVersion(session.AtmRuleVersion));

        var rows = await db.OptionStrikeBars.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.Token).ThenBy(x => x.MetricsVersion).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(checksumBefore, await temp.ScalarAsync("SELECT sum(\"TotalQuantity\") + count(*) * 1000 FROM adaptive_option_strike_bars"));
        Assert.All(rows, r =>
        {
            Assert.Equal(20.5, r.IvStart); Assert.Equal(21.75, r.IvEnd); Assert.Equal(1.25, r.IvChange); Assert.Equal(100.25, r.PriceStart); Assert.Equal(12345L, r.TotalQuantity);
            Assert.Null(r.DeltaStart); Assert.Null(r.DeltaEnd); Assert.Null(r.DeltaChange);          // no Greeks were fabricated for history
            Assert.Null(r.VegaStart); Assert.Null(r.VegaEnd); Assert.Null(r.VegaChange);
            Assert.Contains(r.MetricsVersion, new[] { "option-strike-v1", "option-strike-v2" });     // versions untouched
        });
        Assert.Equal(1, rows.Count(r => r.MetricsVersion == "option-strike-v1"));
    }

    // ------------------------------------------------------------------ 3/4. new v3 rows, uniqueness

    [PgMigrationFact]
    public async Task V3Rows_WriteAndReadBack_SitBesideHistoricalRows_AndTheUniqueKeyStillRejectsADuplicate()
    {
        await using var temp = await HistoricalDatabaseAsync();
        await temp.MigrateAsync();

        await using (var db = temp.Context())
        {
            db.OptionStrikeBars.Add(V3Row(bar: 1, token: "C23000", side: OptionType.Call, deltaChange: 0.0123456789, vegaChange: -12.3456789));   // beside the v1 and v2 rows of the same bar and token
            await db.SaveChangesAsync();
        }

        await using (var db = temp.Context())
        {
            var stored = await db.OptionStrikeBars.AsNoTracking().SingleAsync(x => x.MetricsVersion == "option-strike-v3");
            Assert.Equal(0.0123456789, stored.DeltaChange);                          // exact double round trip, unscaled
            Assert.Equal(-12.3456789, stored.VegaChange);
            Assert.Equal(0.5, stored.DeltaStart); Assert.Equal(3000d, stored.VegaStart);
            Assert.Equal(3, await db.OptionStrikeBars.CountAsync(x => x.BarSeq == 1 && x.Token == "C23000"));

            db.OptionStrikeBars.Add(V3Row(bar: 1, token: "C23000", side: OptionType.Call, deltaChange: 1, vegaChange: 1));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("23505", ((PostgresException)duplicate.InnerException!).SqlState);          // unique_violation on (SessionId, BarSeq, Token, MetricsVersion)
        }

        await using (var db = temp.Context())
        {
            db.Sessions.Add(Session(day: 15, model: AdaptiveFixedClockV1.ModelVersion, stamp: AtmCenterRules.SpotV1Version));
            await db.SaveChangesAsync();
            Assert.Equal("spot-atm-v1", (await db.Sessions.AsNoTracking().SingleAsync(x => x.ModelVersion == AdaptiveFixedClockV1.ModelVersion)).AtmRuleVersion);
            db.Sessions.Add(Session(day: 15, model: AdaptiveFixedClockV1.ModelVersion, stamp: null));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());                // (TradeDate, ModelVersion) is still unique
        }
    }

    static AdaptiveOptionStrikeBarRow V3Row(int bar, string token, OptionType side, double deltaChange, double vegaChange) => new()
    {
        SessionId = 1, BarSeq = bar, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = new DateTimeOffset(2026, 10, 14, 4, 0, 0, TimeSpan.Zero), Expiry = new DateOnly(2026, 10, 12),
        Strike = 23000, Side = side, Token = token, TradingSymbol = "OPT", IvStart = 20.5, IvEnd = 21.75, IvChange = 1.25,
        DeltaStart = 0.5, DeltaEnd = 0.5 + deltaChange, DeltaChange = deltaChange, VegaStart = 3000, VegaEnd = 3000 + vegaChange, VegaChange = vegaChange,
    };

    static AdaptiveSessionStateRow Session(int day, string model, string? stamp) => new()
    {
        TradeDate = new DateOnly(2026, 10, day), ModelVersion = model, SourceBranch = "t", SourceCommitSha = "t", BuildUtc = DateTimeOffset.UtcNow, FutureToken = "FUT", FutureSymbol = "FUT",
        FutureExpiry = new DateOnly(2026, 10, 27), OpeningWindowStartUtc = DateTimeOffset.UtcNow, OpeningWindowEndUtc = DateTimeOffset.UtcNow, EstimatorName = "E", CreatedAtUtc = DateTimeOffset.UtcNow, AtmRuleVersion = stamp,
    };

    // ------------------------------------------------------------------ the project's own persistence + the dashboard query on mixed history

    [PgMigrationFact]
    public async Task EnginePackage_PersistsAsV3OnPostgres_VerifiesOnReplay_AndTheDashboardQueryReadsHistoricalAndNewRows()
    {
        await using var temp = await HistoricalDatabaseAsync();
        await temp.MigrateAsync();
        var package = SpotAtmRuleTests.OneBar();
        var service = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);

        await using (var db = temp.Context())
        {
            Assert.Equal(SupplementalOutcome.Inserted, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));
        }

        await using (var db = temp.Context())
        {
            Assert.Equal(SupplementalOutcome.Verified, await service.PersistOrVerifyStrikesAsync(db, 1, package, default));          // replay after the write: identical, nothing rewritten
            var ce = await db.OptionStrikeBars.AsNoTracking().SingleAsync(x => x.Token == "C23000" && x.BarSeq == package.FutureBar.BarSeq && x.MetricsVersion == "option-strike-v3");
            var src = package.OptionStrikes!.Single(x => x.Token == "C23000");
            Assert.Equal((src.DeltaStart, src.DeltaEnd, src.DeltaChange, src.VegaStart, src.VegaEnd, src.VegaChange), (ce.DeltaStart, ce.DeltaEnd, ce.DeltaChange, ce.VegaStart, ce.VegaEnd, ce.VegaChange));
            Assert.NotNull(ce.DeltaChange);
        }

        // The Dashboard's real fixed-strike query over MIXED data: bar 1 has v1+v2 history AND the engine's v3 rows (same tokens); bar 2 has v2 history only.
        var catalog = new FixedStrikeCatalog(new DateOnly(2026, 10, 12), [new FixedStrikeContract(23000, "C23000", "OPT", "P23000", "OPT")]);
        var data = new AdaptiveObserverDataService(new TempFactory(temp), NullLogger<AdaptiveObserverDataService>.Instance);
        var history = (await data.LoadFixedStrikeHistoryAsync(1, catalog, 23000, [(1, DateTimeOffset.UtcNow), (2, DateTimeOffset.UtcNow)], default, 0))!;
        var bar1 = history.Rows.Single(r => r.BarSeq == 1);
        var bar2 = history.Rows.Single(r => r.BarSeq == 2);
        var engineCe = package.OptionStrikes!.Single(x => x.Token == "C23000");
        var enginePe = package.OptionStrikes!.Single(x => x.Token == "P23000");
        Assert.Equal("option-strike-v3", bar1.Ce!.MetricsVersion);                   // v3 wins where it exists (history v1 + v2 sit beside it)
        Assert.Equal(engineCe.DeltaChange, bar1.Ce.DeltaChange);
        Assert.Equal("option-strike-v3", bar1.Pe!.MetricsVersion);
        Assert.Equal(enginePe.VegaChange, bar1.Pe.VegaChange);
        var withGreeks = OptionDirectionRatios.Context(bar1.Ce, bar1.Pe);
        Assert.Equal((engineCe.DeltaChange - enginePe.DeltaChange)!.Value, withGreeks.Delta.Direction.Raw!.Value, 12);   // Dir = CE change - PE change, from the stored columns
        Assert.Equal("option-strike-v2", bar2.Ce!.MetricsVersion);                   // historical bars keep their own version
        Assert.Null(bar2.Ce.DeltaChange);
        var ratios = OptionDirectionRatios.Context(bar2.Ce, bar2.Pe);
        Assert.Null(ratios.Delta.Direction.Raw);                                      // unavailable, not zero
    }

    sealed class TempFactory(TempAdaptiveDatabase temp) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => temp.Context();
    }

    // ------------------------------------------------------------------ 5. the idempotent SQL script

    [PgMigrationFact]
    public async Task IdempotentScript_OnThePreviousSchema_AppliesOnce_IsANoOpTheSecondTime_AndMatchesTheEfMigrationResult()
    {
        await using var viaScript = await HistoricalDatabaseAsync();
        var sql = await File.ReadAllTextAsync(ScriptPath());
        await viaScript.ExecAsync(sql);
        await viaScript.ExecAsync(sql);                                              // twice: idempotent

        Assert.Equal(1, await viaScript.ScalarAsync($"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{AdaptivePg.Latest}'"));
        Assert.Equal(5, await viaScript.ScalarAsync("SELECT count(*) FROM adaptive_option_strike_bars"));

        await using var viaEf = await HistoricalDatabaseAsync();
        await viaEf.MigrateAsync();
        const string columns = "SELECT table_name || '.' || column_name || ':' || data_type || ':' || coalesce(character_maximum_length::text, '') || ':' || is_nullable FROM information_schema.columns WHERE table_schema = 'public' ORDER BY 1";
        Assert.Equal(await ColumnsAsync(viaEf, columns), await ColumnsAsync(viaScript, columns));       // the script and the EF migration produce the same schema

        await viaScript.MigrateAsync();                                              // EF sees the migration already recorded: nothing pending, nothing re-applied
        await using var db = viaScript.Context();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [PgMigrationFact]
    public async Task IdempotentScript_OnAnAlreadyUpToDateDatabase_ChangesNothing()
    {
        await using var temp = await HistoricalDatabaseAsync();
        await temp.MigrateAsync();
        var before = await ColumnsAsync(temp, "SELECT column_name FROM information_schema.columns WHERE table_name = 'adaptive_option_strike_bars' ORDER BY 1");
        await temp.ExecAsync(await File.ReadAllTextAsync(ScriptPath()));
        Assert.Equal(before, await ColumnsAsync(temp, "SELECT column_name FROM information_schema.columns WHERE table_name = 'adaptive_option_strike_bars' ORDER BY 1"));
        Assert.Equal(1, await temp.ScalarAsync($"SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{AdaptivePg.Latest}'"));
    }

    static async Task<string> ColumnsAsync(TempAdaptiveDatabase temp, string sql)
    {
        await using var c = new NpgsqlConnection(temp.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(r.GetString(0));
        }

        return string.Join("\n", list);
    }

    // ------------------------------------------------------------------ 6. rollback

    [PgMigrationFact]
    public async Task Rollback_ToThePreviousMigration_DropsOnlyTheNewColumns_AndKeepsHistoricalRows()
    {
        await using var temp = await HistoricalDatabaseAsync();
        await temp.MigrateAsync();
        await using (var db = temp.Context())
        {
            db.OptionStrikeBars.Add(V3Row(bar: 1, token: "C23000", side: OptionType.Call, deltaChange: 0.02, vegaChange: 5));
            await db.SaveChangesAsync();
        }

        await temp.MigrateAsync(AdaptivePg.Previous);                                // Down: the new columns (and the v3 data in them) go, history stays

        Assert.Equal(0, await temp.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'adaptive_option_strike_bars' AND column_name IN ('DeltaStart','DeltaEnd','DeltaChange','VegaStart','VegaEnd','VegaChange')"));
        Assert.Equal(0, await temp.ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name = 'adaptive_sessions' AND column_name = 'AtmRuleVersion'"));
        Assert.Equal(6, await temp.ScalarAsync("SELECT count(*) FROM adaptive_option_strike_bars"));          // 5 historical + the v3 row (its Greek values are gone, the row stays)
        Assert.Equal(5, await temp.ScalarAsync("SELECT count(*) FROM adaptive_option_strike_bars WHERE \"MetricsVersion\" IN ('option-strike-v1','option-strike-v2')"));
        Assert.Equal(1, await temp.ScalarAsync("SELECT count(*) FROM adaptive_sessions"));
    }

    // ------------------------------------------------------------------ housekeeping

    [PgMigrationFact]
    public async Task NoDisposableDatabaseIsLeftBehindByThesePostgresTests()
    {
        // The tests of one class run one after another, so every database a previous test created must be gone by now.
        await using var admin = new NpgsqlConnection(AdaptivePg.Maintenance);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname LIKE 'niftysignal_adaptive_mig_test_%'", admin);
        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ 7. replay and recovery on PostgreSQL

    [PgMigrationFact]
    public async Task Recovery_OnPostgres_ReplaysBySessionRule_VerifiesAfterARestart_AndKeepsHistoricalSessionsOnTheLegacyRule()
    {
        await using var temp = await HistoricalDatabaseAsync();
        await temp.MigrateAsync();
        await using var source = SpotAtmRecoveryTests.Source();

        // a NEW stamped session and a HISTORICAL-style unstamped one, recovered from the same ticks
        var stamped = SpotAtmRecoveryTests.Context(AtmCenterRules.SpotV1Version);
        stamped.Session.Id = 10;
        stamped.Session.TradeDate = new DateOnly(2026, 9, 25);
        var legacy = SpotAtmRecoveryTests.Context(atmRule: null);
        legacy.Session.Id = 11;
        legacy.Session.TradeDate = new DateOnly(2026, 9, 25);
        legacy.Session.ModelVersion = "adaptive-fixed13650-v1";                       // a second model on the same day (unique key is (TradeDate, ModelVersion))

        await using (var db = temp.Context())
        {
            await SpotAtmRecoveryTests.SeedSessionAsync(db, stamped);
            await SpotAtmRecoveryTests.SeedSessionAsync(db, legacy);
        }

        var through = SpotAtmRecoveryTests.At0.AddSeconds(10);
        await using (var db = temp.Context())
        {
            await SpotAtmRecoveryTests.ServiceWithSidecars().RecoverAsync(source, db, stamped, through, default);
        }

        await using (var db = temp.Context())
        {
            await SpotAtmRecoveryTests.Service().RecoverAsync(source, db, legacy, through, default);
        }

        await using (var db = temp.Context())
        {
            var spotCenters = (await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == 10 && x.Side == OptionType.Call).OrderBy(x => x.BarSeq).ToListAsync()).Select(x => x.CenterStrike).ToList();
            var legacyCenters = (await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == 11 && x.Side == OptionType.Call).OrderBy(x => x.BarSeq).ToListAsync()).Select(x => x.CenterStrike).ToList();
            Assert.True(spotCenters.Count >= 3 && legacyCenters.Count >= 3, $"band rows: spot {spotCenters.Count}, legacy {legacyCenters.Count}");
            Assert.Equal([23000d, 23050d, 23050d], spotCenters.Take(3));              // by the spot, persisted in PostgreSQL
            Assert.All(legacyCenters, c => Assert.Equal(23000d, c));                  // by the forward
            Assert.Equal("NIFTYSPOT", (await db.SessionSupplemental.AsNoTracking().SingleAsync(x => x.SessionId == 10)).SpotToken);
            Assert.True(await db.OptionStrikeBars.AsNoTracking().AnyAsync(x => x.SessionId == 10 && x.MetricsVersion == "option-strike-v3" && x.DeltaStart != null), "no v3 row with Greeks was persisted by recovery");
        }

        // a restart: the persisted bars are verified against the recomputed ones (strict core parity) and nothing is rewritten
        await using (var db = temp.Context())
        {
            var barsBefore = await db.FutureBars.CountAsync(x => x.SessionId == 10);
            var restarted = await SpotAtmRecoveryTests.ServiceWithSidecars().RecoverAsync(source, db, stamped, through, default);
            Assert.True(restarted.ReconciledBars > 0, $"reconciled bars: {restarted.ReconciledBars}");
            Assert.Equal(barsBefore, await db.FutureBars.CountAsync(x => x.SessionId == 10));
        }

        // the historical session of the previous schema was never touched by any of this
        await using (var db = temp.Context())
        {
            var history = await db.Sessions.AsNoTracking().SingleAsync(x => x.Id == 1);
            Assert.Null(history.AtmRuleVersion);
            Assert.Equal(5, await db.OptionStrikeBars.CountAsync(x => x.SessionId == 1));
        }
    }
}
