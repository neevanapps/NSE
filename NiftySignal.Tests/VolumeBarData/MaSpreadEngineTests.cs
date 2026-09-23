using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 SMA-vs-EMA option-premium research task (docs/VOLUME_BAR_FINDINGS.md's dated
/// section, Parts 9-15): coverage for <see cref="MaSpreadEngine"/>, the pure, DB-free fast/slow
/// SMA/EMA-spread engine this task's Call/Put research is built on. Same synthetic-series,
/// hand-computed-expected-value discipline as <see cref="PriceCrossoverEngineTests"/>.
/// </summary>
public class MaSpreadEngineTests
{
    [Fact]
    public void SmaSma_MatchesPriceCrossoverEngine_ForSameInputs()
    {
        // Both fast=slow=Sma should reproduce PriceCrossoverEngine's own SMA/SMA math exactly.
        var maEngine = new MaSpreadEngine(fastBars: 2, slowBars: 4, MaType.Sma, MaType.Sma);
        var priceEngine = new PriceCrossoverEngine(fastBars: 2, slowBars: 4);

        double[] prices = [100.0, 102.0, 101.0, 105.0, 107.0, 103.0];
        foreach (var p in prices)
        {
            var maStep = maEngine.Observe(p);
            var priceStep = priceEngine.Observe(p, thresholdFraction: 0.0);
            Assert.Equal(priceStep.FastMa, maStep.FastMa);
            Assert.Equal(priceStep.SlowMa, maStep.SlowMa);
            Assert.Equal(priceStep.DiffFraction, maStep.DiffFraction);
        }
    }

    [Fact]
    public void Observe_ReturnsNull_UntilSlowWindowFull_BothLegsEma()
    {
        var engine = new MaSpreadEngine(fastBars: 2, slowBars: 4, MaType.Ema, MaType.Ema);
        Assert.Null(engine.Observe(10.0).DiffFraction);
        Assert.Null(engine.Observe(10.0).DiffFraction);
        Assert.Null(engine.Observe(10.0).DiffFraction);
        Assert.NotNull(engine.Observe(10.0).DiffFraction); // 4th real price fills the slow window
    }

    [Fact]
    public void EmaEma_SeedsAtSmaOfWarmupWindow_ThenRecurses()
    {
        var engine = new MaSpreadEngine(fastBars: 2, slowBars: 3, MaType.Ema, MaType.Ema);
        engine.Observe(10.0);
        engine.Observe(20.0);
        var seedStep = engine.Observe(30.0); // window now [10,20,30], both EMAs seed as their SMA

        Assert.Equal(25.0, seedStep.FastMa); // SMA of last 2: (20+30)/2
        Assert.Equal(20.0, seedStep.SlowMa); // SMA of all 3: (10+20+30)/3

        // Next observation applies the real EMA recursion: alpha=2/(N+1).
        var nextStep = engine.Observe(40.0);
        var expectedFastEma = (40.0 - 25.0) * (2.0 / 3.0) + 25.0; // fastBars=2 -> alpha=2/3
        var expectedSlowEma = (40.0 - 20.0) * (2.0 / 4.0) + 20.0; // slowBars=3 -> alpha=2/4
        Assert.Equal(expectedFastEma, nextStep.FastMa!.Value, precision: 10);
        Assert.Equal(expectedSlowEma, nextStep.SlowMa!.Value, precision: 10);
    }

    [Fact]
    public void EmaFastSmaSlow_MixesLegsIndependently()
    {
        var engine = new MaSpreadEngine(fastBars: 2, slowBars: 3, MaType.Ema, MaType.Sma);
        engine.Observe(10.0);
        engine.Observe(20.0);
        engine.Observe(30.0); // seed bar: fastEma seeds at 25.0, slowSma = 20.0

        var step = engine.Observe(40.0);
        var expectedFastEma = (40.0 - 25.0) * (2.0 / 3.0) + 25.0;
        var expectedSlowSma = (20.0 + 30.0 + 40.0) / 3.0; // plain rolling SMA, no EMA state
        Assert.Equal(expectedFastEma, step.FastMa!.Value, precision: 10);
        Assert.Equal(expectedSlowSma, step.SlowMa);
    }

    [Fact]
    public void NullPrice_LeavesStateUntouched_NeverFabricatesAReading()
    {
        var engine = new MaSpreadEngine(fastBars: 2, slowBars: 3, MaType.Ema, MaType.Ema);
        engine.Observe(10.0);
        engine.Observe(10.0);
        var missing = engine.Observe(null);
        Assert.Null(missing.DiffFraction);

        var recovered = engine.Observe(10.0); // window still needs exactly one more real price
        Assert.NotNull(recovered.DiffFraction);
    }

    [Fact]
    public void FastBarsGreaterOrEqualSlowBars_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaSpreadEngine(fastBars: 5, slowBars: 5, MaType.Sma, MaType.Sma));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MaSpreadEngine(fastBars: 6, slowBars: 5, MaType.Sma, MaType.Sma));
    }
}
