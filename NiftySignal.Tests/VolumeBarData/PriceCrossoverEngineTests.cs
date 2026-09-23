using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 option-price moving-average-crossover research task (docs/VOLUME_BAR_FINDINGS.md's
/// dated section): coverage for <see cref="PriceCrossoverEngine"/>, the pure, DB-free fast/slow
/// SMA-crossover detector this task's simulator method is built on. Synthetic price series with
/// hand-computed expected SMAs/crossings, same "prefer a separable, unit-testable function"
/// discipline this project's other pure-calculator tests already establish.
/// </summary>
public class PriceCrossoverEngineTests
{
    [Fact]
    public void Observe_ReturnsNullMas_UntilSlowWindowIsFull()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);

        var s1 = engine.Observe(100.0, thresholdFraction: 0.0);
        var s2 = engine.Observe(100.0, thresholdFraction: 0.0);
        var s3 = engine.Observe(100.0, thresholdFraction: 0.0);

        Assert.Null(s1.FastMa);
        Assert.Null(s2.FastMa);
        Assert.Null(s3.FastMa);

        var s4 = engine.Observe(100.0, thresholdFraction: 0.0);
        Assert.NotNull(s4.FastMa);
        Assert.NotNull(s4.SlowMa);
    }

    [Fact]
    public void Observe_NullPrice_LeavesWindowUntouched_NeverFabricatesAReading()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 3);
        engine.Observe(10.0, 0.0);
        engine.Observe(10.0, 0.0);
        var missing = engine.Observe(null, 0.0);
        Assert.Null(missing.FastMa);

        // The window still only has 2 real readings (the null bar didn't advance it), so one more
        // real bar should complete the slow window exactly as if the null bar never happened.
        var next = engine.Observe(10.0, 0.0);
        Assert.NotNull(next.FastMa);
    }

    [Fact]
    public void Observe_DetectsCrossUp_WhenFastMovesAboveSlowByAtLeastThreshold()
    {
        // fast=2, slow=4. Feed a flat run of 100s to fill the window with a zero diff, then a jump
        // in the most recent 2 bars pulls the fast MA above the slow MA.
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);
        engine.Observe(100.0, 0.0); // window: [100]
        engine.Observe(100.0, 0.0); // window: [100,100]
        engine.Observe(100.0, 0.0); // window: [100,100,100]
        var flat = engine.Observe(100.0, 0.0); // window full: fast=100, slow=100, diff=0 -> primes previousDiff=0
        Assert.Equal(0.0, flat.DiffFraction);
        Assert.False(flat.CrossedUp);

        var jumpUp = engine.Observe(120.0, thresholdFraction: 0.01); // window: [100,100,100,120]
        // fast = avg(100,120) = 110, slow = avg(100,100,100,120) = 105 -> diff = 5/105 ≈ 4.76%
        Assert.True(jumpUp.CrossedUp);
        Assert.False(jumpUp.CrossedDown);
    }

    [Fact]
    public void Observe_DetectsCrossDown_WhenFastMovesBelowSlowByAtLeastThreshold()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0); // primes previousDiff=0

        var jumpDown = engine.Observe(80.0, thresholdFraction: 0.01); // fast=90, slow=95, diff≈-5.26%
        Assert.True(jumpDown.CrossedDown);
        Assert.False(jumpDown.CrossedUp);
    }

    [Fact]
    public void Observe_DoesNotQualifyCrossing_WhenGapIsBelowThreshold()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0); // primes previousDiff=0

        // Same jump as above but with a threshold higher than the ~4.76%/5.26% actual gap -- no
        // crossing should be reported even though the sign genuinely flipped.
        var tinyMove = engine.Observe(101.0, thresholdFraction: 0.5); // gap far below 50%
        Assert.False(tinyMove.CrossedUp);
        Assert.False(tinyMove.CrossedDown);
    }

    [Fact]
    public void Observe_NoCrossing_WhenDiffStaysOnSameSide()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(110.0, 0.0);
        var first = engine.Observe(120.0, thresholdFraction: 0.0); // fast > slow already here
        Assert.True(first.DiffFraction > 0);

        // Another bar that keeps fast above slow (same sign) must not re-fire CrossedUp.
        var second = engine.Observe(125.0, thresholdFraction: 0.0);
        Assert.True(second.DiffFraction > 0);
        Assert.False(second.CrossedUp);
    }

    [Fact]
    public void Constructor_RejectsFastBarsNotLessThanSlowBars()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceCrossoverEngine(fastBars: 4, slowBars: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceCrossoverEngine(fastBars: 5, slowBars: 4));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveFastBars()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceCrossoverEngine(fastBars: 0, slowBars: 4));
    }
}
