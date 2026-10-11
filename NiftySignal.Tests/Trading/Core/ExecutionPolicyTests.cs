using System.Reflection;
using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class ExecutionPolicyTests
{
    /// <summary>Entry intent at the decision ask (limit = that ask); one execution-time quote; heartbeat past the maturity.</summary>
    private static TradeKit Run(decimal decisionAsk, decimal execBid, decimal execAsk, ExecutionConfig? execution = null, long? askQty = 100000)
    {
        var kit = new TradeKit(TradeKit.MakeConfig(execution, variants: TradeKit.Immediate()));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, decisionAsk - 1m, decisionAsk));
        kit.Feed(kit.Q(kit.At(0.5), execBid, execAsk, askQty: askQty));
        kit.Loop.AdvanceTo(kit.At(1.2));
        return kit;
    }

    private static decimal Dec(string text) => decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ band at execution, price improvement, both below-band modes

    [Theory]
    [InlineData("125", "125.00", true)]
    [InlineData("125", "118.00", true)]          // price improvement: fills at the actual ask, not at the limit
    [InlineData("125", "100.00", true)]          // lower band edge inclusive
    [InlineData("125", "99.95", false)]          // below the band: no fill in the default research mode
    [InlineData("125", "125.05", false)]         // above the limit
    [InlineData("150", "150.00", true)]          // upper band edge inclusive (limit = 150)
    [InlineData("150", "150.05", false)]
    [InlineData("100", "100.00", true)]
    public void DefaultMode_FillsOnlyInsideTheBandAndAtOrBelowTheLimit(string decision, string execAsk, bool expectFill)
    {
        var ask = Dec(execAsk);
        var kit = Run(Dec(decision), ask - 0.05m, ask);

        if (expectFill)
        {
            var fill = Assert.Single(kit.Fills());
            Assert.Equal(ask, fill.Price);
            Assert.False(fill.BelowBand);
            Assert.Equal(650, fill.Quantity);
        }
        else
        {
            Assert.Empty(kit.Fills());
            Assert.Equal(OrderStatus.Working, kit.Engine.Execution.EntryOrders.Single().Status);
        }
    }

    [Fact]
    public void DefaultMode_BelowBandNoFill_KeepsTheOrderWorking_ItFillsLaterIfTheAskReturnsToTheBand()
    {
        var kit = Run(125m, 99.90m, 99.95m);
        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:AskBelowBand");

        kit.Feed(kit.Q(kit.At(2), 109.95m, 110m));

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(110m, fill.Price);
    }

    [Theory]
    [InlineData("99.95", true)]
    [InlineData("90.00", true)]
    [InlineData("100.00", false)]
    public void BrokerRealisticBelowBand_FillsAtTheActualAsk_FlagsItAndNeverClamps(string execAsk, bool belowBand)
    {
        var ask = Dec(execAsk);
        var kit = Run(125m, ask - 0.05m, ask, new ExecutionConfig { BelowBand = BelowBandMode.BrokerRealisticBelowBand });

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(ask, fill.Price);                           // the actual price, never moved up into the band
        Assert.Equal(belowBand, fill.BelowBand);
        Assert.Equal(ask, kit.Position().AvgEntryPrice);
    }

    [Fact]
    public void BrokerRealisticBelowBand_StillRespectsTheLimit_AndIsReportedSeparately()
    {
        var above = Run(125m, 125m, 125.05m, new ExecutionConfig { BelowBand = BelowBandMode.BrokerRealisticBelowBand });
        Assert.Empty(above.Fills());

        var kit = Run(125m, 99.90m, 99.95m, new ExecutionConfig { BelowBand = BelowBandMode.BrokerRealisticBelowBand });
        kit.Loop.Finish(kit.Engine.Variants.Schedule.SessionEndUtc);
        var report = Assert.Single(kit.Out<SessionEndReport>("A"));
        Assert.Equal(1, report.BelowBandFills);
        Assert.Contains("independent-virtual-book", report.Book);
    }

    // ------------------------------------------------------------------ quote quality and liquidity

    [Theory]
    [InlineData("122", "125", "Working:SpreadTooWide")]       // spread 3 > 2.00
    [InlineData("126", "125", "Working:QuoteNotExecutable")]  // crossed
    [InlineData("0", "125", "Working:QuoteNotExecutable")]    // zero bid
    public void UnusableQuotes_DoNotFill(string bid, string ask, string expectedNote)
    {
        var kit = Run(125m, Dec(bid), Dec(ask));

        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == expectedNote);
    }

    [Fact]
    public void MissingBid_IsNotExecutable()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));
        kit.Feed(kit.Q(kit.At(0.5), null, 125m));
        kit.Loop.AdvanceTo(kit.At(1.2));

        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:QuoteNotExecutable");
    }

    [Fact]
    public void RelativeSpreadLimit_AppliesWhenTheAbsoluteLimitIsOff()
    {
        var config = new ExecutionConfig { MaxSpreadAbsolute = null, MaxSpreadFraction = 0.03m };
        Assert.Empty(Run(125m, 106m, 110m, config).Fills());                    // spread 4 on mid 108 = 3.7 %
        Assert.Single(Run(125m, 107.5m, 110m, config).Fills());                 // spread 2.5 on mid 108.75 = 2.3 %
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(64L)]                                                            // less than one lot of 65
    public void UnusableDisplayedQuantity_MeansNoFill(long? askQty)
    {
        var kit = Run(125m, 124m, 125m, askQty: askQty);

        Assert.Empty(kit.Fills());
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:NoLiquidity");
    }

    [Fact]
    public void UnknownQuantity_CanBeAllowedExplicitly()
    {
        var kit = Run(125m, 124m, 125m, new ExecutionConfig { RequireDisplayedQuantity = false }, askQty: null);

        Assert.Equal(650, Assert.Single(kit.Fills()).Quantity);
    }

    [Fact]
    public void PartialEntry_FillsWholeLots_KeepsTheFilledQuantity_AndCancelsTheRemainder()
    {
        var kit = Run(125m, 124m, 125m, askQty: 300);                            // 300 displayed = 4 lots of 65

        var fill = Assert.Single(kit.Fills());
        Assert.Equal(260, fill.Quantity);
        Assert.Equal(260, kit.Position().EntryQuantity);
        Assert.Equal(OrderStatus.Filled, kit.Engine.Execution.EntryOrders.Single().Status);   // terminal: the remaining 390 is cancelled, never worked again
        var opportunity = kit.Engine.Variants.AllOpportunities.Single();
        Assert.Equal(OpportunityState.Consumed, opportunity.State);
        Assert.Equal("PartiallyFilled", opportunity.TerminalReason);
        Assert.Equal(SlotState.Occupied, kit.Engine.Variants.GetVariant(Vk.S("A")).Slots[0].State);

        kit.Feed(kit.Q(kit.At(2), 124m, 125m, askQty: 5000));                      // more liquidity later: no top-up
        Assert.Single(kit.Fills());
    }

    [Fact]
    public void FillFraction_LimitsTheShareOfDisplayedQuantity()
    {
        var kit = Run(125m, 124m, 125m, new ExecutionConfig { FillFractionOfDisplayed = 0.5m }, askQty: 1000);   // 500 usable = 7 lots

        Assert.Equal(455, Assert.Single(kit.Fills()).Quantity);
    }

    [Fact]
    public void UnchangedDisplayedLiquidity_IsNotConsumedTwice_ByASecondEntryInTheSameBook()
    {
        var script = new ScriptedStrategy("S", seq => seq is 1 or 3 ? new StrategyValidity(true, NiftySignal.Trading.Core.Instruments.OptionSide.Call, "x") : new StrategyValidity(false, NiftySignal.Trading.Core.Instruments.OptionSide.Call, "x"));
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()), null, script);
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m, askQty: 300));
        kit.Feed(kit.Q(kit.At(0.5), 124m, 125m, askQty: 300));
        kit.Loop.AdvanceTo(kit.At(1.2));
        Assert.Equal(260, Assert.Single(kit.Fills()).Quantity);

        kit.Feed(kit.Q(kit.At(1.5), 124m, 125m, bidQty: 100000, askQty: 300));    // same displayed ask state
        kit.Engine.Execution.RequestStrategyExit(Vk.S("A"), kit.Position().Id, null, kit.Loop);
        kit.Loop.AdvanceTo(kit.At(3));
        Assert.Equal(PositionState.Closed, kit.Position().State);

        kit.Feed(Ev.Bar(kit.At(3.5), 2));                                          // episode ends
        kit.Bar(kit.At(4), 3, kit.Q(kit.At(4), 124m, 125m, askQty: 300));          // new episode, same displayed ask state (price 125, size 300)
        kit.Loop.AdvanceTo(kit.At(5.2));
        Assert.Single(kit.Fills(), f => f.Side == NiftySignal.Trading.Core.Costs.TradeSide.Buy);                // second entry: 260 of the 300 are still consumed, 40 < one lot
        Assert.Contains(kit.Out<AuditRecord>("A"), r => r.Outcome == "Working:NoLiquidity");

        kit.Feed(kit.Q(kit.At(5.5), 124m, 125m, askQty: 600));                     // a changed displayed state is fresh liquidity
        var buys = kit.Fills().Where(f => f.Side == NiftySignal.Trading.Core.Costs.TradeSide.Buy).ToList();
        Assert.Equal(2, buys.Count);
        Assert.Equal(585, buys[1].Quantity);                                       // 600 / 65 = 9 lots
    }

    [Fact]
    public void IndependentVariants_UseIndependentVirtualBooks_AndTheCombinedFillsAreNotFeasibleInOneAccount()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: new[] { TradeKit.Immediate("A"), TradeKit.Immediate("B") }));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m, askQty: 650));
        kit.Loop.AdvanceTo(kit.At(1.5));

        var a = Assert.Single(kit.Fills("A"));
        var b = Assert.Single(kit.Fills("B"));
        Assert.Equal(650, a.Quantity);
        Assert.Equal(650, b.Quantity);                                             // both took the SAME 650 displayed: a shared account could have had 650 in total
        Assert.Equal("independent-virtual-book:S/A", a.Book);
        Assert.Equal("independent-virtual-book:S/B", b.Book);
        Assert.Equal(BookMode.IndependentVirtualBooks, kit.Config.Execution.Books);
    }

    // ------------------------------------------------------------------ configuration guards

    [Fact]
    public void ExecutionConfig_ValidatesItsAssumptions()
    {
        Assert.Empty(new ExecutionConfig().Validate());
        Assert.NotEmpty((new ExecutionConfig { Latency = TimeSpan.Zero }).Validate());                          // zero latency would let the signal quote fill the order
        Assert.NotEmpty((new ExecutionConfig { EntryOrderLifetime = TimeSpan.FromSeconds(1) }).Validate());
        Assert.NotEmpty((new ExecutionConfig { FillFractionOfDisplayed = 0m }).Validate());
        Assert.NotEmpty((new ExecutionConfig { FillFractionOfDisplayed = 1.01m }).Validate());
        Assert.NotEmpty((new ExecutionConfig { ExitRetryInterval = TimeSpan.Zero }).Validate());
        Assert.NotEmpty((new ExecutionConfig { MaxSpreadAbsolute = 0m }).Validate());
    }

    [Fact]
    public void RealOrderMode_IsUnavailable()
    {
        var config = new ExecutionConfig { Adapter = ExecutionAdapter.Broker };

        Assert.Contains(config.Validate(), e => e.Contains("not available"));
        Assert.Throws<ArgumentException>(() => new TradeKit(TradeKit.MakeConfig(config, variants: TradeKit.Immediate())));
    }

    // ------------------------------------------------------------------ candle price sources never involve LTP

    [Fact]
    public void NoCandlePriceSource_IsBasedOnTheLastTradedPrice()
    {
        Assert.DoesNotContain(Enum.GetNames<CandlePriceSource>(), n => n.Contains("Ltp", StringComparison.OrdinalIgnoreCase) || n.Contains("Last", StringComparison.OrdinalIgnoreCase) || n.Contains("Trade", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "AskClose", "BidClose", "MidClose", "TypicalPrice" }, Enum.GetNames<CandlePriceSource>());
        Assert.DoesNotContain(typeof(Candle).GetProperties(BindingFlags.Public | BindingFlags.Instance), p => p.Name.Contains("Last", StringComparison.Ordinal) && !p.Name.StartsWith("LastBid", StringComparison.Ordinal) && !p.Name.StartsWith("LastAsk", StringComparison.Ordinal) && p.Name != "LastQuoteAt");
    }

    [Fact]
    public void ALastPriceAlone_NeverCreatesACandleObservation()
    {
        var store = new CandleStore(new CandleConfig());

        store.Observe(new QuoteEvent(Clock.Ist(10, 0, 30), EventKind.OptionQuote, 1, "C", null, null, last: 123m, bidQty: null, askQty: null));
        store.Observe(new QuoteEvent(Clock.Ist(10, 0, 40), EventKind.OptionQuote, 2, "C", 0m, 0m, last: 124m));
        store.PublishBar(1, Clock.Ist(10, 1));

        Assert.False(store.ViewFor("C").Get(1)!.IsValid);
        Assert.Equal(CandleInvalidReason.NoObservations, store.ViewFor("C").Get(1)!.InvalidReason);
    }
}
