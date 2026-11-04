using Npgsql;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>The durable opportunity log on a disposable PostgreSQL schema: idempotent append, replay, fail-safe when the owner-run script has not been applied, and backward compatibility of the additive migration.</summary>
[Collection("PostgresTradingTests")]
public class OpportunityStoreTests
{
    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static async Task<long> CountAsync(NpgsqlDataSource db, string sql = "SELECT count(*) FROM pev2.pev2_opportunities")
    {
        await using var c = db.CreateCommand(sql);
        return (long)(await c.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task ADayIsPersisted_ExactlyOncePerOpportunityId_AndAReplayAddsNothing()
    {
        var db = await FreshAsync();
        var store = new PostgresOpportunityStore(db, TimeProvider.System);
        var first = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), null, store);
        ExecFixtures.Run(first, ExecutionTests.Day());
        var rows = await CountAsync(db);
        Assert.Equal(first.Opportunities.Count, rows);
        Assert.True(rows > 0);

        var restart = new PostgresOpportunityStore(db, TimeProvider.System);
        var replay = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), null, restart);
        ExecFixtures.Run(replay, ExecutionTests.Day());                                              // the journal replay after a restart
        Assert.Equal(rows, await CountAsync(db));
        Assert.Equal((0L, (long)replay.Opportunities.Count), (restart.Written, restart.Duplicates));
        Assert.Equal(0L, await CountAsync(db, "SELECT count(*) - count(DISTINCT opportunity_id) FROM pev2.pev2_opportunities"));
        Assert.Equal(0L, await CountAsync(db, "SELECT count(*) FROM pev2.pev2_opportunities WHERE category NOT BETWEEN 1 AND 9"));
    }

    [PostgresFact]
    public async Task AMissingTable_DisablesTheStoreAfterTheFirstFailure_AndNeverTouchesTheLedger()
    {
        var db = await FreshAsync();
        await using (var drop = db.CreateCommand("DROP TABLE pev2.pev2_opportunities")) await drop.ExecuteNonQueryAsync();
        Assert.False(await ObservabilitySchema.HasAsync(db, "pev2_opportunities", default));
        var store = new PostgresOpportunityStore(db, TimeProvider.System);
        var plain = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options());
        ExecFixtures.Run(plain, ExecutionTests.Day());
        var engine = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), null, store);
        ExecFixtures.Run(engine, ExecutionTests.Day());
        Assert.Equal(plain.Events, engine.Events);                                                    // identical fills and decisions
        Assert.Equal(1, store.Failures);                                                              // ONE failed statement, not one per bar
        Assert.Contains("AddObservability", store.DisabledReason, StringComparison.Ordinal);
        Assert.Equal(1, engine.OpportunitySinkFailures);
    }

    [PostgresFact]
    public async Task TheMigration_IsAdditive_TheSchemaVersionIsUnchanged_AndAnOldDatabaseStillPassesTheStartupCheck()
    {
        var db = await FreshAsync();
        Assert.True((await V2SchemaCheck.CheckAsync(db, default)).Compatible);
        await using (var drop = db.CreateCommand("DROP TABLE pev2.pev2_opportunities; DROP TABLE pev2.pev2_live_marks; DELETE FROM pev2.__pev2_migrations_history WHERE \"MigrationId\" LIKE '%AddObservability'")) await drop.ExecuteNonQueryAsync();
        Assert.True((await V2SchemaCheck.CheckAsync(db, default)).Compatible);                       // a database without the optional tables is still compatible
        Assert.Equal(2, V2Schema.CurrentVersion);                                                     // the version was not bumped: an older Host also keeps running on a migrated database
        await using var again = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await again.ExecuteNonQueryAsync();                                                            // re-running the reviewed script is idempotent and restores the tables
        Assert.True(await ObservabilitySchema.HasAsync(db, "pev2_opportunities", default));
        Assert.True(await ObservabilitySchema.HasAsync(db, "pev2_live_marks", default));
    }
}
