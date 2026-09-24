using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>2026-09-24, option-response decomposition. Coverage for <see cref="OptionValueDecomposition"/>'s two new pure calculations -- intrinsic value and moneyness -- and the reconciliation identity they guarantee.</summary>
public sealed class OptionValueDecompositionTests
{
    [Theory]
    [InlineData(25100, 25000, OptionType.Call, 100)] // ITM call.
    [InlineData(24900, 25000, OptionType.Call, 0)]   // OTM call -- clipped at 0, never negative.
    [InlineData(25000, 25000, OptionType.Call, 0)]   // exactly ATM.
    [InlineData(24900, 25000, OptionType.Put, 100)]  // ITM put.
    [InlineData(25100, 25000, OptionType.Put, 0)]    // OTM put -- clipped at 0.
    public void ComputeIntrinsic_MatchesTheStandardDefinition(decimal futures, decimal strike, OptionType side, decimal expected)
        => Assert.Equal(expected, OptionValueDecomposition.ComputeIntrinsic(futures, strike, side));

    [Fact]
    public void ComputeExtrinsic_IsOptionPriceMinusIntrinsic()
        => Assert.Equal(20m, OptionValueDecomposition.ComputeExtrinsic(optionPrice: 120m, intrinsic: 100m));

    [Fact]
    public void ComputeExtrinsic_CanBeNegative_NeverClipped()
        // Unlike intrinsic, extrinsic is never floored at zero -- a distressed/stale print can
        // legitimately show a price below its own intrinsic value.
        => Assert.Equal(-5m, OptionValueDecomposition.ComputeExtrinsic(optionPrice: 95m, intrinsic: 100m));

    [Theory]
    [InlineData(25100, 25000, OptionType.Call, 100)]  // ITM.
    [InlineData(24900, 25000, OptionType.Call, -100)] // OTM -- NEGATIVE, unlike intrinsic's clipping.
    [InlineData(24900, 25000, OptionType.Put, 100)]
    [InlineData(25100, 25000, OptionType.Put, -100)]
    public void ComputeMoneyness_IsSignedAndUnclipped(decimal futures, decimal strike, OptionType side, decimal expected)
        => Assert.Equal(expected, OptionValueDecomposition.ComputeMoneyness(futures, strike, side));

    [Fact]
    public void OptionChange_AlwaysExactlyEqualsIntrinsicChangePlusExtrinsicChange_ByConstruction()
    {
        // The reconciliation "identity" the task asks to verify -- proven here as an exact
        // algebraic guarantee, not an approximation that could show a discrepancy.
        const decimal strike = 25000m;
        const OptionType side = OptionType.Call;

        var signalFutures = 25050m;
        var signalOptionPrice = 80m;
        var signalIntrinsic = OptionValueDecomposition.ComputeIntrinsic(signalFutures, strike, side);
        var signalExtrinsic = OptionValueDecomposition.ComputeExtrinsic(signalOptionPrice, signalIntrinsic);

        var forwardFutures = 25150m;
        var forwardOptionPrice = 165m;
        var forwardIntrinsic = OptionValueDecomposition.ComputeIntrinsic(forwardFutures, strike, side);
        var forwardExtrinsic = OptionValueDecomposition.ComputeExtrinsic(forwardOptionPrice, forwardIntrinsic);

        var optionChange = forwardOptionPrice - signalOptionPrice;
        var intrinsicChange = forwardIntrinsic - signalIntrinsic;
        var extrinsicChange = forwardExtrinsic - signalExtrinsic;

        Assert.Equal(optionChange, intrinsicChange + extrinsicChange);
    }
}
