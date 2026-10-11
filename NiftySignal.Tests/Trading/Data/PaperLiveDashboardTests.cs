using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.Dashboard.Services;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Tests.Trading.Recovery;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>The read-only paper-trading view of the Dashboard, against a real live run in the local PostgreSQL test database, and proof that it has no way to change anything.</summary>
[Collection("PostgresTradingTests")]
public class PaperLiveDashboardTests
{
    private sealed class Factory(string connection) : IDbContextFactory<TradingDbContext>
    {
        public TradingDbContext CreateDbContext()
        {
            var read = new NpgsqlConnectionStringBuilder(connection) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
            return new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(read, n => n.MigrationsHistoryTable("__ef_migrations_history", TradingDbContext.SchemaName)).Options);
        }
    }

    private static LiveEngineRun RunTo(PostgresContractFixture fixture, int steps, bool kill = false)
    {
        var s = LiveEngineRunContractTests.XScenario();
        var run = LiveEngineRun.Open(new BlockingRunStoreAdapter(fixture.Store), "owner-1", LiveHarness.Spec(s.Config, s.Families, "live-1"));
        var script = s.Script.Steps.Take(steps).ToList();
        if (kill)
        {
            script.Insert(0, new GroupStep(new[] { PaperKit.Kill(Clock.Ist(9, 59), 1, "X/X3") }));
        }

        LiveHarness.Deliver(run, script);
        return run;
    }

    [PostgresFact]
    public async Task TheView_ShowsOpenPositionsWithRemainingQuantityTp1AndExcursion_FromTheDurableLedger()
    {
        using var fixture = new PostgresContractFixture();
        var s = LiveEngineRunContractTests.XScenario();
        using var run = RunTo(fixture, s.AfterTp1);
        var service = new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day);

        var view = await service.LoadLatestAsync();

        Assert.NotNull(view);
        Assert.Equal("live-1", view.Status.RunId);
        Assert.False(view.Status.Completed);
        Assert.Equal(1L, view.Status.LastBarSeq);
        Assert.Equal(10_000_000m, view.Status.GlobalCapital);
        Assert.Equal(new[] { "X1", "X2", "X3" }, view.Open.Select(p => p.VariantId).OrderBy(x => x, StringComparer.Ordinal));
        var x1 = view.Open.Single(p => p.VariantId == "X1");
        Assert.Equal((650, 325, "Done"), (x1.EntryQuantity, x1.RemainingQuantity, x1.Tp1State));
        Assert.NotNull(x1.Mfe);
        Assert.NotNull(view.Open.Single(p => p.VariantId == "X2").MaxHoldDeadline);
        Assert.Empty(view.Closed);
        Assert.Equal(3, view.Risk.GlobalTrades);
        Assert.Equal(125m * (325 + 650 + 650), view.Risk.OpenExposure);
        Assert.Equal(new[] { "X" }, view.Families.Select(f => f.Scope));
        Assert.Equal(3, view.Variants.Count);
        Assert.Equal("Healthy", view.Risk.IntegrityState);
        Assert.Empty(view.Status.Problems);
    }

    [PostgresFact]
    public async Task ARunThatNeverReachedItsFinalClose_IsFlaggedOnceItsTradeDateIsPast()
    {
        using var fixture = new PostgresContractFixture();
        using var run = RunTo(fixture, 2);

        var sameDay = await new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day).LoadLatestAsync();
        var nextDay = await new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day.AddDays(1)).LoadLatestAsync();

        Assert.Empty(sameDay!.Status.Problems);
        Assert.Contains(nextDay!.Status.Problems, p => p.Contains("never reached its final close", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task TheView_ShowsClosedTradesWithMaeMfe_FamilyFigures_Rejections_AndKillSwitches()
    {
        using var fixture = new PostgresContractFixture();
        var s = LiveEngineRunContractTests.XScenario();
        using var run = RunTo(fixture, s.AfterStops, kill: true);
        var service = new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day);

        var view = await service.LoadLatestAsync();

        Assert.NotNull(view);
        Assert.Contains("X/X3", view.Risk.KillSwitches);
        var closed = view.Closed.OrderBy(c => c.VariantId, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "X1" }, closed.Select(c => c.VariantId));                       // X3 was killed before it could enter; X2 is still open
        Assert.Equal("TradeSl", closed[0].ExitReason);
        Assert.NotNull(closed[0].Mfe);
        Assert.NotNull(closed[0].Mae);
        Assert.Equal(1, view.Families.Single().Closed);
        Assert.Equal(1, view.Families.Single().Losses);
        Assert.Contains(view.Risk.Rejections, r => r.VariantId == "X3" && r.Reasons.Contains("Kill", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresFact]
    public async Task TheDashboardConnection_IsReadOnly_AFailedWriteChangesNothing()
    {
        using var fixture = new PostgresContractFixture();
        using var run = RunTo(fixture, 2);
        await using var db = new Factory(PostgresTestDatabase.ConnectionString!).CreateDbContext();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM trading.positions"));

        Assert.Contains("read-only", error.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var check = PostgresTestDatabase.NewContext();
        Assert.True(await check.Positions.AnyAsync());
    }

    [Fact]
    public async Task WithoutAConnection_TheServiceIsNotConfigured_AndTheViewIsEmpty()
    {
        var service = new PaperLiveDataService(null);

        Assert.False(service.Configured);
        Assert.Null(await service.LoadLatestAsync());
    }

    [Fact]
    public void TheDashboardCode_HasNoWritePath_AndTheStatusPageHasNoControls()
    {
        var root = TradingModelTests.RepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "NiftySignal.Dashboard", "Services", "PaperLiveDataService.cs"));
        foreach (var forbidden in new[] { "SaveChanges", "ExecuteSql", "db.Add", "db.Remove", "db.Update", ".Entry(", "HttpClient", "Broker", "PlaceOrder" })
        {
            Assert.DoesNotContain(forbidden, service, StringComparison.Ordinal);
        }

        var page = File.ReadAllText(Path.Combine(root, "NiftySignal.Dashboard", "Components", "Pages", "PaperLive.razor"));
        foreach (var control in new[] { "<button", "<form", "<input", "@onclick", "@bind", "EditForm", "<select" })
        {
            Assert.DoesNotContain(control, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("{\"Risk\":{\"GlobalCapital\":\"10000000\"}}", 10000000)]
    [InlineData("{\"GlobalCapital\":5000000.5}", 5000000.5)]
    public void TheCapital_IsReadFromTheRunConfiguration(string json, double expected)
        => Assert.Equal((decimal)expected, PaperLiveDataService.GlobalCapitalOf(json));
}
