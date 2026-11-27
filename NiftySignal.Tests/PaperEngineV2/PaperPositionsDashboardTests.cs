using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Decision A8: the two paper-trading grids, and the Dashboard-side watch of the execution service. Synthetic data only.</summary>
public sealed class PaperPositionsDashboardTests
{
    static readonly DateOnly Day = new(2026, 11, 26);
    static readonly DateTimeOffset Now = new(2026, 11, 26, 5, 30, 0, TimeSpan.Zero);      // 11:00 IST, a Thursday
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(30);
    static readonly string[] Ids = ["TOTAL_ACTIVITY_5_10", "TOTAL_ACTIVITY_5_10_AVG5", "TOTAL_VOTE_5_OF_6", "TOTAL_VOTE_5_OF_6_AVG5"];

    static StrategyStatus Status(string id, PendingEntryView? pending = null, bool faulted = false) =>
        new(id, 1, EntryTiming.Immediate, faulted, 0, 10, 0m, 50_000m, false, 25, pending);

    static PositionView Position(string strategy, string id, PositionState state, int remaining = 65, decimal? net = null, decimal? gross = null, decimal partialNet = 0m) =>
        new(id, strategy, OptionRight.Call, "CE25000", 25000, Day.AddDays(5), 130, state, 100m, 30m, null, null, gross, net, state == PositionState.Closed ? ExitReason.OppositeSignal : null, 0m, 0m, 0, 1,
            state == PositionState.Closed ? 0 : remaining, 0m, partialNet, 125m, "Filled", Now.AddMinutes(-5));

    static ExecutionStateView State(IReadOnlyList<PositionView> positions, DateTimeOffset? heartbeat = null, IReadOnlyList<StrategyStatus>? strategies = null, DateOnly? day = null, params string[] conditions)
    {
        var at = heartbeat ?? Now.AddSeconds(-1);
        var snapshot = new ExecutionSnapshot(at, "Running", day ?? Day, 7, 30, true, null, null, strategies ?? Ids.Select(i => Status(i)).ToList(), positions, conditions);
        return new ExecutionStateView(1, "test", at, Day.ToString("yyyy-MM-dd"), "Running", ExecutionStore.SnapshotJson(snapshot));
    }

    static V2LiveMarkRow Mark(PositionView p, double bid, double quoteAge = 0.5)
    {
        var mark = new MarkPosition(p.StrategyId, 1, Day, 7, p.PositionId, p.Right, p.Token, p.Strike, p.Expiry, p.RemainingQuantity, 2, p.EntryPrice, 15m, p.EntryAt!.Value, p.State, 2000, 0m, _ => 20m, 0m, null, null, null);
        var computed = PositionMarkCalculator.Compute(mark, new MarkQuote(p.Token, bid, bid + 0.5, bid, Now.AddSeconds(-quoteAge), 1), Now, 1);
        return new V2LiveMarkRow(computed, computed.Status, computed.StatusDetail, quoteAge, Now);
    }

    static V2LiveMarksView Marks(params V2LiveMarkRow[] rows) => new(V2PublisherState.Running, null, Day, Now, Now, rows);

    [Fact]
    public void BothGrids_HaveOneRowPerEnabledStrategy_EvenWithNoPosition()
    {
        var view = PaperPositionsProjection.Build(Now, State([]), Marks(), Stale);
        Assert.Equal(Ids, view.Current.Select(r => r.StrategyId));
        Assert.Equal(Ids, view.Closed.Select(r => r.StrategyId));
        Assert.All(view.Current, r => { Assert.Equal("Flat", r.Status); Assert.Null(r.Unrealized); Assert.Null(r.Total); });
        Assert.All(view.Closed, r => Assert.Equal((0, 0m), (r.Closed, r.Net)));
        Assert.Null(view.Notice);
    }

    [Fact]
    public void APartialProfit_IsCountedInExactlyOneGrid()
    {
        var open = Position(Ids[0], "p2", PositionState.Open, remaining: 65, partialNet: 900m);
        var closed = Position(Ids[0], "p1", PositionState.Closed, net: 500m, gross: 560m, partialNet: 300m);      // its Net already includes its partial
        var view = PaperPositionsProjection.Build(Now, State([closed, open]), Marks(Mark(open, 110)), Stale);

        var current = view.Current.Single(r => r.StrategyId == Ids[0]);
        Assert.Equal(900m, current.RealizedPartial);
        Assert.Equal((110m - 100m) * 65 - 15m - 20m, current.Unrealized);                                          // remaining quantity only, its share of the entry charges
        Assert.Equal(900m + current.Unrealized, current.Total);
        var summary = view.Closed.Single(r => r.StrategyId == Ids[0]);
        Assert.Equal((1, 560m, 60m, 500m), (summary.Closed, summary.Gross, summary.Charges, summary.Net));          // neither the open partial nor the closed partial a second time
    }

