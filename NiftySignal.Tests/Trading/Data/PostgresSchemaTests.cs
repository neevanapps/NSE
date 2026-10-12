using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Trading.Adapters;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>
/// The schema on a real (local) PostgreSQL: the reviewed migration and the reviewed idempotent script create the same objects, the constraints really reject what
/// they must (two open positions of one variant slot, bad hashes, bad enum text) and really allow what they must (the same VariantId under another strategy, several
/// variants of one family open at once). Skipped when <c>NSE_TRADING_TEST_DB</c> is not set.
/// </summary>
[Collection("PostgresTradingTests")]
public class PostgresSchemaTests
{
    private const string RunId = "run-pg-1";

    private static async Task<(RunEntity Run, MappedProjections Mapped, IReadOnlyList<OutputRecord> Records, StepRecord Step)> SeedAsync(TradingDbContext db, bool crash = false)
    {
        var exit = new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m };
        var config = PaperKit.Config(10_000_000m, PaperKit.Spec("X", "A", exit), PaperKit.Spec("Y", "A"), PaperKit.Spec("X", "B"));
        var kit = new PaperKit(
            config,
            Kit.StandardUniverse().Select(c => c with { Symbol = $"NIFTY26SEP{c.Strike:0}{(c.Side == NiftySignal.Trading.Core.Instruments.OptionSide.Call ? "CE" : "PE")}" }).ToList(),
            PaperKit.Family("X", ("A", PaperKit.Script(new[] { (1L, 1L) })), ("B", PaperKit.Script(new[] { (1L, 1L) }))),
            new PlumbingFamily("Y", new Dictionary<string, PlumbingVariantScript> { ["A"] = new(new[] { new PlumbingWindow(1, 1, StrategyDirection.Short) }) }));
        var t = kit.Bar(10, 0, advance: 1.1);
        kit.Move(t.AddSeconds(2.2), ceBid: 140m, advance: 0.5);
        if (crash)
        {
            kit.Move(t.AddSeconds(4.2), ceBid: 100m);                                // the stop of X/A fires: it closes
        }

        var records = ProjectionMapperTests.Records(kit.Kit.Engine.Outputs);
        var keys = new[] { new VariantKey("X", "A"), new VariantKey("X", "B"), new VariantKey("Y", "A") };
        var mapped = ProjectionMapper.Map(RunId, records, ProjectionMapperTests.Header(keys));

        var baseHeader = ContractData.Snapshot.Header!;
        var header = RunHeader.Create(baseHeader with { RunId = RunId, Variants = ProjectionMapperTests.Header(keys), ContentHash = string.Empty });
        var run = EntityMapper.ToEntity(header);
        var digests = TradingDbContext.Channels.Select(c => new ChannelDigestRecord(c, 1, new string('A', 64))).ToList();
        var step = StepRecord.Create(new StepRecord(0, UnitKind.DataGroup, records[^1].At, 1, 1, 1, 1, records[^1].At, 1, 0, records.Count, Array.Empty<string>(), digests, string.Empty));

        {
            db.Runs.Add(run);
            foreach (var account in header.Variants.Select(v => v.AccountId).Distinct())
            {
                db.Accounts.Add(new AccountEntity { RunId = RunId, AccountId = account });
            }

            await db.SaveChangesAsync();
            db.Steps.Add(EntityMapper.ToEntity(RunId, step));
            await db.SaveChangesAsync();
            db.Outputs.AddRange(records.Select(r => EntityMapper.ToEntity(RunId, r)));
            await db.SaveChangesAsync();
        }

