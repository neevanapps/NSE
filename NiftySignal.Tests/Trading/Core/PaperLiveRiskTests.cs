using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>CP2: the PaperLiveV1 profile. Scoped entry limits (variant, family, global), shared capital, typed rejections, kill scopes, max hold, the session rules.</summary>
public class PaperLiveRiskTests
{
    private static readonly VariantKey X1 = new("X", "X1");
    private static readonly VariantKey X2 = new("X", "X2");
    private static readonly VariantKey X3 = new("X", "X3");
    private static readonly VariantKey Y1 = new("Y", "Y1");
    private static readonly VariantKey Y2 = new("Y", "Y2");

    private static readonly ExitSpec Stop10 = new() { TradeSlPct = 0.10m };

    private static (long From, long To) W(long from, long to) => (from, to);

    private static PlumbingVariantScript S(params (long From, long To)[] windows) => PaperKit.Script(windows);

    private static PlumbingVariantScript Short(params (long From, long To)[] windows)
        => new(windows.Select(w => new PlumbingWindow(w.From, w.To, StrategyDirection.Short)).ToList());

    private static string Reasons(PaperKit kit, VariantKey key) => string.Join(" | ", kit.Rejections(key).Select(r => r.Reasons));

    /// <summary>Provokes a stop-loss on the CE contract: the bid collapses, the stop order is created on the quote and fills one latency later.</summary>
    private static void CrashCe(PaperKit kit, DateTimeOffset at, decimal bid = 100m) => kit.Move(at, ceBid: bid);

    // ------------------------------------------------------------------ session rules

