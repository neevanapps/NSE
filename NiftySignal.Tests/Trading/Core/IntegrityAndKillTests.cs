using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

internal static class Ctl
{
    public static FeedHealthEvent Degraded(DateTimeOffset at, long seq, FeedFaultReason reason = FeedFaultReason.SocketDisconnected) => new(at, seq, FeedHealthState.Degraded, reason, "test");

    public static FeedHealthEvent Healthy(DateTimeOffset at, long seq) => new(at, seq, FeedHealthState.Healthy, FeedFaultReason.None, "test");

    public static OperatorCommandEvent Kill(DateTimeOffset at, long seq, string scope = "*", KillMode mode = KillMode.BlockEntries) => new(at, seq, OperatorCommand.Kill, scope, "tester", "test", mode);

    public static OperatorCommandEvent Reset(DateTimeOffset at, long seq, string scope = "*") => new(at, seq, OperatorCommand.Reset, scope, "tester", "test");
}

/// <summary>Recovery-evidence builders: a futures quote and bars with readiness information (CP5 correction of the CP4 recovery rule).</summary>
internal static class Rec
{
    public static QuoteEvent Fut(DateTimeOffset at, long id) => Ev.Fut(at, 7000 + id);

    public static BarCompletedEvent Bar(DateTimeOffset start, DateTimeOffset end, long seq, bool ready)
        => new(end, seq, "FUT", new BarInfo(start, end, 100, 12, ready, ready ? 10 : 4, 10, true, ready, "test-readiness"));

    public static BarCompletedEvent ReadyBar(DateTimeOffset start, DateTimeOffset end, long seq) => Bar(start, end, seq, ready: true);
}

public class IntegrityTests
{
    private static TradeKit NewKit(ExecutionConfig? execution = null, params VariantSpec[] variants)
        => new(TradeKit.MakeConfig(execution, variants: variants.Length == 0 ? new[] { TradeKit.Immediate() } : variants));

    private static List<IntegrityTransition> Transitions(TradeKit kit) => kit.Out<IntegrityTransition>().ToList();

    [Fact]
    public void States_Healthy_Degraded_Recovering_Healthy()
    {
        var kit = NewKit();

        kit.Feed(Ctl.Degraded(kit.At(10), 1, FeedFaultReason.NoMessageWatchdog));
        Assert.Equal(IntegrityState.Degraded, kit.Engine.Integrity.State);
        kit.Feed(Ctl.Degraded(kit.At(20), 2));                                          // already degraded: no new transition
        kit.Feed(Ctl.Healthy(kit.At(30), 3));
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        Assert.Equal("IntegrityRecovering", kit.Engine.Integrity.BlockReason("A", kit.At(30)));    // still blocked until the evidence arrives
        kit.Feed(Rec.Fut(kit.At(40), 1));
        kit.Feed(Rec.ReadyBar(kit.At(35), kit.At(41), 5));                              // ready bar that started after the recovery instant

        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);
        Assert.Null(kit.Engine.Integrity.BlockReason("A", kit.At(41)));
        var t = Transitions(kit);
        Assert.Equal(
            new[] { (IntegrityState.Healthy, IntegrityState.Degraded), (IntegrityState.Degraded, IntegrityState.Recovering), (IntegrityState.Recovering, IntegrityState.Healthy) },
            t.Select(x => (x.From, x.To)).ToArray());
        Assert.Equal(FeedFaultReason.NoMessageWatchdog, t[0].Reason);
        Assert.Equal(kit.At(41), t[2].At);
        Assert.Contains("readyBar=b5", t[2].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Edge_Cases_HealthyWhileHealthy_DegradedWhileRecovering_AndRecoveryOnTheSameInstant()
    {
        var kit = NewKit();
        kit.Feed(Ctl.Healthy(kit.At(1), 1));
        Assert.Empty(Transitions(kit));

        kit.Feed(Ctl.Degraded(kit.At(2), 2));
        kit.Feed(Ctl.Healthy(kit.At(3), 3));
        kit.Feed(Ctl.Degraded(kit.At(4), 4));
        Assert.Equal(IntegrityState.Degraded, kit.Engine.Integrity.State);              // a new fault during recovery is a fresh Degraded

        // health ranks before data at one instant: a futures quote and a ready bar that START at the recovery instant complete it.
        kit.Feed(Ctl.Healthy(kit.At(5), 5), Rec.Fut(kit.At(5), 1));
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        kit.Feed(Rec.ReadyBar(kit.At(5), kit.At(6), 3));
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);
        Assert.Equal(kit.At(6), Transitions(kit).Last().At);
    }

