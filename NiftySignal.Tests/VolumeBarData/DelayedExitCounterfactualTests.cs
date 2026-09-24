using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class DelayedExitCounterfactualTests
{
    static readonly DateTimeOffset BaselineExit = new(2026, 9, 8, 9, 30, 0, TimeSpan.FromHours(5.5));
    static readonly DateTimeOffset MandatoryCutoff = new(2026, 9, 8, 15, 15, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void ComputeAlternativeExitTarget_AddsTheDelayWhenWellBeforeCutoff()
        => Assert.Equal(BaselineExit.AddMinutes(5), DelayedExitCounterfactual.ComputeAlternativeExitTarget(BaselineExit, TimeSpan.FromMinutes(5), MandatoryCutoff));

    [Fact]
    public void ComputeAlternativeExitTarget_CapsAtMandatoryCutoff_WhenDelayWouldExceedIt()
    {
        var lateBaselineExit = MandatoryCutoff.AddMinutes(-3);
        var result = DelayedExitCounterfactual.ComputeAlternativeExitTarget(lateBaselineExit, TimeSpan.FromMinutes(10), MandatoryCutoff);
        Assert.Equal(MandatoryCutoff, result);
    }

    [Fact]
    public void ComputeAlternativeExitTarget_NeverExceedsCutoff_EvenAtExactBoundary()
    {
        var result = DelayedExitCounterfactual.ComputeAlternativeExitTarget(MandatoryCutoff, TimeSpan.FromMinutes(1), MandatoryCutoff);
        Assert.Equal(MandatoryCutoff, result);
    }

    [Theory]
    [InlineData(5.0, "Continuation")]
    [InlineData(-5.0, "Reversal")]
    [InlineData(0.0, "Flat")]
    [InlineData(null, "Unavailable")]
    public void ClassifyContinuationVsReversal_IsAZeroThresholdSignSplit(double? postExitReturn, string expected)
        => Assert.Equal(expected, DelayedExitCounterfactual.ClassifyContinuationVsReversal((decimal?)postExitReturn));

    [Fact]
    public void MinutesSinceSessionOpen_ComputesElapsedMinutesFrom0915Ist()
    {
        var ist = TimeSpan.FromHours(5.5);
        var timestamp = new DateTimeOffset(2026, 9, 8, 9, 45, 0, ist);
        Assert.Equal(30.0, DelayedExitCounterfactual.MinutesSinceSessionOpen(timestamp, ist));
    }

    [Fact]
    public void MinutesSinceSessionOpen_AtSessionOpen_IsZero()
    {
        var ist = TimeSpan.FromHours(5.5);
        var timestamp = new DateTimeOffset(2026, 9, 8, 9, 15, 0, ist);
        Assert.Equal(0.0, DelayedExitCounterfactual.MinutesSinceSessionOpen(timestamp, ist));
    }

    [Theory]
    [InlineData(-100, 50, "LossToWin")]
    [InlineData(-100, -50, "LossToLoss")]
    [InlineData(100, 50, "WinToWin")]
    [InlineData(100, -50, "WinToLoss")]
    [InlineData(0, 50, "LossToWin")]   // NetPnl == 0 is treated as a loss (not a win), consistent with ">0" being the win convention used throughout.
    public void ClassifyOutcomeTransition_UsesNetPnlGreaterThanZeroAsWin(decimal baseline, decimal alternative, string expected)
        => Assert.Equal(expected, DelayedExitCounterfactual.ClassifyOutcomeTransition(baseline, alternative));
}
