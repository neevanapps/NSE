using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22, Experiment 5 (docs/VOLUME_BAR_FINDINGS.md's dated "Experiment 5" section): coverage
/// for <see cref="Experiment5UnderlyingAnalyzer.CollectByState"/>/<see cref="Experiment5UnderlyingAnalyzer.Collect"/>
/// and <see cref="Experiment5UnderlyingAnalyzer.SummarizeUnderlying"/> -- a small synthetic day with a
/// known qualifying bar and a known forward-price path, verifying the state-selection logic reuses
/// Experiment 3's threshold unchanged and the forward-outcome arithmetic (fwd return, MFE/MAE,
/// bars-to-extreme, reversal probability) matches hand computation.
/// </summary>
public class Experiment5UnderlyingAnalyzerTests
{
    static VolumeBarRow Bar(int index, decimal open, decimal high, decimal low, decimal close, int tickCount, double durationSeconds) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = index,
        BarVolumeThreshold = 2600,
        StartTimestamp = new DateTimeOffset(2026, 9, 8, 4, 0, index, TimeSpan.Zero),
        EndTimestamp = new DateTimeOffset(2026, 9, 8, 4, 0, index + 1, TimeSpan.Zero),
        OpenPrice = open,
        HighPrice = high,
        LowPrice = low,
        ClosePrice = close,
        Volume = 2600,
        TickCount = tickCount,
        DurationSeconds = durationSeconds,
    };

    [Fact]
    public void CollectByState_SelectsOnlyBarsMatchingTheRequestedActivityEfficiencyCell()
    {
        // Bar 0: high tick count (dense=fast velocity) + big net move (high PriceEfficiency=|move|/ticks... )
        // Constructed so bar 0 is clearly HighAct+HighEff, bar 1 is clearly LowAct+LowEff (few ticks,
        // long duration, tiny move) -- a pooled 2-bar threshold splits exactly down the middle.
        var bars = new List<VolumeBarRow>
        {
            Bar(0, open: 100m, high: 102m, low: 99m, close: 102m, tickCount: 50, durationSeconds: 10), // fast, big move -> high velocity, high efficiency
            Bar(1, open: 200m, high: 200.1m, low: 199.9m, close: 200m, tickCount: 5, durationSeconds: 300), // slow, flat -> low velocity, low efficiency (Direction==0 too, excluded either way)
            Bar(2, open: 300m, high: 301m, low: 299m, close: 301m, tickCount: 4, durationSeconds: 5), // n/a for horizon math, just padding
        };

        var samples = TickActivityAnalyzer.CollectDay(bars);
        var threshold = TickActivityAnalyzer.ComputeThreshold(samples);

        var stateD = Experiment5UnderlyingAnalyzer.CollectByState(bars, threshold, new Dictionary<int, OptionAtmBarRow>(), dte: 5, isZeroDte: false, requireHighActivity: true, requireHighEfficiency: true);

        Assert.Contains(stateD, q => q.BarIndex == 0);
        Assert.DoesNotContain(stateD, q => q.BarIndex == 1); // Direction==0, excluded from every cell
    }

    [Fact]
    public void Collect_IsEquivalentToCollectByStateWithBothFlagsTrue()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, open: 100m, high: 102m, low: 99m, close: 102m, tickCount: 50, durationSeconds: 10),
            Bar(1, open: 200m, high: 202m, low: 199m, close: 201m, tickCount: 40, durationSeconds: 12),
        };
        var samples = TickActivityAnalyzer.CollectDay(bars);
        var threshold = TickActivityAnalyzer.ComputeThreshold(samples);

        var viaCollect = Experiment5UnderlyingAnalyzer.Collect(bars, threshold, new Dictionary<int, OptionAtmBarRow>(), 3, false);
        var viaState = Experiment5UnderlyingAnalyzer.CollectByState(bars, threshold, new Dictionary<int, OptionAtmBarRow>(), 3, false, true, true);

        Assert.Equal(viaCollect.Select(q => q.BarIndex), viaState.Select(q => q.BarIndex));
    }

    [Fact]
    public void QualifyingBar_ForwardOutcomes_MatchHandComputedMfeMaeAndBarsToExtreme()
    {
        // Signal bar closes at 100. Horizon=2: bar1 High=103/Low=99, bar2 High=101/Low=97.
        // Best high over the 2-bar window is 103 (offset 1) -> MFE=(103-100)/100=3%.
        // Worst low is 97 (offset 2) -> MAE=(97-100)/100=-3%. Fwd return uses bar2's close.
        var bars = new List<VolumeBarRow>
        {
            Bar(0, open: 98m, high: 100m, low: 98m, close: 100m, tickCount: 50, durationSeconds: 10), // Direction>0 (close>open) -> pos cell, expects reversal DOWN
            Bar(1, open: 100m, high: 103m, low: 99m, close: 102m, tickCount: 10, durationSeconds: 20),
            Bar(2, open: 102m, high: 101m, low: 97m, close: 96m, tickCount: 10, durationSeconds: 20),
        };

        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0); // everything qualifies as "high" against a zero floor
        var atm = new Dictionary<int, OptionAtmBarRow>();
        var qualifying = Experiment5UnderlyingAnalyzer.CollectByState(bars, threshold, atm, dte: 0, isZeroDte: true, requireHighActivity: true, requireHighEfficiency: true);

        var signalBar = Assert.Single(qualifying, q => q.BarIndex == 0);
        Assert.Equal(1, signalBar.Direction); // closed up

        var h2 = signalBar.Underlying.Single(u => u.Horizon == 2);
        Assert.Equal(3.0, h2.Mfe!.Value * 100.0, 6);
        Assert.Equal(-3.0, h2.Mae!.Value * 100.0, 6);
        Assert.Equal(1, h2.BarsToMfe);
        Assert.Equal(2, h2.BarsToMae);
        Assert.Equal(-4.0, h2.FwdReturn!.Value * 100.0, 6); // (96-100)/100

        // pos cell expects reversal DOWN -> favorable excursion is -MAE = +3%, adverse is MFE = +3% too here (coincidental symmetry in this fixture).
        Assert.Equal(3.0, signalBar.ReversalFavorableExcursion(2)!.Value * 100.0, 6);
        Assert.True(signalBar.Reversed(2) == true); // fwd return is negative, matches the expected DOWN reversal
    }

    [Fact]
    public void SummarizeUnderlying_AveragesAndProbUpMatchHandComputation()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, open: 98m, high: 100m, low: 98m, close: 100m, tickCount: 50, durationSeconds: 10), // pos cell, day 1
            Bar(1, open: 100m, high: 101m, low: 95m, close: 96m, tickCount: 10, durationSeconds: 20), // fwd(1) = -4%
        };

        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0);
        var qualifying = Experiment5UnderlyingAnalyzer.CollectByState(bars, threshold, new Dictionary<int, OptionAtmBarRow>(), 0, false, true, true);
        var pos = qualifying.Where(q => q.Direction > 0).ToList();

        var stats = Experiment5UnderlyingAnalyzer.SummarizeUnderlying(pos);
        var h1 = stats.Single(s => s.Horizon == 1);

        Assert.Equal(1, h1.N);
        Assert.Equal(-4.0, h1.MeanFwdPct!.Value, 3);
        Assert.Equal(0.0, h1.ProbUpPct!.Value, 3); // the only sample was down
        Assert.Equal(100.0, h1.ReversalProbPct!.Value, 3); // pos cell expects down, and it went down -> 100% reversed
    }
}
