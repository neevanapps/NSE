using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class VariantEngineTests
{
    private const string Ce = "CE23900";
    private const string Pe = "PE23900";

    private static Kit NewKit(IBaseStrategy strategy, params VariantSpec[] variants)
        => new(Kit.Config1(variants: variants), Kit.StandardUniverse(), strategy);

    /// <summary>Bars 1..upTo, each a group of {CE quote, PE quote, bar}. Returns nothing; asserts nothing.</summary>
    private static void Warm(Kit kit, int upTo, Func<int, decimal> ceAsk, decimal peAsk = 140m)
    {
        for (var k = 1; k <= upTo; k++)
        {
            kit.BarGroup(k, kit.Q(Kit.Bar(k), Ce, ceAsk(k)), kit.Q(Kit.Bar(k), Pe, peAsk));
        }
    }

    // ------------------------------------------------------------------ T30 / T34: immediate vs waiting, intent content

    [Fact]
    public void Immediate_EntersAtOnce_WhileAskBelowMa_WaitsForAnEligibleQuote_AndIntentsCarryTheirPremiumLimits()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 4, null), Kit.Immediate("A"), Kit.Ma("A2", n: 3));
        Warm(kit, 3, _ => 130m);

        kit.BarGroup(4, kit.Q(Kit.Bar(4), Ce, 128m), kit.Q(Kit.Bar(4), Pe, 140m));

        var a = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(Kit.Bar(4), a.At);
        Assert.Equal(Ce, a.Contract.Token);                      // Call side only; the cheaper-looking put is not a candidate
        Assert.Equal(128m, a.LimitPrice);                        // marketable limit at the observed qualifying ask
        Assert.Equal(128m, a.QualifyingAsk);
        Assert.Equal(100m, a.MinimumEntryPremium);
        Assert.Equal(150m, a.MaximumEntryPremium);
        Assert.Equal(650, a.RequestedQuantity);                  // confirmed default
        Assert.Equal("Immediate", a.ConditionKind);
        Assert.Equal(0, a.SlotNo);
        Assert.Empty(kit.Out<EntryIntent>("A2"));
        Assert.Equal(OpportunityState.Waiting, kit.Variant("A2").Active!.State);
        Assert.Equal(Ce, kit.Variant("A2").Active!.Contract!.Token);

        // MA over bars 2..4 of the SAME contract: asks 130, 130, 128.
        var average = (130m + 130m + 128m) / 3m;
        kit.Feed(kit.Q(Kit.Bar(4).AddSeconds(5), Ce, 129m));

        var a2 = Assert.Single(kit.Out<EntryIntent>("A2"));
        Assert.Equal(129m, a2.LimitPrice);
        Assert.Equal(average, a2.ConditionAverage);
        Assert.Equal(3, a2.ConditionN);
        Assert.Equal(CandlePriceSource.AskClose, a2.ConditionPriceSource);
        Assert.Equal(Ce, a2.Contract.Token);
        Assert.NotEqual(a.IntentId, a2.IntentId);
        Assert.NotEqual(a.OpportunityId, a2.OpportunityId);
        Assert.Single(kit.Out<EntryIntent>("A"));                 // A is OrderPending: no second intent
    }

    [Fact]
    public void WaitingCondition_IsNotSatisfiedAtOrAboveTheAverage()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, null), Kit.Ma("A2", n: 3));
        Warm(kit, 3, _ => 130m);

        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(1), Ce, 130m));      // equal to the average: not below
        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(2), Ce, 131m));

        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("A2"), r => r.Outcome == "NotSatisfied:AskNotBelowAverage");
    }

    // ------------------------------------------------------------------ candle publication and equal-time handling

    [Fact]
    public void QuoteInTheSameGroupAsABar_DoesNotSeeThatBarsCandle_AndTheNextGroupDoes()
    {
        // Bars 1..3 closes: 140, 100, 100 -> MA(3) = 113.33.  A quote at 110 must be judged against bars 1..3.
        // Had bar 4's candle (close 110) been published first, MA(2..4) = 103.33 and the quote would NOT qualify.
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, null), Kit.Ma("A2", n: 3));
        Warm(kit, 3, k => k == 1 ? 140m : 100m);
        Assert.Equal(OpportunityState.Waiting, kit.Variant("A2").Active!.State);

        kit.BarGroup(4, kit.Q(Kit.Bar(4), Ce, 110m));

        var intent = Assert.Single(kit.Out<EntryIntent>("A2"));
        Assert.Equal((140m + 100m + 100m) / 3m, intent.ConditionAverage);
        Assert.Equal(110m, intent.LimitPrice);
    }

    [Fact]
    public void QuoteInTheNextGroup_UsesTheNewlyPublishedCandle()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, null), Kit.Ma("A2", n: 3));
        Warm(kit, 3, k => k == 1 ? 140m : 100m);

        kit.BarGroup(4, kit.Q(Kit.Bar(4), Ce, 120m));              // 120 > 113.33: not satisfied; bar 4's candle closes at 120
        Assert.Empty(kit.Out<EntryIntent>());

        kit.Feed(kit.Q(Kit.Bar(4).AddSeconds(1), Ce, 110m));       // MA(2..4) = (100+100+120)/3 = 106.67 -> 110 not below it

        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Equal(WindowStatus.Ready, kit.Engine.Candles.ViewFor(Ce).ConsecutiveWindow(3).Status);
        Assert.Equal(new long[] { 2, 3, 4 }, kit.Engine.Candles.ViewFor(Ce).ConsecutiveWindow(3).Candles.Select(c => c.BarSeq).ToArray());
    }

    [Fact]
    public void WaitingEvaluation_HappensOnlyOnQuotesOfTheSelectedContract()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, null), Kit.Ma("A2", n: 3));
        Warm(kit, 3, _ => 130m);
        var opportunity = kit.Variant("A2").Active!;
        var before = opportunity.Evaluations;

        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(1), "CE24000", 100m), kit.Q(Kit.Bar(3).AddSeconds(1), Pe, 100m));
        Assert.Equal(before, opportunity.Evaluations);              // other contracts never trigger an evaluation

        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(2), Ce, 131m));
        Assert.Equal(before + 1, opportunity.Evaluations);
    }

    [Fact]
    public void HistoryExistingBeforeTheOpportunity_IsUsedImmediately()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 6, null), Kit.Ma("A2", n: 5));
        Warm(kit, 5, _ => 120m);                                   // five candles of the future contract exist before any opportunity does
        Assert.Null(kit.Variant("A2").Active);
        Assert.Equal(WindowStatus.Ready, kit.Engine.Candles.ViewFor(Ce).ConsecutiveWindow(5).Status);

        kit.BarGroup(6, kit.Q(Kit.Bar(6), Ce, 125m));              // opportunity created and contract selected here (ask 125 is the band midpoint)
        kit.Feed(kit.Q(Kit.Bar(6).AddSeconds(1), Ce, 119m));       // bars 2..6 = 120,120,120,120,125 -> 121; 119 < 121

        Assert.Single(kit.Out<EntryIntent>("A2"));
    }

    [Fact]
    public void InsufficientHistory_IsNotEvaluable_UntilEnoughConsecutiveCandlesExist()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, null), Kit.Ma("A2", n: 5));
        Warm(kit, 3, _ => 120m);

        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(1), Ce, 100m));
        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("A2"), r => r.Outcome == "NotEvaluable:InsufficientHistory");

        Warm4and5(kit);
        kit.Feed(kit.Q(Kit.Bar(5).AddSeconds(1), Ce, 100m));        // bars 1..5 available now: 120,120,120,120,120
        var intent = Assert.Single(kit.Out<EntryIntent>("A2"));
        Assert.Equal(120m, intent.ConditionAverage);

        static void Warm4and5(Kit k)
        {
            for (var bar = 4; bar <= 5; bar++)
            {
                k.BarGroup(bar, k.Q(Kit.Bar(bar), Ce, 120m), k.Q(Kit.Bar(bar), Pe, 140m));
            }
        }
    }

    [Fact]
    public void AMissingCandle_BreaksTheWindow_NothingIsForwardFilled()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Ma("A2", n: 3));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m), kit.Q(Kit.Bar(1), Pe, 140m));
        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 120m), kit.Q(Kit.Bar(2), Pe, 140m));
        kit.BarGroup(3, kit.Q(Kit.Bar(3), Pe, 140m));                // no quote of the selected contract in bar 3
        kit.BarGroup(4, kit.Q(Kit.Bar(4), Ce, 120m), kit.Q(Kit.Bar(4), Pe, 140m));

        kit.Feed(kit.Q(Kit.Bar(4).AddSeconds(1), Ce, 100m));
        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("A2"), r => r.Outcome == "NotEvaluable:InvalidCandleInWindow");

        kit.BarGroup(5, kit.Q(Kit.Bar(5), Ce, 120m), kit.Q(Kit.Bar(5), Pe, 140m));
        kit.BarGroup(6, kit.Q(Kit.Bar(6), Ce, 120m), kit.Q(Kit.Bar(6), Pe, 140m));
        kit.Feed(kit.Q(Kit.Bar(6).AddSeconds(1), Ce, 100m));         // bars 4..6 all valid
        Assert.Single(kit.Out<EntryIntent>("A2"));
    }

    // ------------------------------------------------------------------ premium band and contract fixation

    [Theory]
    [InlineData("99.95", false)]
    [InlineData("100.00", true)]
    [InlineData("150.00", true)]
    [InlineData("150.05", false)]
    public void EntryPremiumBand_AppliesAtSelection_WithInclusiveBoundaries(string askText, bool expectIntent)
    {
        var ask = decimal.Parse(askText, System.Globalization.CultureInfo.InvariantCulture);
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));

        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, ask, spread: 0.05m));

        Assert.Equal(expectIntent ? 1 : 0, kit.Out<EntryIntent>().Count());
        if (!expectIntent)
        {
            Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "NoEligibleContract");
            Assert.Equal(OpportunityState.AwaitingContract, kit.Variant("A").Active!.State);
        }
    }

    [Fact]
    public void SelectedContract_StaysFixed_WhenItLeavesAndReentersTheBand_AndWhenABetterStrikeAppears()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 2, null), Kit.Ma("W", n: 1));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 140m));
        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 130m));                 // CE23900 selected at 130; candle 2 closes at 130 -> MA(1) = 130
        var opportunity = kit.Variant("W").Active!;
        Assert.Equal(Ce, opportunity.Contract!.Token);

        kit.Feed(kit.Q(Kit.Bar(2).AddSeconds(1), "CE24000", 125m));   // a better-centred strike appears: no switch
        kit.Feed(kit.Q(Kit.Bar(2).AddSeconds(2), Ce, 151m));            // above the band: no intent, contract kept
        kit.Feed(kit.Q(Kit.Bar(2).AddSeconds(3), Ce, 90m));             // below the band: no intent, contract kept
        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("W"), r => r.Outcome == "Blocked:AskAboveBand");
        Assert.Contains(kit.Out<AuditRecord>("W"), r => r.Outcome == "Blocked:AskBelowBand");
        Assert.Equal(OpportunityState.Waiting, opportunity.State);
        Assert.Equal(Ce, opportunity.Contract!.Token);

        kit.Feed(kit.Q(Kit.Bar(2).AddSeconds(4), Ce, 129m));            // back in the band and below the average: qualifies again
        var intent = Assert.Single(kit.Out<EntryIntent>("W"));
        Assert.Equal(Ce, intent.Contract.Token);
        Assert.Equal(129m, intent.QualifyingAsk);
        Assert.Equal(opportunity.Id, intent.OpportunityId);

        kit.Engine.RequireFixedContract(opportunity.Id, Ce);
        Assert.Throws<ContractChangedException>(() => kit.Engine.RequireFixedContract(opportunity.Id, "CE24000"));
    }

    [Fact]
    public void NoEligibleContract_EmitsNoIntent_AndSelectionIsRetriedOnLaterBarsOnly()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));

        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 90m));
        var opportunity = kit.Variant("A").Active!;
        Assert.Equal(OpportunityState.AwaitingContract, opportunity.State);
        Assert.Equal(1, opportunity.SelectionAttempts);

        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(10), Ce, 120m));          // a qualifying quote between bars does not trigger selection
        Assert.Equal(OpportunityState.AwaitingContract, opportunity.State);
        Assert.Equal(1, opportunity.SelectionAttempts);
        Assert.Empty(kit.Out<EntryIntent>());

        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 120m));                  // next completed bar retries, selects and (Immediate) enters
        Assert.Equal(2, opportunity.SelectionAttempts);
        Assert.Single(kit.Out<EntryIntent>("A"));
    }

    [Fact]
    public void StaleQuotesAtTheBar_YieldNoEligibleContract()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));

        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(-30), Ce, 125m));
        kit.BarGroup(1);                                              // the quote is 30 s old at the bar: stale (limit 2 s)

        Assert.Empty(kit.Out<EntryIntent>());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "NoEligibleContract" && r.Detail.Contains("QuoteStale=1"));
    }

    // ------------------------------------------------------------------ invalidation, cancellation, re-arm

    [Fact]
    public void Invalidation_CancelsAWaitingOpportunity()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 3, 4), Kit.Ma("A2", n: 3));
        Warm(kit, 4, _ => 130m);
        var opportunity = kit.Variant("A2").Active!;
        Assert.Equal(OpportunityState.Waiting, opportunity.State);

        kit.BarGroup(5, kit.Q(Kit.Bar(5), Ce, 130m));                  // strategy no longer valid

        Assert.Equal(OpportunityState.Cancelled, opportunity.State);
        Assert.StartsWith("StrategyInvalidated", opportunity.TerminalReason);
        Assert.Null(kit.Variant("A2").Active);
        kit.Feed(kit.Q(Kit.Bar(5).AddSeconds(1), Ce, 100m));
        Assert.Empty(kit.Out<EntryIntent>());
    }

    [Fact]
    public void Invalidation_OfAPendingEntry_RequestsCancellation_AndKeepsTheReservationUntilTheOutcome()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, 1), Kit.Immediate("A"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        var slot = Assert.Single(kit.Variant("A").Slots);
        Assert.Equal(SlotState.Reserved, slot.State);

        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 120m));                  // invalid now
        var cancel = Assert.Single(kit.Out<CancelRequest>("A"));
        Assert.Equal(intent.IntentId, cancel.IntentId);
        Assert.Equal(CancelReason.StrategyInvalidated, cancel.Reason);
        Assert.Equal(SlotState.Reserved, slot.State);
        Assert.Equal(OpportunityState.OrderPending, kit.AllOpportunities().Single().State);

        kit.BarGroup(3, kit.Q(Kit.Bar(3), Ce, 120m));                  // further bars do not repeat the request
        Assert.Single(kit.Out<CancelRequest>("A"));

        kit.At(Kit.Bar(3).AddSeconds(10), s => kit.Outcome(intent, EntryOutcomeKind.Cancelled, s));
        kit.Loop.AdvanceTo(Kit.Bar(3).AddSeconds(11));

        Assert.Equal(SlotState.Free, slot.State);
        var opportunity = kit.AllOpportunities().Single();
        Assert.Equal(OpportunityState.Cancelled, opportunity.State);
        Assert.Equal("StrategyInvalidated", opportunity.TerminalReason);
    }

    [Fact]
    public void AFillAfterACancelRequest_StillConsumesTheOpportunity()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, 1), Kit.Immediate("A"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 120m));
        Assert.Single(kit.Out<CancelRequest>("A"));

        kit.At(Kit.Bar(2).AddSeconds(5), s => kit.Outcome(intent, EntryOutcomeKind.PartiallyFilled, s));
        kit.Loop.AdvanceTo(Kit.Bar(2).AddSeconds(6));

        Assert.Equal(OpportunityState.Consumed, kit.AllOpportunities().Single().State);
        Assert.Equal(SlotState.Occupied, kit.Variant("A").Slots[0].State);
    }

    [Fact]
    public void ReArm_HappensOnlyAfterTheAttemptIsTerminal_AndNeverOverlaps()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var first = Assert.Single(kit.Out<EntryIntent>("A"));

        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(1), Ce, 119m));
        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(2), Ce, 118m));
        Assert.Single(kit.Out<EntryIntent>("A"));                       // still pending: no overlapping attempt, no second reservation
        Assert.Single(kit.Variant("A").Slots, s => s.State == SlotState.Reserved);

        kit.At(Kit.Bar(1).AddSeconds(5), s => kit.Outcome(first, EntryOutcomeKind.Expired, s));
        kit.Loop.AdvanceTo(Kit.Bar(1).AddSeconds(6));
        var opportunity = kit.Variant("A").Active!;
        Assert.Equal(OpportunityState.Waiting, opportunity.State);      // re-armed: strategy still valid, in session
        Assert.Equal(SlotState.Free, kit.Variant("A").Slots[0].State);
        Assert.Single(kit.Out<EntryIntent>("A"));                       // re-arming itself issues nothing

        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(10), Ce, 117m));
        var second = kit.Out<EntryIntent>("A").Last();
        Assert.Equal(2, kit.Out<EntryIntent>("A").Count());
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.IntentId, second.IntentId);
        Assert.Equal(opportunity.Id, second.OpportunityId);
        Assert.Equal(Ce, second.Contract.Token);                         // same contract on every attempt
    }

    [Fact]
    public void MaxAttempts_EndsTheOpportunity_AndWrongOutcomeReportsAreRejected()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A", maxAttempts: 1));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        var opportunity = kit.Variant("A").Active!;

        kit.At(Kit.Bar(1).AddSeconds(5), s =>
        {
            Assert.Throws<InvalidOperationException>(() => kit.Engine.ReportEntryOutcome(new EntryOutcome("A", opportunity.Id, "ord_other", EntryOutcomeKind.Expired), s));
            Assert.Throws<KeyNotFoundException>(() => kit.Engine.ReportEntryOutcome(new EntryOutcome("ZZ", opportunity.Id, intent.IntentId, EntryOutcomeKind.Expired), s));
            kit.Outcome(intent, EntryOutcomeKind.Expired, s);
        });
        kit.Loop.AdvanceTo(Kit.Bar(1).AddSeconds(6));

        Assert.Equal(OpportunityState.Expired, opportunity.State);
        Assert.Equal("MaxAttempts", opportunity.TerminalReason);
        Assert.Equal(SlotState.Free, kit.Variant("A").Slots[0].State);
    }

    [Fact]
    public void ARejectedAttempt_ReArmsLikeAnExpiredOne()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));

        kit.At(Kit.Bar(1).AddSeconds(5), s => kit.Outcome(intent, EntryOutcomeKind.Rejected, s));
        kit.Loop.AdvanceTo(Kit.Bar(1).AddSeconds(6));

        Assert.Equal(OpportunityState.Waiting, kit.Variant("A").Active!.State);
    }

    // ------------------------------------------------------------------ consumption, re-entry, capacity

    [Fact]
    public void PartialFill_ConsumesTheOpportunity_AndANewEpisodeIsRequiredAfterTheTradeCloses()
    {
        var script = new ScriptedStrategy("S", seq => seq is >= 1 and <= 3 or >= 5 and <= 6
            ? new StrategyValidity(true, OptionSide.Call, "x")
            : new StrategyValidity(false, OptionSide.Call, "x"));
        var kit = NewKit(script, Kit.Immediate("A"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        var firstOpportunity = kit.Variant("A").Active!;

        kit.At(Kit.Bar(1).AddSeconds(5), s => kit.Outcome(intent, EntryOutcomeKind.PartiallyFilled, s));
        kit.Loop.AdvanceTo(Kit.Bar(1).AddSeconds(6));
        Assert.Equal(OpportunityState.Consumed, firstOpportunity.State);
        Assert.Equal(SlotState.Occupied, kit.Variant("A").Slots[0].State);

        kit.At(Kit.Bar(1).AddSeconds(30), s => kit.Engine.ReportPositionClosed("A", firstOpportunity.Id, s));
        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 120m));                  // position closed during/before this bar; strategy still valid
        kit.BarGroup(3, kit.Q(Kit.Bar(3), Ce, 120m));
        Assert.Equal(SlotState.Free, kit.Variant("A").Slots[0].State);
        Assert.Single(kit.Out<EntryIntent>("A"));                       // same episode: no new opportunity after the closed trade
        Assert.Null(kit.Variant("A").Active);

        kit.BarGroup(4, kit.Q(Kit.Bar(4), Ce, 120m));                  // invalid
        kit.BarGroup(5, kit.Q(Kit.Bar(5), Ce, 118m));                  // valid again: a new episode -> a new opportunity
        var intents = kit.Out<EntryIntent>("A").ToList();
        Assert.Equal(2, intents.Count);
        Assert.NotEqual(firstOpportunity.Id, intents[1].OpportunityId);
        Assert.Equal(118m, intents[1].LimitPrice);
        Assert.Equal(2, kit.AllOpportunities().Count);
    }

    [Fact]
    public void OccupiedSlot_RejectsAnImmediateVariantsNewOpportunity_ButOnlyDelaysAWaitingOne()
    {
        var script = new ScriptedStrategy("S", seq => seq is 1 or 3 or 4
            ? new StrategyValidity(true, OptionSide.Call, "x")
            : new StrategyValidity(false, OptionSide.Call, "x"));
        var kit = NewKit(script, Kit.Immediate("A"), Kit.Ma("W", n: 1));
        // bar 1 candle closes at 130
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 130m));
        var a1 = Assert.Single(kit.Out<EntryIntent>("A"));
        var w = kit.Variant("W").Active!;
        kit.Feed(kit.Q(Kit.Bar(1).AddSeconds(1), Ce, 129m));            // W: 129 < 130
        var w1 = Assert.Single(kit.Out<EntryIntent>("W"));

        kit.At(Kit.Bar(1).AddSeconds(5), s =>
        {
            kit.Outcome(a1, EntryOutcomeKind.Filled, s);
            kit.Outcome(w1, EntryOutcomeKind.Filled, s);
        });
        kit.BarGroup(2, kit.Q(Kit.Bar(2), Ce, 130m));                  // invalid bar: ends the episode (positions stay open)
        kit.BarGroup(3, kit.Q(Kit.Bar(3), Ce, 130m));                  // a new episode begins while both slots are Occupied

        var aSecond = kit.AllOpportunities().Last(o => o.VariantId == "A");
        Assert.Equal(OpportunityState.Rejected, aSecond.State);
        Assert.Equal("CapacityBusy", aSecond.TerminalReason);

        var wSecond = kit.Variant("W").Active!;
        Assert.NotEqual(w.Id, wSecond.Id);
        Assert.Equal(OpportunityState.Waiting, wSecond.State);
        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(1), Ce, 120m));            // qualifies, but the slot is occupied: keeps waiting
        Assert.Single(kit.Out<EntryIntent>("W"));
        Assert.Contains(kit.Out<AuditRecord>("W"), r => r.Outcome == "Blocked:CapacityBusy");

        kit.At(Kit.Bar(3).AddSeconds(5), s => kit.Engine.ReportPositionClosed("W", w.Id, s));
        kit.Loop.AdvanceTo(Kit.Bar(3).AddSeconds(6));
        kit.Feed(kit.Q(Kit.Bar(3).AddSeconds(7), Ce, 119m));
        Assert.Equal(2, kit.Out<EntryIntent>("W").Count());
    }

    [Fact]
    public void Capacity_IsPerVariant_AndAccountsAreSeparateByDefault()
    {
        var kit = NewKit(ScriptedStrategy.Valid("S", 1, null), Kit.Immediate("A"), Kit.Immediate("B"));
        kit.BarGroup(1, kit.Q(Kit.Bar(1), Ce, 120m));

        Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Single(kit.Out<EntryIntent>("B"));                       // A's reservation does not block B
        Assert.NotSame(kit.Variant("A").Slots[0], kit.Variant("B").Slots[0]);
        Assert.Equal("A", kit.Variant("A").Spec.ResolveAccountId());
        Assert.Equal("shared", (Kit.Immediate("C") with { AccountId = "shared" }).ResolveAccountId());
        Assert.NotEqual(kit.Out<EntryIntent>("A").Single().IntentId, kit.Out<EntryIntent>("B").Single().IntentId);
        Assert.NotEqual(kit.Variant("A").VariantHash, kit.Variant("B").VariantHash);
    }
}

internal static class KitExtensions
{
    public static IReadOnlyList<Opportunity> AllOpportunities(this Kit kit) => kit.Engine.AllOpportunities;
}
