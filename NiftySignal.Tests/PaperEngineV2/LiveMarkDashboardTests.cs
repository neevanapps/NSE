using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Cross-process live marks: the Host's publisher writes a tiny snapshot table, the Dashboard's read-only service reads it. Publication, silence detection, trade-date isolation, fail-safe and rendering.</summary>
[Collection("PostgresTradingTests")]
public class LiveMarkDashboardTests
{
    static readonly DateTimeOffset Noon = new(2026, 11, 4, 6, 30, 0, TimeSpan.Zero);          // 12:00 IST
    static readonly DateOnly Day = new(2026, 11, 4);

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    static async Task<NpgsqlDataSource> FreshAsync()
    {
        var db = NpgsqlDataSource.Create(PostgresTestDatabase.ConnectionString!);
        await using (var drop = db.CreateCommand("DROP SCHEMA IF EXISTS pev2 CASCADE")) await drop.ExecuteNonQueryAsync();
        await using var create = db.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")));
        await create.ExecuteNonQueryAsync();
        return db;
    }

    static MarkPosition Pos(string id, DateOnly day, PositionState state = PositionState.Open, string strategy = "S01") =>
        new(strategy, 2, day, 1, id, OptionRight.Call, "CE26000", 26000, new DateOnly(2026, 11, 10), 130, 2, 127.50m, 30m, Noon.AddMinutes(-20), state, 2000, 0.05m, v => Math.Round(v * 0.001m, 2), 0m,
            state == PositionState.Closed ? 140m : null, null, state == PositionState.Closed ? 1500m : null);

    static CleanObserverTick Tick(long id, DateTimeOffset at, double bid = 130, double ask = 131) => new(id, at, at, 130.5, bid, ask, 650, 650, 0, null);

    static (LiveMarkPublisher Pub, LiveMarkBoard Board, Clock Clock, PostgresLiveMarkStore Store) Rig(NpgsqlDataSource db, params MarkPosition[] positions)
    {
        var board = new LiveMarkBoard(["CE26000"]);
        var clock = new Clock(Noon);
        var store = new PostgresLiveMarkStore(db);
        return (new LiveMarkPublisher(() => positions, () => board, store, clock, TimeSpan.FromSeconds(1)), board, clock, store);
    }

    [PostgresFact]
    public async Task APublishedMark_IsReadByTheDashboardService_WithItsQuoteAndPnl()
    {
        var db = await FreshAsync();
        var (pub, board, clock, _) = Rig(db, Pos("P1", Day));
        board.Observe("CE26000", Tick(7, Noon.AddSeconds(-0.4)));
        await pub.TickAsync(default);

        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadLiveMarksAsync(Noon.AddSeconds(0.2), default);
        Assert.Equal(V2PublisherState.Running, v.Publisher);
        var row = Assert.Single(v.Rows);
        Assert.Equal(MarkStatus.Fresh, row.Status);
        Assert.Equal(130d, row.Mark.Bid); Assert.Equal(7L, row.Mark.QuoteTickId);
        Assert.Equal((130m - 127.50m) * 130, row.Mark.GrossBidMarked);
        Assert.Equal(0.6, row.QuoteAgeSeconds!.Value, 3);                                          // the Dashboard recomputes the age from the quote's own availability time
        clock.Now = Noon;
    }

    [PostgresFact]
    public async Task ASilentPublisher_TurnsFreshRowsStale_AndNeverLooksLikeAnEmptyBook()
    {
        var db = await FreshAsync();
        var (pub, board, _, _) = Rig(db, Pos("P1", Day));
        board.Observe("CE26000", Tick(7, Noon));
        await pub.TickAsync(default);
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadLiveMarksAsync(Noon.AddSeconds(30), default);                          // the Host stopped publishing 30 s ago
        Assert.Equal(V2PublisherState.Silent, v.Publisher);
        Assert.Equal(MarkStatus.Stale, Assert.Single(v.Rows).Status);
        Assert.Contains("silent", v.Message, StringComparison.Ordinal);
        Assert.Null(V2LiveMarksView.IndicativeTotal(v.Rows));                                         // no total is shown from stale marks
    }

    [PostgresFact]
    public async Task RowsOfAnotherTradeDate_AreNeverShown()
    {
        var db = await FreshAsync();
        var (pub, board, _, _) = Rig(db, Pos("OLD", new DateOnly(2026, 11, 3)), Pos("NEW", Day));
        board.Observe("CE26000", Tick(7, Noon));
        await pub.TickAsync(default);
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadLiveMarksAsync(Noon, default);
        Assert.Equal(["NEW"], v.Rows.Select(r => r.Mark.PositionId).ToArray());
        var tomorrow = await svc.LoadLiveMarksAsync(Noon.AddDays(1), default);                       // the next IST day: yesterday's heartbeat and rows are invisible
        Assert.Equal(V2PublisherState.Waiting, tomorrow.Publisher); Assert.Empty(tomorrow.Rows);
    }

    [PostgresFact]
    public async Task RepeatedPublication_UpsertsTheSameRows_NeverGrowsTheTable()
    {
        var db = await FreshAsync();
        var (pub, board, clock, _) = Rig(db, Pos("P1", Day), Pos("P2", Day, PositionState.Closed));
        for (var i = 0; i < 25; i++)
        {
            board.Observe("CE26000", Tick(i + 1, Noon.AddSeconds(i)));
            clock.Now = Noon.AddSeconds(i);
            await pub.TickAsync(default);
        }

        await using var c = db.CreateCommand("SELECT count(*) FROM pev2.pev2_live_marks");
        Assert.Equal(3L, await c.ExecuteScalarAsync());                                              // two positions + the heartbeat row, whatever the number of samples
    }

