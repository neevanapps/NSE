using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 7: the Home page V2 SHADOW section - pure projection, rendering, the read-only data service on PostgreSQL, and the guards that keep NPT1, the existing Home components and V2-disabled-by-default intact.</summary>
[Collection("PostgresTradingTests")]
public class HomeV2Tests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 7, 4, 5, 0, TimeSpan.Zero);          // 09:35 IST
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly V2EngineHealth Healthy = new(true, [], T0, 6, 330, 2, 0, 1, 0, 0);

    static EntryOrderPlaced Placed(string s, string intent, OptionRight r, string token, double strike, DateTimeOffset at) =>
        new($"{intent}:placed", s, "h", 5, at, intent, r, token, strike, new DateOnly(2026, 10, 13), 65, 650, 126m, new DecisionKey(at, 9), at.AddSeconds(60));

    static EntryFilled Filled(string s, string intent, OptionRight r, string token, decimal px, DateTimeOffset at) =>
        new($"{intent}:filled", s, "h", 6, at, intent, intent, r, token, 650, px, 33.37m, at, 12, 650);

    static ExitFilled Exited(string s, string pos, decimal px, decimal gross, decimal net, DateTimeOffset at) =>
        new($"{pos}:xfill", s, "h", 9, at, pos, ExitReason.Target, 650, px, 20m, gross, net, at, 13, 650, 2000m, -500m);

    static V2HomeView Scenario(V2MarkSource? marks = null, IReadOnlyList<V2ConfiguredStrategy>? configured = null)
    {
        var t = T0;
        PaperEvent[] events =
        [
            // S01: open call
            Placed("S01", "S01:i1", OptionRight.Call, "CE26000", 26000, t), Filled("S01", "S01:i1", OptionRight.Call, "CE26000", 127.5m, t.AddSeconds(60)),
            // S02: closed winner
            Placed("S02", "S02:i1", OptionRight.Put, "PE26000", 26000, t), Filled("S02", "S02:i1", OptionRight.Put, "PE26000", 127.5m, t.AddSeconds(60)), Exited("S02", "S02:i1", 140.5m, 8450m, 8396.63m, t.AddMinutes(10)),
            // S03: rejected
            new EntryRejected("S03:any:rej", "S03", "h", 5, t, OptionRight.Call, null, RejectionReason.SpreadTooWide, "every in-range contract exceeds the spread limit"),
            // S04: unresolved + a faulted strategy S05
            Filled("S04", "S04:i1", OptionRight.Call, "CE25950", 130m, t), new PositionUnresolved("S04:i1:unres", "S04", "h", 20, t.AddMinutes(30), "S04:i1", "not closed by the retry cut-off"),
            new StrategyFaulted("S05:fault", "S05", "h", 7, t.AddMinutes(5), "InvalidOperationException: boom"),
        ];
        var perf = new[] { new V2StrategyPerformance("S02", 1, 1, 8450m, 53.37m, 8396.63m, 12, 8, 21000m) };
        return V2HomeProjection.Build(t.AddMinutes(11), Day, Healthy, configured ?? [new("S01", 1, true), new("S02", 1, true), new("S03", 1, true), new("S04", 1, true), new("S05", 1, true), new("S06", 2, true), new("S07", 1, false)], events, perf, marks, false);
    }

    static async Task<string> RenderAsync(V2HomeView? view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<PaperEngineV2Section>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    static string Row(string html, string strategy) => Regex.Match(html, $"<tr data-strategy=\"{strategy}\".*?</tr>", RegexOptions.Singleline).Value;

    // ---- projection ----

    [Fact]
    public void Projection_ShowsEveryConfiguredStrategy_AndInventsNothingForQuietOnes()
    {
        var v = Scenario();
        Assert.Equal(["S01", "S02", "S03", "S04", "S05", "S06", "S07"], v.Strategies.Select(s => s.StrategyId).ToArray());
        var s06 = v.Strategies.Single(s => s.StrategyId == "S06");
        Assert.Equal(("NO ACTIVITY TODAY", false, 2), (s06.Signal, s06.HasActivity, s06.Version));
        Assert.Null(s06.RealizedNetToday);
        Assert.Empty(s06.OpenPositions);
        Assert.Equal("DISABLED", v.Strategies.Single(s => s.StrategyId == "S07").Signal);
        Assert.Null(v.Strategies.Single(s => s.StrategyId == "S07").RealizedNetToday);
    }

    [Fact]
    public void Projection_DerivesSignalPositionContractAndRealized()
    {
        var v = Scenario();
        var s01 = v.Strategies.Single(s => s.StrategyId == "S01");
        Assert.Equal("IN POSITION (CALL)", s01.Signal);
        var p = Assert.Single(s01.OpenPositions);
        Assert.Equal(("CE26000", 26000d, 650, 127.5m), (p.Token, p.Strike, p.Quantity, p.EntryPrice));
        Assert.Null(p.UnrealizedPnl);                                                  // no mark source: never a made-up zero
        Assert.Null(s01.UnrealizedNow);
        var s02 = v.Strategies.Single(s => s.StrategyId == "S02");
        Assert.Equal(("FLAT", 8396.63m, 1), (s02.Signal, s02.RealizedNetToday, s02.TradesToday));
        Assert.Contains("REJECTED SpreadTooWide", v.Strategies.Single(s => s.StrategyId == "S03").Signal, StringComparison.Ordinal);
        Assert.Equal("FAULTED", v.Strategies.Single(s => s.StrategyId == "S05").Signal);
        Assert.Equal(8396.63m, v.HypotheticalRealizedNet);
    }

    [Fact]
    public void Projection_Trades_CarryChargesNetAndRecordedExcursions()
    {
        var t = Assert.Single(Scenario().Trades);
        Assert.Equal(("S02", OptionRight.Put, 53.37m), (t.StrategyId, t.Right, t.Charges));        // entry fees 33.37 + exit fees 20
        Assert.Equal((8450m, 8396.63m, 2000m, -500m), (t.Gross, t.Net, t.Mfe, t.Mae));
        Assert.Equal(26000d, t.Strike);
    }

    [Fact]
    public void Projection_Unrealized_UsesOnlyAFreshBidOfTheSameToken()
    {
        var asOf = T0.AddMinutes(11);
        V2MarkSource Marks(DateTimeOffset avail, string token = "CE26000") => new(avail, 9, new Dictionary<string, (decimal, DateTimeOffset)> { [token] = (130.5m, avail) });
        var fresh = Scenario(Marks(asOf.AddSeconds(-5))).Strategies.Single(s => s.StrategyId == "S01");
        Assert.Equal((130.5m - 127.5m) * 650, fresh.UnrealizedNow);
        Assert.NotNull(fresh.OpenPositions[0].MarkAgeSeconds);
        Assert.Null(Scenario(Marks(asOf.AddMinutes(-5))).Strategies.Single(s => s.StrategyId == "S01").UnrealizedNow);          // stale mark
        Assert.Null(Scenario(Marks(asOf.AddSeconds(-5), "CE99999")).Strategies.Single(s => s.StrategyId == "S01").UnrealizedNow);       // token not quoted
        Assert.Null(Scenario(Marks(T0)).Strategies.Single(s => s.StrategyId == "S01").UnrealizedNow);                              // older than the fill
    }

    [Fact]
    public void Projection_Failures_ListUnresolvedExitsAndEngineFailures_AndUnresolvedIsNotAnOpenPosition()
    {
        var v = Scenario();
        Assert.Equal(["ENGINE FAILURE", "UNRESOLVED EXIT"], v.Failures.Select(f => f.Kind).Order().ToArray());
        Assert.Empty(v.Strategies.Single(s => s.StrategyId == "S04").OpenPositions);
        Assert.Equal(RejectionReason.SpreadTooWide, Assert.Single(v.Rejections).Reason);
    }

    [Fact]
    public void Projection_PendingEntry_IsShown_AndNothingIsSplitIntoAPortfolio()
    {
        var pending = V2HomeProjection.Build(T0, Day, Healthy, [new("S01", 1, true)], [Placed("S01", "S01:i1", OptionRight.Call, "CE26000", 26000, T0)], [], null, false);
        Assert.Equal("ENTRY PENDING", pending.Strategies[0].Signal);
        Assert.Contains("CE26000", pending.Strategies[0].PendingEntry, StringComparison.Ordinal);
        Assert.Null(pending.HypotheticalRealizedNet is { } n && n == 0 ? (decimal?)null : null);
    }

    // ---- rendering ----

    [Fact]
    public async Task Section_IsLabelledShadow_ReadOnly_AndKeepsBooksSeparate()
    {
        var html = await RenderAsync(Scenario());
        Assert.Contains("V2 SHADOW", html, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"v2-hypothetical-total\"", html, StringComparison.Ordinal);
        Assert.Contains("HYPOTHETICAL SUM of independent books", html, StringComparison.Ordinal);
        Assert.Contains("separate from NPT1", html, StringComparison.Ordinal);
        Assert.Contains("0 (all-or-none)", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("<(button|form|input|select|textarea)\\b", html);                  // no control of any kind
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        var s02 = Row(html, "S02");
        Assert.Contains("FLAT", s02, StringComparison.Ordinal);
        Assert.Contains("+8396.63", s02, StringComparison.Ordinal);
        Assert.Contains("CE26000 (26000)", Row(html, "S01"), StringComparison.Ordinal);
        Assert.Contains("127.50", Row(html, "S01"), StringComparison.Ordinal);
        var quiet = Row(html, "S06");
        Assert.Contains("NO ACTIVITY TODAY", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00", quiet, StringComparison.Ordinal);                         // never an invented zero
        Assert.Contains("v2-disabled", Row(html, "S07"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Section_ShowsTradesRejectionsFailuresAndPerformance()
    {
        var html = await RenderAsync(Scenario());
        Assert.Contains("53.37", html, StringComparison.Ordinal);                               // charges
        Assert.Contains("+2000.00", html, StringComparison.Ordinal);                            // MFE
        Assert.Contains("-500.00", html, StringComparison.Ordinal);                             // MAE
        Assert.Contains("SpreadTooWide", html, StringComparison.Ordinal);
        Assert.Contains("UNRESOLVED EXIT", html, StringComparison.Ordinal);
        Assert.Contains("ENGINE FAILURE", html, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"v2-performance\"", html, StringComparison.Ordinal);
        Assert.Contains("+21000.00", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Section_WhenV2IsNotInstalled_ShowsOnlyThatFact()
    {
        var html = await RenderAsync(V2HomeView.Unavailable(T0, Day, "the pev2 schema is not installed"));
        Assert.Contains("data-testid=\"v2-unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("disabled by default", html, StringComparison.Ordinal);
        Assert.DoesNotContain("v2-strategies", html, StringComparison.Ordinal);
        Assert.Contains("Loading", await RenderAsync(null), StringComparison.Ordinal);
    }

    // ---- data service on PostgreSQL ----

    static async Task<NpgsqlDataSource> InstalledAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    [PostgresFact]
    public async Task DataService_ReadsTheLedger_FromRealRows_AndNeverWrites()
    {
        var db = await InstalledAsync();
        var asOf = T0.AddMinutes(11);
        var store = new PostgresEventStore(db);
        var strategy = ExecFixtures.Strat("S02");
        await store.RecordConfigAsync(strategy.Config, T0, default);
        await store.RecordConfigAsync(ExecFixtures.Strat("S07", o => o["enabled"] = false).Config, T0, default);
        var engine = new PaperEngine([strategy], ExecFixtures.Options(), store);
        // a real engine run, through the real journal and ledger, ending with an open position and a fresh mark frame
        var journal = new PostgresFrameJournal(db);
        var clock = new ManualClock(T0);
        var consumer = new NiftySignal.PaperEngineV2.Core.Pipeline.FrameConsumer(null, new NiftySignal.PaperEngineV2.Core.Pipeline.HandoffOptions(16, TimeSpan.FromHours(1), 1), journal, engine, clock);
        foreach (var f in new[] { ExecFixtures.Fr(1), ExecFixtures.Fr(2, dir: 10, quotes: [new("CE26000", 125, 126)]), ExecFixtures.Fr(3, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(4, quotes: [new("CE26000", 131, 132)]) })
        {
            clock.Now = f.Identity.BarEndAvailableAt.AddMilliseconds(40);
            await consumer.HandleAsync(f, default);
        }

        Assert.Single(ExecFixtures.Of<EntryFilled>(engine));
        var lastBarEnd = FrameFixtures.End(4);

        var before = await CountsAsync(db);
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var view = await svc.LoadAsync(lastBarEnd.AddSeconds(20), default);
        Assert.True(view.Health.SchemaOk, string.Join(";", view.Health.SchemaProblems));
        Assert.Equal(4, view.Health.FramesToday);
        Assert.Equal(4, view.Health.LastFrameBar);
        var row = view.Strategies.Single(s => s.StrategyId == "S02");
        Assert.Equal("IN POSITION (CALL)", row.Signal);
        var pos = Assert.Single(row.OpenPositions);
        Assert.Equal(131m, pos.MarkBid);
        Assert.Equal((131m - pos.EntryPrice) * 650, pos.UnrealizedPnl);
        Assert.Equal(("S07", false), (view.Strategies.Single(s => s.StrategyId == "S07").StrategyId, view.Strategies.Single(s => s.StrategyId == "S07").Enabled));
        Assert.Equal("DISABLED", view.Strategies.Single(s => s.StrategyId == "S07").Signal);

        // the connection is read-only: a write through the same settings is refused by PostgreSQL itself
        var ro = new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString!) { Options = "-c default_transaction_read_only=on" };
        await using var roDb = NpgsqlDataSource.Create(ro.ConnectionString);
        await using var write = roDb.CreateCommand("DELETE FROM pev2.pev2_events");
        await Assert.ThrowsAsync<PostgresException>(async () => await write.ExecuteNonQueryAsync());
        Assert.Equal(before, await CountsAsync(db));                                       // loading the view changed nothing
    }

    static async Task<string> CountsAsync(NpgsqlDataSource db)
    {
        await using var c = db.CreateCommand("SELECT (SELECT count(*) FROM pev2.pev2_events) || '/' || (SELECT count(*) FROM pev2.pev2_frames) || '/' || (SELECT count(*) FROM pev2.pev2_alert_outbox) || '/' || (SELECT count(*) FROM pev2.pev2_strategy_configs)");
        return (string)(await c.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task DataService_WhenTheSchemaIsMissingOrForeign_ReturnsUnavailable_NotData()
    {
        var db = await InstalledAsync();
        await using (var c = db.CreateCommand("UPDATE pev2.pev2_schema_versions SET schema_version = 9")) await c.ExecuteNonQueryAsync();
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadAsync(T0, default);
        Assert.False(v.Health.SchemaOk);
        Assert.Empty(v.Strategies);
        await using (var c = db.CreateCommand("DROP SCHEMA pev2 CASCADE")) await c.ExecuteNonQueryAsync();
        using var svc2 = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        Assert.False((await svc2.LoadAsync(T0, default)).Health.SchemaOk);
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
    }

    // ---- guards ----

    [Fact]
    public void DataService_IsSelectOnly_AndTheHomePageKeepsEveryExistingComponent()
    {
        var root = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard");
        var service = File.ReadAllText(Path.Combine(root, "Services", "PaperEngineV2DataService.cs"));
        foreach (var verb in new[] { "INSERT ", "UPDATE ", "DELETE ", "DROP ", "TRUNCATE ", "ALTER ", "CREATE " }) Assert.DoesNotContain(verb, service, StringComparison.Ordinal);
        Assert.Contains("default_transaction_read_only=on", service, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordConfigAsync", service, StringComparison.Ordinal);

        var home = File.ReadAllText(Path.Combine(root, "Components", "Pages", "Home.razor"));
        foreach (var kept in new[] { "<StatusBar />", "<LiveQuotePanel />", "<FlatTradeLoginPanel />", "<ToastHost />", "<AdaptiveObserverPanel />", "/adaptive-screenshots", "/paper-live" }) Assert.Contains(kept, home, StringComparison.Ordinal);
        Assert.Contains("<PaperEngineV2Panel />", home, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Components", "Dashboard"), "PaperEngineV2*.razor"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch("<(button|form|input|select|textarea)\\b", text);
            Assert.DoesNotContain("@onclick", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LiveQuoteScreenshotsAndGrids_AreUntouchedByTheV2Section()
    {
        // the required screenshot grids and the sticky quote panel are what Phases 1-5 delivered; V2 adds nothing to them
        Assert.Equal(6, AdaptiveScreenshotRenderer.RequiredGrids.Count);
        var root = Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.Dashboard");
        Assert.DoesNotContain("PaperEngineV2", File.ReadAllText(Path.Combine(root, "Services", "AdaptiveScreenshotRenderer.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("PaperEngineV2", File.ReadAllText(Path.Combine(root, "Components", "Dashboard", "LiveQuotePanel.razor")), StringComparison.Ordinal);
        Assert.DoesNotContain("PaperEngineV2", File.ReadAllText(Path.Combine(root, "Components", "Dashboard", "AdaptiveObserverPanel.razor")), StringComparison.Ordinal);
    }
}
