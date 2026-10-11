using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class SizingTests
{
    // Expected values come from the independent Python recomputation (ledger.py), not from the engine.
    private static TradeKit WithSizing(SizingSpec sizing, decimal ask = 125m, IEnumerable<OptionContract>? universe = null)
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(sizing: sizing)), universe);
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, ask - 1m, ask));
        return kit;
    }

    private static IReadOnlyList<OptionContract> UniverseWithLot(int lot)
        => Kit.StandardUniverse().Select(c => c with { LotSize = lot }).ToList();

    [Fact]
    public void Default_IsFixed650_TenLotsOfSixtyFive()
    {
        var kit = WithSizing(new FixedQuantitySpec());

        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(650, intent.RequestedQuantity);
        Assert.Equal(10, intent.Lots);
        Assert.Equal(65, intent.LotSize);
    }

    [Fact]
    public void LotSize_IsTakenFromTheContractOfTheTradeDate_NotAGlobalConstant()
    {
        // 650 is not a whole number of 75-unit lots: the run fails validation with a clear reason instead of rounding.
        var error = Assert.Throws<ArgumentException>(() => WithSizing(new FixedQuantitySpec { Units = 650 }, universe: UniverseWithLot(75)));
        Assert.Contains("650", error.Message);
        Assert.Contains("75", error.Message);
        Assert.Contains("lot", error.Message);

        var kit = WithSizing(new FixedQuantitySpec { Units = 675 }, universe: UniverseWithLot(75));      // 9 lots of 75
        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(9, intent.Lots);
        Assert.Equal(75, intent.LotSize);
        Assert.Equal(675, intent.RequestedQuantity);
    }

    [Fact]
    public void MixedLotSizes_FailForEveryOffendingContract()
    {
        var universe = new[] { Kit.Contract(OptionSide.Call, 23900) with { LotSize = 75 }, Kit.Contract(OptionSide.Put, 23900) };

        var error = Assert.Throws<ArgumentException>(() => WithSizing(new FixedQuantitySpec { Units = 650 }, universe: universe));

        Assert.Contains("CE23900", error.Message);
        Assert.DoesNotContain("PE23900", error.Message);
    }

    [Fact]
    public void Sizer_NeverRoundsAFixedQuantity()
    {
        foreach (var units in new[] { 0, -65, 64, 66, 649, 651 })
        {
            var result = Sizer.Size(new FixedQuantitySpec { Units = units }, 65, 125m, null);
            Assert.Equal(SizingStatus.InvalidQuantity, result.Status);
            Assert.Equal(0, result.Quantity);
            Assert.Contains("not rounded", result.Detail);
        }

        Assert.Equal(SizingStatus.InvalidQuantity, Sizer.Size(new FixedQuantitySpec(), 0, 125m, null).Status);
        Assert.Equal(10, Sizer.Size(new FixedQuantitySpec(), 65, 125m, null).Lots);
    }

    [Fact]
    public void FixedQuantity_HasNoCapitalLimit()
    {
        var kit = WithSizing(new FixedQuantitySpec(), ask: 150m);          // 650 x 150 = Rs 97,500 of premium in one entry

        kit.Loop.AdvanceTo(kit.At(1.5));
        Assert.Equal(650, Assert.Single(kit.Fills()).Quantity);
    }

    [Theory]
    [InlineData(50000, true, null, 6)]       // 6 x 8,125 = 48,750 + 45.56 costs <= 50,000
    [InlineData(48760, true, null, 5)]       // 48,750 + 45.56 = 48,795.56 > 48,760: the costs push it down a lot
    [InlineData(48760, false, null, 6)]      // without costs 6 lots fit
    [InlineData(50000, true, 3, 3)]          // MaxLots caps
    public void PremiumBudget_RoundsDownToWholeLotsAtTheOrderLimit(int rupees, bool includeCosts, int? maxLots, int expectedLots)
    {
        var kit = WithSizing(new PremiumBudgetSpec { Rupees = rupees, IncludeEntryCosts = includeCosts, MaxLots = maxLots });

        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(expectedLots, intent.Lots);
        Assert.Equal(expectedLots * 65, intent.RequestedQuantity);
    }

    [Fact]
    public void PremiumBudget_UsesTheOrderLimit_NotALaterBetterFillPrice()
    {
        var kit = WithSizing(new PremiumBudgetSpec { Rupees = 50000 });                // limit 125 -> 6 lots
        kit.Feed(kit.Q(kit.At(0.5), 117m, 118m));
        kit.Loop.AdvanceTo(kit.At(1.2));

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(118m, fill.Price);
        Assert.Equal(390, fill.Quantity);                                              // still 6 lots: sized at 125, not re-sized at 118
    }

    [Fact]
    public void UnaffordableOneLot_IsSkipped_WithoutReservation_AndCanQualifyLater()
    {
        var kit = WithSizing(new PremiumBudgetSpec { Rupees = 8000 });                 // one lot at 125 = 8,125 + costs > 8,000

        Assert.Empty(kit.Out<EntryIntent>("A"));
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Blocked:InsufficientBudget");
        Assert.Equal(SlotState.Free, kit.Engine.Variants.GetVariant(Vk.S("A")).Slots[0].State);
        Assert.Equal(OpportunityState.Waiting, kit.Engine.Variants.GetVariant(Vk.S("A")).Active!.State);

        kit.Feed(kit.Q(kit.At(2), 119m, 120m));                                        // 7,800 + 27.11 <= 8,000

        var intent = Assert.Single(kit.Out<EntryIntent>("A"));
        Assert.Equal(1, intent.Lots);
    }

    [Fact]
    public void PremiumBudget_WithCostsNeedsACostSchedule()
    {
        Assert.Throws<InvalidOperationException>(() => Sizer.Size(new PremiumBudgetSpec { Rupees = 50000 }, 65, 125m, null));
        Assert.Equal(6, Sizer.Size(new PremiumBudgetSpec { Rupees = 50000, IncludeEntryCosts = false }, 65, 125m, null).Lots);
    }

    [Fact]
    public void SpecValidation()
    {
        Assert.NotEmpty((TradeKit.Immediate(sizing: new FixedQuantitySpec { Units = 0 })).Validate(64));
        Assert.NotEmpty((TradeKit.Immediate(sizing: new PremiumBudgetSpec { Rupees = 0m })).Validate(64));
        Assert.NotEmpty((TradeKit.Immediate(sizing: new PremiumBudgetSpec { Rupees = 1m, MaxLots = 0 })).Validate(64));
        Assert.Empty((TradeKit.Immediate()).Validate(64));
    }
}

