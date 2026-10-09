using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// Clock semantics for execution. Three different times: the SCHEDULED deadline time (what a matured order's callback sees as Now and what
/// execution time is), the ENGINE CLOCK / watermark (may already be later when a heartbeat processes the deadline), and event times.
/// Execution and quote freshness always use the first; no quote delivered later is visible to an earlier deadline.
/// </summary>
public class ExecutionTimingTests
{
    private static TradeKit NewKit(ExecutionConfig? execution = null)
        => new(TradeKit.MakeConfig(execution, variants: TradeKit.Immediate()));

    [Fact]
    public void HeartbeatBeyondMaturity_FillsAtTheDeadlineTime_WithTheLatestPreMaturityQuote_NotALaterOne()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                 // intent, limit 125, matures at T0+1 s
        var before = kit.Q(kit.At(0.5), 123m, 124m);                              // fillable quote BEFORE the maturity instant
        kit.Feed(before);

        kit.Loop.AdvanceTo(kit.At(1.2));                                          // heartbeat / watermark beyond maturity, before the next quote
        Assert.Equal(kit.At(1.2), kit.Loop.Now);

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(kit.At(1), fill.At);                                         // execution time = scheduled deadline time, not the watermark
        Assert.Equal(124m, fill.Price);
        Assert.Equal(before.SourceId, fill.QuoteSourceId);

