using System.Text.Json;
using System.Text.Json.Nodes;
using NiftySignal.AdaptiveObserver;
using NiftySignal.ExecutionService;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Tests.Trading.Data;
using Rig = NiftySignal.Tests.PaperEngineV2.ExecutionServicePostgresTests.Rig;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// The paged journal replay of a restart: whatever the page size, the rebuilt state (strategies, pending Avg5 intent, positions, partial exit, frozen target, counters, P&amp;L, events, bar cursor) is the state the
/// stopped process held, nothing is journaled or booked twice, and the continuation is identical. Synthetic inputs on a scratch PostgreSQL ledger.
/// </summary>
[Collection("PostgresTradingTests")]
public class ExecutionJournalPagedRecoveryTests
{
    const string Policy = """
        { "enabled": true, "exitQuantityPercent": 50, "vixThreshold": 16, "maxVixAgeSeconds": 60, "freezeAtEntry": true, "missingVixPolicy": "DisablePartialTargetForPosition",
          "targets": [ { "dte": [6, 5], "belowVixPercent": 20, "atOrAboveVixPercent": 25 }, { "dte": [0], "belowVixPercent": 40, "atOrAboveVixPercent": 50 } ] }
        """;

    static CompiledStrategy[] Strategies() =>
    [
        ExecFixtures.Strat("S01", o => o["partialProfit"] = JsonNode.Parse(Policy)),
        ExecFixtures.Strat("S02", o => { o["entryTiming"] = "Avg5Pullback"; o["partialProfit"] = JsonNode.Parse(Policy); }),
    ];

    sealed record Run(string Journal, string Events, string Final, IReadOnlyList<string> AtCuts);

    static string State(ExecutionCoordinator c) => JsonSerializer.Serialize(new
    {
        Strategies = c.Engine!.Statuses(),
        Positions = c.Engine.Runtimes.SelectMany(r => r.Positions()),
        Events = c.Engine.Events.Select(EventSerializer.Serialize),
    });