public class ExitTests
{
    private static ExitSpec Spec(decimal? sl = null, decimal? tp1 = null, decimal? p = null, decimal? final = null, RemainderSlMode remainder = RemainderSlMode.Unchanged)
        => new() { TradeSlPct = sl, Tp1Pct = tp1, Tp1Proportion = p, FinalTpPct = final, RemainderSl = remainder };

    private static TradeKit Open(ExitSpec exit, int units = 650)
        => TradeKit.Opened(TradeKit.Immediate(sizing: new FixedQuantitySpec { Units = units }, exit: exit));

    private static void Settle(TradeKit kit, double seconds) => kit.Loop.AdvanceTo(kit.At(seconds));

    [Fact]
    public void PositionAndAverageEntry_ComeFromTheActualFill()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(), entryAsk: 125m);

        var position = kit.Position();
        Assert.Equal(650, position.OpenQuantity);
        Assert.Equal(125m, position.AvgEntryPrice);
        Assert.Equal(81250m, position.EntryCostBasis);
        Assert.Equal(60.20m, position.EntryCosts);
        Assert.Equal(-60.20m, position.NetRealised);                                   // all entry costs count at once
        Assert.Equal(-(81250m + 60.20m), kit.Engine.Execution.CashOf(Vk.S("A")));
        Assert.Equal("CE23900", position.Contract.Token);
    }

    // ------------------------------------------------------------------ TP1 rounding

    [Theory]
    [InlineData(650, "0.5", 325L)]       // 10 lots -> 5
    [InlineData(325, "0.5", 130L)]       // 5 lots (odd) -> floor(2.5) = 2
    [InlineData(195, "0.4", 65L)]        // 3 lots -> floor(1.2) = 1
    [InlineData(65, "0.5", 0L)]          // one lot -> 0: TP1 is skipped
    [InlineData(650, "0.99", 585L)]      // 10 lots -> 9 (floor 9.9), never the whole position
    public void Tp1Quantity_RoundsDownToWholeLots(int units, string proportion, long expected)
    {
        var kit = Open(Spec(tp1: 0.10m, p: decimal.Parse(proportion, System.Globalization.CultureInfo.InvariantCulture)), units);

        kit.Feed(kit.Q(kit.At(2), 138m, 139m));                                        // bid 138 >= 125 x 1.10 = 137.5
        Settle(kit, 3.5);

        var resolved = Assert.Single(kit.Out<Tp1Resolved>("A"));
        if (expected == 0)
        {
            Assert.Equal(Tp1State.Skipped, resolved.State);
            Assert.Empty(kit.Out<ExitOrderCreated>("A"));
            Assert.Equal(units, kit.Position().OpenQuantity);                         // not altered, not sold
            Assert.Single(kit.Fills());
        }
        else
        {
            Assert.Equal(expected, resolved.TargetQuantity);
            Assert.Equal(expected, kit.Position().Tp1TargetQuantity);
            var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
            Assert.Equal(expected, sell.Quantity);
            Assert.Equal(138m, sell.Price);
            Assert.Equal(ExitReason.Tp1, sell.Reason);
            Assert.Equal(units - expected, kit.Position().OpenQuantity);
            Assert.Equal(Tp1State.Done, kit.Position().Tp1);
        }
    }

    [Fact]
    public void Triggers_UseTheExecutableBid_NotTheAsk_AndTheLevelIsInclusive()
    {
        var kit = Open(Spec(tp1: 0.10m, p: 0.5m));

        kit.Feed(kit.Q(kit.At(2), 137m, 139m));                                        // ask is above the level, bid is not
        kit.Feed(kit.Q(kit.At(2.5), 140m, 139m));                                      // crossed quote: not executable, no trigger
        Assert.Empty(kit.Out<ExitOrderCreated>("A"));

        kit.Feed(kit.Q(kit.At(3), 137.5m, 138m));                                      // bid exactly at 125 x 1.10
        Assert.Single(kit.Out<ExitOrderCreated>("A"));
    }

    // ------------------------------------------------------------------ gap behaviour, priority, reservations

    [Fact]
    public void GapThroughTheStop_FillsAtTheAvailableBid_NotAtTheThreshold_AndTheBandDoesNotApplyToExits()
    {
        var kit = Open(Spec(sl: 0.10m));                                               // stop level 125 x 0.90 = 112.5

        kit.Feed(kit.Q(kit.At(2), 95m, 96m));                                          // gaps far below the stop, and below the Rs 100 entry band
        Settle(kit, 3.5);

        var created = Assert.Single(kit.Out<ExitOrderCreated>("A"));
        Assert.Equal(ExitReason.TradeSl, created.Reason);
        Assert.Equal(112.5m, created.TriggerThreshold);
        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(95m, sell.Price);
        Assert.Equal(112.5m, sell.TriggerThreshold);
        Assert.Equal(PositionState.Closed, kit.Position().State);
        Assert.True(kit.Position().NetRealised < 0m);
    }

    [Fact]
    public void FinalTarget_And_Tp1_QualifyingTogether_TheHigherPriorityWins_AsOneOrder()
    {
        var kit = Open(Spec(tp1: 0.10m, p: 0.5m, final: 0.20m));

        kit.Feed(kit.Q(kit.At(2), 151m, 152m));                                        // both levels are passed

        var created = Assert.Single(kit.Out<ExitOrderCreated>("A"));
        Assert.Equal(ExitReason.FinalTarget, created.Reason);
        Assert.Equal(650, created.Quantity);
        Assert.Equal(Tp1State.NotTriggered, kit.Position().Tp1);
    }

    [Fact]
    public void PendingExitQuantity_IsReserved_SoTp1AndStopCannotOversell()
    {
        var kit = Open(Spec(sl: 0.10m, tp1: 0.10m, p: 0.5m));

        kit.Feed(kit.Q(kit.At(2), 138m, 139m));                                        // TP1: 325 reserved
        Assert.Equal(325, kit.Position().ReservedExitQuantity);
        kit.Feed(kit.Q(kit.At(2.5), 100m, 101m));                                      // stop while TP1 is still pending: only the unreserved 325
        Assert.Equal(650, kit.Position().ReservedExitQuantity);
        Assert.True(kit.Position().ReservedExitQuantity <= kit.Position().OpenQuantity);

        var created = kit.Out<ExitOrderCreated>("A").ToList();
        Assert.Equal(new[] { ExitReason.Tp1, ExitReason.TradeSl }, created.Select(c => c.Reason).ToArray());
        Assert.Equal(new long[] { 325, 325 }, created.Select(c => c.Quantity).ToArray());

        kit.Feed(kit.Q(kit.At(2.7), 100m, 101m));                                      // another trigger finds nothing unreserved
        Assert.Equal(2, kit.Out<ExitOrderCreated>("A").Count());

        Settle(kit, 5);
        Assert.Equal(650, kit.Fills().Where(f => f.Side == TradeSide.Sell).Sum(f => f.Quantity));
        Assert.Equal(0, kit.Position().OpenQuantity);
        Assert.Equal(0, kit.Position().ReservedExitQuantity);
        Assert.Equal(PositionState.Closed, kit.Position().State);
    }

    [Fact]
    public void PartialTp1_ConsumesLiquidityOnce_RetriesIdempotently_AndNeverCreatesASecondTp1()
    {
        string Run(out TradeKit kitOut)
        {
            var k = Open(Spec(tp1: 0.10m, p: 0.5m));                                   // TP1 target 325
            kitOut = k;
            k.Feed(k.Q(k.At(2), 138m, 139m, bidQty: 130));                             // trigger; only 130 (2 lots) displayed
            k.Feed(k.Q(k.At(2.5), 138m, 139m, bidQty: 130));                           // trigger again: TP1 is already Triggered
            Settle(k, 3.5);                                                            // attempt at 3 s: fills 130
            Assert.Equal(130, k.Position().Tp1FilledQuantity);
            Assert.Equal(Tp1State.Triggered, k.Position().Tp1);
            k.Feed(k.Q(k.At(3.7), 138m, 139m, bidQty: 130));                           // the same displayed state again
            Settle(k, 4.5);                                                            // retry at 4 s: nothing left of the SAME liquidity
            Assert.Equal(130, k.Position().Tp1FilledQuantity);
            k.Feed(k.Q(k.At(4.7), 138m, 139m, bidQty: 260));                           // a changed displayed size is new liquidity
            Settle(k, 5.5);                                                            // retry at 5 s: the remaining 195
            return TradeKit.Signature(k.Engine.Outputs);
        }

        var first = Run(out var kit);
        Assert.Equal(325, kit.Position().Tp1FilledQuantity);
        Assert.Equal(Tp1State.Done, kit.Position().Tp1);
        Assert.Equal(325, kit.Position().OpenQuantity);
        Assert.Single(kit.Out<ExitOrderCreated>("A"), c => c.Reason == ExitReason.Tp1);
        Assert.Equal(new long[] { 130, 195 }, kit.Fills().Where(f => f.Side == TradeSide.Sell).Select(f => f.Quantity).ToArray());

        // Reconstructing the in-memory state from the same events yields exactly the same orders, ids and fills.
        var second = Run(out var rebuilt);
        Assert.Equal(first, second);
        Assert.Equal(kit.Out<ExitOrderCreated>("A").Single().OrderId, rebuilt.Out<ExitOrderCreated>("A").Single().OrderId);
    }

    [Theory]
    [InlineData(RemainderSlMode.Unchanged, false)]
    [InlineData(RemainderSlMode.MoveToBreakeven, true)]
    public void RemainderStop_StaysUnchangedByDefault_AndMovesToBreakevenOnlyWhenConfigured(RemainderSlMode mode, bool expectStop)
    {
        var kit = Open(Spec(sl: 0.10m, tp1: 0.10m, p: 0.5m, remainder: mode));
        kit.Feed(kit.Q(kit.At(2), 138m, 139m));
        Settle(kit, 3.5);
        Assert.Equal(Tp1State.Done, kit.Position().Tp1);

        kit.Feed(kit.Q(kit.At(5), 120m, 121m));                                        // below average entry 125, above the original stop 112.5

        Assert.Equal(expectStop, kit.Out<ExitOrderCreated>("A").Any(c => c.Reason == ExitReason.TradeSl));
    }

    [Fact]
    public void StrategyExit_UsesTheSameExecutionPath_WholeLotsOnly_AndCannotOversell()
    {
        var kit = Open(new ExitSpec());
        var id = kit.Position().Id;

        Assert.Throws<ArgumentException>(() => kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), id, 100, kit.Loop));      // not whole lots
        Assert.Throws<ArgumentException>(() => kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), id, 715, kit.Loop));      // more than open
        kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), id, 130, kit.Loop);
        Assert.Throws<ArgumentException>(() => kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), id, 650, kit.Loop));      // only 520 unreserved
        kit.Feed(kit.Q(kit.At(2), 120m, 121m));
        Settle(kit, 4);

        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(130, sell.Quantity);
        Assert.Equal(ExitReason.StrategyExit, sell.Reason);
        Assert.Equal(520, kit.Position().OpenQuantity);
    }

    [Fact]
    public void ExitSpecValidation()
    {
        Assert.Empty(new ExitSpec().Validate("A"));
        Assert.NotEmpty(Spec(sl: 0m).Validate("A"));
        Assert.NotEmpty(Spec(sl: 1m).Validate("A"));
        Assert.NotEmpty(Spec(tp1: 0.1m).Validate("A"));                                // TP1 needs its proportion
        Assert.NotEmpty(Spec(tp1: 0.1m, p: 1m).Validate("A"));
        Assert.NotEmpty(Spec(final: -0.1m).Validate("A"));
        Assert.Empty(Spec(sl: 0.1m, tp1: 0.1m, p: 0.5m, final: 0.2m).Validate("A"));
    }
}
