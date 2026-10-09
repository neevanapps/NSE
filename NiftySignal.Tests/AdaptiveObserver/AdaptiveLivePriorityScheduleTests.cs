using NiftySignal.Host;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>Ended-session recovery must never run inside the live window; it runs pre-market and at/after the 15:35 hard close.</summary>
public class AdaptiveLivePriorityScheduleTests
{
    [Theory]
    [InlineData(9, 15)] [InlineData(9, 16)] [InlineData(10, 0)] [InlineData(15, 34)]
    public void WeekdayLiveWindow_IsLive_SoEndedSessionRecoveryIsSkipped(int h, int m) =>
        Assert.True(AdaptiveObserverWorker.IsLiveMarketWindow(true, new TimeOnly(h, m)));

    [Theory]
    [InlineData(0, 0)] [InlineData(8, 23)] [InlineData(9, 14)]
    public void PreMarket_IsOutsideLiveWindow_SoRecoveryRuns(int h, int m) =>
        Assert.False(AdaptiveObserverWorker.IsLiveMarketWindow(true, new TimeOnly(h, m)));

    [Theory]
    [InlineData(15, 35)] [InlineData(15, 36)] [InlineData(23, 59)]
    public void PostMarket_IsOutsideLiveWindow_SoHardCloseAndRecoveryRun(int h, int m) =>
        Assert.False(AdaptiveObserverWorker.IsLiveMarketWindow(true, new TimeOnly(h, m)));

    [Fact]
    public void Weekend_IsNeverLive() =>
        Assert.False(AdaptiveObserverWorker.IsLiveMarketWindow(false, new TimeOnly(11, 0)));
}