    [Fact]
    public void AStaleQuote_ShowsTheLastBid_ButNoProfit()
    {
        var open = Position(Ids[0], "p", PositionState.Open);
        var row = PaperPositionsProjection.Build(Now, State([open]), Marks(Mark(open, 110, quoteAge: 20)), Stale).Current[0];
        Assert.Equal((MarkStatus.Stale, 110m), (row.Quote, row.Bid));
        Assert.Null(row.Unrealized);
        Assert.Null(row.Total);
    }

    [Fact]
    public void AMarkOfAnotherQuantity_IsNotCombinedWithThePartialProfit()
    {
        var beforePartial = Position(Ids[0], "p", PositionState.Open, remaining: 130);
        var afterPartial = Position(Ids[0], "p", PositionState.Open, remaining: 65, partialNet: 900m);
        var row = PaperPositionsProjection.Build(Now, State([afterPartial]), Marks(Mark(beforePartial, 110)), Stale).Current[0];
        Assert.Equal(MarkStatus.Stale, row.Quote);
        Assert.Null(row.Total);
        Assert.Equal(900m, row.RealizedPartial);
    }

    [Fact]
    public void ASilentService_MarksEverythingAsLastReported_AndNeverReportsAPositionClosed()
    {
        var open = Position(Ids[0], "p", PositionState.ExitPending);
        var view = PaperPositionsProjection.Build(Now, State([open], heartbeat: Now.AddSeconds(-45)), Marks(Mark(open, 110)), Stale);
        Assert.True(view.Stale);
        Assert.Contains("EXECUTION SERVICE SILENT", view.Notice);
        Assert.All(view.Current, r => Assert.StartsWith("Last reported:", r.Status));
        var row = view.Current[0];
        Assert.Equal(MarkStatus.Stale, row.Quote);
        Assert.Null(row.Unrealized);
        Assert.Equal(65, row.Position!.RemainingQuantity);
        Assert.Equal(0, view.Closed[0].Closed);
    }

    [Fact]
    public void AnEarlierDaysReport_HasNoTodaySummary_AndAnUnreportedServiceShowsNoRows()
    {
        var old = PaperPositionsProjection.Build(Now, State([Position(Ids[0], "p", PositionState.Unresolved)], heartbeat: Now.AddDays(-1), day: Day.AddDays(-1)), Marks(), Stale);
        Assert.Empty(old.Closed);
        Assert.True(old.Current[0].Critical);
        Assert.Contains("UNRESOLVED", old.Current[0].Status);
        var none = PaperPositionsProjection.Build(Now, null, Marks(), Stale);
        Assert.Empty(none.Current);
        Assert.Contains("never reported", none.Notice);
    }

    [Fact]
    public void PendingEntries_AreNamed()
    {
        var strategies = new[]
        {
            Status(Ids[0], new PendingEntryView("i", OptionRight.Call, Now, "CE25000", false, null)),
            Status(Ids[1], new PendingEntryView("j", OptionRight.Put, Now, null, true, "PE25000")),
            Status(Ids[2], faulted: true),
        };
        var view = PaperPositionsProjection.Build(Now, State([], strategies: strategies), Marks(), Stale);
        Assert.Equal(["Waiting for Avg5", "Entry order pending", "Faulted"], view.Current.Select(r => r.Status));
    }

