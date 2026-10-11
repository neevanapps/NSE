using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>CP3 review items: (A) quote reuse policy, (B) brokerage charging units. Expected numbers come from brokerage.py (independent decimal recomputation).</summary>
public class BrokerageUnitTests
{
    private record Expected(decimal Brokerage, decimal Stt, decimal Exchange, decimal Sebi, decimal Gst, decimal Total);

    // One exit order (TP1, 325 units) filled in two parts: 130 @ 138.00 (turnover 17,940.00) and 195 @ 138.00 (turnover 26,910.00).
    private static readonly Expected[] FlatPerOrder = { new(20.00m, 26.91m, 6.37m, 0.02m, 4.75m, 58.05m), new(0.00m, 40.37m, 9.56m, 0.03m, 1.73m, 51.69m) };
    private static readonly Expected[] FlatPerFill = { new(20.00m, 26.91m, 6.37m, 0.02m, 4.75m, 58.05m), new(20.00m, 40.37m, 9.56m, 0.03m, 5.33m, 75.29m) };
    private static readonly Expected[] PercentPerOrder = { new(8.97m, 26.91m, 6.37m, 0.02m, 2.76m, 45.03m), new(11.03m, 40.37m, 9.56m, 0.03m, 3.71m, 64.70m) };
    private static readonly Expected[] PercentPerFill = { new(8.97m, 26.91m, 6.37m, 0.02m, 2.76m, 45.03m), new(13.46m, 40.37m, 9.56m, 0.03m, 4.15m, 67.57m) };

