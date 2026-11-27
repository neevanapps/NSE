using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Ordering rules that must hold around entry restrictions and restarts. Synthetic inputs only.</summary>
public partial class ExecutionLifecycleTests
{
    [Theory]
    [InlineData("daily-trades")] [InlineData("after-cutoff")] [InlineData("entries-blocked-bar")]
    public void OppositeSignal_ClosesThePosition_EvenWhenNoEntryIsAllowed(string restriction)
    {
        var sim = HoldingCall(Strategy(edit: o => { if (restriction == "daily-trades") o["risk"]!["maxDailyTrades"] = 1; }));
        var seq = restriction == "after-cutoff" ? 332 : 6;                                       // bar 332 ends after 15:00 and before the 15:08 liquidation
        sim.Bar(seq, dir: -10, entriesAllowed: restriction != "entries-blocked-bar");
        var request = sim.Of<ExitRequested>();
        Assert.Equal(ExitReason.OppositeSignal, Assert.Single(request).Reason);
        sim.Tick(At(seq, 5), quotes: [Ce(), Pe()]);
        sim.Tick(At(seq, 6), quotes: [Ce(), Pe()]);
        Assert.Single(sim.Of<ExitFilled>());
        Assert.Single(sim.Of<EntryFilled>());                                                    // the opposite side is not opened
        Assert.Equal(1, sim.Status.TradesToday);
    }

    [Fact]
    public void TheFrozenPartialTarget_SurvivesAJournalReplay_AndTheFilledPartialIsNeverRepeated()
    {
        var strategy = Strategy(partial: true);
        var sim = HoldingCall(strategy);

        Sim Restore(Sim from)
        {
            var restored = New(strategy);
            foreach (var input in from.Journal) { restored.Engine.Step(input); restored.Journal.Add(input); }
            Assert.Equal(from.Engine.Events, restored.Engine.Events);
            Assert.Equal(from.Status, restored.Status);
            return restored;
        }

        // restart between the fill and the target: the target assigned at entry (VIX 14) is the one used, whatever VIX is now
        sim = Restore(sim);
        sim.Tick(At(6), vix: 30, quotes: [Ce(153, 154)]);
        Assert.Equal(153.0m, Assert.Single(sim.Of<PartialTargetAssigned>()).TargetPrice);
        Assert.Single(sim.Of<PartialExitRequested>());

        // restart with the partial requested but unfilled
        sim = Restore(sim);
        sim.Tick(At(6, 3), quotes: [Ce(154, 155)]);
        Assert.Single(sim.Of<PartialExitFilled>());

        // restart after the partial fill
        sim = Restore(sim);
        sim.Tick(At(6, 4), vix: 14, quotes: [Ce(170, 171)]);
        Assert.Single(sim.Of<PartialExitRequested>());
        Assert.Single(sim.Of<PartialExitFilled>());
        Assert.Equal(325, sim.Engine.Runtimes.Single().Positions().Single().RemainingQuantity);
    }
}