    [Fact]
    public void Recovery_IsNeverGrantedByAnUnrelatedOptionQuote()
    {
        var kit = NewKit();
        kit.Feed(Ctl.Degraded(kit.At(10), 1));
        kit.Feed(Ctl.Healthy(kit.At(30), 2));

        kit.Feed(kit.Q(kit.At(31), 124m, 125m));                                        // the contract's own option quotes, any number of them
        kit.Feed(kit.Q(kit.At(32), 124m, 125m, token: "PE23900"));
        kit.Feed(kit.Q(kit.At(33), 124m, 125m));

        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        Assert.Equal("IntegrityRecovering", kit.Engine.Integrity.BlockReason("A", kit.At(33)));
        Assert.Equal(2, Transitions(kit).Count);
    }

    [Fact]
    public void Recovery_NeedsFreshFuturesState_AStaleFuturesQuoteIsNotEvidence()
    {
        var kit = NewKit();
        kit.Feed(Rec.Fut(kit.At(5), 1));                                                // futures quote BEFORE the fault: stale
        kit.Feed(Ctl.Degraded(kit.At(10), 1));
        kit.Feed(Ctl.Healthy(kit.At(30), 2));

        kit.Feed(Rec.ReadyBar(kit.At(31), kit.At(32), 6));                              // a ready bar, but no futures quote since the recovery instant
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);