    private static TradeKit PartialTp1Kit(BrokerageSpec brokerage)
    {
        var costs = new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(brokerage) } };
        var kit = new TradeKit(TradeKit.MakeConfig(costs: costs, variants: TradeKit.Immediate(exit: new ExitSpec { Tp1Pct = 0.10m, Tp1Proportion = 0.5m })));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Loop.AdvanceTo(kit.At(1.5));
        kit.Feed(kit.Q(kit.At(2), 138m, 139m, bidQty: 130));                  // TP1 target 325; only 130 displayed
        kit.Loop.AdvanceTo(kit.At(3.5));                                      // first part: 130
        kit.Feed(kit.Q(kit.At(4.7), 138m, 139m, bidQty: 260));                // new displayed state
        kit.Loop.AdvanceTo(kit.At(5.5));                                      // second part: 195
        return kit;
    }

    private static void Check(Expected expected, CostBreakdown actual)
    {
        Assert.Equal(expected.Brokerage, actual.Brokerage);
        Assert.Equal(expected.Stt, actual.Stt);
        Assert.Equal(expected.Exchange, actual.ExchangeCharge);
        Assert.Equal(expected.Sebi, actual.SebiFee);
        Assert.Equal(expected.Gst, actual.Gst);
        Assert.Equal(expected.Total, actual.Total);
    }

    [Theory]
    [InlineData("flat-order")]
    [InlineData("flat-fill")]
    [InlineData("pct-order")]
    [InlineData("pct-fill")]
    public void TwoFillsOfOneExitOrder_ChargeBrokerageAccordingToTheConfiguredUnit(string scenario)
    {
        var (spec, expected) = scenario switch
        {
            "flat-order" => ((BrokerageSpec)new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder }, FlatPerOrder),
            "flat-fill" => (new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerFill }, FlatPerFill),
            "pct-order" => (new PercentOfTurnoverBrokerage { Rate = 0.0005m, Cap = 20m, Unit = BrokerageUnit.PerOrder }, PercentPerOrder),
            _ => (new PercentOfTurnoverBrokerage { Rate = 0.0005m, Cap = 20m, Unit = BrokerageUnit.PerFill }, PercentPerFill),
        };

        var kit = PartialTp1Kit(spec);

        var sells = kit.Fills("A").Where(f => f.Side == TradeSide.Sell).ToList();
        Assert.Equal(2, sells.Count);
        Assert.Equal(sells[0].OrderId, sells[1].OrderId);                      // one order, two fills
        Assert.Equal(new long[] { 130, 195 }, sells.Select(f => f.Quantity).ToArray());
        Check(expected[0], sells[0].Costs);
        Check(expected[1], sells[1].Costs);
        Assert.Equal(expected.Sum(e => e.Brokerage), sells.Sum(f => f.Costs.Brokerage));
        Assert.Equal(expected.Sum(e => e.Total), kit.Position().ExitCosts);
    }

    [Fact]
    public void PerOrderBrokerage_IsNotMultipliedByPartialFills_WhereAsPerFillIs()
    {
        var perOrder = PartialTp1Kit(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerOrder });
        var perFill = PartialTp1Kit(new FlatBrokerage { Amount = 20m, Unit = BrokerageUnit.PerFill });

        Assert.Equal(20m, perOrder.Fills("A").Where(f => f.Side == TradeSide.Sell).Sum(f => f.Costs.Brokerage));
        Assert.Equal(40m, perFill.Fills("A").Where(f => f.Side == TradeSide.Sell).Sum(f => f.Costs.Brokerage));
        Assert.Equal(133.34m - 109.74m, perFill.Position().ExitCosts - perOrder.Position().ExitCosts);   // the difference is exactly one extra brokerage plus its GST
    }

    [Fact]
    public void Entry_ChargesTheFirstFillOfItsOrder_UnderEitherUnit()
    {
        foreach (var unit in new[] { BrokerageUnit.PerOrder, BrokerageUnit.PerFill })
        {
            var kit = TradeKit.Opened(TradeKit.Immediate());
            var model = new CostModel(TradeKit.TestCosts(20m, unit), Clock.Day);
            Assert.Equal(20m, model.Compute(TradeSide.Buy, 81250m).Brokerage);
            Assert.Equal(60.20m, kit.Position().EntryCosts);
        }
    }

    [Fact]
    public void CostModel_UsesTheOrderChargeState()
    {
        var perOrder = new CostModel(TradeKit.TestCosts(20m, BrokerageUnit.PerOrder), Clock.Day);
        var perFill = new CostModel(TradeKit.TestCosts(20m, BrokerageUnit.PerFill), Clock.Day);
        var second = new OrderChargeState(1, 17940m, 20m);

        Assert.Equal(0m, perOrder.Compute(TradeSide.Sell, 26910m, second).Brokerage);
        Assert.Equal(20m, perFill.Compute(TradeSide.Sell, 26910m, second).Brokerage);
        Assert.Equal(20m, perOrder.Compute(TradeSide.Sell, 26910m).Brokerage);                          // a fresh order
        Assert.Equal(BrokerageUnit.PerOrder, perOrder.BrokerageUnit);
    }

    [Fact]
    public void PercentPerOrder_NeverExceedsTheOrderCap_AcrossManyFills()
    {
        var model = new CostModel(new CostsConfig { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new PercentOfTurnoverBrokerage { Rate = 0.0005m, Cap = 20m, Unit = BrokerageUnit.PerOrder }) } }, Clock.Day);
        var state = OrderChargeState.None;
        var total = 0m;
        for (var i = 0; i < 20; i++)
        {
            var costs = model.Compute(TradeSide.Sell, 10000m, state);
            total += costs.Brokerage;
            state = new OrderChargeState(state.Fills + 1, state.Turnover + 10000m, state.BrokerageCharged + costs.Brokerage);
        }

        Assert.Equal(20.00m, total);                                                                   // 200,000 x 0.05 % = 100, capped at the order cap
    }

    [Fact]
    public void BrokerageUnit_IsRequired_AndPartOfTheHash()
    {
        var unitRequired = typeof(BrokerageSpec).GetProperty(nameof(BrokerageSpec.Unit))!
            .GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length > 0;
        Assert.True(unitRequired);

        var a = TradeKit.MakeConfig(costs: TradeKit.TestCosts(20m, BrokerageUnit.PerOrder), variants: TradeKit.Immediate()).ComputeHash();
        var b = TradeKit.MakeConfig(costs: TradeKit.TestCosts(20m, BrokerageUnit.PerFill), variants: TradeKit.Immediate()).ComputeHash();
        Assert.NotEqual(a, b);
        Assert.Contains(NseIndexOptionsCostSchedules.Provisional, c => c == "Brokerage");
    }
}

public class SignalQuoteReuseTests
{
    private static TradeKit Kit(ExecutionConfig? execution = null)
        => new(TradeKit.MakeConfig(execution, variants: TradeKit.Immediate()));

