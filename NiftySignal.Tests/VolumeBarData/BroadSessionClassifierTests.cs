using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class BroadSessionClassifierTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static DateTimeOffset At(int hour, int minute) => new(2026, 9, 8, hour, minute, 0, Ist);

    [Theory]
    [InlineData(9, 15, "Morning (09:15-12:00)")]
    [InlineData(11, 59, "Morning (09:15-12:00)")]
    [InlineData(12, 0, "Midday (12:00-14:00)")]
    [InlineData(13, 59, "Midday (12:00-14:00)")]
    [InlineData(14, 0, "Afternoon (14:00-15:15)")]
    [InlineData(15, 14, "Afternoon (14:00-15:15)")]
    public void Classify_MapsTimeOfDayToTheThreeBroadGroups(int hour, int minute, string expected)
        => Assert.Equal(expected, BroadSessionClassifier.Classify(At(hour, minute), Ist));
}