    static async Task<string> RowsAsync(Rig rig, string sql)
    {
        await using var c = rig.Db.CreateCommand(sql);
        await using var r = await c.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await r.ReadAsync()) rows.Add(r.GetString(0));
        return string.Join('\n', rows);
    }

    static async Task<Run> RunAsync(int batch)
    {
        await using var rig = await Rig.CreateAsync();
        var strategies = Strategies();
        rig.Feed.Average = bar => bar >= 9 ? 200 : 100;                 // the Avg5 condition (mid strictly below) first holds at bar 9
        var (c, _) = rig.New(strategies: strategies, journalBatch: batch);
        long vixId = 1;
        void Vix() => rig.Feed.Ticks.Enqueue(new FeedTick("VIX", new CleanObserverTick(++vixId, rig.Clock.Now, rig.Clock.Now, 14, 0, 0, 0, 0, 1, null), true));
        async Task Bar(int seq, double dir = 0)
        {
            rig.Clock.Now = FrameFixtures.End(seq).AddSeconds(1);
            Vix();
            await rig.BarAsync(c, seq, dir);
        }

        async Task Quote(double bid, double ask)
        {
            rig.Advance(2);
            Vix();
            rig.Feed.Quote("CE26000", bid, ask, rig.Clock.Now.AddMilliseconds(-100));
            await rig.CycleAsync(c, quotes: false);
        }

        var cuts = new List<string>();
        async Task Restart()
        {
            var before = State(c);
            var lastBar = c.Snapshot!.LastBarSeq;
            var journaled = await rig.CountAsync("pev2_execution_journal");
            var stored = await rig.CountAsync("pev2_events");
            await c.DisposeAsync();
            (c, _) = rig.New(strategies: strategies, journalBatch: batch);
            Vix();                                                          // market data is current, as it would be for the real feed; the same instant, no quote
            await rig.CycleAsync(c, quotes: false);
            Assert.True(c.Snapshot!.Reconciled, string.Join("; ", c.Snapshot.Conditions));
            Assert.Equal(before, State(c));
            Assert.Equal(lastBar, c.Snapshot.LastBarSeq);
            // the replayed inputs are not journaled again; the new market data of the first step may be one new input
            Assert.InRange(await rig.CountAsync("pev2_execution_journal"), journaled, journaled + 1);
            Assert.Equal(stored, await rig.CountAsync("pev2_events"));
            cuts.Add(before);
        }

        for (var seq = 1; seq <= 4; seq++) await Bar(seq);
        await Bar(5, dir: 10);
        await Restart();                                                    // an order outstanding and a pending Avg5 intent
        Assert.Single(c.Engine!.Events.OfType<EntryOrderPlaced>());
        await Quote(125.5, 127);
        Assert.Single(c.Engine.Events.OfType<EntryFilled>());
        await Restart();                                                    // a position with a frozen target (DTE 6, VIX 14: 20 %) beside the pending intent
        var held = Assert.Single(c.Engine!.Runtimes.SelectMany(r => r.Positions()));
        Assert.Equal(153m, held.PartialTargetPrice);
        for (var seq = 6; seq <= 8; seq++) await Bar(seq);
        await Quote(160, 161);
        Assert.Single(c.Engine.Events.OfType<PartialExitRequested>());
        await Restart();                                                    // the partial exit ordered, not filled
        await Quote(160, 161);
        var partial = Assert.Single(c.Engine!.Runtimes.SelectMany(r => r.Positions()));
        Assert.True(partial.RemainingQuantity < partial.Quantity && partial.PartialNetPnl > 0m, $"{partial.RemainingQuantity}/{partial.Quantity} {partial.PartialNetPnl} {partial.PartialStatus}");
        await Restart();                                                    // partly closed, booked P&L
        for (var seq = 9; seq <= 25; seq++) { await Bar(seq); if (seq % 3 == 0) await Quote(125.5, 127); }
        await Quote(125.5, 127);
        Assert.Single(c.Engine!.Events.OfType<Avg5EntryEvidence>());       // the intent that survived four restarts is filled once
        Assert.Equal(2, c.Engine.Events.OfType<EntryFilled>().Count());
        Assert.Single(c.Engine.Events.OfType<PartialExitRequested>(), e => e.StrategyId == "S01");
        await Restart();

        // the cursor walks the whole journal once, in order, at any page size
        var store = new ExecutionStore(rig.Db);
        var ids = new List<long>();
        for (var page = await store.ReadJournalPageAsync(FrameFixtures.Day, 0, batch, default); page.Count > 0; page = await store.ReadJournalPageAsync(FrameFixtures.Day, ids[^1], batch, default))
        {
            Assert.True(page.Count <= batch);
            ids.AddRange(page.Select(x => x.Id));
        }

        Assert.Equal(await rig.CountAsync("pev2_execution_journal"), ids.Count);
        Assert.Equal(ids.Distinct().Order(), ids);
        return new Run(await RowsAsync(rig, "SELECT payload_json FROM pev2.pev2_execution_journal ORDER BY id"), await RowsAsync(rig, "SELECT payload_json FROM pev2.pev2_events ORDER BY event_id"), State(c), cuts);
    }

    [PostgresFact]
    public async Task RestartsAtEveryStage_RecoverTheSameState_AndContinueIdentically_AtAnyPageSize()
    {
        var whole = await RunAsync(5_000);                        // one page holds the day
        foreach (var batch in new[] { 1, 2, 7 })
        {
            var paged = await RunAsync(batch);
            Assert.Equal(whole.Journal, paged.Journal);
            Assert.Equal(whole.Events, paged.Events);
            Assert.Equal(whole.Final, paged.Final);
            Assert.Equal(whole.AtCuts, paged.AtCuts);
        }
    }

    [Fact]
    public void ReplayBatchSize_IsBoundedByConfiguration()
    {
        Assert.Empty(Errors(1));
        Assert.Empty(Errors(5_000));
        Assert.Contains(Errors(0), e => e.Contains("JournalReplayBatchSize"));
        Assert.Contains(Errors(5_001), e => e.Contains("JournalReplayBatchSize"));
    }

    static IEnumerable<string> Errors(int batch) => (new ExecutionServiceOptions { JournalReplayBatchSize = batch }).Validate().Where(e => e.Contains("JournalReplayBatchSize"));
}