    [Fact]
    public void Default_AFreshSignalQuote_MayFillTheOrder_PositiveLatencyAloneDoesNotPreventIt()
    {
        var kit = Kit();
        var signal = kit.Q(TradeKit.T0, 124m, 125m);
        kit.Bar(TradeKit.T0, 1, signal);                                          // the quote that triggers the intent is the only quote there is

        kit.Loop.AdvanceTo(kit.At(1.5));

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(kit.At(1), fill.At);                                          // executed one latency later ...
        Assert.Equal(signal.SourceId, fill.QuoteSourceId);                         // ... against the very quote that generated the signal (age 1 s <= 2 s)
        Assert.Equal(signal.SourceId, kit.Out<EntryIntent>().Single().QuoteSourceId);
        Assert.Equal(SignalQuoteReusePolicy.AllowFreshSignalQuote, kit.Config.Execution.SignalQuoteReuse);
    }

    [Fact]
    public void Freshness_StillGovernsReuse_ASignalQuoteTooOldAtTheExecutionTimeIsNotUsed()
    {
        var kit = Kit(new ExecutionConfig { Latency = TimeSpan.FromSeconds(3), EntryOrderLifetime = TimeSpan.FromSeconds(8) });
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));                  // 3 s old at the maturity instant > MaxQuoteAge 2 s

        kit.Loop.AdvanceTo(kit.At(3.2));
        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:QuoteStale");

        var newer = kit.Q(kit.At(3.5), 123m, 124m);
        kit.Feed(newer);
        var fill = Assert.Single(kit.Fills());
        Assert.Equal(newer.SourceId, fill.QuoteSourceId);
    }

    [Fact]
    public void RequireQuoteAfterDecision_WaitsForAStrictlyNewerQuote_EvenWhenTheSignalQuoteIsFresh()
    {
        var kit = Kit(new ExecutionConfig { SignalQuoteReuse = SignalQuoteReusePolicy.RequireQuoteAfterDecision });
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m), kit.Q(TradeKit.T0, 123m, 124m));   // a second quote at the SAME instant is not newer than the decision

        kit.Loop.AdvanceTo(kit.At(1.5));
        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:AwaitingNewerQuote");

        var newer = kit.Q(kit.At(1.6), 122m, 123m);
        kit.Feed(newer);

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(newer.SourceId, fill.QuoteSourceId);
        Assert.Equal(123m, fill.Price);
    }

    [Fact]
    public void SignalQuoteLiquidity_ConsumedByAnEarlierFill_IsNotAvailableAgainToTheSameBook()
    {
        var script = new ScriptedStrategy("S", seq => new StrategyValidity(seq is 1 or 3, NiftySignal.Trading.Core.Instruments.OptionSide.Call, "x"));
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()), null, script);
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m, askQty: 300));       // the signal quote shows 300 (4 lots)
        kit.Loop.AdvanceTo(kit.At(1.5));
        Assert.Equal(260, Assert.Single(kit.Fills()).Quantity);                     // reuse: the first order takes 4 lots from the signal quote

        kit.Engine.Execution.RequestStrategyExit("A", kit.Position().Id, null, kit.Loop);
        kit.Feed(kit.Q(kit.At(2), 124m, 125m, askQty: 300));
        kit.Loop.AdvanceTo(kit.At(4));
        kit.Feed(Ev.Bar(kit.At(4.5), 2));
        kit.Bar(kit.At(5), 3, kit.Q(kit.At(5), 124m, 125m, askQty: 300));          // the next signal quote has the SAME displayed state
        kit.Loop.AdvanceTo(kit.At(6.5));

        Assert.Single(kit.Fills(), f => f.Side == TradeSide.Buy);                   // only 40 of the 300 remain: less than one lot
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:NoLiquidity");
    }

    [Fact]
    public void IndependentBooks_EachMayReuseTheSameSignalQuote()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: new[] { TradeKit.Immediate("A"), TradeKit.Immediate("B") }));
        var signal = kit.Q(TradeKit.T0, 124m, 125m, askQty: 650);
        kit.Bar(TradeKit.T0, 1, signal);
        kit.Loop.AdvanceTo(kit.At(1.5));

        Assert.All(new[] { "A", "B" }, id => Assert.Equal(signal.SourceId, Assert.Single(kit.Fills(id)).QuoteSourceId));
    }
}
