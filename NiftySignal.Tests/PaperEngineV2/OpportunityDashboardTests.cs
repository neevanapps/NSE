using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>The compact opportunity view on the Dashboard: counts, rejection reasons, a bounded recent table, a read-only connection, and graceful degradation when the owner-run script or the GRANT is missing.</summary>
[Collection("PostgresTradingTests")]
public class OpportunityDashboardTests
{
    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static async Task<string> RenderAsync(PaperEngineV2DataService? service)
    {
        var sc = new ServiceCollection().AddLogging();
        if (service is not null) sc.AddSingleton(service);
        var services = sc.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<PaperEngineV2OpportunityPanel>(ParameterView.Empty)).ToHtmlString()));
    }

    [PostgresFact]
    public async Task TheView_CountsSignalsAttemptsFillsSuppressedAndExpired_FromTheDurableLog()
    {
        var db = await FreshAsync();
        var store = new PostgresOpportunityStore(db, TimeProvider.System);
        var engine = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options(), null, store);
        ExecFixtures.Run(engine, ExecutionTests.Day());
        var day = engine.Opportunities[0].TradeDate;

        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadOpportunitiesAsync(day, default);
        Assert.True(v.Available);
        var s = Assert.Single(v.Strategies);
        var decisions = engine.Opportunities.Where(o => o.Stage == OpportunityStage.Decision).ToList();
        Assert.Equal(decisions.Count, s.Bars);
        Assert.Equal(decisions.Count(o => o.Direction is "CE" or "PE"), s.Signals);
        Assert.Equal(decisions.Count(o => o.OrderAttempted), s.Attempts);
        Assert.Equal(engine.Opportunities.Count(o => o.Category == OpportunityCategory.OrderFilled), s.Fills);
        Assert.Equal(decisions.Count(o => o.Category is OpportunityCategory.SameDirectionWhileHolding or OpportunityCategory.BlockedByPosition or OpportunityCategory.BlockedByRiskOrWindow), s.Suppressed);
        Assert.Equal(engine.Opportunities.Count(o => o.Category == OpportunityCategory.OrderAcceptedNotFilled), s.Expired);
        Assert.True(v.Recent.Count <= 25);
        Assert.All(v.Rejections, r => Assert.True(r.Count > 0));

        var html = await RenderAsync(svc);
        Assert.Contains("V2 OPPORTUNITIES", html, StringComparison.Ordinal);                                // the panel renders today's (IST) date; the fixture day differs, so it reads as empty here
        Assert.DoesNotMatch("<(button|form|input|select|textarea)\\b", html);                           // read-only: no control of any kind
    }

    [PostgresFact]
    public async Task TheConnection_IsReadOnly()
    {
        var db = await FreshAsync();
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        Assert.True((await svc.LoadOpportunitiesAsync(new DateOnly(2026, 11, 4), default)).Available);
        var b = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString!) { Options = "-c default_transaction_read_only=on" };
        await using var ro = NpgsqlDataSource.Create(b.ConnectionString);
        await using var write = ro.CreateCommand("DELETE FROM pev2.pev2_opportunities");
        await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
    }

    [PostgresFact]
    public async Task AMissingTable_OrAMissingGrant_DegradesToAMessage_NeverACrash()
    {
        var db = await FreshAsync();
        await using (var drop = db.CreateCommand("DROP TABLE pev2.pev2_opportunities")) await drop.ExecuteNonQueryAsync();
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadOpportunitiesAsync(new DateOnly(2026, 11, 4), default);
        Assert.False(v.Available);
        Assert.Contains("AddObservability", v.Message, StringComparison.Ordinal);
        Assert.Contains("not installed", await RenderAsync(svc), StringComparison.Ordinal);
        Assert.Contains("not configured", await RenderAsync(null), StringComparison.Ordinal);
    }
}
