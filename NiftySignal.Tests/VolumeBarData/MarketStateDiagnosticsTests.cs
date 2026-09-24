using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class MarketStateDiagnosticsTests
{
    [Fact]
    public void ComputeVolumeRate_IsVolumeDividedBySecondsOfDuration()
        => Assert.Equal(130.0, MarketStateDiagnostics.ComputeVolumeRate(1300, 10_000));

    [Fact]
    public void ComputeVolumeRate_ReturnsNull_WhenDurationIsNotPositive()
    {
        Assert.Null(MarketStateDiagnostics.ComputeVolumeRate(1300, 0));
        Assert.Null(MarketStateDiagnostics.ComputeVolumeRate(1300, -5));
    }

    [Fact]
    public void ComputeCePeDivergenceMagnitude_IsSumOfAbsoluteChanges()
        => Assert.Equal(1.5m, MarketStateDiagnostics.ComputeCePeDivergenceMagnitude(-1.0m, 0.5m));

    [Fact]
    public void ComputeCePeDivergenceMagnitude_ReturnsNull_WhenEitherLegMissing()
    {
        Assert.Null(MarketStateDiagnostics.ComputeCePeDivergenceMagnitude(null, 0.5m));
        Assert.Null(MarketStateDiagnostics.ComputeCePeDivergenceMagnitude(-1.0m, null));
    }

    [Fact]
    public void ClassifyPriorStateClass_RecognizesPatternAAndPatternBVerbatim()
    {
        Assert.Equal("PatternA", MarketStateDiagnostics.ClassifyPriorStateClass(ForwardValidationAnalysis.State2_BullishDivergence_PatternA));
        Assert.Equal("PatternB", MarketStateDiagnostics.ClassifyPriorStateClass(ForwardValidationAnalysis.State4_BearishDivergence_PatternB));
    }

    [Theory]
    [InlineData("OptionDataIncomplete")]
    [InlineData("ContractTransition")]
    [InlineData("UnderlyingFlat_NoDivergence")]
    public void ClassifyPriorStateClass_EverythingElseIsOther(string rawCategory)
        => Assert.Equal("Other", MarketStateDiagnostics.ClassifyPriorStateClass(rawCategory));
}