    [PostgresFact]
    public async Task AMissingTable_DisablesTheStoreAfterOneFailure_AndTheDashboardSaysNotInstalled()
    {
        var db = await FreshAsync();
        await using (var drop = db.CreateCommand("DROP TABLE pev2.pev2_live_marks")) await drop.ExecuteNonQueryAsync();
        var (pub, board, _, store) = Rig(db, Pos("P1", Day));
        board.Observe("CE26000", Tick(1, Noon));
        await pub.TickAsync(default); await pub.TickAsync(default); await pub.TickAsync(default);
        Assert.Equal(1, pub.Failures);                                                                // one failed statement, then the store is quiet
        Assert.Contains("AddObservability", store.DisabledReason, StringComparison.Ordinal);
        using var svc = new PaperEngineV2DataService(PostgresTestDatabase.ConnectionString!);
        var v = await svc.LoadLiveMarksAsync(Noon, default);
        Assert.Equal(V2PublisherState.NotInstalled, v.Publisher);
        Assert.Contains("not installed", v.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task APostgresOutage_DoesNotChangeTheLedger_AndTheEngineKeepsFilling()
    {
        var db = await FreshAsync();
        var bad = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        var engine = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options());
        var pub = new LiveMarkPublisher(() => engine.MarkPositions, () => new LiveMarkBoard(["CE26000"]), new PostgresLiveMarkStore(bad), TimeProvider.System, TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(() => pub.RunAsync(cts.Token));
        ExecFixtures.Run(engine, ExecutionTests.Day());
        for (var i = 0; i < 200 && pub.Failures < 1; i++) await Task.Delay(50);                       // an unreachable server may take its connect timeout to fail
        cts.Cancel();
        await loop;
        var plain = new PaperEngine([ExecFixtures.Strat("S01")], ExecFixtures.Options());
        ExecFixtures.Run(plain, ExecutionTests.Day());
        Assert.Equal(plain.Events, engine.Events);
        Assert.True(pub.Failures >= 1);
        _ = db;
    }

    // ---- rendering ----

    static async Task<string> RenderAsync(V2LiveMarksView? view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<PaperEngineV2LiveMarksSection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    static V2LiveMarkRow Row(MarkPosition p, MarkQuote? q, DateTimeOffset now) =>
        new(LiveMarkCalculator.Compute(p, q, now, 1), LiveMarkCalculator.Compute(p, q, now, 1).Status, "", q is null ? null : (now - q.AvailableAt).TotalSeconds, now);

    [Fact]
    public async Task TheSection_ShowsAllThreeStrategies_WithUnavailablePricesForMissingAndInvalidQuotes_AndNoControls()
    {
        var rows = new[]
        {
            Row(Pos("A", Day, strategy: "TOTAL_ACTIVITY_5_10"), new MarkQuote("CE26000", 130, 131, 130.5, Noon, 1), Noon),
            Row(Pos("B", Day, strategy: "TOTAL_VOTE_4_OF_6"), null, Noon),
            Row(Pos("C", Day, strategy: "TOTAL_VOTE_5_OF_6"), new MarkQuote("CE26000", 0, 131, 130.5, Noon, 1), Noon),
            Row(Pos("D", Day, PositionState.Closed, "TOTAL_VOTE_5_OF_6"), null, Noon),
        };
        var html = await RenderAsync(new V2LiveMarksView(V2PublisherState.Running, null, Day, Noon, Noon, rows));
        foreach (var s in new[] { "TOTAL_ACTIVITY_5_10", "TOTAL_VOTE_4_OF_6", "TOTAL_VOTE_5_OF_6" }) Assert.Contains(s, html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"Fresh\"", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"Missing\"", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"Invalid\"", html, StringComparison.Ordinal);
        Assert.Contains("data-status=\"Closed\"", html, StringComparison.Ordinal);
        Assert.Contains("unavailable", html, StringComparison.Ordinal);
        Assert.Matches("data-position=\"C\".*?v2-mark-bid\">unavailable<", html.Replace('\n', ' '));    // a zero bid is Invalid and is never rendered as 0.00
        Assert.Contains("+325.00", html, StringComparison.Ordinal);
        Assert.Contains("not a fill", html, StringComparison.Ordinal);
        Assert.DoesNotMatch("<(button|form|input|select|textarea)\\b", html);
    }

    [Fact]
    public async Task TheSection_DegradesToAMessage_WhenNotInstalled_Silent_OrLoading()
    {
        Assert.Contains("not installed", await RenderAsync(V2LiveMarksView.Unavailable(V2PublisherState.NotInstalled, "live marks are not installed (the owner has not run the AddObservability script)", Day, Noon)), StringComparison.Ordinal);
        Assert.Contains("Loading", await RenderAsync(null), StringComparison.Ordinal);
        var silent = await RenderAsync(new V2LiveMarksView(V2PublisherState.Silent, "the Host's mark publisher has been silent for 30s", Day, Noon, Noon.AddSeconds(-30), []));
        Assert.Contains("SILENT", silent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndicativeTotal_IsRealizedPlusOpenEstimates_OnlyWhenEveryOpenMarkIsFresh()
    {
        var fresh = Row(Pos("A", Day), new MarkQuote("CE26000", 130, 131, 130.5, Noon, 1), Noon);
        var total = V2LiveMarksView.IndicativeTotal([fresh]);
        Assert.Equal(fresh.Mark.RealizedNetToday + fresh.Mark.EstNetLiquidation, total);
        var stale = Row(Pos("B", Day), new MarkQuote("CE26000", 130, 131, 130.5, Noon.AddSeconds(-30), 1), Noon);
        Assert.Null(V2LiveMarksView.IndicativeTotal([fresh, stale]));
        Assert.Null(V2LiveMarksView.IndicativeTotal([]));
    }
}
