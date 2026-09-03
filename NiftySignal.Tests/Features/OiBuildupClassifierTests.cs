using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public class OiBuildupClassifierTests
{
    [Theory]
    [InlineData(1, 1, OiBuildupClassification.LongBuildup)]      // price up, OI up
    [InlineData(-1, 1, OiBuildupClassification.ShortBuildup)]    // price down, OI up
    [InlineData(-1, -1, OiBuildupClassification.LongUnwinding)]  // price down, OI down
    [InlineData(1, -1, OiBuildupClassification.ShortCovering)]   // price up, OI down
    public void Classify_MapsAllFourQuadrants(decimal priceChange, decimal oiChange, OiBuildupClassification expected)
    {
        var result = OiBuildupClassifier.Classify(priceChange, oiChange);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    public void Classify_IsNeutral_WhenEitherPriceOrOiDidNotActuallyChange(decimal priceChange, decimal oiChange)
    {
        var result = OiBuildupClassifier.Classify(priceChange, oiChange);

        Assert.Equal(OiBuildupClassification.Neutral, result);
    }

    [Theory]
    [InlineData(0.01, 0.01, OiBuildupClassification.LongBuildup)]
    [InlineData(-0.01, -0.01, OiBuildupClassification.LongUnwinding)]
    public void Classify_UsesSignNotMagnitude(decimal priceChange, decimal oiChange, OiBuildupClassification expected)
    {
        var result = OiBuildupClassifier.Classify(priceChange, oiChange);

        Assert.Equal(expected, result);
    }
}