    [Fact]
    public void NoEntryBefore0930_TheFirstInstantOf0930IsEligible()
    {
        var kit = new PaperKit(PaperKit.Config(1_000_000m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 3)))));

        kit.Bar(9, 29, 59, advance: 0.4);                                // bar 1 is still before the window (09:29:59)
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X1));
        var before = Assert.Single(kit.Rejections(X1));
        Assert.Equal("BeforeEntryWindow", before.Reasons);
        Assert.Null(before.OpportunityId);                                // no opportunity could be created

        kit.Bar(9, 30, 0);                                               // bar 2 at exactly 09:30:00: inclusive
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Single(kit.Rejections(X1));                                // no further rejection
    }

    [Fact]
    public void NoEntryAt1500_OneSecondEarlierStillFills()
    {
        var kit = new PaperKit(PaperKit.Config(1_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(2, 2)))));

        kit.Bar(14, 59, 58);                                             // decision 14:59:58, fill 14:59:59: inside the window
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X1));

        kit.Bar(15, 0, 0);                                               // decision at exactly 15:00:00: closed
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Equal("EntryWindowClosed", Assert.Single(kit.Rejections(X2)).Reasons);
    }

    // ------------------------------------------------------------------ trade counts

    [Fact]
    public void VariantMaxTrades_CountsFilledEntries_AndOnlyBlocksThatVariant()
    {
        var limits = new ScopeLimits { MaxTradesPerDay = 1 };
        var kit = new PaperKit(
            PaperKit.Config(1_000_000m, PaperKit.Spec("X", "X1", Stop10, limits), PaperKit.Spec("X", "X2")),
            PaperKit.Family("X", ("X1", S(W(1, 1), W(3, 3))), ("X2", S(W(3, 3)))));

        var t = kit.Bar(10, 0);                                          // X1 fills: trade 1
        CrashCe(kit, t.AddSeconds(10));                                  // stopped out: the slot is free again
        Assert.Equal(PositionState.Closed, kit.Kit.Position(X1).State);
        kit.Bar(10, 5);                                                  // bar 2: both wait
        kit.Bar(10, 10, ceBid: 124m);                                    // bar 3: X1 is valid again, X2 enters

        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X1));         // the second X1 entry never happened
        Assert.Equal(1, kit.Kit.Engine.Execution.TradesOf(X1));
        Assert.Equal("VariantTradeLimit", Assert.Single(kit.Rejections(X1)).Reasons);
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X2));          // X2 has no limit
    }

    [Fact]
    public void AnEnterIntent_ARejection_OrAWaitingOpportunity_IsNotATrade()
    {
        var kit = new PaperKit(PaperKit.Config(100m, PaperKit.Spec("X", "X1", limits: new ScopeLimits { MaxTradesPerDay = 1 })), PaperKit.Family("X", ("X1", S(W(1, 2)))));

        kit.Bar(10, 0);                                                  // capital 100 cannot hold 81,250: rejected, nothing fills
        kit.Bar(10, 1);

        Assert.Equal(0, kit.Kit.Engine.Execution.TradesOf(X1));
        Assert.Equal(0, kit.Kit.Engine.Execution.GlobalTrades());
        Assert.NotEmpty(kit.Rejections(X1));
    }

    [Fact]
    public void FamilyMaxTrades_IsCollective_AcrossItsVariants_AndNeverLimitsAnotherFamily()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, families: new Dictionary<string, ScopeLimits> { ["X"] = new() { MaxTradesPerDay = 2 } });
        var kit = new PaperKit(
            PaperKit.Config(risk, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2"), PaperKit.Spec("X", "X3"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(2, 2))), ("X3", S(W(3, 3)))),
            PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        kit.Bar(10, 0);
        kit.Bar(10, 5);
        kit.Bar(10, 10);                                                 // X3 would be the family's third trade; Y1 is another family

        Assert.Equal(2, kit.Kit.Engine.Execution.FamilyTradesOf("X"));
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X3));
        Assert.Equal("FamilyTradeLimit", Assert.Single(kit.Rejections(X3)).Reasons);
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(Y1));
        Assert.Equal(1, kit.Kit.Engine.Execution.FamilyTradesOf("Y"));
    }

    [Fact]
    public void GlobalMaxTrades_CountsEveryStrategy()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, global: new ScopeLimits { MaxTradesPerDay = 2 });
        var kit = new PaperKit(
            PaperKit.Config(risk, PaperKit.Spec("X", "X1"), PaperKit.Spec("Y", "Y1"), PaperKit.Spec("Y", "Y2")),
            PaperKit.Family("X", ("X1", S(W(1, 1)))),
            PaperKit.Family("Y", ("Y1", S(W(2, 2))), ("Y2", S(W(3, 3)))));

        kit.Bar(10, 0);
        kit.Bar(10, 5);
        kit.Bar(10, 10);

        Assert.Equal(2, kit.Kit.Engine.Execution.GlobalTrades());
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(Y2));
        Assert.Equal("GlobalTradeLimit", Assert.Single(kit.Rejections(Y2)).Reasons);
    }

    // ------------------------------------------------------------------ daily realized loss

    [Fact]
    public void FamilyDailyLoss_UsesRealizedNetOnly_BlocksEntriesOfTheFamily_AndKeepsManagingOpenPositions()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, families: new Dictionary<string, ScopeLimits> { ["X"] = new() { MaxDailyLoss = 10_000m } });
        var kit = new PaperKit(
            PaperKit.Config(risk, PaperKit.Spec("X", "X1", Stop10), PaperKit.Spec("X", "X2", Stop10), PaperKit.Spec("X", "X3"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", Short(W(1, 1))), ("X3", S(W(3, 3)))),
            PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        var t = kit.Bar(10, 0);                                          // X1 enters long (CE) and X2 short (PE): the family has lost nothing yet
        Assert.Equal(PositionState.Open, kit.Kit.Position(X2).State);
        CrashCe(kit, t.AddSeconds(10));                                  // X1 stops out with a realized loss of about 16.5k; X2 (PE) is untouched and stays open
        Assert.Equal(PositionState.Closed, kit.Kit.Position(X1).State);
        Assert.True(kit.Kit.Engine.Execution.RealizedNetOf(X1) < -10_000m);

        kit.Bar(10, 5);
        kit.Bar(10, 10);                                                 // X3 would be a new X entry: blocked; Y1 is another family: allowed
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X3));
        Assert.Equal("FamilyDailyLoss", Assert.Single(kit.Rejections(X3)).Reasons);
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(Y1));
        Assert.Equal(PositionState.Open, kit.Kit.Position(X2).State);    // the lockout liquidated nothing

        kit.Move(Clock.Ist(10, 11), peBid: 100m);                        // the stop of X2 still works: existing positions are managed normally
        Assert.Equal(PositionState.Closed, kit.Kit.Position(X2).State);
    }

    [Fact]
    public void UnrealizedLoss_NeverTriggersTheDailyLossLockout()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, global: new ScopeLimits { MaxDailyLoss = 1_000m });
        var kit = new PaperKit(PaperKit.Config(risk, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")), PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(3, 3)))));

        var t = kit.Bar(10, 0);
        CrashCe(kit, t.AddSeconds(10), bid: 60m);                        // the open position is under water by about 42k, nothing is realized
        kit.Bar(10, 5, ceBid: 60m, ceAsk: 61m);
        kit.Bar(10, 10);

        Assert.True(kit.Kit.Engine.Execution.RealizedNetOf(X1) > -1_000m);
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Empty(kit.Rejections(X2));
    }

    [Fact]
    public void GlobalAndVariantDailyLoss_AreSeparateScopes()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, global: new ScopeLimits { MaxDailyLoss = 10_000m });
        var kit = new PaperKit(
            PaperKit.Config(risk, PaperKit.Spec("X", "X1", Stop10, new ScopeLimits { MaxDailyLoss = 10_000m }), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1), W(3, 3)))),
            PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        var t = kit.Bar(10, 0);
        CrashCe(kit, t.AddSeconds(10));
        kit.Bar(10, 5);
        kit.Bar(10, 10);

        Assert.Equal("GlobalDailyLoss,VariantDailyLoss", Assert.Single(kit.Rejections(X1)).Reasons);   // both hold; stable order
        Assert.Equal("GlobalDailyLoss", Assert.Single(kit.Rejections(Y1)).Reasons);
    }

    [Fact]
    public void ATp1Leg_BecomesRealizedPnlImmediately_BeforeTheTradeCloses()
    {
        var exit = new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m };
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1", exit)), PaperKit.Family("X", ("X1", S(W(1, 1)))));

        var t = kit.Bar(10, 0);
        var afterEntry = kit.Kit.Engine.Execution.RealizedNetOf(X1);
        Assert.True(afterEntry < 0m);                                    // only the entry costs so far
        kit.Move(t.AddSeconds(10), ceBid: 140m);                         // TP1 triggers at +10%, the leg fills one latency later

        Assert.Equal(PositionState.Open, kit.Kit.Position(X1).State);
        Assert.Equal(325, kit.Kit.Position(X1).OpenQuantity);
        Assert.True(kit.Kit.Engine.Execution.RealizedNetOf(X1) > 0m);    // 325 x (140 - 125) = 4,875 realized, minus all costs
    }

    // ------------------------------------------------------------------ consecutive losses

    [Fact]
    public void VariantConsecutiveLosses_BlockAtTheLimit_AWinningCloseResetsTheStreak()
    {
        var limits = new ScopeLimits { MaxConsecutiveLosses = 2 };
        var kit = new PaperKit(
            PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1", Stop10, limits), PaperKit.Spec("X", "X2", null, limits)),
            PaperKit.Family("X", ("X1", S(W(1, 1), W(3, 3), W(5, 5))), ("X2", S(W(1, 1), W(3, 3), W(5, 5)))));

        var t = kit.Bar(10, 0);                                          // both enter (CE)
        CrashCe(kit, t.AddSeconds(10));                                  // X1: stopped out (loss 1); X2 has no stop: still open
        Assert.Equal(1, kit.Kit.Engine.Execution.ConsecutiveLossesOf(X1));
        Assert.Equal(0, kit.Kit.Engine.Execution.ConsecutiveLossesOf(X2));

        kit.Bar(10, 5);                                                  // bar 2: both wait (X2 keeps its open position)
        var t2 = kit.Bar(10, 10);                                        // bar 3: X1 enters again; X2's slot is busy -> capacity
        CrashCe(kit, t2.AddSeconds(10));
        Assert.Equal(2, kit.Kit.Engine.Execution.ConsecutiveLossesOf(X1));

        kit.Bar(10, 15);
        kit.Bar(10, 20);                                                 // bar 5: X1 is valid again but the streak limit is reached
        Assert.Equal(2, kit.Kit.Engine.Execution.PositionsOf(X1).Count);
        Assert.Contains(kit.Rejections(X1), r => r.Reasons == "VariantConsecutiveLoss");

        // a closing trade at net >= 0 resets the streak: X2 closes its position in profit through a strategy exit
        var x2 = kit.Kit.Position(X2);
        kit.Kit.Engine.Execution.RequestStrategyExit(X2, x2.Id, null, kit.Kit.Loop);
        kit.Move(Clock.Ist(10, 21), ceBid: 140m);
        Assert.Equal(PositionState.Closed, x2.State);
        Assert.True(x2.NetRealised > 0m);
        Assert.Equal(0, kit.Kit.Engine.Execution.ConsecutiveLossesOf(X2));
    }

    [Fact]
    public void FamilyConsecutiveLosses_AreChronologicalAcrossVariants_AndATp1LegAloneDoesNotChangeTheStreak()
    {
        var risk = RiskProfile.PaperLiveV1(10_000_000m, families: new Dictionary<string, ScopeLimits> { ["X"] = new() { MaxConsecutiveLosses = 2 } });
        var exit = new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m };
        var kit = new PaperKit(
            PaperKit.Config(risk, PaperKit.Spec("X", "X1", exit), PaperKit.Spec("X", "X2", Stop10), PaperKit.Spec("X", "X3"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1))), ("X3", S(W(3, 3)))),
            PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        var t = kit.Bar(10, 0);                                          // X1 and X2 enter (CE)
        kit.Move(t.AddSeconds(10), ceBid: 140m);                         // X1 takes TP1 (a realized leg); X2 has no target
        Assert.Equal(0, kit.Kit.Engine.Execution.FamilyConsecutiveLosses("X"));   // TP1 alone changes nothing
        CrashCe(kit, t.AddSeconds(20), bid: 100m);                       // X1's remainder and X2 stop out
        // X1: +4,875 leg and a loss on the remainder: total is judged once, at the final close; X2: a plain loss
        var x1 = kit.Kit.Position(X1);
        var x2 = kit.Kit.Position(X2);
        Assert.Equal(PositionState.Closed, x1.State);
        Assert.Equal(PositionState.Closed, x2.State);
        var losses = new[] { x1, x2 }.Count(p => p.NetRealised < 0m);
        Assert.Equal(losses, kit.Kit.Engine.Execution.FamilyConsecutiveLosses("X"));   // chronological, one step per final close

        kit.Bar(10, 5);
        kit.Bar(10, 10);                                                 // X3 and Y1 become valid
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(Y1));          // another family is not touched
        if (losses >= 2)
        {
            Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X3));
            Assert.Equal("FamilyConsecutiveLoss", Assert.Single(kit.Rejections(X3)).Reasons);
        }
    }

    [Fact]
    public void ConsecutiveLossOrdering_OfSimultaneousCloses_IsDeterministic()
    {
        static string Run()
        {
            var risk = RiskProfile.PaperLiveV1(10_000_000m, families: new Dictionary<string, ScopeLimits> { ["X"] = new() { MaxConsecutiveLosses = 5 } });
            var kit = new PaperKit(
                PaperKit.Config(risk, PaperKit.Spec("X", "X1", Stop10), PaperKit.Spec("X", "X2", Stop10), PaperKit.Spec("X", "X3", Stop10)),
                PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1))), ("X3", S(W(1, 1)))));
            var t = kit.Bar(10, 0);
            CrashCe(kit, t.AddSeconds(10));
            return TradeKit.Signature(kit.Kit.Engine.Outputs) + "|" + kit.Kit.Engine.Execution.FamilyConsecutiveLosses("X");
        }

        var first = Run();
        Assert.Equal(first, Run());
        Assert.EndsWith("|3", first);                                    // three simultaneous losing closes, counted once each
    }

    // ------------------------------------------------------------------ the shared capital

    [Fact]
    public void GlobalCapital_IsOnePool_SharedAcrossVariantsAndStrategies()
    {
        var kit = new PaperKit(
            PaperKit.Config(150_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(2, 2)))),
            PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        kit.Bar(10, 0);                                                  // 650 x 125 = 81,250 committed
        Assert.Equal(81_250m, kit.Kit.Engine.Execution.OpenExposure());
        kit.Bar(10, 5);                                                  // a second 81,250 would exceed the one 150,000 pool
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Equal("GlobalCapital", Assert.Single(kit.Rejections(X2)).Reasons);
        kit.Bar(10, 10);                                                 // another strategy draws on the SAME pool
        Assert.Empty(kit.Kit.Engine.Execution.PositionsOf(Y1));
        Assert.Equal("GlobalCapital", Assert.Single(kit.Rejections(Y1)).Reasons);
    }

    [Fact]
    public void GlobalCapital_IsInclusive_AtExactlyTheLimit()
    {
        var kit = new PaperKit(PaperKit.Config(162_500m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")), PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1)))));

        kit.Bar(10, 0);

        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Equal(162_500m, kit.Kit.Engine.Execution.OpenExposure());
    }

    [Fact]
    public void TwoEntriesDecidedOnTheSameBar_CannotBothPassTheCapital()
    {
        var kit = new PaperKit(PaperKit.Config(100_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1)))), PaperKit.Family("Y", ("Y1", S(W(1, 1)))));

        kit.Bar(10, 0);

        Assert.Equal(1, kit.Kit.Engine.Execution.GlobalTrades());
        var blocked = new[] { X1, Y1 }.Single(k => kit.Kit.Engine.Execution.PositionsOf(k).Count == 0);
        Assert.Equal("GlobalCapital", Assert.Single(kit.Rejections(blocked)).Reasons);
    }

    [Fact]
    public void ATp1PartialExit_ReleasesTheCorrespondingCommittedExposure()
    {
        var exit = new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m };
        var kit = new PaperKit(PaperKit.Config(150_000m, PaperKit.Spec("X", "X1", exit), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1)))), PaperKit.Family("Y", ("Y1", S(W(3, 3)))));

        var t = kit.Bar(10, 0);
        Assert.Equal(81_250m, kit.Kit.Engine.Execution.OpenExposure());
        kit.Move(t.AddSeconds(10), ceBid: 140m);                         // TP1 sells half
        Assert.Equal(40_625m, kit.Kit.Engine.Execution.OpenExposure());   // 325 x 125 still committed (premium cost basis; costs are separate)

        kit.Bar(10, 5);
        kit.Bar(10, 10);                                                 // 40,625 + 81,250 = 121,875 <= 150,000
        Assert.Single(kit.Kit.Engine.Execution.PositionsOf(Y1));
    }

    // ------------------------------------------------------------------ kill switch scopes

    [Fact]
    public void KillSwitch_VariantFamilyAndGlobal_HaveTheirOwnScopes_AndTypedReasons()
    {
        PaperKit NewKit() => new(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1)))), PaperKit.Family("Y", ("Y1", S(W(1, 1)))));

        var variant = NewKit();                                          // kill X/X1 only
        variant.Bar(10, 0, first: PaperKit.Kill(Clock.Ist(9, 59), 1, "X/X1"));
        Assert.Empty(variant.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Single(variant.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Single(variant.Kit.Engine.Execution.PositionsOf(Y1));
        Assert.Equal("KillSwitchVariant", Assert.Single(variant.Rejections(X1)).Reasons);

        var family = NewKit();                                           // kill family X: X1 and X2, not Y
        family.Bar(10, 0, first: PaperKit.Kill(Clock.Ist(9, 59), 1, "X"));
        Assert.Empty(family.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Empty(family.Kit.Engine.Execution.PositionsOf(X2));
        Assert.Single(family.Kit.Engine.Execution.PositionsOf(Y1));
        Assert.Equal("KillSwitchFamily", Assert.Single(family.Rejections(X1)).Reasons);

        var global = NewKit();                                           // kill everything
        global.Bar(10, 0, first: PaperKit.Kill(Clock.Ist(9, 59), 1, "*"));
        Assert.Equal(0, global.Kit.Engine.Execution.GlobalTrades());
        Assert.Equal("KillSwitchGlobal", Assert.Single(global.Rejections(Y1)).Reasons);

        var both = NewKit();                                             // variant AND family kill: both reasons, in stable order
        both.Feed(PaperKit.Kill(Clock.Ist(9, 58), 1, "X"));
        both.Feed(PaperKit.Kill(Clock.Ist(9, 59), 2, "X/X1"));
        both.Bar(10, 0);
        Assert.Empty(both.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Equal("KillSwitchFamily,KillSwitchVariant", Assert.Single(both.Rejections(X1)).Reasons);
    }

    [Fact]
    public void KillingAFamily_WithLiquidation_ClosesThatFamilysPositionsOnly()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2"), PaperKit.Spec("Y", "Y1")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1)))), PaperKit.Family("Y", ("Y1", S(W(1, 1)))));

        var t = kit.Bar(10, 0);
        kit.Feed(PaperKit.Kill(t.AddSeconds(5), 1, "X", KillMode.BlockEntriesAndLiquidate));
        kit.Move(t.AddSeconds(5.5), ceBid: 124m);
        kit.AdvanceTo(t.AddSeconds(9));

        Assert.Equal(PositionState.Closed, kit.Kit.Position(X1).State);
        Assert.Equal(PositionState.Closed, kit.Kit.Position(X2).State);
        Assert.Equal(PositionState.Open, kit.Kit.Position(Y1).State);
        Assert.All(kit.Out<ExitOrderCreated>(X1), e => Assert.Equal(ExitReason.ManualKill, e.Reason));
        Assert.Empty(kit.Out<ExitOrderCreated>(Y1));
    }

    [Fact]
    public void KillReset_FollowsTheScopeHierarchy()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("Y", "Y1")), PaperKit.Family("X", ("X1", S(W(1, 1)))), PaperKit.Family("Y", ("Y1", S(W(1, 1)))));
        string Effect() => kit.Kit.Engine.Outputs.OfType<KillSwitchChanged>().Last().Effect;
        var t = Clock.Ist(9, 50);

        kit.Feed(PaperKit.Kill(t, 1, "*"));
        kit.Feed(PaperKit.Kill(t.AddSeconds(1), 2, "X"));
        kit.Feed(PaperKit.Reset(t.AddSeconds(2), 3, "X"));
        Assert.Equal("ResetIgnored:GlobalKillActive", Effect());
        kit.Feed(PaperKit.Reset(t.AddSeconds(3), 4, "*"));               // clears everything
        Assert.Equal("Reset", Effect());

        kit.Feed(PaperKit.Kill(t.AddSeconds(4), 5, "X"));
        kit.Feed(PaperKit.Kill(t.AddSeconds(5), 6, "X/X1"));
        kit.Feed(PaperKit.Reset(t.AddSeconds(6), 7, "X/X1"));
        Assert.Equal("ResetIgnored:FamilyKillActive", Effect());
        kit.Feed(PaperKit.Reset(t.AddSeconds(7), 8, "X"));
        Assert.Equal("Reset", Effect());
        Assert.True(kit.Kit.Engine.Integrity.IsKilled(X1));               // the variant kill is still there
        kit.Feed(PaperKit.Reset(t.AddSeconds(8), 9, "X/X1"));
        Assert.False(kit.Kit.Engine.Integrity.IsKilled(X1));

        kit.Feed(PaperKit.Kill(t.AddSeconds(9), 10, "Q"));
        Assert.Equal("Rejected:UnknownVariant", Effect());                // no such family
    }

    // ------------------------------------------------------------------ max hold

    [Fact]
    public void MaxHold_StartsAtTheEntryFill_IsNotResetByTp1_AndSellsTheRemainder()
    {
        var exit = new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m, MaxHold = TimeSpan.FromMinutes(5) };
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1", exit)), PaperKit.Family("X", ("X1", S(W(1, 1)))));

        var t = kit.Bar(10, 0);
        var position = kit.Kit.Position(X1);
        var fillAt = position.OpenedAt;
        Assert.Equal(fillAt.AddMinutes(5), position.MaxHoldDeadline);

        kit.Move(t.AddMinutes(2), ceBid: 140m);                          // TP1 fills: the max-hold clock does not move
        Assert.Equal(325, position.OpenQuantity);
        Assert.Equal(fillAt.AddMinutes(5), position.MaxHoldDeadline);

        kit.Move(t.AddMinutes(4), ceBid: 135m);                          // keeps a bid fresh
        kit.Move(fillAt.AddMinutes(5).AddSeconds(-0.5), ceBid: 130m);
        kit.AdvanceTo(fillAt.AddMinutes(5).AddSeconds(3));

        Assert.Equal(PositionState.Closed, position.State);
        var hold = Assert.Single(position.ExitOrders, o => o.Reason == ExitReason.MaxHold);
        Assert.Equal(325, hold.FilledQuantity);
        Assert.True(position.ClosedAt >= fillAt.AddMinutes(5));
    }

    [Fact]
    public void MaxHold_IsPerPosition_AndValidatedPositive()
    {
        Assert.NotEmpty(new ExitSpec { MaxHold = TimeSpan.Zero }.Validate("X1"));
        Assert.Empty(new ExitSpec { MaxHold = TimeSpan.FromSeconds(1) }.Validate("X1"));

        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1", new ExitSpec { MaxHold = TimeSpan.FromMinutes(1) }), PaperKit.Spec("X", "X2")),
            PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1)))));
        kit.Bar(10, 0);
        Assert.NotNull(kit.Kit.Position(X1).MaxHoldDeadline);
        Assert.Null(kit.Kit.Position(X2).MaxHoldDeadline);               // no max hold configured for X2
    }

    // ------------------------------------------------------------------ 15:10 liquidation

    [Fact]
    public void At1510_EveryRemainingPositionStartsMandatoryLiquidation_AndFillsOnTheExecutableBid()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")), PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(1, 1)))));

        kit.Bar(10, 0);
        kit.Move(Clock.Ist(15, 9, 59, 500), ceBid: 130m);                // the last bid before the liquidation starts
        kit.AdvanceTo(Clock.Ist(15, 10, 3));

        Assert.All(new[] { X1, X2 }, k =>
        {
            var p = kit.Kit.Position(k);
            Assert.Equal(PositionState.Closed, p.State);
            var order = Assert.Single(p.ExitOrders);
            Assert.Equal(ExitReason.SessionLiquidation, order.Reason);
            Assert.Equal(Clock.Ist(15, 10, 0), order.CreatedAt);
            Assert.Equal(130m, kit.Out<FillRecord>(k).Last(f => f.Side == TradeSide.Sell).Price);
        });
        Assert.Empty(kit.Out<MandatoryExitPending>(X1));
    }

    [Fact]
    public void WithoutAnExecutableBidAt1510_NothingIsFabricated_TheExitStaysPending_ThenFillsWhenABidReturns()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 1)))));
        kit.Bar(10, 0);                                                  // the last quote of the contract is from 10:00

        kit.AdvanceTo(Clock.Ist(15, 10, 30));

        var position = kit.Kit.Position(X1);
        Assert.Equal(PositionState.Open, position.State);
        Assert.True(position.ExitPending);
        Assert.Equal(650, position.OpenQuantity);
        Assert.DoesNotContain(kit.Out<FillRecord>(X1), f => f.Side == TradeSide.Sell);
        var pending = kit.Out<MandatoryExitPending>(X1).First();
        Assert.Equal(ExitReason.SessionLiquidation, pending.Reason);
        Assert.Equal("QuoteStale", pending.Failure);
        Assert.Equal(650, pending.RemainingQuantity);

        kit.Move(Clock.Ist(15, 12, 0), ceBid: 128m);                     // an executable bid appears: the retrying order fills at that bid
        Assert.Equal(PositionState.Closed, position.State);
        Assert.Equal(128m, kit.Out<FillRecord>(X1).Last(f => f.Side == TradeSide.Sell).Price);
    }

    [Fact]
    public void WithNoBidAtAll_ThePositionIsReportedUnresolvedAtTheHardCloseAndNoFillIsInvented()
    {
        var kit = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 1)))));
        kit.Bar(10, 0);

        kit.AdvanceTo(Clock.Ist(15, 36, 0));

        var position = kit.Kit.Position(X1);
        Assert.Equal(PositionState.Unresolved, position.State);
        Assert.Equal(650, position.OpenQuantity);
        Assert.DoesNotContain(kit.Out<FillRecord>(X1), f => f.Side == TradeSide.Sell);
        Assert.Single(kit.Out<UnresolvedAtSessionEnd>(X1));
        Assert.NotEmpty(kit.Out<MandatoryExitPending>(X1));
    }

    // ------------------------------------------------------------------ typed rejection audit

    [Fact]
    public void ARejection_CarriesIdentity_Source_DecisionId_AndEveryReasonInStableOrder()
    {
        var risk = RiskProfile.PaperLiveV1(100_000m, families: new Dictionary<string, ScopeLimits> { ["X"] = new() { MaxTradesPerDay = 1 } });
        var kit = new PaperKit(PaperKit.Config(risk, PaperKit.Spec("X", "X1"), PaperKit.Spec("X", "X2")), PaperKit.Family("X", ("X1", S(W(1, 1))), ("X2", S(W(2, 2)))));

        kit.Bar(10, 0);                                                  // X1 fills (81,250 of 100,000)
        kit.Bar(10, 5);                                                  // X2: the family trade limit AND the capital both hold

        var rejection = Assert.Single(kit.Rejections(X2));
        Assert.Equal("FamilyTradeLimit,GlobalCapital", rejection.Reasons);          // ascending enum order, deterministic
        Assert.Equal("X", rejection.StrategyId);
        Assert.Equal("X2", rejection.VariantId);
        Assert.Equal("session:2026-09-22", rejection.SourceSessionId);
        Assert.Equal(2, rejection.BarSeq);
        Assert.StartsWith("dec_", rejection.DecisionId);
        Assert.Equal("Gate", rejection.Stage);
        Assert.NotNull(rejection.OpportunityId);
    }

    [Fact]
    public void ARejection_IsRecordedOncePerDistinctReasonSet_AndRunsAreIdentical()
    {
        static string Run()
        {
            var kit = new PaperKit(PaperKit.Config(100m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 4)))));
            kit.Bar(10, 0);
            kit.Move(Clock.Ist(10, 1), ceBid: 124m);
            kit.Move(Clock.Ist(10, 2), ceBid: 124m);
            kit.Bar(10, 3);
            return TradeKit.Signature(kit.Kit.Engine.Outputs);
        }

        var kit1 = new PaperKit(PaperKit.Config(100m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 4)))));
        kit1.Bar(10, 0);
        kit1.Move(Clock.Ist(10, 1), ceBid: 124m);
        kit1.Move(Clock.Ist(10, 2), ceBid: 124m);

        Assert.Single(kit1.Rejections(X1));                               // the same reason set is not repeated on every quote
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void TheRemainingTypedReasons_AreReported()
    {
        // VariantCapacity, then PremiumOutsideBand and NoExecutableAsk for the waiting opportunity: X1 owns its slot and waits for it to free
        var wait = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1", capacity: CapacityBlockedPolicy.Wait)), PaperKit.Family("X", ("X1", S(W(1, 1), W(3, 3)))));
        wait.Bar(10, 0);
        wait.Bar(10, 5);
        var t = wait.Bar(10, 10);                                        // a new episode while the first position is open: the capacity policy says wait
        wait.Move(t.AddSeconds(3), ceBid: 159m);                         // the contract ask (160) leaves the 100-150 band
        wait.Feed(wait.Kit.Q(t.AddSeconds(6), 124m, null, token: PaperKit.Ce));   // and later it is quoted one-sided: no executable ask
        Assert.Equal(new[] { "VariantCapacity", "PremiumOutsideBand", "NoExecutableAsk" }, wait.Rejections(X1).Select(r => r.Reasons).ToArray());

        // NoEligibleContract: nothing is quoted inside the band
        var none = new PaperKit(PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 2)))));
        none.Bar(10, 0, ceBid: 199m, ceAsk: 200m, peBid: 199m, peAsk: 200m);
        none.Bar(10, 1, ceBid: 199m, ceAsk: 200m, peBid: 199m, peAsk: 200m);
        Assert.Equal("NoEligibleContract", Assert.Single(none.Rejections(X1)).Reasons);   // once, however often it is re-evaluated
        Assert.Empty(none.Kit.Engine.Execution.PositionsOf(X1));
    }

    // ------------------------------------------------------------------ slippage

    [Fact]
    public void Slippage_EntryPaysAskPlusSlippage_ExitReceivesBidMinusSlippage_AndTheBandAppliesToTheExecutablePremium()
    {
        var config = PaperKit.Config(10_000_000m, PaperKit.Spec("X", "X1")) with { Execution = new ExecutionConfig { EntrySlippage = 0.5m, ExitSlippage = 0.5m } };
        var kit = new PaperKit(config, PaperKit.Family("X", ("X1", S(W(1, 1)))));

        var t = kit.Bar(10, 0);
        var position = kit.Kit.Position(X1);
        Assert.Equal(125.5m, position.AvgEntryPrice);                    // ask 125 + 0.5
        Assert.Equal(125.5m, kit.Out<FillRecord>(X1).Single().Price);

        kit.Kit.Engine.Execution.RequestStrategyExit(X1, position.Id, null, kit.Kit.Loop);
        kit.Move(t.AddSeconds(5), ceBid: 130m);
        Assert.Equal(129.5m, kit.Out<FillRecord>(X1).Last().Price);      // bid 130 - 0.5

        // the executable premium (ask + slippage) must be inside [100, 150]: 149.5 + 0.5 = 150 fills, 149.75 + 0.5 = 150.25 does not
        var edge = new PaperKit(config, PaperKit.Family("X", ("X1", S(W(1, 1)))));
        edge.Bar(10, 0, ceBid: 148m, ceAsk: 149.5m);
        Assert.Single(edge.Kit.Engine.Execution.PositionsOf(X1));
        Assert.Equal(150m, edge.Kit.Position(X1).AvgEntryPrice);
        var over = new PaperKit(config, PaperKit.Family("X", ("X1", S(W(1, 1)))));
        over.Bar(10, 0, ceBid: 148m, ceAsk: 149.75m);
        Assert.Empty(over.Kit.Engine.Execution.PositionsOf(X1));

        Assert.NotEmpty((new ExecutionConfig { EntrySlippage = -0.1m }).Validate());
        Assert.NotEmpty((new ExecutionConfig { ExitSlippage = -0.1m }).Validate());
    }

    // ------------------------------------------------------------------ configuration and safety

    [Fact]
    public void PaperLiveV1_NeedsCapital_FixedSessionRules_AndScopedLimitsOnlyLiveThere()
    {
        var variants = new[] { PaperKit.Spec("X", "X1") };
        Assert.Empty(PaperKit.Config(1_000_000m, variants).Validate());

        Assert.NotEmpty((PaperKit.Config(1_000_000m, variants) with { Risk = new RiskProfile { Name = RiskProfile.PaperLiveV1Name } }).Validate());          // no capital
        Assert.NotEmpty((PaperKit.Config(0m, variants)).Validate());
        Assert.NotEmpty((PaperKit.Config(1_000_000m, variants) with { Session = new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) } }).Validate());    // session rules are fixed
        Assert.NotEmpty(PaperKit.Config(RiskProfile.PaperLiveV1(1_000m, families: new Dictionary<string, ScopeLimits> { ["Nope"] = new() { MaxTradesPerDay = 1 } }), variants).Validate());
        Assert.NotEmpty(PaperKit.Config(RiskProfile.PaperLiveV1(1_000m, global: new ScopeLimits { MaxTradesPerDay = 0 }), variants).Validate());

        var researchWithLimits = TradeKit.MakeConfig(variants: PaperKit.Spec("X", "X1", limits: new ScopeLimits { MaxTradesPerDay = 1 }));
        Assert.NotEmpty(researchWithLimits.Validate());                                     // ResearchV1 enforces nothing
        Assert.NotEmpty((TradeKit.MakeConfig(variants: PaperKit.Spec("X", "X1")) with { Risk = RiskProfile.ResearchV1() with { GlobalCapital = 5m } }).Validate());
    }

    [Fact]
    public void ResearchV1_StillEnforcesNothing_AndRealOrderAndBrokerExecutionStayUnstartable()
    {
        var research = new PaperKit(TradeKit.MakeConfig(variants: PaperKit.Spec("X", "X1")), PaperKit.Family("X", ("X1", S(W(1, 1)))));
        research.Bar(10, 0);
        Assert.Single(research.Kit.Engine.Execution.PositionsOf(X1));

        var real = PaperKit.Config(1_000_000m, PaperKit.Spec("X", "X1")) with { Risk = new RiskProfile { Name = RiskProfile.RealOrderName } };
        Assert.Throws<ArgumentException>(() => new TradeKit(real, new Dictionary<string, IStrategyFamily> { ["X"] = PaperKit.Family("X", ("X1", S(W(1, 1)))) }));
        Assert.Empty(RiskProfile.PaperLiveV1(1m).StartBlockers());
        Assert.NotEmpty(new RiskProfile { Name = RiskProfile.RealOrderName }.StartBlockers());

        var broker = PaperKit.Config(1_000_000m, PaperKit.Spec("X", "X1")) with { Execution = new ExecutionConfig { Adapter = ExecutionAdapter.Broker } };
        Assert.NotEmpty(broker.Validate());
        Assert.Throws<ArgumentException>(() => new TradeKit(broker, new Dictionary<string, IStrategyFamily> { ["X"] = PaperKit.Family("X", ("X1", S(W(1, 1)))) }));
    }
}
