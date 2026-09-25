using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class AdaptiveWindowAnalysisTests
{
    static readonly DateOnly Day = new(2026, 9, 8);

    static FutureEventBar Bar(int id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, Day, start, end, 100m, 100m, 100m, 100m, 2600, null, null, 1, false);

    static DateTimeOffset T(int seconds) => new DateTimeOffset(2026, 9, 8, 9, 15, 0, TimeSpan.FromHours(5.5)).AddSeconds(seconds);

    [Fact]
    public void FindWindowStartIndex_WalksBackUntilTargetElapsedReached()
    {
        // Each bar is 60s long; target 180s should require going back 3 bars (indices 2,1,0 -> start=0).
        var bars = new List<FutureEventBar>
        {
            Bar(0, T(0), T(60)), Bar(1, T(60), T(120)), Bar(2, T(120), T(180)), Bar(3, T(180), T(240)),
        };
        var start = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, endIdx: 3, targetSeconds: 180);
        Assert.Equal(1, start); // bars[1].Start(60s) -> bars[3].End(240s) = 180s, first satisfied at bar 1.
    }

    [Fact]
    public void FindWindowStartIndex_OneVerySlowBarAloneCanSatisfyTarget()
    {
        var bars = new List<FutureEventBar> { Bar(0, T(0), T(300)) }; // single 300s bar.
        var start = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, endIdx: 0, targetSeconds: 180);
        Assert.Equal(0, start); // WindowBarCount = 1, valid per spec.
    }

    [Fact]
    public void FindWindowStartIndex_NeverLooksPastEndIdx()
    {
        // A huge bar AFTER endIdx must not affect the result (no look-ahead).
        var bars = new List<FutureEventBar>
        {
            Bar(0, T(0), T(10)), Bar(1, T(10), T(20)), Bar(2, T(20), T(10000)),
        };
        var start = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, endIdx: 1, targetSeconds: 180);
        Assert.Equal(0, start); // clamped at bar 0 (only 20s total available at or before endIdx=1), future bar 2 ignored.
    }

    [Fact]
    public void FindWindowStartIndex_ClampsAtZero_WhenNotEnoughHistory()
    {
        var bars = new List<FutureEventBar> { Bar(0, T(0), T(10)), Bar(1, T(10), T(20)) };
        var start = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, endIdx: 1, targetSeconds: 180);
        Assert.Equal(0, start);
    }
}
