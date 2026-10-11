using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class SessionLiquidationTests
{
    private static DateTimeOffset At(int h, int m, int s = 0, int ms = 0) => Clock.Ist(h, m, s, ms);

    [Fact]
    public void QuietMarketToTheEnd_ReportsUnresolvedPositions_AndNeverManufacturesAFill()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        var cashAfterEntry = kit.Engine.Execution.CashOf(Vk.S("A"));

        kit.Loop.AdvanceTo(At(15, 5, 30));
        var created = Assert.Single(kit.Out<ExitOrderCreated>("A"));
        Assert.Equal(ExitReason.SessionLiquidation, created.Reason);
        Assert.Equal(At(15, 5), created.At);
        Assert.Equal(At(15, 5, 1), created.FirstAttemptAt);
        Assert.Equal(650, created.Quantity);
        Assert.Single(kit.Out<AuditRecord>("A"), r => r.Outcome == "Retry:QuoteStale");     // the 10:00 quote is hours old; one note, not one per retry
        Assert.Single(kit.Fills());

        kit.Loop.AdvanceTo(At(15, 10, 1));
        var atClose = Assert.Single(kit.Out<UnresolvedAtClose>("A"));
        Assert.Equal(At(15, 10), atClose.At);
        Assert.Equal(650, atClose.OpenQuantity);
        Assert.Equal(1, atClose.LiveExitOrders);
        Assert.True(atClose.LastBidAge > TimeSpan.FromHours(5));                             // the last bid is reported WITH its age, not used as a price

        kit.Loop.AdvanceTo(At(15, 35, 1));
        var atEnd = Assert.Single(kit.Out<UnresolvedAtSessionEnd>("A"));
        Assert.Equal(At(15, 35), atEnd.At);
        Assert.Equal(650, atEnd.OpenQuantity);
        Assert.Equal(PositionState.Unresolved, kit.Position().State);
        Assert.Equal(650, kit.Position().OpenQuantity);
        Assert.Single(kit.Fills());                                                           // still only the entry
        Assert.Equal(cashAfterEntry, kit.Engine.Execution.CashOf(Vk.S("A")));
        Assert.Empty(kit.Out<PositionClosed>("A"));
        var report = Assert.Single(kit.Out<SessionEndReport>("A"));
        Assert.Equal(1, report.UnresolvedPositions);
        Assert.True(kit.Engine.Execution.Ended);
        Assert.All(kit.Position().ExitOrders, o => Assert.Equal(OrderStatus.Cancelled, o.Status));
        Assert.DoesNotContain(kit.Loop.PendingDeadlines, d => d.Key.StartsWith("exec-", StringComparison.Ordinal));
    }

    [Fact]
    public void LiquidationBeforeTheFinalClose_ClosesThePositionWithRealFills_NoUnresolvedReport()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        kit.Loop.AdvanceTo(At(15, 5, 30));

        kit.Feed(kit.Q(At(15, 7), 120m, 121m));
        kit.Loop.AdvanceTo(At(15, 11));

        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(At(15, 7, 1), sell.At);                                                  // first attempt after the quote existed
        Assert.Equal(120m, sell.Price);
        Assert.Equal(ExitReason.SessionLiquidation, sell.Reason);
        Assert.False(sell.AfterFinalClose);
        Assert.Equal(PositionState.Closed, kit.Position().State);
        Assert.Empty(kit.Out<UnresolvedAtClose>("A"));
        Assert.Equal(SlotState.Free, kit.Engine.Variants.GetVariant(Vk.S("A")).Slots[0].State);
    }

    [Fact]
    public void LateLiquidationFill_AfterTheFinalClose_IsRealButFlagged_AndCountedInTheSessionReport()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        kit.Loop.AdvanceTo(At(15, 10, 30));
        Assert.Single(kit.Out<UnresolvedAtClose>("A"));

        kit.Feed(kit.Q(At(15, 12), 119m, 120m));
        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);

        var sell = Assert.Single(kit.Fills(), f => f.Side == TradeSide.Sell);
        Assert.Equal(At(15, 12, 1), sell.At);
        Assert.True(sell.AfterFinalClose);
        Assert.Equal(PositionState.Closed, kit.Position().State);
        var report = Assert.Single(kit.Out<SessionEndReport>("A"));
        Assert.Equal(1, report.FillsAfterFinalClose);
        Assert.Equal(0, report.UnresolvedPositions);
        Assert.Empty(kit.Out<UnresolvedAtSessionEnd>("A"));
    }

    [Fact]
    public void NothingFillsAtOrAfterTheSessionEnd_EvenWithAFreshQuoteJustBefore()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        kit.Loop.AdvanceTo(At(15, 34, 50));
        kit.Feed(kit.Q(At(15, 34, 59, 500), 120m, 121m));                                     // the retry at 15:35:00 sits after the SessionEnd boundary in deadline order

        kit.Loop.AdvanceTo(At(15, 36));

        Assert.Single(kit.Fills());
        Assert.Equal(PositionState.Unresolved, kit.Position().State);
    }

    [Fact]
    public void LiquidationAddsOnlyTheUnreservedQuantity_NeverOverselling()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m }));
        kit.Loop.AdvanceTo(At(15, 4, 59));
        kit.Feed(kit.Q(At(15, 4, 59, 500), 138m, 139m));                                      // TP1 (325) pending when the liquidation starts

        kit.Loop.AdvanceTo(At(15, 5, 30));

        var created = kit.Out<ExitOrderCreated>("A").ToList();
        Assert.Equal(new[] { ExitReason.Tp1, ExitReason.SessionLiquidation }, created.Select(c => c.Reason).ToArray());
        Assert.Equal(new long[] { 325, 325 }, created.Select(c => c.Quantity).ToArray());
        Assert.Equal(650, kit.Fills().Where(f => f.Side == TradeSide.Sell).Sum(f => f.Quantity));
        Assert.Equal(PositionState.Closed, kit.Position().State);
    }

    // ------------------------------------------------------------------ a runnable engine needs explicit policy

    [Fact]
    public void RunnableEngine_RequiresAnExplicitLiquidationStart_AtOrAfterTheEntryClose()
    {
        var none = TradeKit.MakeConfig(session: new SessionConfig(), variants: TradeKit.Immediate());
        Assert.Contains("liquidation start", Assert.Throws<ArgumentException>(() => new TradeKit(none)).Message);

        var early = TradeKit.MakeConfig(session: new SessionConfig { LiquidationStart = new TimeOnly(14, 59, 0) }, variants: TradeKit.Immediate());
        Assert.Throws<ArgumentException>(() => new TradeKit(early));

        var atClose = TradeKit.MakeConfig(session: new SessionConfig { LiquidationStart = new TimeOnly(15, 0, 0) }, variants: TradeKit.Immediate());
        Assert.NotNull(new TradeKit(atClose));
        Assert.Null(new SessionConfig().LiquidationStart);                                    // 15:05 is not silently assumed anywhere
    }

    [Fact]
    public void RunnableEngine_RequiresACostScheduleCoveringTheTradeDate()
    {
        var empty = TradeKit.MakeConfig(costs: new CostsConfig(), variants: TradeKit.Immediate());
        Assert.Contains("cost schedule", Assert.Throws<ArgumentException>(() => new TradeKit(empty)).Message);

        var later = new CostsConfig
        {
            Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }) with { EffectiveFrom = new DateOnly(2026, 10, 1) } },
        };
        Assert.Throws<ArgumentException>(() => new TradeKit(TradeKit.MakeConfig(costs: later, variants: TradeKit.Immediate())));
    }
}