    static async Task<string> RenderAsync(PaperPositionsView view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<PaperEngineV2PositionsSection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    [Fact]
    public async Task TheSection_HasExactlyTwoGrids_WithFourRowsEach_AndShowsUnavailableInsteadOfZero()
    {
        var open = Position(Ids[1], "p", PositionState.Open, partialNet: 900m);
        var html = await RenderAsync(PaperPositionsProjection.Build(Now, State([open], conditions: "protective flatten: 1 position(s) outstanding"), Marks(), Stale));
        Assert.Equal(2, Regex.Matches(html, "<table[ >]").Count);
        foreach (var grid in new[] { "paper-current", "paper-closed" })
        {
            var table = Regex.Match(html, "<table[^>]*data-testid=\"" + grid + "\".*?</table>", RegexOptions.Singleline).Value;
            Assert.Equal(4, Regex.Matches(table, "<tr data-strategy=").Count);
        }

        Assert.Contains("protective flatten: 1 position(s) outstanding", html);
        Assert.Contains(">unavailable</td>", html);                               // no mark: no bid and no unrealized figure
        Assert.Contains("+900.00", html);
        Assert.DoesNotMatch("<(button|form|input|select|textarea)\\b", html);
    }

    // ---- the watch on the execution service ----

    static readonly ExecutionHealthOptions Health = new();

    [Fact]
    public void Health_ASilentService_IsCritical_AndNamesTheUnprotectedPositions()
    {
        var state = State([Position(Ids[0], "p", PositionState.Open)], heartbeat: Now.AddSeconds(-31));
        var health = ExecutionHealth.Evaluate(state, null, Now, Health);
        Assert.Equal(ExecutionCondition.Silent, health.Condition);
        Assert.True(health.IsCritical);
        Assert.Contains("NO protection", health.Message);
        Assert.Contains("CE25000 x65", health.Message);
        Assert.Equal(ExecutionCondition.Healthy, ExecutionHealth.Evaluate(State([], heartbeat: Now.AddSeconds(-29)), null, Now, Health).Condition);
    }

    [Fact]
    public void Health_OutsideHours_SendsNothing_ButKeepsLeftOverPositionsOnTheBanner()
    {
        var night = Now.AddHours(8);
        var health = ExecutionHealth.Evaluate(State([Position(Ids[0], "p", PositionState.Unresolved)]), null, night, Health);
        Assert.Equal(ExecutionCondition.OutsideHours, health.Condition);
        Assert.True(health.ShowBanner);
        Assert.Null(new ExecutionAlertTracker(Health).Due(health));
        Assert.False(ExecutionHealth.Evaluate(State([]), null, night, Health).ShowBanner);
    }

    [Fact]
    public void Health_WatchingStartsWithTheFirstReport_UnlessRequired()
    {
        Assert.Equal(ExecutionCondition.NotWatched, ExecutionHealth.Evaluate(null, null, Now, Health).Condition);
        Assert.Equal(ExecutionCondition.NeverReported, ExecutionHealth.Evaluate(null, null, Now, new ExecutionHealthOptions { Required = true }).Condition);
        Assert.Equal(ExecutionCondition.NotWatched, ExecutionHealth.Evaluate(State([], heartbeat: Now.AddHours(-1)), null, Now, new ExecutionHealthOptions { Enabled = false }).Condition);
    }

    [Theory]
    [InlineData("Recovering", false)]
    [InlineData("Unreconciled", false)]
    [InlineData("Running", false)]
    public void Health_RecoveryOrReconciliationFailure_IsCritical_WithoutASecondMessage(string status, bool reconciled)
    {
        var snapshot = new ExecutionSnapshot(Now, status, Day, 7, 30, reconciled, "blocked", null, [], [], ["why"]);
        var health = ExecutionHealth.Evaluate(new ExecutionStateView(1, "t", Now, Day.ToString("yyyy-MM-dd"), status, ExecutionStore.SnapshotJson(snapshot)), null, Now, Health);

        Assert.Equal(ExecutionCondition.RecoveryFailed, health.Condition);
        Assert.True(health.IsCritical && !health.IsOutage && health.ShowBanner);
        Assert.Null(new ExecutionAlertTracker(Health).Due(health));        // the running service sends its own message
    }

    [Fact]
    public void Health_OutstandingProtectiveAction_IsCritical()
    {
        Assert.Equal(ExecutionCondition.ProtectionOutstanding, ExecutionHealth.Evaluate(State([Position(Ids[0], "p", PositionState.Unresolved)]), null, Now, Health).Condition);

        var flattening = new ExecutionSnapshot(Now, "Protecting", Day, 7, 30, true, "x", "observer heartbeat is stale", [], [Position(Ids[0], "p", PositionState.ExitPending)], []);
        var view = new ExecutionStateView(1, "t", Now, Day.ToString("yyyy-MM-dd"), "Protecting", ExecutionStore.SnapshotJson(flattening));
        Assert.Equal(ExecutionCondition.ProtectionOutstanding, ExecutionHealth.Evaluate(view, null, Now, Health).Condition);
    }

    [Fact]
    public void Health_AlertsOnce_RemindsAtTheInterval_AndReportsRecovery()
    {
        var tracker = new ExecutionAlertTracker(Health);
        var silent = ExecutionHealth.Evaluate(State([], heartbeat: Now.AddMinutes(-1)), null, Now, Health);
        Assert.StartsWith("NiftySignal CRITICAL:", tracker.Due(silent));
        tracker.Delivered(silent);
        Assert.Null(tracker.Due(silent with { EvaluatedAtUtc = Now.AddMinutes(14) }));
        Assert.NotNull(tracker.Due(silent with { EvaluatedAtUtc = Now.AddMinutes(15) }));
        var healthy = ExecutionHealth.Evaluate(State([], heartbeat: Now.AddMinutes(16)), null, Now.AddMinutes(16), Health);
        Assert.StartsWith("NiftySignal RECOVERED:", tracker.Due(healthy));
        tracker.Delivered(healthy);
        Assert.Null(tracker.Due(healthy));
    }
}
