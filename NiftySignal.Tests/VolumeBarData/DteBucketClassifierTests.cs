using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class DteBucketClassifierTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(4, "4-6")]
    [InlineData(5, "4-6")]
    [InlineData(6, "4-6")]
    [InlineData(7, "7-8")]
    [InlineData(8, "7-8")]
    [InlineData(11, "11-13")]
    [InlineData(12, "11-13")]
    [InlineData(13, "11-13")]
    public void Classify_MapsKnownCalendarDtesToTheirBucket(int dte, string expected)
        => Assert.Equal(expected, DteBucketClassifier.Classify(dte));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(-1)]
    public void Classify_ReturnsOther_ForDtesNotObservedInThisDataset(int dte)
        => Assert.Equal(DteBucketClassifier.Other, DteBucketClassifier.Classify(dte));
}
