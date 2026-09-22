using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 Part A score-smoothing task (docs/VOLUME_BAR_FINDINGS.md): coverage for
/// <see cref="ExponentialMean"/> against deterministic synthetic score sequences with a known EMA
/// output, per the project's own "prefer a separable, unit-testable function over an untested
/// inline calculation" instruction (same discipline as <c>MaeMfeCalculatorTests</c>). Also covers
/// <see cref="BarCountRollingMean"/>'s null-handling convention, which
/// <see cref="ExponentialMean"/> is required to mirror exactly (same "quiet cadence" semantics).
/// </summary>
public class ExponentialMeanTests
{
    [Fact]
    public void Observe_FirstValue_SeedsDirectly()
    {
        var ema = new ExponentialMean(3);

        var result = ema.Observe(10.0);

        Assert.Equal(10.0, result);
    }

    [Fact]
    public void Observe_StandardFormula_AlphaIsTwoOverLengthPlusOne()
    {
        // length=3 -> alpha = 2/(3+1) = 0.5. Seed=10, next observation=20:
        // EMA = 0.5*20 + 0.5*10 = 15.
        var ema = new ExponentialMean(3);
        ema.Observe(10.0);

        var result = ema.Observe(20.0);

        Assert.Equal(15.0, result!.Value, precision: 10);
    }

    [Fact]
    public void Observe_ThreeObservations_MatchesHandComputedEma()
    {
        // length=4 -> alpha = 2/5 = 0.4.
        // seed=100 -> 100
        // obs=110 -> 0.4*110 + 0.6*100 = 104
        // obs=90  -> 0.4*90 + 0.6*104 = 98.4
        var ema = new ExponentialMean(4);

        Assert.Equal(100.0, ema.Observe(100.0));
        Assert.Equal(104.0, ema.Observe(110.0)!.Value, precision: 10);
        Assert.Equal(98.4, ema.Observe(90.0)!.Value, precision: 10);
    }

    [Fact]
    public void Observe_NullValue_ReturnsNullAndLeavesStateUntouched()
    {
        // Same "quiet cadence" convention every other smoother in TradeSimulator.cs follows: a
        // missing bar reading yields a null smoothed score, not a fabricated one from stale state.
        var ema = new ExponentialMean(3);
        ema.Observe(10.0);

        var duringGap = ema.Observe(null);
        // State must be untouched by the gap -- the next real observation continues from the
        // pre-gap EMA value exactly as if the null bar never happened.
        var afterGap = ema.Observe(20.0);

        Assert.Null(duringGap);
        Assert.Equal(15.0, afterGap!.Value, precision: 10); // same as the no-gap case above.
    }

    [Fact]
    public void Observe_NullBeforeAnyRealValue_ReturnsNull()
    {
        var ema = new ExponentialMean(5);

        var result = ema.Observe(null);

        Assert.Null(result);
    }

    [Fact]
    public void BarCountRollingMean_Observe_MatchesHandComputedSma()
    {
        // window=3: [10] -> 10, [10,20] -> 15, [10,20,30] -> 20, [20,30,40] -> 30 (10 drops off).
        var sma = new BarCountRollingMean(3);

        Assert.Equal(10.0, sma.Observe(10.0));
        Assert.Equal(15.0, sma.Observe(20.0));
        Assert.Equal(20.0, sma.Observe(30.0));
        Assert.Equal(30.0, sma.Observe(40.0));
    }

    [Fact]
    public void BarCountRollingMean_Observe_NullValue_ReturnsNullAndDoesNotEnqueue()
    {
        var sma = new BarCountRollingMean(2);
        sma.Observe(10.0);

        var duringGap = sma.Observe(null);
        var afterGap = sma.Observe(20.0);

        Assert.Null(duringGap);
        // The gap contributed nothing to the window -- this is still just the 2-value mean of
        // [10, 20], not diluted by a fabricated null-as-zero entry.
        Assert.Equal(15.0, afterGap);
    }
}