public class CostAndLedgerTests
{
    // ----- values below come from the independent Python recomputation (ledger.py) of the documented formulas -----
    private static readonly (decimal Brokerage, decimal Stt, decimal Exchange, decimal Sebi, decimal Stamp, decimal Gst, decimal Total) BuyCosts = (20.00m, 0.00m, 28.87m, 0.08m, 2.44m, 8.81m, 60.20m);
    private static readonly (decimal Brokerage, decimal Stt, decimal Exchange, decimal Sebi, decimal Stamp, decimal Gst, decimal Total) Tp1Costs = (20.00m, 67.28m, 15.94m, 0.04m, 0.00m, 6.48m, 109.74m);
    private static readonly (decimal Brokerage, decimal Stt, decimal Exchange, decimal Sebi, decimal Stamp, decimal Gst, decimal Total) FinalCosts = (20.00m, 73.61m, 17.44m, 0.05m, 0.00m, 6.75m, 117.85m);

    private static TradeKit GoldenKit()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m, FinalTpPct = 0.20m })));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                                // entry: 650 @ 125.00
        kit.Loop.AdvanceTo(kit.At(1.5));
        kit.Feed(kit.Q(kit.At(120), 138m, 139m));                                               // TP1: 325 @ 138.00
        kit.Feed(kit.Q(kit.At(180), 151m, 152m));                                               // gap through the final target: 325 @ 151.00
        kit.Loop.AdvanceTo(kit.At(200));
        return kit;
    }

    private static void AssertCosts((decimal Brokerage, decimal Stt, decimal Exchange, decimal Sebi, decimal Stamp, decimal Gst, decimal Total) expected, CostBreakdown actual)
    {
        Assert.Equal(expected.Brokerage, actual.Brokerage);
        Assert.Equal(expected.Stt, actual.Stt);
        Assert.Equal(expected.Exchange, actual.ExchangeCharge);
        Assert.Equal(expected.Sebi, actual.SebiFee);
        Assert.Equal(expected.Stamp, actual.StampDuty);
        Assert.Equal(expected.Gst, actual.Gst);
        Assert.Equal(expected.Total, actual.Total);
    }

    [Fact]
    public void GoldenTradeLedger_EntryTp1FinalExit_MatchesTheIndependentComputation()
    {
        var kit = GoldenKit();

        var fills = kit.Fills("A").ToList();
        Assert.Equal(3, fills.Count);

        Assert.Equal((TradeSide.Buy, 650L, 125.00m, 81250.00m), (fills[0].Side, fills[0].Quantity, fills[0].Price, fills[0].Notional));
        Assert.Equal(kit.At(1), fills[0].At);
        AssertCosts(BuyCosts, fills[0].Costs);

        Assert.Equal((TradeSide.Sell, 325L, 138.00m, 44850.00m, ExitReason.Tp1), (fills[1].Side, fills[1].Quantity, fills[1].Price, fills[1].Notional, fills[1].Reason!.Value));
        Assert.Equal(kit.At(121), fills[1].At);
        AssertCosts(Tp1Costs, fills[1].Costs);

        Assert.Equal((TradeSide.Sell, 325L, 151.00m, 49075.00m, ExitReason.FinalTarget), (fills[2].Side, fills[2].Quantity, fills[2].Price, fills[2].Notional, fills[2].Reason!.Value));
        Assert.Equal(kit.At(181), fills[2].At);
        Assert.Equal(150m, fills[2].TriggerThreshold);                                           // gap: filled at 151, threshold was 150
        AssertCosts(FinalCosts, fills[2].Costs);

        var closed = Assert.Single(kit.Out<PositionClosed>("A"));
        Assert.Equal(12675.00m, closed.GrossPnl);                                                // (138-125) x 325 + (151-125) x 325
        Assert.Equal(287.79m, closed.TotalCosts);                                                // 60.20 + 109.74 + 117.85
        Assert.Equal(12387.21m, closed.NetPnl);
        Assert.Equal(12387.21m, kit.Engine.Execution.CashOf(Vk.S("A")));
    }

    [Fact]
    public void QuantityAndCashAreConserved_AcrossTheWholeLedger()
    {
        var kit = GoldenKit();
        var position = kit.Position();

        var bought = kit.Fills("A").Where(f => f.Side == TradeSide.Buy).Sum(f => f.Quantity);
        var sold = kit.Fills("A").Where(f => f.Side == TradeSide.Sell).Sum(f => f.Quantity);
        Assert.Equal(bought, sold + position.OpenQuantity);
        Assert.Equal(0, position.OpenQuantity);
        Assert.Equal(0, position.ReservedExitQuantity);

        var cashFlows = kit.Fills("A").Sum(f => f.Side == TradeSide.Buy ? -(f.Notional + f.Costs.Total) : f.Notional - f.Costs.Total);
        Assert.Equal(cashFlows, kit.Engine.Execution.CashOf(Vk.S("A")));
        Assert.Equal(kit.Engine.Execution.CashOf(Vk.S("A")), kit.Engine.Execution.PositionsOf(Vk.S("A")).Sum(p => p.NetRealised));
    }

    [Fact]
    public void PartialExitCosts_AreIncludedInRealisedNet_AtEachStep()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m }));
        kit.Feed(kit.Q(kit.At(2), 138m, 139m));
        kit.Loop.AdvanceTo(kit.At(3.5));

        var position = kit.Position();
        Assert.Equal(60.20m + 109.74m, position.TotalCosts);                                      // all entry costs + the partial-exit costs
        Assert.Equal((138m - 125m) * 325m, position.GrossRealised);
        Assert.Equal(4225.00m - 169.94m, position.NetRealised);
    }

    // ------------------------------------------------------------------ cost schedule mechanics

    [Fact]
    public void EffectiveDating_PicksTheScheduleByTradeDate_AndNeverFallsBackToZero()
    {
        var v1 = NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }) with { Version = "v1", EffectiveTo = new DateOnly(2026, 9, 21) };
        var v2 = NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }) with { Version = "v2", EffectiveFrom = new DateOnly(2026, 9, 22), SttSellRate = 0.002m };
        var config = new CostsConfig { Schedules = new[] { v2, v1 } };
        Assert.Empty(config.Validate());

        var before = new CostModel(config, new DateOnly(2026, 9, 21));
        var after = new CostModel(config, new DateOnly(2026, 9, 22));

        Assert.Equal("v1", before.ScheduleVersion);
        Assert.Equal("v2", after.ScheduleVersion);
        Assert.Equal(150.00m, before.Compute(TradeSide.Sell, 100000m).Stt);
        Assert.Equal(200.00m, after.Compute(TradeSide.Sell, 100000m).Stt);
        Assert.Throws<InvalidOperationException>(() => new CostModel(config, new DateOnly(2026, 3, 31)));
        Assert.False(CostModel.IsConfigured(config, new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void OverlappingSchedules_AreInvalid()
    {
        var a = NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }) with { Version = "a" };
        var b = a with { Version = "b", EffectiveFrom = new DateOnly(2026, 6, 1) };

        Assert.NotEmpty(new CostsConfig { Schedules = new[] { a, b } }.Validate());
    }

    [Fact]
    public void Rounding_IsPerComponentToPaise_HalfAwayFromZero_ThenSummed()
    {
        var model = new CostModel(TradeKit.TestCosts(), Clock.Day);

        var sell = model.Compute(TradeSide.Sell, 44850m);
        Assert.Equal(67.28m, sell.Stt);                                                          // 67.275 -> 67.28
        Assert.Equal(0.04m, sell.SebiFee);                                                       // 0.04485 -> 0.04
        Assert.Equal(67.31m, model.Compute(TradeSide.Sell, 44870m).Stt);                         // 67.305 is an exact tie: half-away gives 67.31, banker's rounding would give 67.30
        Assert.Equal(sell.Brokerage + sell.Stt + sell.ExchangeCharge + sell.SebiFee + sell.StampDuty + sell.Gst, sell.Total);
        Assert.Equal(0m, sell.StampDuty);                                                        // stamp duty is buy side only
        var buy = model.Compute(TradeSide.Buy, 81250m);
        Assert.Equal(0m, buy.Stt);                                                               // STT is sell side only
        Assert.Equal(2.44m, buy.StampDuty);                                                      // 2.4375 -> 2.44
        Assert.Equal(0.08m, buy.SebiFee);
        Assert.Equal(0.18m * (buy.Brokerage + buy.ExchangeCharge + buy.SebiFee), buy.Gst + 0.0000m, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Compute(TradeSide.Buy, -1m));
    }

    [Fact]
    public void Brokerage_IsConfigurable_AndPercentBrokerageSupportsACap()
    {
        var pct = new CostsConfig
        {
            Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new PercentOfTurnoverBrokerage { Rate = 0.0003m, Cap = 20m, Unit = BrokerageUnit.PerFill }) },
        };
        var model = new CostModel(pct, Clock.Day);

        Assert.Equal(20.00m, model.Compute(TradeSide.Buy, 81250m).Brokerage);                    // 24.375 capped at 20
        Assert.Equal(13.46m, model.Compute(TradeSide.Sell, 44850m).Brokerage);                   // 13.455 -> 13.46
        Assert.Equal(0m, new CostModel(TradeKit.TestCosts(0m), Clock.Day).Compute(TradeSide.Buy, 81250m).Brokerage);
    }

    [Fact]
    public void BrokerageIsNeverInvented_AndTheSourceStatusStaysProvisional()
    {
        var required = typeof(CostScheduleEntry).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length > 0)
            .Select(p => p.Name)
            .ToArray();
        Assert.Contains("Brokerage", required);
        Assert.Contains("SourceStatus", required);
        Assert.Contains("PROVISIONAL", NseIndexOptionsCostSchedules.StatusText);
        Assert.Contains("NSE/FATAX/73524", NseIndexOptionsCostSchedules.StatusText);
        Assert.Contains("NSE/FA/73061", NseIndexOptionsCostSchedules.StatusText);
        Assert.Contains("brokerage not supplied", NseIndexOptionsCostSchedules.StatusText);
        Assert.Empty(new CostsConfig().Schedules);                                               // no default schedule: costs are configured explicitly
    }

    // ------------------------------------------------------------------ valuation flags

    [Fact]
    public void UnrealisedValuation_UsesTheEligibleBid_AndFlagsStaleMissingAndNonExecutable()
    {
        var kit = TradeKit.Opened(TradeKit.Immediate());
        var position = kit.Position();
        var execution = kit.Engine.Execution;

        var marked = execution.Value(position, kit.At(1.5));
        Assert.Equal(ValuationStatus.Marked, marked.Status);
        Assert.Equal(124m, marked.Bid);
        Assert.Equal(124m * 650m, marked.GrossValue);
        Assert.Equal(marked.GrossValue - marked.EstimatedExitCosts, marked.LiquidationValue);

        Assert.Equal(ValuationStatus.Stale, execution.Value(position, kit.At(100)).Status);      // the 10:00 quote is far too old at 10:01:40
        Assert.Null(execution.Value(position, kit.At(100)).LiquidationValue);
        Assert.Equal(ValuationStatus.Missing, execution.Value(position, kit.At(-5)).Status);     // before any quote

        kit.Feed(kit.Q(kit.At(120), null, 125m));
        Assert.Equal(ValuationStatus.NotExecutable, execution.Value(position, kit.At(120)).Status);
    }

    // ------------------------------------------------------------------ ResearchV1: daily SL is record-only

    [Fact]
    public void DailySl_IsRecordedOnly_NoLockoutNoForcedExitNoTradeCap()
    {
        var script = new ScriptedStrategy("S", seq => new StrategyValidity(seq % 2 == 1, OptionSide.Call, "x"));
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate(dailySl: 10m)), null, script);

        for (var cycle = 0; cycle < 7; cycle++)
        {
            var t = kit.At(100 * cycle);
            kit.Bar(t, (2 * cycle) + 1, kit.Q(t, 124m, 125m));                                   // entry 650 @ 125
            kit.Loop.AdvanceTo(t.AddSeconds(1.5));
            kit.Feed(kit.Q(t.AddSeconds(2), 119m, 120m));
            kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), kit.Position("A", cycle).Id, null, kit.Loop);   // a loss-making discretionary exit
            kit.Loop.AdvanceTo(t.AddSeconds(5));
            kit.Feed(Ev.Bar(t.AddSeconds(10), (2 * cycle) + 2));                                 // the episode ends
        }

        Assert.Equal(7, kit.Out<PositionOpened>("A").Count());                                   // more than 5 trades: no cap
        Assert.Equal(7, kit.Out<PositionClosed>("A").Count());
        Assert.All(kit.Out<PositionClosed>("A"), c => Assert.True(c.NetPnl < 0m));
        var breach = Assert.Single(kit.Out<DailySlBreached>("A"));                               // recorded once, at the first breach
        Assert.True(breach.DailyPnl <= -10m);
        Assert.False(breach.ConservativeMark);
        Assert.DoesNotContain(kit.Out<ExitOrderCreated>("A"), c => c.Reason != ExitReason.StrategyExit);   // nothing was force-closed because of the breach
        Assert.Equal(7, kit.Out<EntryIntent>("A").Count());                                      // entries continued after the breach
    }

    // ------------------------------------------------------------------ determinism of the whole engine

    private static string Scenario(Random? heartbeats)
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: new[]
        {
            TradeKit.Immediate("A", exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m, FinalTpPct = 0.20m, TradeSlPct = 0.10m }),
            TradeKit.Immediate("B", sizing: new PremiumBudgetSpec { Rupees = 50000 }),
        }));
        void Beat(double from, double to)
        {
            if (heartbeats is null)
            {
                return;
            }

            for (var i = 0; i < 3; i++)
            {
                kit.Loop.AdvanceTo(kit.At(from + (heartbeats.NextDouble() * (to - from))));
            }

            kit.Loop.AdvanceTo(kit.At(to));
        }

        Beat(0, 0);
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m, askQty: 500));
        Beat(0, 120);
        kit.Feed(kit.Q(kit.At(120), 138m, 139m, bidQty: 200));
        Beat(120, 180);
        kit.Feed(kit.Q(kit.At(180), 151m, 152m, bidQty: 5000));
        Beat(180, 400);
        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);
        return TradeKit.Signature(kit.Engine.Outputs);
    }

    [Fact]
    public void WholeEngine_IsIdenticalAcrossRepeatsHeartbeatsParallelRunsAndCultures()
    {
        var baseline = Scenario(null);
        Assert.Contains("FillRecord", baseline);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(baseline, Scenario(null));
        }

        var rng = new Random(4242);
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(baseline, Scenario(rng));
        }

        var cultures = new[] { "tr-TR", "ar-SA", "de-DE", "th-TH", "fr-FR", "ja-JP", "hi-IN", "en-US" };
        var results = new string[cultures.Length * 2];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            results[i] = Culture.Under(cultures[i % cultures.Length], () => Scenario(null)));
        Assert.All(results, r => Assert.Equal(baseline, r));
    }
}