        kit.Feed(kit.Q(kit.At(1.5), 90m, 91m));                                   // a later (even absurd) quote changes nothing
        Assert.Single(kit.Fills());
        Assert.Equal(124m, kit.Position().AvgEntryPrice);
    }

    [Fact]
    public void QuoteAfterTheDeadline_IsNotUsedByTheDeadline_ButEvaluatedAsANewQuoteOfTheRestingOrder()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 125m, 126m));                                 // above the 125 limit
        kit.Loop.AdvanceTo(kit.At(1.2));                                          // maturity processed under the heartbeat

        Assert.Empty(kit.Fills());
        var note = Assert.Single(kit.Out<AuditRecord>("A"), r => r.Stage == "EntryOrder" && r.Outcome == "Working:AskAboveLimit");
        Assert.Equal(kit.At(1), note.At);

        var later = kit.Q(kit.At(1.5), 117m, 118m);
        kit.Feed(later);

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(kit.At(1.5), fill.At);
        Assert.Equal(118m, fill.Price);
        Assert.Equal(later.SourceId, fill.QuoteSourceId);
    }

    [Fact]
    public void QuoteFreshness_IsMeasuredAtTheExecutionTime_NotAtTheWatermark()
    {
        var execution = new ExecutionConfig { MaxQuoteAge = TimeSpan.FromMilliseconds(500) };

        var fresh = NewKit(execution);
        fresh.Bar(TradeKit.T0, 1, fresh.Q(TradeKit.T0, 124m, 125m));
        fresh.Feed(fresh.Q(fresh.At(0.8), 124m, 125m));                           // 0.2 s old at the maturity instant, 0.8 s old at the watermark
        fresh.Loop.AdvanceTo(fresh.At(1.6));
        Assert.Single(fresh.Fills());

        var stale = NewKit(execution);
        stale.Bar(TradeKit.T0, 1, stale.Q(TradeKit.T0, 124m, 125m));              // 1 s old at the maturity instant
        stale.Loop.AdvanceTo(stale.At(1.2));
        Assert.Empty(stale.Fills());
        Assert.Contains(stale.Out<AuditRecord>("A"), r => r.Outcome == "Working:QuoteStale");
    }

    [Fact]
    public void QuoteAtTheMaturityInstant_IsNotVisibleToTheDeadline_ItIsAFreshQuoteForTheRestingOrder()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 125m, 126m));

        kit.Feed(kit.Q(kit.At(1), 119m, 120m));                                   // exactly the maturity instant

        var outputs = kit.Engine.Outputs.ToList();
        var noFill = outputs.FindIndex(o => o is AuditRecord { Outcome: "Working:AskAboveLimit" });
        var fillIndex = outputs.FindIndex(o => o is FillRecord);
        Assert.True(noFill >= 0 && fillIndex > noFill);
        var fill = kit.Fills().Single();
        Assert.Equal(kit.At(1), fill.At);
        Assert.Equal(120m, fill.Price);
    }

    [Fact]
    public void NothingFillsBeforeTheLatencyElapses()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 119m, 120m));

        kit.Loop.AdvanceTo(kit.At(0.9));
        Assert.Empty(kit.Fills());

        kit.Loop.AdvanceTo(kit.At(1.1));
        Assert.Equal(kit.At(1), Assert.Single(kit.Fills()).At);
    }

    [Fact]
    public void OrderLifetime_ExpiresExactlyAtItsBoundary_AQuoteAtTheExpiryInstantCannotFillIt()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 130m, 131m));                                 // never fillable at limit 125 (the maturity at 1 s sees this quote)
        kit.Feed(kit.Q(kit.At(1.5), 130m, 131m));

        kit.Feed(kit.Q(kit.At(5), 119m, 120m));                                   // the expiry instant (T0 + 5 s)

        var first = kit.Engine.Execution.EntryOrders[0];
        Assert.Equal(OrderStatus.Expired, first.Status);
        Assert.Empty(kit.Fills());                                                // the expiry deadline ran before this quote
        Assert.Equal(2, kit.Out<EntryIntent>("A").Count());                       // re-armed; the quote itself then qualified for a second attempt
    }

    // ------------------------------------------------------------------ cancellation vs execution ordering

    private static TradeKit InvalidatingKit()
    {
        var kit = new TradeKit(
            TradeKit.MakeConfig(variants: TradeKit.Immediate()),
            null,
            ScriptedStrategy.Valid("S", 1, 1));
        return kit;
    }

    [Fact]
    public void QuoteOrderedBeforeTheInvalidatingBarInTheSameGroup_FillsFirst()
    {
        var kit = InvalidatingKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 125m, 126m));
        kit.Loop.AdvanceTo(kit.At(1.2));                                          // matured, not fillable yet (ask 126 > limit 125)

        kit.Feed(kit.Q(kit.At(2), 117m, 118m), Ev.Bar(kit.At(2), 2));            // quote rank < bar rank: executes before the strategy becomes invalid

        Assert.Single(kit.Fills());
        Assert.Equal(OpportunityState.Consumed, kit.Engine.Variants.AllOpportunities.Single().State);
        Assert.Empty(kit.Out<CancelRequest>());                                   // nothing left to cancel
    }

    [Fact]
    public void DeadlineAtTheSameInstantAsAnInvalidatingBar_ExecutesFirst()
    {
        var kit = InvalidatingKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 123m, 124m));

        kit.Feed(Ev.Bar(kit.At(1), 2));                                           // invalid bar exactly at the maturity instant

        Assert.Equal(kit.At(1), Assert.Single(kit.Fills()).At);                   // the deadline (class 0) fired before the bar event
        Assert.Equal(OpportunityState.Consumed, kit.Engine.Variants.AllOpportunities.Single().State);
    }

    [Fact]
    public void InvalidationBeforeMaturity_CancelsAtMaturityByRevalidation_NoFill()
    {
        var kit = InvalidatingKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 123m, 124m));                                 // a perfectly fillable quote

        kit.Feed(Ev.Bar(kit.At(0.9), 2));                                         // strategy invalid before the order matures
        kit.Loop.AdvanceTo(kit.At(1.2));

        Assert.Empty(kit.Fills());
        var order = kit.Engine.Execution.EntryOrders.Single();
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        var opportunity = kit.Engine.Variants.AllOpportunities.Single();
        Assert.Equal(OpportunityState.Cancelled, opportunity.State);
        Assert.Equal("StrategyInvalidated", opportunity.TerminalReason);
        Assert.Equal(SlotState.Free, kit.Engine.Variants.GetVariant("A").Slots[0].State);
        Assert.DoesNotContain(kit.Loop.PendingDeadlines, d => d.Key.StartsWith("exec-", StringComparison.Ordinal));   // its cancel/expiry deadlines were removed
    }

    [Fact]
    public void StrategyInvalidationWhileWorking_CancelsAtTheFirstEvaluation_EvenBeforeTheCancelLatencyElapses()
    {
        var kit = InvalidatingKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 130m, 131m));                                 // working, unfillable at maturity
        kit.Loop.AdvanceTo(kit.At(1.2));
        kit.Feed(Ev.Bar(kit.At(2), 2));                                           // CancelRequest at 2 s, effective 3 s (cancel latency 1 s)

        kit.Feed(kit.Q(kit.At(2.5), 117m, 118m));                                 // fillable quote before the cancel takes effect

        Assert.Empty(kit.Fills());                                                // revalidation (strategy no longer valid) wins: no fill after invalidation
        Assert.Equal(OrderStatus.Cancelled, kit.Engine.Execution.EntryOrders.Single().Status);
    }

    // ------------------------------------------------------------------ 15:00 boundary

    private static TradeKit EntryCloseKit(DateTimeOffset decisionAt)
    {
        var kit = NewKit();
        kit.Bar(decisionAt, 1, kit.Q(decisionAt, 124m, 125m));
        return kit;
    }

    [Fact]
    public void OrderMaturingExactlyAtFifteenHundred_DoesNotFill_AndIsCancelled()
    {
        var kit = EntryCloseKit(Clock.Ist(14, 59, 59));                           // matures at 15:00:00 exactly
        kit.Loop.AdvanceTo(Clock.Ist(15, 0, 2));

        Assert.Empty(kit.Fills());
        Assert.Equal(OrderStatus.Cancelled, kit.Engine.Execution.EntryOrders.Single().Status);
        var opportunity = kit.Engine.Variants.AllOpportunities.Single();
        Assert.Equal(OpportunityState.Expired, opportunity.State);
        Assert.Equal("EntryClose", opportunity.TerminalReason);
        Assert.Equal(SlotState.Free, kit.Engine.Variants.GetVariant("A").Slots[0].State);
    }

    [Fact]
    public void OrderMaturingJustBeforeFifteenHundred_StillFills_AndOneAfterIsCancelledAtTheBoundary()
    {
        var before = EntryCloseKit(Clock.Ist(14, 59, 58));                        // matures 14:59:59
        before.Loop.AdvanceTo(Clock.Ist(15, 0, 2));
        Assert.Equal(Clock.Ist(14, 59, 59), Assert.Single(before.Fills()).At);
        Assert.Equal(PositionState.Open, before.Position().State);                // entries stop at 15:00, positions are not touched by it

        var after = EntryCloseKit(Clock.Ist(14, 59, 59, 500));                    // would mature at 15:00:00.5, after the cut-off
        after.Loop.AdvanceTo(Clock.Ist(15, 0, 2));
        Assert.Empty(after.Fills());
        var cancel = Assert.Single(after.Out<CancelRequest>("A"));
        Assert.Equal(Clock.Ist(15, 0), cancel.At);                                // the 15:00 cancellation is immediate: no cancel latency
        Assert.Equal(OrderStatus.Cancelled, after.Engine.Execution.EntryOrders.Single().Status);
        Assert.Equal("EntryClose", after.Engine.Variants.AllOpportunities.Single().TerminalReason);
    }

    [Fact]
    public void ReservationsStayUntilTerminal_AndNoSecondOrderOverlaps()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        var slot = kit.Engine.Variants.GetVariant("A").Slots[0];
        for (var i = 1; i <= 8; i++)
        {
            kit.Feed(kit.Q(kit.At(i * 0.5), 129m, 130m));                         // unfillable at limit 125, qualifying for a new intent were the old one terminal
            Assert.True(kit.Engine.Execution.EntryOrders.Count(o => o.Status == OrderStatus.Working) <= 1);
            if (kit.Engine.Execution.EntryOrders.Any(o => o.Status == OrderStatus.Working))
            {
                Assert.Equal(SlotState.Reserved, slot.State);
            }
        }

        Assert.True(kit.Engine.Execution.EntryOrders.Count >= 1);
    }

    [Fact]
    public void EntryAttemptLimits_AreDisabledByDefault_InResearchV1()
    {
        Assert.Null(TradeKit.Immediate().MaxAttemptsPerOpportunity);
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        for (var i = 1; i < 6; i++)
        {
            var t = kit.At(i * 10);
            kit.Feed(kit.Q(t.AddSeconds(-9.5), 139m, 140m));                      // rises above the previous limit: the order cannot fill and expires
            kit.Feed(kit.Q(t, 124m, 125m));                                       // the order expired 5 s after its intent; the re-armed opportunity qualifies again
        }

        var opportunity = kit.Engine.Variants.AllOpportunities.Single();
        Assert.Equal(6, kit.Out<EntryIntent>("A").Count());
        Assert.Equal(6, opportunity.Attempts);
        Assert.False(opportunity.IsTerminal);
        Assert.Empty(kit.Fills());
    }
}
