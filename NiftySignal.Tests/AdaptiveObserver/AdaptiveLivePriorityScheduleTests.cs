using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Old ended-session repair runs only on weekends or on a weekday at/after the 15:35 hard close, never on a weekday morning or in market hours
/// (the awaited reconstruction is not preemptible). In ExecuteAsync the outside-market branch closes the live session at/after 15:35 first,
/// then calls the finalizer only when <see cref="AdaptiveObserverWorker.ShouldRunEndedSessionRecovery"/> is true.
/// </summary>
public class AdaptiveLivePriorityScheduleTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(8, 0)] [InlineData(9, 14)]
    [InlineData(9, 15)] [InlineData(10, 0)] [InlineData(15, 34)]
    public void WeekdayBeforeHardClose_DoesNotRunEndedSessionRecovery(int h, int m) =>
        Assert.False(AdaptiveObserverWorker.ShouldRunEndedSessionRecovery(true, new TimeOnly(h, m)));

    [Theory]
    [InlineData(15, 35)] [InlineData(15, 36)] [InlineData(23, 59)]
    public void WeekdayAtOrAfterHardClose_RunsEndedSessionRecovery(int h, int m) =>
        Assert.True(AdaptiveObserverWorker.ShouldRunEndedSessionRecovery(true, new TimeOnly(h, m)));

    [Theory]
    [InlineData(8, 0)] [InlineData(12, 0)] [InlineData(20, 0)]
    public void Weekend_RunsEndedSessionRecovery(int h, int m) =>
        Assert.True(AdaptiveObserverWorker.ShouldRunEndedSessionRecovery(false, new TimeOnly(h, m)));

    [Theory]
    [InlineData(9, 14, false)] [InlineData(9, 15, true)] [InlineData(15, 34, true)] [InlineData(15, 35, false)]
    public void LiveMarketWindow_IsWeekdayHalfOpenInterval(int h, int m, bool live) =>
        Assert.Equal(live, AdaptiveObserverWorker.IsLiveMarketWindow(true, new TimeOnly(h, m)));

    [Fact]
    public void LiveMarketWindow_NeverOnWeekend() =>
        Assert.False(AdaptiveObserverWorker.IsLiveMarketWindow(false, new TimeOnly(11, 0)));

    [Fact]
    public void RecoveryAndLiveWindows_NeverOverlap()
    {
        foreach (var weekday in new[] { true, false })
            for (var minute = 0; minute < 24 * 60; minute++)
            {
                var t = new TimeOnly(minute / 60, minute % 60);
                Assert.False(AdaptiveObserverWorker.IsLiveMarketWindow(weekday, t) && AdaptiveObserverWorker.ShouldRunEndedSessionRecovery(weekday, t));
            }
    }
}
