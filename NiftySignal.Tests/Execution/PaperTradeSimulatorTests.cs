using NiftySignal.Execution;
using NiftySignal.Rules;

namespace NiftySignal.Tests.Execution;

public class PaperTradeSimulatorTests
{
    static readonly CostsConfig Costs = new(BrokeragePerOrder: 20, SlippageTicks: 2);
    const decimal TickSize = 0.05m;

    [Fact]
    public void FillEntry_FillsAtAskPlusSlippage_NotAtLtp()
    {
        var fill = PaperTradeSimulator.FillEntry(askPrice: 180m, TickSize, quantity: 65, Costs);

        // ask 180 + (2 ticks * 0.05) = 180.10
        Assert.Equal(180.10m, fill.FillPrice);
    }

    [Fact]
    public void FillExit_FillsAtBidMinusSlippage_NotAtLtp()
    {
        var fill = PaperTradeSimulator.FillExit(bidPrice: 200m, TickSize, quantity: 65, Costs);

        // bid 200 - (2 ticks * 0.05) = 199.90
        Assert.Equal(199.90m, fill.FillPrice);
    }

    [Fact]
    public void FillExit_NeverGoesNegative_EvenWithSlippageExceedingTheBid()
    {
        var fill = PaperTradeSimulator.FillExit(bidPrice: 0.05m, tickSize: 1m, quantity: 65, Costs);

        Assert.Equal(0m, fill.FillPrice);
    }

    [Fact]
    public void FillEntry_NetValue_IncludesBrokerageOnTopOfGrossCost()
    {
        var fill = PaperTradeSimulator.FillEntry(askPrice: 180m, TickSize, quantity: 65, Costs);

        var expectedGross = 180.10m * 65;
        Assert.Equal(expectedGross, fill.GrossValue);
        Assert.Equal(expectedGross + 20m, fill.NetValue);
    }

    [Fact]
    public void FillExit_NetValue_SubtractsBrokerageFromGrossProceeds()
    {
        var fill = PaperTradeSimulator.FillExit(bidPrice: 200m, TickSize, quantity: 65, Costs);

        var expectedGross = 199.90m * 65;
        Assert.Equal(expectedGross, fill.GrossValue);
        Assert.Equal(expectedGross - 20m, fill.NetValue);
    }

    [Fact]
    public void ComputeNetPnl_IsPositive_ForAProfitableRoundTrip()
    {
        var entry = PaperTradeSimulator.FillEntry(askPrice: 150m, TickSize, quantity: 65, Costs);
        var exit = PaperTradeSimulator.FillExit(bidPrice: 200m, TickSize, quantity: 65, Costs);

        var pnl = PaperTradeSimulator.ComputeNetPnl(entry, exit);

        Assert.True(pnl > 0);
    }

    [Fact]
    public void ComputeNetPnl_IsNegative_WhenSlippageAndBrokerageErodeATinyGrossGain()
    {
        // Same price both ways -- costs alone should make this a net loss, proving costs
        // are actually applied rather than netting to zero.
        var entry = PaperTradeSimulator.FillEntry(askPrice: 180m, TickSize, quantity: 65, Costs);
        var exit = PaperTradeSimulator.FillExit(bidPrice: 180m, TickSize, quantity: 65, Costs);

        var pnl = PaperTradeSimulator.ComputeNetPnl(entry, exit);

        Assert.True(pnl < 0);
    }

    [Fact]
    public void ComputeNetPnl_ExactArithmetic_MatchesManualCalculation()
    {
        var entry = PaperTradeSimulator.FillEntry(askPrice: 150m, TickSize, quantity: 65, Costs);
        var exit = PaperTradeSimulator.FillExit(bidPrice: 200m, TickSize, quantity: 65, Costs);

        // entry fill 150.10, exit fill 199.90
        var expected = ((199.90m * 65) - 20m) - ((150.10m * 65) + 20m);

        Assert.Equal(expected, PaperTradeSimulator.ComputeNetPnl(entry, exit));
    }
}