        kit.Feed(Rec.Fut(kit.At(33), 2));                                               // the futures stream is now demonstrably alive: both pieces of post-recovery evidence exist
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);
        Assert.Equal(kit.At(33), Transitions(kit).Last().At);
        Assert.Contains("readyBar=b6", Transitions(kit).Last().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_NeedsAReadyBar_IncompleteWarmUpOrABarThatStartedBeforeTheRecoveryDoesNotCount()
    {
        var kit = NewKit();
        kit.Feed(Ctl.Degraded(kit.At(10), 1));
        kit.Feed(Ctl.Healthy(kit.At(30), 2));
        kit.Feed(Rec.Fut(kit.At(31), 1));

        kit.Feed(Rec.Bar(kit.At(31), kit.At(32), 8, ready: false));                    // warm-up incomplete (fewer than the required consecutive valid bars)
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        kit.Feed(Rec.ReadyBar(kit.At(20), kit.At(33), 9));                              // ready, but it started during the outage: ticks from the gap are inside it
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        kit.Feed(Ev.Bar(kit.At(34), 10));                                               // a bar without readiness information is never evidence
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);

        kit.Feed(Rec.ReadyBar(kit.At(33.5), kit.At(35), 11));
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);
        Assert.Contains("readyBar=b11", Transitions(kit).Last().Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryPolicy_IsConfigurable_ButNeverEmpty()
    {
        var relaxed = TradeKit.MakeConfig(variants: TradeKit.Immediate()) with { Integrity = new IntegrityPolicy { RequireReadyBarAfterRecovery = false, ReadyBarMustStartAfterRecovery = false } };
        var kit = new TradeKit(relaxed);
        kit.Feed(Ctl.Degraded(kit.At(10), 1));
        kit.Feed(Ctl.Healthy(kit.At(30), 2));
        kit.Feed(kit.Q(kit.At(31), 124m, 125m));
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);            // still no option-quote recovery
        kit.Feed(Rec.Fut(kit.At(32), 1));
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);

        Assert.NotEmpty(new IntegrityPolicy { RequireFutureQuoteAfterRecovery = false, RequireReadyBarAfterRecovery = false }.Validate());
        Assert.NotEmpty(new IntegrityPolicy { RequireReadyBarAfterRecovery = false }.Validate());   // "must start after" without a bar requirement
        Assert.NotEqual(TradeKit.MakeConfig(variants: TradeKit.Immediate()).ComputeHash(), relaxed.ComputeHash());
    }

    [Fact]
    public void Degraded_BlocksNewEntries_CancelsPendingOnes_KeepsTheReservation_AndEntriesResumeOnlyAfterVerifiedRecovery()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                       // an entry attempt is pending (matures at 1 s)
        var slot = kit.Engine.Variants.GetVariant("A").Slots[0];
        Assert.Equal(SlotState.Reserved, slot.State);

        kit.Feed(Ctl.Degraded(kit.At(0.5), 1));
        var cancel = Assert.Single(kit.Out<CancelRequest>("A"));
        Assert.Equal(CancelReason.IntegrityDegraded, cancel.Reason);
        Assert.Equal(SlotState.Reserved, slot.State);                                  // reservation kept until the cancellation is resolved
        Assert.Equal(OpportunityState.OrderPending, kit.Engine.Variants.GetVariant("A").Active!.State);

        kit.Loop.AdvanceTo(kit.At(1.2));                                               // the order matures: the integrity gate cancels it instead of filling it
        Assert.Empty(kit.Fills());
        Assert.Equal(OrderStatus.Cancelled, kit.Engine.Execution.EntryOrders.Single().Status);
        Assert.Equal(SlotState.Free, slot.State);
        Assert.Equal(OpportunityState.Waiting, kit.Engine.Variants.GetVariant("A").Active!.State);       // the idea survives; entries stay blocked

        kit.Feed(kit.Q(kit.At(2), 124m, 125m));                                        // a perfectly good quote while Degraded
        Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Blocked:IntegrityDegraded");

        kit.Feed(Ctl.Healthy(kit.At(3), 2));
        Assert.Single(kit.Out<EntryIntent>("A"));                                      // Recovering still blocks

        kit.Feed(kit.Q(kit.At(3.5), 124m, 125m));                                      // an option quote is not recovery evidence
        Assert.Equal(IntegrityState.Recovering, kit.Engine.Integrity.State);
        Assert.Single(kit.Out<EntryIntent>("A"));

        kit.Feed(Rec.Fut(kit.At(3.6), 1));
        kit.Feed(Rec.ReadyBar(kit.At(3.2), kit.At(3.8), 2));                           // evidence complete: Healthy, from the next quote on entries resume
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);
        kit.Feed(kit.Q(kit.At(4), 124m, 125m));
        var intents = kit.Out<EntryIntent>("A").ToList();
        Assert.Equal(2, intents.Count);
        Assert.Equal(kit.At(4), intents[1].At);
    }

    [Fact]
    public void Degraded_NeverStopsExits_TheyKeepUsingEligibleQuotes()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { TradeSlPct = 0.10m }));
        kit.Feed(Ctl.Degraded(kit.At(2), 1));

        kit.Feed(kit.Q(kit.At(3), 95m, 96m));
        kit.Loop.AdvanceTo(kit.At(4.5));

        Assert.Equal(IntegrityState.Degraded, kit.Engine.Integrity.State);
        Assert.Equal(ExitReason.TradeSl, Assert.Single(kit.Out<ExitOrderCreated>("A")).Reason);
        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(95m, sell.Price);
        Assert.Equal(PositionState.Closed, kit.Position().State);
    }

    [Fact]
    public void Degraded_CompulsoryLiquidation_UsesEligibleQuotesOnly_AndNeverManufacturesAFill()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        kit.Feed(Ctl.Degraded(kit.At(2), 1));

        kit.Loop.AdvanceTo(Clock.Ist(15, 10, 30));
        Assert.Single(kit.Out<UnresolvedAtClose>("A"));
        Assert.Single(kit.Fills());                                                    // no eligible quote: still only the entry

        kit.Feed(kit.Q(Clock.Ist(15, 12), 119m, 120m));                                // a fresh eligible quote appears while still Degraded
        kit.Loop.AdvanceTo(Clock.Ist(15, 13));

        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(119m, sell.Price);
        Assert.True(sell.AfterFinalClose);
    }

    [Fact]
    public void StaleQuoteOfOneContract_IsNotAFeedFault_AndAffectsOnlyThatContractsEntries()
    {
        var execution = new ExecutionConfig { MaxQuoteAge = TimeSpan.FromMilliseconds(500) };
        var put = TradeKit.Immediate("B") with { StrategyId = "P" };
        var kit = new TradeKit(
            TradeKit.MakeConfig(execution, variants: new[] { TradeKit.Immediate("A"), put }),
            null,
            ScriptedStrategy.Valid("S", 1, null),
            ScriptedStrategy.Valid("P", 1, null, OptionSide.Put));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m), kit.Q(TradeKit.T0, 124m, 125m, token: "PE23900"));
        kit.Feed(kit.Q(kit.At(0.8), 124m, 125m, token: "PE23900"));                    // only the put keeps quoting

        kit.Loop.AdvanceTo(kit.At(1.5));

        Assert.Empty(kit.Fills("A"));                                                  // the call's only quote is 1 s old (> 0.5 s)
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:QuoteStale");
        Assert.Single(kit.Fills("B"));
        Assert.Equal(IntegrityState.Healthy, kit.Engine.Integrity.State);              // no global state change
        Assert.Empty(kit.Out<IntegrityTransition>());
        Assert.Empty(kit.Out<CancelRequest>());                                        // and nobody's order was cancelled
        Assert.Equal(OrderStatus.Working, kit.Engine.Execution.EntryOrders.Single(o => o.Intent.VariantId == "A").Status);
    }

    [Fact]
    public void HealthEvent_AtTheSameInstantAsAFillableQuote_AppliesFirst()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 125m, 126m));
        kit.Loop.AdvanceTo(kit.At(1.2));                                               // matured, unfillable at the moment

        kit.Feed(kit.Q(kit.At(2), 117m, 118m), Ctl.Degraded(kit.At(2), 1));           // a fault and a fillable quote at one instant

        Assert.Empty(kit.Fills());
        Assert.Equal(OrderStatus.Cancelled, kit.Engine.Execution.EntryOrders.Single().Status);
    }

    [Fact]
    public void FeedHealthAdapterContract_IsAnInterfaceOnly()
    {
        var source = new FakeHealthSource();
        var drained = source.Drain(Clock.Ist(10, 0));

        Assert.Single(drained);
        Assert.Empty(source.Drain(Clock.Ist(10, 0)));
        Assert.Equal(FeedHealthState.Degraded, drained[0].State);
    }

    private sealed class FakeHealthSource : IFeedHealthSource
    {
        private bool _given;

        public IReadOnlyList<FeedHealthEvent> Drain(DateTimeOffset watermark)
        {
            if (_given)
            {
                return Array.Empty<FeedHealthEvent>();
            }

            _given = true;
            return new[] { Ctl.Degraded(watermark, 1, FeedFaultReason.DataGapOpen) };
        }
    }

    // ------------------------------------------------------------------ quarantine

    [Fact]
    public void LateConflictingAndInconsistentEvents_AreQuarantinedWithAuditRecords_AndHaveNoEffect()
    {
        var kit = NewKit();
        var good = kit.Q(kit.At(20), 124m, 125m);
        kit.Feed(good);
        var admission = kit.Engine.Admission;

        var late = admission.Admit(kit.Loop, new EngineEvent[] { kit.Q(kit.At(5), 100m, 101m) });
        var sameInstant = admission.Admit(kit.Loop, new EngineEvent[] { kit.Q(kit.At(20), 100m, 101m) });
        var conflicting = admission.Admit(kit.Loop, new EngineEvent[] { new QuoteEvent(kit.At(30), good.Kind, good.SourceId, good.Token, 124m, 126m) });
        var negative = admission.Admit(kit.Loop, new EngineEvent[] { kit.Q(kit.At(31), 100m, -1m) });
        var exactDuplicate = admission.Admit(kit.Loop, new EngineEvent[] { good });
        var fine = admission.Admit(kit.Loop, new EngineEvent[] { kit.Q(kit.At(32), 124m, 125m) });

        Assert.Empty(late);
        Assert.Empty(sameInstant);
        Assert.Empty(conflicting);
        Assert.Empty(negative);
        Assert.Single(exactDuplicate);                                                // the loop ignores exact re-deliveries idempotently
        Assert.Single(fine);
        Assert.Equal(new[] { "Late", "Late", "ConflictingDuplicate", "Inconsistent:NegativeValue" }, kit.Out<EventQuarantined>().Select(q => q.Reason).ToArray());
        Assert.Equal(4, admission.QuarantinedCount);
        Assert.Equal(125m, kit.Engine.Variants.Quotes.Get("CE23900")!.Ask);          // the rejected events changed nothing
    }

    [Fact]
    public void NonIncreasingBarSequence_IsQuarantined()
    {
        var kit = NewKit();
        var admission = kit.Engine.Admission;

        Assert.Single(admission.Admit(kit.Loop, new EngineEvent[] { Ev.Bar(kit.At(1), 5) }));
        Assert.Empty(admission.Admit(kit.Loop, new EngineEvent[] { Ev.Bar(kit.At(2), 5) }));
        Assert.Empty(admission.Admit(kit.Loop, new EngineEvent[] { Ev.Bar(kit.At(3), 4) }));

        Assert.All(kit.Out<EventQuarantined>(), q => Assert.Equal("Inconsistent:BarSequence", q.Reason));
        Assert.Equal(2, kit.Out<EventQuarantined>().Count());
    }

    [Fact]
    public void EventKinds_ControlAndHealth_RankBeforeDataAtOneInstant()
    {
        var t = Clock.Ist(10, 0);
        var ordered = new EngineEvent[] { Ev.Bar(t, 1), Ev.Opt(t, 2), Ctl.Degraded(t, 3), Ctl.Kill(t, 4) }
            .OrderBy(e => e, EventOrdering.Instance)
            .Select(e => e.Kind)
            .ToArray();

        Assert.Equal(new[] { EventKind.Control, EventKind.FeedHealth, EventKind.OptionQuote, EventKind.BarCompleted }, ordered);
    }
}

