using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NiftySignal.Host.Npt1Stream;
using NiftySignal.Npt1M1;
using NiftySignal.Npt1M1.Data;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>The npt1m1 schema on the local PostgreSQL test database: the reviewed idempotent script creates it (twice without harm), the context reads and writes it, frames are immutable and hash-checked.</summary>
[Collection("PostgresTradingTests")]
public class Npt1M1SchemaTests
{
    private static async Task InstallAsync()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Npt1M1.Data", "Sql", "npt1m1-schema.idempotent.sql"));
        await using var connection = new NpgsqlConnection(PostgresTestDatabase.ConnectionString);
        await connection.OpenAsync();
        await using (var drop = new NpgsqlCommand("DROP SCHEMA IF EXISTS npt1m1 CASCADE", connection))
        {
            await drop.ExecuteNonQueryAsync();
        }

        for (var i = 0; i < 2; i++)                                           // idempotent: the second run changes nothing and does not fail
        {
            await using var create = new NpgsqlCommand(script, connection);
            await create.ExecuteNonQueryAsync();
        }
    }

    private static Npt1M1DbContext NewContext()
        => new(new DbContextOptionsBuilder<Npt1M1DbContext>().UseNpgsql(PostgresTestDatabase.ConnectionString!, o => o.MigrationsHistoryTable("__ef_migrations_history", Npt1M1DbContext.Schema)).Options);

    private static M1Bar Bar(long seq, double? m)
    {
        var end = new DateTimeOffset(2026, 9, 22, 4, 30, 0, TimeSpan.Zero).AddSeconds(seq * 45);
        var frame = new AdaptiveStrategyFrame(M1Identity.SessionId(new DateOnly(2026, 9, 22)), new DateOnly(2026, 9, 22), seq, end.AddSeconds(-45), end, true, true, 5, M1Identity.ModelVersion, "spec", "micro", m, 12.5, -42L, 23450d, new DateOnly(2026, 9, 22), null, "23350|23400|23450|23500|23550");
        var evt = new BarCompletedEvent(end, seq, "FUT", new BarInfo(end.AddSeconds(-45), end, 1000, 10, true, 5, 3, true, true, "spec"), frame);
        return new M1Bar(evt, frame, M1Frames.Hash(frame));
    }

    private sealed class Scopes(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => provider.CreateScope();
    }

    [PostgresFact]
    public async Task TheReviewedScript_CreatesTheSchema_AndFramesAreStoredOnce_AndAChangedFrameIsReportedAsDivergent()
    {
        await PostgresTestDatabase.ResetAsync();
        await InstallAsync();
        var services = new ServiceCollection();
        services.AddDbContext<Npt1M1DbContext>(o => o.UseNpgsql(PostgresTestDatabase.ConnectionString!, n => n.MigrationsHistoryTable("__ef_migrations_history", Npt1M1DbContext.Schema)));
        await using var provider = services.BuildServiceProvider();
        var store = new EfFrameStore(new Scopes(provider));

        Assert.True(await store.EnsureAsync(Bar(1, 0.25), DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.True(await store.EnsureAsync(Bar(1, 0.25), DateTimeOffset.UtcNow, CancellationToken.None));            // the same frame again (a restart re-derives it): fine
        Assert.False(await store.EnsureAsync(Bar(1, 0.26), DateTimeOffset.UtcNow, CancellationToken.None));           // same identity, different content: divergence
        Assert.True(await store.EnsureAsync(Bar(2, null), DateTimeOffset.UtcNow, CancellationToken.None));            // a missing metric is stored as NULL, never as zero

        await using var db = NewContext();
        var rows = await db.Frames.OrderBy(f => f.BarSeq).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(0.25, rows[0].MicroDevTimeWeighted);
        Assert.Equal(-42L, rows[0].PeBandContractStrictDelta);
        Assert.Null(rows[1].MicroDevTimeWeighted);
        Assert.Equal(new DateOnly(2026, 9, 22), rows[0].BandExpiry);
        Assert.Equal(M1Frames.Canonical(Bar(1, 0.25).Frame), rows[0].CanonicalText);
    }

    [PostgresFact]
    public async Task TheSessionRow_IsWrittenOnce_AndARebuiltFreezeWithAnotherFingerprintIsRefused()
    {
        await PostgresTestDatabase.ResetAsync();
        await InstallAsync();
        await using var db = NewContext();
        var builder = new M1SessionBuilder(new Npt1TickReader());
        var day = new DateOnly(2026, 9, 22);
        M1SessionBuild Build(string fingerprint)
        {
            var decision = OpeningVolumeProjectionV2.Resolve(day, 1000, true, Array.Empty<PriorSession>(), OpeningVolumeProjectionV2.BetaM1, 65);
            var session = new NiftySignal.Trading.Adapters.SessionFreeze(
                new NiftySignal.AdaptiveObserver.AdaptiveSessionDefinition(day, "FUT", "FUT", day.AddDays(7), 65, 1000), day, 0.065, null, M1Time.IstTime(day, 9, 30), M1Time.IstTime(day, 9, 15),
                Array.Empty<NiftySignal.AdaptiveObserver.ObserverOptionInstrument>(), null, new Dictionary<string, decimal>(), M1Identity.ModelVersion);
            return new M1SessionBuild(new M1Freeze(session, new M1FreezeReport("2026-09-22", "2026-09-22", 15, 1000, 1000, "", "", 0, null, fingerprint), decision), new FutureInstrument("FUT", "FUT", day, 65), Array.Empty<VolumeHistoryRow>(), Array.Empty<NiftySignal.Trading.Adapters.SourceTick>());
        }

        Assert.Null(await builder.PersistOrVerifyAsync(db, Build("aaa"), DateTimeOffset.UtcNow, CancellationToken.None));
        await using var db2 = NewContext();
        Assert.Null(await builder.PersistOrVerifyAsync(db2, Build("aaa"), DateTimeOffset.UtcNow, CancellationToken.None));                 // a restart with the same freeze
        var problem = await builder.PersistOrVerifyAsync(db2, Build("bbb"), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(problem);
        Assert.Contains("refuses to run on a different freeze", problem, StringComparison.Ordinal);
        Assert.Equal(1, await db2.Sessions.CountAsync());
    }
}