        return (run, mapped, records, step);
    }

    private static async Task InsertProjectionsAsync(TradingDbContext db, MappedProjections mapped)
    {
        db.Positions.AddRange(mapped.Positions);
        await db.SaveChangesAsync();
        db.Orders.AddRange(mapped.Orders);
        await db.SaveChangesAsync();
        db.Fills.AddRange(mapped.Fills);
        await db.SaveChangesAsync();
        db.LedgerEntries.AddRange(mapped.Ledger);
        db.AuditEntries.AddRange(mapped.Audit);
        db.Rejections.AddRange(mapped.Rejections);
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    public async Task TheReviewedMigration_CreatesTheSchema_AndTheVersionRowMatchesThisBuild()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();

        var row = await db.SchemaVersions.SingleAsync();
        Assert.Equal(TradingSchema.Component, row.Component);
        Assert.True(SchemaCompatibility.CheckSchema(row).Compatible);
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var tables = await TablesAsync();
        Assert.Contains("positions", tables);
        Assert.Contains("rejections", tables);
        Assert.Equal(23 + 1, tables.Count);                              // the 23 model tables plus the migration history
    }

    [PostgresFact]
    public async Task TheReviewedIdempotentScript_CreatesTheSameObjects_AsTheMigration_AndCanRunTwice()
    {
        await PostgresTestDatabase.ResetAsync();
        var viaMigration = await ObjectsAsync();

        await using (var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString))
        {
            await connection.OpenAsync();
            await using (var drop = new NpgsqlCommand("DROP SCHEMA trading CASCADE", connection))
            {
                await drop.ExecuteNonQueryAsync();
            }

            var script = await File.ReadAllTextAsync(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Trading.Data", "Sql", "trading-schema.idempotent.sql"));
            for (var i = 0; i < 2; i++)
            {
                await using var run = new NpgsqlCommand(script, connection);
                await run.ExecuteNonQueryAsync();
            }
        }

        Assert.Equal(viaMigration, await ObjectsAsync());
        await using var db = PostgresTestDatabase.NewContext();
        Assert.True(SchemaCompatibility.CheckSchema(await db.SchemaVersions.SingleAsync()).Compatible);
    }

    [PostgresFact]
    public async Task TheStoredTrades_RoundTripEveryColumn_AndTheClosePopulatesTheLifetimeExcursion()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();
        var (_, mapped, _, _) = await SeedAsync(db, crash: true);
        var closure = Assert.Single(mapped.Closures);
        var closing = mapped.Positions.Single(p => p.PositionId == closure.PositionId);
        ProjectionMapper.ApplyClosure(closing, closure);                     // the close columns are written once, together with the close output
        Assert.Throws<InvalidOperationException>(() => ProjectionMapper.ApplyClosure(closing, closure));
        await InsertProjectionsAsync(db, mapped);

        await using var read = PostgresTestDatabase.NewContext();
        var positions = await read.Positions.AsNoTracking().OrderBy(p => p.StrategyId).ThenBy(p => p.VariantId).ToListAsync();

        Assert.Equal(new[] { ("X", "A"), ("X", "B"), ("Y", "A") }, positions.Select(p => (p.StrategyId, p.VariantId)).ToArray());
        Assert.All(positions, p => Assert.Equal(0, p.SlotNo));            // all three own slot 0 (the first slot of a variant): capacity is per variant, never per family or database-wide
        Assert.Equal(new[] { "Long", "Long", "Short" }, positions.Select(p => p.Direction).ToArray());
        Assert.All(positions, p => Assert.Equal(p.OptionType == "CE" ? "NIFTY26SEP23900CE" : "NIFTY26SEP23900PE", p.Symbol));

        var stored = positions.Single(p => p.PositionId == closing.PositionId);
        foreach (var property in typeof(PositionEntity).GetProperties())
        {
            Assert.Equal(property.GetValue(closing), property.GetValue(stored));      // every column survives the database unchanged
        }

        Assert.Equal("TradeSl", stored.FinalExitReason);
        Assert.NotNull(stored.MfeText);
        Assert.NotNull(stored.MaeText);
        Assert.Equal(1, await read.Positions.CountAsync(p => p.ClosedOutputSeq != null));
        Assert.Equal(2, await read.Positions.CountAsync(p => p.ClosedOutputSeq == null && p.FinalExitReason == null && p.NetPnlText == null));
    }

    [PostgresFact]
    public async Task OnePositionPerVariantSlot_IsEnforcedByTheDatabase_NotPerFamilyAndNotPerDatabase()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();
        var (_, mapped, _, _) = await SeedAsync(db);
        await InsertProjectionsAsync(db, mapped);
        var xa = mapped.Positions.Single(p => p.StrategyId == "X" && p.VariantId == "A");
        var yaCopy = Clone(mapped.Positions.Single(p => p.StrategyId == "Y"));
        yaCopy.PositionId = "pos-second-open-ya";

        // a second OPEN position of the same (strategy, variant, slot) is refused ...
        var duplicate = Clone(xa);
        duplicate.PositionId = "pos-second-open-xa";
        await using (var dup = PostgresTestDatabase.NewContext())
        {
            dup.Positions.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync());
            Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            Assert.Equal("ux_positions_open_slot", ((PostgresException)error.InnerException!).ConstraintName);
        }

        // ... the same VariantId and slot under ANOTHER strategy is a different slot, and sibling variants of one family each have their own
        await using (var ok = PostgresTestDatabase.NewContext())
        {
            Assert.Equal(1, await ok.Positions.CountAsync(p => p.VariantId == "A" && p.StrategyId == "Y" && p.SlotNo == 0));
            Assert.Equal(2, await ok.Positions.CountAsync(p => p.StrategyId == "X" && p.SlotNo == 0));
        }

        // closing the position releases its slot
        await using (var close = PostgresTestDatabase.NewContext())
        {
            var open = await close.Positions.SingleAsync(p => p.PositionId == xa.PositionId);
            var closeOutput = new OutputRecord(9_000, 0, "PositionClosed", "X", "A", DateTimeOffset.UnixEpoch.AddYears(60), "{}", OutputCodec.ContentHash("{}"));
            close.Outputs.Add(EntityMapper.ToEntity(RunId, closeOutput));
            await close.SaveChangesAsync();
            open.ClosedOutputSeq = 9_000;
            open.ExitUtcTicks = 1;
            open.FinalExitReason = "TradeSl";
            open.GrossPnlText = "-1";
            open.TotalCostsText = "0";
            open.NetPnlText = "-1";
            await close.SaveChangesAsync();
        }

        await using (var again = PostgresTestDatabase.NewContext())
        {
            var next = Clone(xa);
            next.PositionId = "pos-next-xa";
            again.Positions.Add(next);
            await again.SaveChangesAsync();                                  // allowed: the earlier X/A position is closed
        }
    }

    [PostgresFact]
    public async Task TheConstraints_RejectBadHashesBadEnumsAndHalfClosedTrades()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();
        var (_, mapped, _, _) = await SeedAsync(db);
        await InsertProjectionsAsync(db, mapped);
        var template = mapped.Positions.First();

        async Task<string> Violation(Action<PositionEntity> mutate)
        {
            await using var c = PostgresTestDatabase.NewContext();
            var p = Clone(template);
            p.PositionId = "pos-bad-" + Guid.NewGuid().ToString("N");
            p.SlotNo = 5;
            mutate(p);
            c.Positions.Add(p);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => c.SaveChangesAsync());
            return ((PostgresException)error.InnerException!).ConstraintName ?? string.Empty;
        }

        Assert.Equal("ck_positions_direction", await Violation(p => p.Direction = "Sideways"));
        Assert.Equal("ck_positions_option_type", await Violation(p => p.OptionType = "XX"));
        Assert.Equal("ck_positions_quantity", await Violation(p => p.EntryQuantity = 0));
        Assert.Equal("ck_positions_close_complete", await Violation(p => p.NetPnlText = "1"));                  // a net P&L without a close
        Assert.Equal("fk_positions_variants_run_id_strategy_id_variant_id", await Violation(p => p.StrategyId = "Nobody"));

        await using var bad = PostgresTestDatabase.NewContext();
        bad.RiskCheckpoints.Add(new RiskCheckpointEntity { RunId = RunId, StepSeq = 0, SourceOutputSeq = 1, CountersText = "x", CountersHash = "not-a-hash" });
        var hashError = await Assert.ThrowsAsync<DbUpdateException>(() => bad.SaveChangesAsync());
        Assert.Equal("ck_risk_checkpoints_hash", ((PostgresException)hashError.InnerException!).ConstraintName);
    }

    [PostgresFact]
    public async Task TicksTextAndOrdinalOrdering_SurviveARealDatabaseExactly()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();
        var (_, mapped, records, _) = await SeedAsync(db);
        await InsertProjectionsAsync(db, mapped);

        await using var read = PostgresTestDatabase.NewContext();
        var back = await read.Outputs.AsNoTracking().OrderBy(o => o.Seq).ToListAsync();

        Assert.Equal(records.Select(r => TickTime.ToTicks(r.At)), back.Select(o => o.AtUtcTicks));       // exact 100 ns ticks
        Assert.Equal(records.Select(r => r.Payload), back.Select(o => o.Payload));                        // payload text unchanged byte for byte
        Assert.All(back, o => Assert.True(EntityMapper.ToRecord(o).HashIsValid));
        var fill = await read.Fills.AsNoTracking().FirstAsync();
        Assert.Equal(mapped.Fills.First(f => f.FillId == fill.FillId).PriceText, fill.PriceText);

        // text compares ordinally ("C" collation): upper case sorts before lower case, '_' between them
        await using (var write = PostgresTestDatabase.NewContext())
        {
            foreach (var id in new[] { "b", "B", "_", "a", "A" })
            {
                write.Accounts.Add(new AccountEntity { RunId = RunId, AccountId = "ord-" + id });
            }

            await write.SaveChangesAsync();
        }

        var ordered = await read.Accounts.AsNoTracking().Where(a => a.AccountId.StartsWith("ord-")).OrderBy(a => a.AccountId).Select(a => a.AccountId).ToListAsync();
        Assert.Equal(new[] { "ord-A", "ord-B", "ord-_", "ord-a", "ord-b" }, ordered);
    }

    [PostgresFact]
    public async Task TheCacheRows_AreStampedWithTheirSourceAndCannotPointAtNothing()
    {
        await PostgresTestDatabase.ResetAsync();
        await using var db = PostgresTestDatabase.NewContext();
        var (_, mapped, _, step) = await SeedAsync(db);
        await InsertProjectionsAsync(db, mapped);
        var position = mapped.Positions.First();

        db.PositionSnapshots.Add(new PositionSnapshotEntity
        {
            RunId = RunId,
            PositionId = position.PositionId,
            StrategyId = position.StrategyId,
            VariantId = position.VariantId,
            AsOfUtcTicks = 1,
            SourceOutputSeq = 5,
            SourceStepSeq = step.StepSeq,
            Status = "Open",
            RemainingQuantity = 325,
            ReservedExitQuantity = 0,
            Tp1State = "Done",
            Tp1FilledQuantity = 325,
            MfeText = "15",
            MaeText = "-1",
            ExcursionCoverage = "Complete",
        });
        db.RiskCheckpoints.Add(new RiskCheckpointEntity { RunId = RunId, StepSeq = step.StepSeq, SourceOutputSeq = 5, CountersText = "global.trades=3", CountersHash = new string('A', 64) });
        await db.SaveChangesAsync();

        await using var orphan = PostgresTestDatabase.NewContext();
        orphan.PositionSnapshots.Add(new PositionSnapshotEntity { RunId = RunId, PositionId = "no-such-position", StrategyId = "X", VariantId = "A", SourceStepSeq = 0, Status = "Open", Tp1State = "NotTriggered", ExcursionCoverage = "None" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => orphan.SaveChangesAsync());
        Assert.Equal("23503", ((PostgresException)error.InnerException!).SqlState);
    }

    // ------------------------------------------------------------------ helpers

    private static PositionEntity Clone(PositionEntity p)
    {
        var copy = new PositionEntity();
        foreach (var property in typeof(PositionEntity).GetProperties())
        {
            property.SetValue(copy, property.GetValue(p));
        }

        return copy;
    }

    private static async Task<List<string>> TablesAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select table_name from information_schema.tables where table_schema = 'trading' order by 1", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<string>();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Tables, columns (type, nullability, collation), constraints and indexes of the trading schema as one comparable text.</summary>
    private static async Task<string> ObjectsAsync()
    {
        await using var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        var parts = new List<string>();
        foreach (var sql in new[]
        {
            "select table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(collation_name, '') from information_schema.columns where table_schema = 'trading' order by 1",
            "select conrelid::regclass::text || ' ' || conname || ' ' || pg_get_constraintdef(oid) from pg_constraint where connamespace = 'trading'::regnamespace order by 1",
            "select indexdef from pg_indexes where schemaname = 'trading' order by 1",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                parts.Add(reader.GetString(0));
            }
        }

        return string.Join("\n", parts);
    }
}