public class KillSwitchTests
{
    private static TradeKit NewKit(params VariantSpec[] variants)
        => new(TradeKit.MakeConfig(variants: variants.Length == 0 ? new[] { TradeKit.Immediate() } : variants));

    private static KillSwitchChanged Last(TradeKit kit) => kit.Out<KillSwitchChanged>().Last();

    [Fact]
    public void Kill_EndsWaitingOpportunities_BlocksEntries_AndAResetDoesNotResurrectThem()
    {
        var script = new ScriptedStrategy("S", seq => new StrategyValidity(seq is 1 or 3, OptionSide.Call, "x"));
        var kit = new TradeKit(TradeKit.MakeConfig(variants: new[] { new VariantSpec { VariantId = "A", StrategyId = "S", EntryCondition = new NiftySignal.Trading.Core.Entry.AskBelowCandleMaSpec { N = 1, PriceSource = NiftySignal.Trading.Core.Candles.CandlePriceSource.AskClose } } }), null, script);
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                       // waiting
        var first = kit.Engine.Variants.GetVariant("A").Active!;

        kit.Feed(Ctl.Kill(kit.At(2), 1));
        Assert.Equal("Killed", Last(kit).Effect);
        Assert.Equal(1, Last(kit).CancelledWaiting);
        Assert.Equal(OpportunityState.Cancelled, first.State);
        Assert.Equal("KillSwitch", first.TerminalReason);
        Assert.Equal("KillSwitch", kit.Engine.Integrity.BlockReason("A", kit.At(2)));

        kit.Feed(Ev.Bar(kit.At(3), 2));                                                // strategy invalid
        kit.Feed(Ctl.Reset(kit.At(4), 2));
        Assert.Equal("Reset", Last(kit).Effect);
        Assert.Null(kit.Engine.Integrity.BlockReason("A", kit.At(4)));
        Assert.Equal(OpportunityState.Cancelled, first.State);                         // not resurrected

        kit.Bar(kit.At(6), 3, kit.Q(kit.At(6), 129m, 130m));                           // a NEW episode after the reset
        kit.Feed(kit.Q(kit.At(7), 128m, 129m));                                        // below the candle close 130: qualifies
        var second = kit.Engine.Variants.AllOpportunities.Last();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Single(kit.Out<EntryIntent>("A"));
    }

