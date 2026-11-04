using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class ListOpportunitySink : IOpportunitySink
{
    public List<OpportunityRecord> Rows { get; } = [];

    public void Append(OpportunityRecord r) => Rows.Add(r);
}

sealed class ThrowingOpportunitySink : IOpportunitySink
{
    public void Append(OpportunityRecord r) => throw new InvalidOperationException("opportunity store down");
}

/// <summary>The opportunity log: nine deterministic categories, one Decision row per strategy per verified bar, Outcome rows for placed orders, observation only (the ledger is identical with or without it).</summary>
public class OpportunityLogTests
{
    static PaperEngine Engine(IOpportunitySink? sink = null, params string[] ids) =>
        new((ids.Length == 0 ? ["S01"] : ids).Select(i => ExecFixtures.Strat(i)), ExecFixtures.Options(), null, sink);

    static OpportunityRecord Decision(PaperEngine e, int bar, string id = "S01") => Assert.Single(e.Opportunities, o => o.StrategyId == id && o.BarSeq == bar && o.Stage == OpportunityStage.Decision);

    [Fact]
    public void NoSignal_IsCategory1_OnEveryVerifiedBar_AndAnUnverifiedBarWritesNoDecisionRow()
    {
        var e = Engine();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 0), ExecFixtures.Fr(6, dir: 0, verified: false), ExecFixtures.Fr(7, dir: 0)]);
        Assert.Equal(OpportunityCategory.NoDirectionalSignal, Decision(e, 5).Category);
        Assert.Equal("Neutral", Decision(e, 5).Direction);
        Assert.Equal(OpportunityCategory.NoDirectionalSignal, Decision(e, 7).Category);
        Assert.DoesNotContain(e.Opportunities, o => o.BarSeq == 6);
        Assert.Empty(e.Events);                                                          // no order was fabricated
    }

    [Fact]
    public void ValidSignal_PlacesAnOrder_AndTheFillIsAnOutcomeRowOnTheDecisionBar()
    {
        var e = Engine();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, dir: 0, quotes: [new("CE26000", 125.5, 127)])]);
        var d = Decision(e, 5);
        Assert.Equal((OpportunityCategory.ValidDirectionalSignal, "CE", true, true, "Placed"), (d.Category, d.Direction, d.OrderEligible, d.OrderAttempted, d.OrderResult));
        Assert.NotNull(d.IntentId); Assert.Equal("CE26000", d.Token); Assert.Equal($"{d.SessionId}:5", d.FrameRef);
        var o = Assert.Single(e.Opportunities, x => x.Stage == OpportunityStage.Outcome);
        Assert.Equal((OpportunityCategory.OrderFilled, 5, 6, "Filled"), (o.Category, o.BarSeq, o.ResolvedBarSeq, o.OrderResult));
        Assert.Equal(d.IntentId, o.IntentId);
        Assert.Equal(1, e.Events.OfType<EntryFilled>().Count());
    }

    [Fact]
    public void SameDirectionWhileHolding_IsCategory3_AndOpensNothing()
    {
        var e = Engine();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, dir: 0, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(7, dir: 10, quotes: [new("CE26000", 125.5, 127)])]);
        var d = Decision(e, 7);
        Assert.Equal((OpportunityCategory.SameDirectionWhileHolding, true, false), (d.Category, d.InPosition, d.OrderAttempted));
        Assert.Single(e.Events.OfType<EntryOrderPlaced>());                               // the suppressed signal produced no order
    }

    [Fact]
    public void OppositeSignal_IsCategory4_AnExitRequestWithoutAReversal()
    {
        var e = Engine();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, dir: 0, quotes: [new("CE26000", 125.5, 127)]), ExecFixtures.Fr(7, dir: -10, quotes: [new("CE26000", 125.5, 127), new("PE26000", 125, 126)])]);
        var d = Decision(e, 7);
        Assert.Equal((OpportunityCategory.OppositeSignalExit, "PE", true, false), (d.Category, d.Direction, d.InPosition, d.OrderAttempted));
        Assert.Single(e.Events.OfType<EntryOrderPlaced>());                               // exit only
    }

    [Fact]
    public void ABlockedFrame_IsCategory6_WithTheCoverageReasonRecorded()
    {
        var e = Engine();
        ExecFixtures.Process(e, ExecFixtures.Fr(5, dir: 10), ExecFixtures.Blocked);
        var d = Decision(e, 5);
        Assert.Equal((OpportunityCategory.BlockedByRiskOrWindow, "EntriesBlocked", false), (d.Category, d.Reason, d.EntriesAllowed));
        Assert.Contains("SequenceGap", d.Coverage, StringComparison.Ordinal);
        Assert.False(d.OrderAttempted);
    }

    [Fact]
    public void ANoQualifyingContract_IsCategory7_AnAttemptRejectedForAContractReason()
    {
        var e = Engine();
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10, quotes: [new("CE26000", 300, 302)])]);        // premium far above the configured range
        var d = Decision(e, 5);
        Assert.Equal((OpportunityCategory.OrderRejectedQuoteOrContract, "NoQualifyingContract", true, "Rejected"), (d.Category, d.Reason, d.OrderAttempted, d.OrderResult));
    }

    [Fact]
    public void AnOrderThatNeverGetsAQualifyingFillQuote_IsCategory8_AnOutcomeRow()
    {
        var e = Engine();
        var stale = new ExecFixtures.Q[] { new("CE26000", 125.5, 127, AgeMs: 600_000) };
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10), ExecFixtures.Fr(6, dir: 0, quotes: stale), ExecFixtures.Fr(7, dir: 0, quotes: stale), ExecFixtures.Fr(8, dir: 0, quotes: stale), ExecFixtures.Fr(9, dir: 0, quotes: stale)]);
        var o = Assert.Single(e.Opportunities, x => x.Stage == OpportunityStage.Outcome);
        Assert.Equal((OpportunityCategory.OrderAcceptedNotFilled, 5, "ExpiredUnfilled"), (o.Category, o.BarSeq, o.OrderResult));
        Assert.Empty(e.Events.OfType<EntryFilled>());
    }

    [Fact]
    public void TheLog_IsDeterministic_UniquelyKeyed_AndIdenticalAfterAReplay()
    {
        IEnumerable<NiftySignal.PaperEngineV2.Core.Frames.MarketStateFrame> Frames() => ExecutionTests.Day();
        var a = Engine(); ExecFixtures.Run(a, Frames());
        var b = Engine(); ExecFixtures.Run(b, Frames());                                  // the restart replays the journal through a fresh engine
        Assert.Equal(a.Opportunities, b.Opportunities);
        Assert.Equal(a.Opportunities.Count, a.Opportunities.Select(o => o.OpportunityId).Distinct().Count());
        Assert.Equal(a.Opportunities.Count(o => o.Stage == OpportunityStage.Decision), Frames().Count(f => f.Identity.Verified));          // exactly one Decision row per verified bar
    }

    [Fact]
    public void TheLedger_IsIdentical_WithAndWithoutTheLog_AndASinkFailureIsContained()
    {
        var plain = Engine(); ExecFixtures.Run(plain, ExecutionTests.Day());
        var logged = Engine(new ListOpportunitySink()); ExecFixtures.Run(logged, ExecutionTests.Day());
        var broken = Engine(new ThrowingOpportunitySink()); ExecFixtures.Run(broken, ExecutionTests.Day());
        Assert.Equal(plain.Events, logged.Events);
        Assert.Equal(plain.Events, broken.Events);                                        // a failing store changes no fill, no decision
        Assert.True(broken.OpportunitySinkFailures > 0);
        Assert.Equal(0, logged.OpportunitySinkFailures);
    }

    [Fact]
    public void Strategies_AreIsolated_EachHasItsOwnRows()
    {
        var sink = new ListOpportunitySink();
        var e = Engine(sink, "S01", "S02");
        ExecFixtures.Run(e, [ExecFixtures.Fr(5, dir: 10)]);
        Assert.Equal(["S01", "S02"], sink.Rows.Select(r => r.StrategyId).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(sink.Rows, r => Assert.Equal(OpportunityCategory.ValidDirectionalSignal, r.Category));
    }
}
