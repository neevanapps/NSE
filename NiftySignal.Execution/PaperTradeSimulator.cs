using NiftySignal.Rules;

namespace NiftySignal.Execution;

/// <summary>
/// Fill and cost logic from plan section 9: entry fills at ask + slippage, exit at bid -
/// slippage (never at LTP -- that flatters results by ignoring the spread you'd actually
/// cross). Brokerage only for now -- see the note on <see cref="CostsConfig"/> in
/// RulesetConfig.cs; the plan mentions an STT/charges approximation but never gives a rate
/// to apply, so nothing is fabricated here.
/// </summary>
public static class PaperTradeSimulator
{
    public static FillResult FillEntry(decimal askPrice, decimal tickSize, int quantity, CostsConfig costs)
    {
        var fillPrice = askPrice + (tickSize * costs.SlippageTicks);
        var grossValue = fillPrice * quantity;

        // Cost to acquire the position = what you paid for it + the brokerage charged to enter.
        return new FillResult(fillPrice, costs.BrokeragePerOrder, grossValue, grossValue + costs.BrokeragePerOrder);
    }

    public static FillResult FillExit(decimal bidPrice, decimal tickSize, int quantity, CostsConfig costs)
    {
        var fillPrice = Math.Max(0m, bidPrice - (tickSize * costs.SlippageTicks));
        var grossValue = fillPrice * quantity;

        // Proceeds from closing the position = what you received - the brokerage charged to exit.
        return new FillResult(fillPrice, costs.BrokeragePerOrder, grossValue, grossValue - costs.BrokeragePerOrder);
    }

    /// <summary>Net P&amp;L for one full leg: what came back out minus what went in, costs included both sides.</summary>
    public static decimal ComputeNetPnl(FillResult entry, FillResult exit) => exit.NetValue - entry.NetValue;
}