    [Fact]
    public void Kill_RequestsCancellationOfPendingEntries_KeepsTheReservation_AndTheOpportunityEnds()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        var slot = kit.Engine.Variants.GetVariant("A").Slots[0];

        kit.Feed(Ctl.Kill(kit.At(0.5), 1));
        Assert.Equal(1, Last(kit).CancelledPending);
        Assert.Equal(CancelReason.KillSwitch, Assert.Single(kit.Out<CancelRequest>("A")).Reason);
        Assert.Equal(SlotState.Reserved, slot.State);

        kit.Loop.AdvanceTo(kit.At(1.2));

        Assert.Empty(kit.Fills());
        Assert.Equal(SlotState.Free, slot.State);
        var opportunity = kit.Engine.Variants.AllOpportunities.Single();
        Assert.Equal(OpportunityState.Cancelled, opportunity.State);
        Assert.Equal("KillSwitch", opportunity.TerminalReason);
    }

    [Fact]
    public void KillScope_IsPerVariant_GlobalOverridesVariantReset_UnknownScopeIsRejected_AndKillIsIdempotent()
    {
        var kit = NewKit(TradeKit.Immediate("A"), TradeKit.Immediate("B"));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));

        kit.Feed(Ctl.Kill(kit.At(0.5), 1, "A"));
        kit.Loop.AdvanceTo(kit.At(1.5));
        Assert.Empty(kit.Fills("A"));
        Assert.Single(kit.Fills("B"));                                                 // B was not in scope

        kit.Feed(Ctl.Kill(kit.At(2), 2, "A"));
        Assert.Equal("AlreadyKilled", Last(kit).Effect);
        kit.Feed(Ctl.Kill(kit.At(3), 3, "Z"));
        Assert.Equal("Rejected:UnknownVariant", Last(kit).Effect);

        kit.Feed(Ctl.Kill(kit.At(4), 4));
        Assert.True(kit.Engine.Integrity.KillAll);
        kit.Feed(Ctl.Reset(kit.At(5), 5, "A"));
        Assert.Equal("ResetIgnored:GlobalKillActive", Last(kit).Effect);
        Assert.Equal("KillSwitch", kit.Engine.Integrity.BlockReason("A", kit.At(5)));
        kit.Feed(Ctl.Reset(kit.At(6), 6));
        Assert.Equal("Reset", Last(kit).Effect);
        kit.Feed(Ctl.Reset(kit.At(7), 7));
        Assert.Equal("NothingToReset", Last(kit).Effect);
        Assert.Null(kit.Engine.Integrity.BlockReason("A", kit.At(7)));
    }

    [Fact]
    public void Kill_NeverStopsExits()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { TradeSlPct = 0.10m }));

        kit.Feed(Ctl.Kill(kit.At(2), 1));
        Assert.Empty(kit.Out<ExitOrderCreated>("A"));                                  // BlockEntries only: the position is left to its own rules
        kit.Feed(kit.Q(kit.At(3), 95m, 96m));
        kit.Loop.AdvanceTo(kit.At(4.5));

        Assert.Equal(ExitReason.TradeSl, Assert.Single(kit.Out<ExitOrderCreated>("A")).Reason);
        Assert.Equal(PositionState.Closed, kit.Position().State);
    }

    [Fact]
    public void KillWithLiquidation_ExitsThroughTheNormalPath_OnlyTheUnreservedQuantity()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m }));
        kit.Feed(kit.Q(kit.At(2), 138m, 139m));                                         // TP1: 325 reserved

        kit.Feed(Ctl.Kill(kit.At(2.2), 1, "*", KillMode.BlockEntriesAndLiquidate));
        Assert.Equal(1, Last(kit).LiquidationOrders);
        kit.Loop.AdvanceTo(kit.At(5));

        var created = kit.Out<ExitOrderCreated>("A").ToList();
        Assert.Equal(new[] { ExitReason.Tp1, ExitReason.ManualKill }, created.Select(c => c.Reason).ToArray());
        Assert.Equal(new long[] { 325, 325 }, created.Select(c => c.Quantity).ToArray());
        Assert.Equal(PositionState.Closed, kit.Position().State);
        Assert.Equal(650, kit.Fills().Where(f => f.Side == TradeSide.Sell).Sum(f => f.Quantity));
    }

    [Fact]
    public void ControlEvent_AtTheSameInstantAsAFillableQuote_AppliesFirst()
    {
        var kit = NewKit();
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), 125m, 126m));
        kit.Loop.AdvanceTo(kit.At(1.2));

        kit.Feed(kit.Q(kit.At(2), 117m, 118m), Ctl.Kill(kit.At(2), 1));

        Assert.Empty(kit.Fills());
    }
}
