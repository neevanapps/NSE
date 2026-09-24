using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class TransactionCostCalculatorTests
{
    [Fact]
    public void Compute_SttIsQuarterPercentOfSellPremium_RoundedToTwoDecimals()
    {
        // exitGrossValue = 100000 -> STT = 100000 * 0.000625 = 62.5.
        var result = TransactionCostCalculator.Compute(exitGrossValue: 100_000m, brokerage: 0m);

        Assert.Equal(62.5m, result.Stt);
    }

    [Fact]
    public void Compute_GstIsZero_WhenBrokerageIsZero()
    {
        var result = TransactionCostCalculator.Compute(exitGrossValue: 50_000m, brokerage: 0m);

        Assert.Equal(0m, result.Gst);
    }

    [Fact]
    public void Compute_GstIs18PercentOfBrokerage_WhenBrokerageIsNonZero()
    {
        var result = TransactionCostCalculator.Compute(exitGrossValue: 50_000m, brokerage: 40m);

        Assert.Equal(7.2m, result.Gst);
    }

    [Fact]
    public void Compute_OtherIsAlwaysZero_NotModeled()
    {
        var result = TransactionCostCalculator.Compute(exitGrossValue: 50_000m, brokerage: 40m);

        Assert.Equal(0m, result.Other);
    }

    [Fact]
    public void Compute_TotalIsSumOfAllThreeComponents()
    {
        var result = TransactionCostCalculator.Compute(exitGrossValue: 100_000m, brokerage: 40m);

        Assert.Equal(result.Stt + result.Gst + result.Other, result.Total);
    }
}
