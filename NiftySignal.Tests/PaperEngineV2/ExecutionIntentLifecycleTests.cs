using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Temporary eligibility failures against terminal invalidation of a pending entry, and the retry rules of an Avg5 order that ended without a fill. Synthetic inputs only.</summary>
public partial class ExecutionLifecycleTests
{
    [Theory]
    [InlineData("blocked")] [InlineData("bad-bar")] [InlineData("spread")] [InlineData("no-contract")] [InlineData("stale-quote")] [InlineData("no-average")] [InlineData("above-average")]
    [InlineData("signal-weakens")] [InlineData("no-quote")]
    public void Avg5_TemporaryIneligibility_PreventsExecution_ButKeepsTheIntent(string how)
    {
        var sim = New(Strategy(avg5: true, edit: o => o["execution"]!["maxSpread"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "fraction": 0.02 }""")));
        sim.Bar(5, dir: 10);
        var seq = 5;
        switch (how)
        {
            case "blocked": sim.Tick(At(5), safety: new SafetyState(EntryBlock: "market data is stale"), averages: Average(200), quotes: [Ce()]); break;
            case "bad-bar": sim.Bar(6, entriesAllowed: false, averages: Average(200, bar: 6)); sim.Tick(At(6), averages: Average(200, bar: 6), quotes: [Ce()]); seq = 6; break;
            case "spread": sim.Tick(At(5), averages: Average(200), quotes: [Ce(100, 126)]); break;
            case "no-contract": sim.Tick(At(5), averages: Average(300), quotes: [Ce(199, 200)]); break;
            case "stale-quote": sim.Tick(At(5), averages: Average(200), quotes: [Ce(ageMs: 9_000)]); break;
            case "no-average": sim.Tick(At(5), quotes: [Ce()]); break;
            case "above-average": sim.Tick(At(5), averages: Average(100), quotes: [Ce()]); break;
            case "signal-weakens": sim.Bar(6, dir: 0); seq = 6; break;
            case "no-quote": sim.Tick(At(5), averages: Average(200)); break;
        }

        Assert.Empty(sim.Of<EntryOrderPlaced>());
        Assert.Empty(sim.Of<EntryIntentCancelled>());
        Assert.True(sim.Engine.Runtimes.Single().HasPendingEntry);

        if (seq == 6 && how == "bad-bar") { sim.Bar(7); seq = 7; }
        sim.Tick(At(seq, 5), averages: Average(200, bar: seq), quotes: [Ce()]);
        sim.Tick(At(seq, 6), averages: Average(200, bar: seq), quotes: [Ce()]);
        Assert.Single(sim.Of<EntryFilled>());
        Assert.Equal(1, sim.Status.TradesToday);
    }

    [Theory]
    [InlineData("blocked")] [InlineData("bad-bar")]
    public void Avg5_TemporaryBlock_CancelsTheOutstandingOrder_AndRetriesOnlyOnALaterQuote(string how)
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        sim.Tick(At(5), averages: Average(200), quotes: [Ce()]);
        var first = Assert.Single(sim.Of<EntryOrderPlaced>()).IntentId;
        var bar = 5;
        if (how == "blocked") sim.Tick(At(5, 2), safety: new SafetyState(EntryBlock: "market data is stale"), averages: Average(200), quotes: [Ce()]);
        else { sim.Bar(6, entriesAllowed: false, averages: Average(200, bar: 6)); bar = 6; }

        Assert.Empty(sim.Of<EntryFilled>());                                                  // a quote that could have filled it is ignored while blocked
        Assert.Equal(RejectionReason.EntriesBlocked, Assert.Single(sim.Of<EntryRejected>()).Reason);
        Assert.Empty(sim.Of<EntryIntentCancelled>());

        if (how == "bad-bar") { sim.Bar(7); bar = 7; }
        // healthy again, but the only quote is the one that existed when the order ended
        sim.Tick(At(bar, 2), averages: Average(200, bar: bar), quotes: [Ce(ageMs: how == "blocked" ? 100 : 1_999)]);
        sim.Tick(At(bar, 3), averages: Average(200, bar: bar), quotes: [Ce()]);
        sim.Tick(At(bar, 4), averages: Average(200, bar: bar), quotes: [Ce()]);
        Assert.Equal(2, sim.Of<EntryOrderPlaced>().Count);
        Assert.Equal(first + "#2", sim.Of<EntryOrderPlaced>()[1].IntentId);                   // the same logical intent
        Assert.Single(sim.Of<EntryFilled>());
        Assert.Equal(1, sim.Status.TradesToday);
    }

    [Fact]
    public void Avg5_ExpiredOrder_IsNeverRetriedAgainstTheSameQuote()
    {
        var sim = New(Strategy(avg5: true, edit: o => o["execution"]!["maxQuoteAgeMs"] = 600_000));
        sim.Bar(5, dir: 10);
        var quoteAt = At(5);
        sim.Tick(quoteAt, averages: Average(200), quotes: [Ce(ageMs: 0)]);
        Assert.Single(sim.Of<EntryOrderPlaced>());
        // the order expires; the same quote (same availability instant) is still the only one and stays "fresh"
        foreach (var s in new[] { 70, 71, 72 }) sim.Tick(At(5, s), averages: Average(200), quotes: [Ce(ageMs: (s - 1) * 1_000)]);
        Assert.Equal(RejectionReason.ExpiredOpportunity, Assert.Single(sim.Of<EntryRejected>()).Reason);
        Assert.Single(sim.Of<EntryOrderPlaced>());
        sim.Tick(At(5, 75), averages: Average(200), quotes: [Ce()]);
        Assert.Equal(2, sim.Of<EntryOrderPlaced>().Count);
        Assert.Empty(sim.Of<EntryFilled>());
    }

    [Fact]
    public void Avg5_UnaffordableContract_IsReportedOnce_AndTheIntentStays()
    {
        var sim = New(Strategy(avg5: true, edit: o => o["capital"]!["virtualCapital"] = 50_000));
        sim.Bar(5, dir: 10);
        for (var s = 1; s <= 5; s++) sim.Tick(At(5, s), averages: Average(200), quotes: [Ce()]);
        Assert.Equal(RejectionReason.InsufficientCapital, Assert.Single(sim.Of<EntryRejected>()).Reason);
        Assert.True(sim.Engine.Runtimes.Single().HasPendingEntry);
        Assert.Empty(sim.Of<EntryOrderPlaced>());
    }

    [Fact]
    public void ImmediateEntry_TemporaryBlock_ConsumesTheSignal_AndNeverWaits()
    {
        var sim = HoldingCall(Strategy());
        sim.Bar(7, dir: -10);                                                                  // reversal intent waiting for the exit
        sim.Tick(At(7, 1), safety: new SafetyState(EntryBlock: "market data is stale"));
        Assert.Single(sim.Of<EntryIntentCancelled>());
        sim.Tick(At(7, 2), quotes: [Ce(), Pe()]);
        sim.Tick(At(7, 3), quotes: [Pe()]);
        Assert.Single(sim.Of<EntryOrderPlaced>());
        Assert.False(sim.Engine.Runtimes.Single().HasPendingEntry);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Invalidation_ConsumesAWaitingIntent_AndAnOutstandingOrder(bool ordered)
    {
        var sim = New(Strategy(avg5: true));
        sim.Bar(5, dir: 10);
        if (ordered) sim.Tick(At(5), averages: Average(200), quotes: [Ce()]);
        sim.Tick(At(5, 2), safety: new SafetyState(EntryBlock: "observer heartbeat is stale", Invalidate: "observer heartbeat is stale"), averages: Average(200), quotes: [Ce()]);
        Assert.Single(sim.Of<EntryIntentCancelled>());
        Assert.Empty(sim.Of<EntryFilled>());
        sim.Tick(At(5, 30), averages: Average(200), quotes: [Ce()]);
        Assert.False(sim.Engine.Runtimes.Single().HasPendingEntry);
        Assert.Equal(ordered ? 1 : 0, sim.Of<EntryOrderPlaced>().Count);
    }

    [Fact]
    public void Avg5_PendingIntent_AndItsRetryRule_SurviveAJournalReplay()
    {
        var strategy = Strategy(avg5: true);
        var sim = New(strategy);
        sim.Bar(5, dir: 10);
        sim.Tick(At(5), averages: Average(200), quotes: [Ce()]);
        sim.Tick(At(5, 2), safety: new SafetyState(EntryBlock: "market data is stale"), averages: Average(200), quotes: [Ce()]);

        var restored = New(strategy);
        foreach (var input in sim.Journal) restored.Engine.Step(input);
        Assert.Equal(sim.Engine.Events, restored.Engine.Events);
        Assert.Equal(sim.Status, restored.Status);
        foreach (var s in new[] { sim, restored }) s.Tick(At(5, 4), averages: Average(200), quotes: [Ce()]);
        Assert.Equal(sim.Engine.Events, restored.Engine.Events);
        Assert.Equal(2, restored.Of<EntryOrderPlaced>().Count);
    }
}
