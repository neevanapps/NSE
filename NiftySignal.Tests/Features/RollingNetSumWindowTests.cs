using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class RollingNetSumWindowTests
{
    [Fact]
    public void FreshWindow_IsNotWarmedUp_AndSumIsZero()
    {
        var window = new RollingNetSumWindow(TimeSpan.FromMinutes(5));
        Assert.False(window.IsWarmedUp);
        Assert.Equal(0, window.Sum);
    }

    [Fact]
    public void NotWarmedUp_UntilRealElapsedTimeReachesTheFullWindow()
    {
        var window = new RollingNetSumWindow(TimeSpan.FromMinutes(5));
        var t0 = new DateTimeOffset(2026, 9, 11, 9, 15, 0, TimeSpan.Zero);

        window.Add(t0, 100);
        window.Add(t0.AddMinutes(4), -50);
        Assert.False(window.IsWarmedUp); // only 4 minutes have elapsed so far

        window.Add(t0.AddMinutes(5), 25);
        Assert.True(window.IsWarmedUp); // now spans the full 5-minute window
    }

    [Fact]
    public void Sum_AddsPositiveAndNegativeValuesCorrectly()
    {
        var window = new RollingNetSumWindow(TimeSpan.FromMinutes(5));
        var t0 = new DateTimeOffset(2026, 9, 11, 9, 15, 0, TimeSpan.Zero);

        window.Add(t0, 4290);
        window.Add(t0.AddSeconds(15), -1040);
        window.Add(t0.AddSeconds(30), 500);

        Assert.Equal(4290 - 1040 + 500, window.Sum);
    }

    [Fact]
    public void OldValues_AreEvictedOnceTheyAgeOutOfTheWindow()
    {
        var window = new RollingNetSumWindow(TimeSpan.FromMinutes(5));
        var t0 = new DateTimeOffset(2026, 9, 11, 9, 15, 0, TimeSpan.Zero);

        window.Add(t0, 1000); // this one should age out
        window.Add(t0.AddMinutes(6), 200); // still within 5 minutes of the next add

        // At t0+6min, only the second value is within the trailing 5-minute window --
        // the first (at t0) is now 6 minutes old and must have been evicted.
        Assert.Equal(200, window.Sum);
    }
}
