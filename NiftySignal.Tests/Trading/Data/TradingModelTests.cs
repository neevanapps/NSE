using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>The trading model without a database: the shape of every table, key, index, constraint and the migration that creates them.</summary>
public class TradingModelTests
{
    private static TradingDbContext NewContext()
        => new(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(TradingDbContextFactory.PlaceholderConnection, n => n.MigrationsHistoryTable("__ef_migrations_history", TradingDbContext.SchemaName)).Options);

    private static IReadOnlyList<IEntityType> Entities()
    {
        using var db = NewContext();
        return db.GetService<IDesignTimeModel>().Model.GetEntityTypes().ToList();
    }

    private static IEntityType Entity(string table) => Entities().Single(e => e.GetTableName() == table);

    [Fact]
    public void EveryTable_LivesInTheTradingSchema_WithSnakeCaseNamesAndPrimaryKeys()
    {
        var entities = Entities();

        Assert.NotEmpty(entities);
        Assert.All(entities, e =>
        {
            Assert.Equal("trading", e.GetSchema());
            Assert.Matches("^[a-z_]+$", e.GetTableName()!);
            Assert.NotNull(e.FindPrimaryKey());
            Assert.StartsWith("pk_", e.FindPrimaryKey()!.GetName(), StringComparison.Ordinal);
            Assert.All(e.GetProperties(), p => Assert.Matches("^[a-z0-9_]+$", p.GetColumnName()));
        });
        Assert.Equal(new[]
        {
            "accounts", "audit_entries", "checkpoints", "control_applications", "control_journal", "engine_outputs", "fills", "final_lines", "final_records", "ledger_entries", "orders",
            "position_snapshots", "positions", "recovery_log", "rejections", "risk_checkpoints", "run_owner", "runs", "schema_versions", "step_digests", "steps", "variants",
        }, entities.Select(e => e.GetTableName()!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TheVariantIdentity_IsStrategyPlusVariant_InEveryTableThatOwnsAVariant()
    {
        var variants = Entity("variants").FindPrimaryKey()!.Properties.Select(p => p.GetColumnName()).ToArray();
        Assert.Equal(new[] { "run_id", "strategy_id", "variant_id" }, variants);

        foreach (var table in new[] { "engine_outputs", "positions", "position_snapshots", "orders", "fills", "audit_entries", "rejections" })
        {
            var columns = Entity(table).GetProperties().Select(p => p.GetColumnName()).ToList();
            Assert.Contains("strategy_id", columns);
            Assert.Contains("variant_id", columns);
        }

        // ownership foreign keys point at the composite identity, never at VariantId alone
        foreach (var table in new[] { "positions", "orders", "fills" })
        {
            var toVariants = Entity(table).GetForeignKeys().Single(fk => fk.PrincipalEntityType.GetTableName() == "variants");
            Assert.Equal(new[] { "run_id", "strategy_id", "variant_id" }, toVariants.Properties.Select(p => p.GetColumnName()).ToArray());
        }
    }

    [Fact]
    public void CapacityIsOneOpenPositionPerVariantSlot_NeverPerFamily()
    {
        var slot = Entity("positions").GetIndexes().Single(i => i.GetDatabaseName() == "ux_positions_open_slot");

        Assert.True(slot.IsUnique);
        Assert.Equal(new[] { "run_id", "strategy_id", "variant_id", "slot_no" }, slot.Properties.Select(p => p.GetColumnName()).ToArray());
        Assert.Equal("closed_output_seq IS NULL", slot.GetFilter());       // closed positions release the slot
        Assert.DoesNotContain(Entity("positions").GetIndexes(), i => i.IsUnique && i.GetDatabaseName() != "ux_positions_open_slot");
    }

    [Fact]
    public void TextIsOrdinal_HashesAreFixedHex_AndNoColumnIsFloatingPointOrNumeric()
    {
        foreach (var entity in Entities())
        {
            foreach (var p in entity.GetProperties())
            {
                if (p.ClrType == typeof(string))
                {
                    Assert.Equal("C", p.GetCollation());
                }

                Assert.NotEqual(typeof(double), p.ClrType);
                Assert.NotEqual(typeof(float), p.ClrType);
                Assert.NotEqual(typeof(decimal), p.ClrType);               // decimals are canonical text: no numeric column can round them
            }
        }

        foreach (var (table, column) in new[] { ("runs", "content_hash"), ("steps", "content_hash"), ("engine_outputs", "content_hash"), ("risk_checkpoints", "counters_hash"), ("checkpoints", "state_digest") })
        {
            var property = Entity(table).GetProperties().Single(p => p.GetColumnName() == column);
            Assert.Equal(64, property.GetMaxLength());
            Assert.True(property.IsFixedLength());
            Assert.Contains(Entity(table).GetCheckConstraints(), c => c.Sql.Contains("[0-9A-F]{64}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheStoredTrade_HasEveryColumnTheReviewListsForEntryAndClose()
    {
        var columns = Entity("positions").GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
        {
            "run_id", "strategy_id", "variant_id", "source_session_id", "entry_bar_seq", "direction", "option_type", "token", "symbol", "strike_text", "expiry_date", "entry_quantity",
            "entry_decision_utc_ticks", "entry_quote_bid_text", "entry_quote_ask_text", "avg_entry_price_text", "stop_level_text", "tp1_level_text", "max_hold_deadline_utc_ticks",
            "exit_bar_seq", "exit_utc_ticks", "final_exit_reason", "exit_quote_bid_text", "avg_exit_price_text", "gross_pnl_text", "total_costs_text", "net_pnl_text",
            "mfe_text", "mae_text", "mfe_pct_text", "mae_pct_text", "highest_bid_text", "lowest_bid_text", "excursion_coverage", "tp1_state", "tp1_filled_quantity",
        })
        {
            Assert.Contains(required, columns);
        }

        var snapshot = Entity("position_snapshots").GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "remaining_quantity", "tp1_state", "mfe_text", "mae_text", "mfe_pct_text", "mae_pct_text", "source_output_seq", "source_step_seq", "exit_pending", "last_bid_text" })
        {
            Assert.Contains(required, snapshot);
        }

        var rejection = Entity("rejections").GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "strategy_id", "variant_id", "source_session_id", "bar_seq", "decision_id", "reasons", "stage" })
        {
            Assert.Contains(required, rejection);
        }
    }

    [Fact]
    public void TheModelHasNoPendingChanges_ComparedWithTheMigrationSnapshot()
    {
        using var db = NewContext();

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheMigrationSource_AndTheReviewedScript_AreChecked_AndNothingMigratesImplicitly()
    {
        using var db = NewContext();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        Assert.EndsWith("InitialTradingSchema", Assert.Single(migrations), StringComparison.Ordinal);

        var script = File.ReadAllText(Path.Combine(RepoRoot(), "NiftySignal.Trading.Data", "Sql", "trading-schema.idempotent.sql"));
        Assert.Contains("CREATE SCHEMA trading", script, StringComparison.Ordinal);
        Assert.Contains("ux_positions_open_slot ON trading.positions (run_id, strategy_id, variant_id, slot_no) WHERE closed_output_seq IS NULL", script, StringComparison.Ordinal);
        Assert.Contains("trading.__ef_migrations_history", script, StringComparison.Ordinal);
        Assert.Contains("COLLATE \"C\"", script, StringComparison.Ordinal);

        // the data assembly never creates or migrates a database by itself
        var source = string.Join("\n", Directory.EnumerateFiles(Path.Combine(RepoRoot(), "NiftySignal.Trading.Data"), "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText));
        Assert.DoesNotMatch(@"\.(EnsureCreated|EnsureCreatedAsync|EnsureDeleted|Migrate|MigrateAsync)\s*\(", source);
    }

    [Fact]
    public void TheSchemaVersionRow_IsInsertedByTheMigration_AndMatchesThisBuild()
    {
        using var db = NewContext();
        var seed = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(SchemaVersionEntity))!.GetSeedData().Single();

        Assert.Equal(TradingSchema.Component, seed["Component"]);
        Assert.Equal(OutputCodec.Version, seed["CodecVersion"]);
        Assert.True(SchemaCompatibility.CheckSchema(SchemaCompatibility.Expected()).Compatible);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NiftySignal.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot find the repository root.");
    }
}
