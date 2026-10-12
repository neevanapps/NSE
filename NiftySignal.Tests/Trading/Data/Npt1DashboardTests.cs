using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NiftySignal.Dashboard.Services;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Tests.Trading.Live;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Data;

/// <summary>The NPT1 section of the read-only paper view: three strategies shown independently, with their audited decisions, from a real durable run in the local PostgreSQL test database.</summary>
[Collection("PostgresTradingTests")]
public class Npt1DashboardTests
{
    private sealed class Factory(string connection) : IDbContextFactory<TradingDbContext>
    {
        public TradingDbContext CreateDbContext()
        {
            var read = new NpgsqlConnectionStringBuilder(connection) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
            return new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(read, n => n.MigrationsHistoryTable("__ef_migrations_history", TradingDbContext.SchemaName)).Options);
        }
    }

    [PostgresFact]
    public async Task AReversal_ClosingAndOpeningTheSameSlotInOneStep_IsCommittedToPostgresWithoutAFaultOrRebuild()
    {
        // an exit and the opposite entry fill on the SAME completed bar: one step closes the slot-0 position and opens the next one in slot 0 (the partial unique index allows one open position per slot)
        using var fixture = new PostgresContractFixture();
        var options = Npt1Harness.Options(Npt1Harness.All);
        var strategies = PaperLiveSetup.Npt1Strategies(options);
        using var coordinator = new PaperLiveCoordinator(options, strategies, () => new BlockingRunStoreAdapter(fixture.Store), NullLogger.Instance, "host", configFactory: _ => PaperLiveSetup.Npt1Config(options, strategies));
        coordinator.StartSession(Npt1Harness.Session());
        var feeder = new Npt1Harness.Feeder(coordinator, Clock.Ist(10, 0));
        Func<int, (double? M, double? W, long? P)> reversing = k => (k < 22 ? 0d : k < 30 ? 1d : -3d, 1d, k < 22 ? 0L : k < 30 ? -100L : k < 34 ? 400L : -300L);
        for (var k = 1; k <= 70; k++)
        {
            feeder.Bar(k, metrics: reversing);
        }

        Assert.Equal(PaperLiveState.Live, coordinator.Status.State);
        Assert.Equal(1, coordinator.Status.Recoveries);                         // the initial open only: no store fault, no rebuild
        var sameBar = coordinator.Run!.Engine.Outputs.OfType<NiftySignal.Trading.Core.Execution.PositionClosed>()
            .Join(coordinator.Run.Engine.Outputs.OfType<NiftySignal.Trading.Core.Execution.PositionOpened>(), c => (c.Variant, c.At), o => (o.Variant, o.At), (c, o) => c).ToList();
        Assert.NotEmpty(sameBar);                                               // the scenario really contains same-bar reversals

        var service = new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day);
        var view = await service.LoadLatestAsync();
        Assert.Empty(view!.Status.Problems);
        Assert.True(view.Closed.Count >= sameBar.Count);
    }

    [PostgresFact]
    public async Task EachNpt1Strategy_IsShownOnItsOwn_WithItsPosition_Ledger_AndAuditedDecisions()
    {
        using var fixture = new PostgresContractFixture();
        var options = Npt1Harness.Options(Npt1Harness.All);
        var strategies = PaperLiveSetup.Npt1Strategies(options);
        using var coordinator = new PaperLiveCoordinator(options, strategies, () => new BlockingRunStoreAdapter(fixture.Store), NullLogger.Instance, "host", configFactory: _ => PaperLiveSetup.Npt1Config(options, strategies));
        coordinator.StartSession(Npt1Harness.Session());
        var feeder = new Npt1Harness.Feeder(coordinator, Clock.Ist(14, 30));
        for (var k = 1; k <= 23; k++)
        {
            feeder.Bar(k, ceBid: 128m, ceAsk: 129m);
        }

        var service = new PaperLiveDataService(new Factory(PostgresTestDatabase.ConnectionString!), () => Clock.Day);
        var view = await service.LoadLatestAsync();
        Assert.NotNull(view);
        var npt1 = await service.LoadNpt1Async(view);

        Assert.NotNull(npt1);
        Assert.Equal(new[] { "NPT1-MD5", "NPT1-MDX3", "NPT1-PE5" }, npt1.Strategies.Select(s => s.StrategyId));
        Assert.All(npt1.Strategies, s =>
        {
            Assert.StartsWith("HOLDING", s.State, StringComparison.Ordinal);
            Assert.NotNull(s.Open);
            Assert.Equal(650, s.Open!.RemainingQuantity);
            Assert.Equal(129m * 650, s.OpenCommitted);
            Assert.Contains(s.Decisions, d => d.Stage == "StrategyDecision" && d.Detail.Contains("ready=1", StringComparison.Ordinal));
            Assert.Contains(s.Decisions, d => d.Stage == "NextBarEntry" && d.Outcome == "Filling");
        });
        Assert.Contains(npt1.Strategies.Single(s => s.StrategyId == "NPT1-MD5").Decisions, d => d.Detail.Contains("A5=", StringComparison.Ordinal));
        Assert.Contains(npt1.Strategies.Single(s => s.StrategyId == "NPT1-MDX3").Decisions, d => d.Detail.Contains("dPrev=", StringComparison.Ordinal));
        Assert.Contains(npt1.Strategies.Single(s => s.StrategyId == "NPT1-PE5").Decisions, d => d.Detail.Contains("marginPrev=", StringComparison.Ordinal));

        coordinator.OnHeartbeat(Clock.Ist(15, 0, 1));
        var closedView = await service.LoadLatestAsync();
        var closed = await service.LoadNpt1Async(closedView!);
        Assert.All(closed!.Strategies, s =>
        {
            Assert.Equal("FLAT", s.State);
            Assert.Equal(1, s.Trades);
            Assert.Equal("TerminalClose", Assert.Single(s.Closed).ExitReason);
            Assert.Equal(s.Gross - s.Fees, s.Net);
        });
    }
}
