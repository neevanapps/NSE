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

    // 2026-09-23 EMA support. Same hand-computed-expectation discipline as the SMA tests above.
    [Fact]
    public void Observe_DefaultsToSmaSma_SoExistingCallersAreUnaffectedByEmaSupport()
    {
        var withoutType = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);
        var explicitSma = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Sma, MaType.Sma);
        double[] prices = [100.0, 100.0, 100.0, 100.0, 120.0, 90.0];

        foreach (var p in prices)
        {
            var a = withoutType.Observe(p, thresholdFraction: 0.01);
            var b = explicitSma.Observe(p, thresholdFraction: 0.01);
            Assert.Equal(a.FastMa, b.FastMa);
            Assert.Equal(a.SlowMa, b.SlowMa);
            Assert.Equal(a.DiffFraction, b.DiffFraction);
            Assert.Equal(a.CrossedUp, b.CrossedUp);
            Assert.Equal(a.CrossedDown, b.CrossedDown);
        }
    }

    [Fact]
    public void Observe_EmaLeg_SeedsAtSlowWindowFill_WithSmaValue()
    {
        // fast=2, slow=4, both EMA. At the seeding bar (window first reaches slowBars=4 real
        // prices), both EMA legs must equal the plain SMA over their own window -- the documented
        // seeding convention (no recursion applied on the seeding bar itself).
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Ema);
        engine.Observe(10.0, 0.0);
        engine.Observe(20.0, 0.0);
        engine.Observe(30.0, 0.0);
        var seed = engine.Observe(40.0, 0.0); // window: [10,20,30,40]

        Assert.Equal(35.0, seed.FastMa); // avg(30,40)
        Assert.Equal(25.0, seed.SlowMa); // avg(10,20,30,40)
    }

    [Fact]
    public void Observe_EmaLeg_AppliesStandardAlphaRecursion_AfterSeeding()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Ema);
        engine.Observe(10.0, 0.0);
        engine.Observe(20.0, 0.0);
        engine.Observe(30.0, 0.0);
        engine.Observe(40.0, 0.0); // seed: fastEma=35, slowEma=25

        var next = engine.Observe(50.0, 0.0);
        // alphaFast = 2/(2+1) = 0.6667: fastEma = (50-35)*0.6667 + 35 = 45.0
        // alphaSlow = 2/(4+1) = 0.4:    slowEma = (50-25)*0.4 + 25 = 35.0
        Assert.Equal(45.0, next.FastMa!.Value, precision: 6);
        Assert.Equal(35.0, next.SlowMa!.Value, precision: 6);
    }

    [Fact]
    public void Observe_EmaFastVsSmaSlow_DivergesFromPureSma_OnTheSameRealPriceSeries()
    {
        // A jump right after seeding should move the EMA fast leg by a different amount than the
        // SMA fast leg would, since EMA weights the newest price more heavily -- proves the two
        // MaType branches are genuinely independent code paths, not just re-labeled SMA.
        var smaEngine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Sma, MaType.Sma);
        var emaEngine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Sma);
        double[] prices = [100.0, 100.0, 100.0, 100.0, 130.0];

        PriceCrossoverEngine.Step smaStep = default, emaStep = default;
        foreach (var p in prices)
        {
            smaStep = smaEngine.Observe(p, 0.0);
            emaStep = emaEngine.Observe(p, 0.0);
        }

        Assert.Equal(115.0, smaStep.FastMa); // avg(100,130)
        Assert.Equal(120.0, emaStep.FastMa!.Value, precision: 6); // (130-100)*(2/3)+100
        Assert.NotEqual(smaStep.FastMa, emaStep.FastMa);
        // Slow leg (SMA on both engines) must be identical.
        Assert.Equal(smaStep.SlowMa, emaStep.SlowMa);
    }

    // 2026-09-23 correctness-review fix (docs/Price_Based_Findings.md): Reset() must let the engine
    // start fresh on a new instrument's price series without ever averaging it against the prior
    // instrument's history.
    [Fact]
    public void Reset_ClearsWindowAndEmaState_SoNextObserveStartsFreshWarmUp()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Ema);
        engine.Observe(10.0, 0.0);
        engine.Observe(20.0, 0.0);
        engine.Observe(30.0, 0.0);
        var seeded = engine.Observe(40.0, 0.0);
        Assert.NotNull(seeded.FastMa); // fully warmed up before reset

        engine.Reset();

        // Immediately after reset, same "no fabricated reading until slowBars real prices" warm-up
        // discipline as a brand-new engine -- three prices are not enough.
        engine.Observe(999.0, 0.0);
        engine.Observe(999.0, 0.0);
        var stillWarmingUp = engine.Observe(999.0, 0.0);
        Assert.Null(stillWarmingUp.FastMa);
    }

    [Fact]
    public void Reset_ThenFullRewarm_MatchesABrandNewEngineObservingTheSamePricesFromScratch()
    {
        var reused = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Sma, MaType.Sma);
        reused.Observe(500.0, 0.0); // pollutes state that Reset must fully erase
        reused.Observe(500.0, 0.0);
        reused.Observe(500.0, 0.0);
        reused.Observe(500.0, 0.0);
        reused.Reset();

        var fresh = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Sma, MaType.Sma);
        double[] prices = [10.0, 20.0, 30.0, 40.0, 130.0];

        PriceCrossoverEngine.Step reusedStep = default, freshStep = default;
        foreach (var p in prices)
        {
            reusedStep = reused.Observe(p, thresholdFraction: 0.01);
            freshStep = fresh.Observe(p, thresholdFraction: 0.01);
        }

        Assert.Equal(freshStep.FastMa, reusedStep.FastMa);
        Assert.Equal(freshStep.SlowMa, reusedStep.SlowMa);
        Assert.Equal(freshStep.DiffFraction, reusedStep.DiffFraction);
        Assert.Equal(freshStep.CrossedUp, reusedStep.CrossedUp);
    }

    [Fact]
    public void Observe_EmaLeg_StillDetectsCrossings_UsingSameThresholdRule()
    {
        var engine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Ema);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        engine.Observe(100.0, 0.0);
        var flat = engine.Observe(100.0, 0.0); // primes previousDiff=0
        Assert.Equal(0.0, flat.DiffFraction);

        var jumpUp = engine.Observe(200.0, thresholdFraction: 0.01);
        Assert.True(jumpUp.CrossedUp);
        Assert.False(jumpUp.CrossedDown);
    }
}
