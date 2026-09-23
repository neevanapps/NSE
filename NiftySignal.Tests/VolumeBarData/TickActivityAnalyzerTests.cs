using NiftySignal.Features;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// Experiment 3, 2026-09-22 "Validating the high-activity/high-efficiency mean-reversion finding"
/// task (docs/VOLUME_BAR_FINDINGS.md's dated section): coverage for
/// <see cref="TickActivityAnalyzer.ComputeThreshold"/> and
/// <see cref="TickActivityAnalyzer.MeanReversionSplit"/>, the two new pure, DB-free methods this
/// task factored out of the original E4 code so the day/session/DTE/leave-one-out breakdowns could
/// reuse it against arbitrary slices instead of only the full pooled dataset.
/// </summary>
public class TickActivityAnalyzerTests
{
    static TickActivityAnalyzer.Sample MakeSample(
        DateOnly date, int barIndex, double tickVelocity, double priceEfficiency, int direction, double? niftyFwd10) =>
        new(
            date, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(barIndex), barIndex,
            new TickActivityFeatures.Result(
                TickDensity: 1.0, VolumePerTick: 1.0,
                TickVelocity: tickVelocity, VolumeVelocity: 1.0,
                NetMove: direction, AbsNetMove: Math.Abs(direction), PriceEfficiency: priceEfficiency, RangeEfficiency: 1.0,
                PricePerVolume: 1.0,
                Churn: 1.0,
                Direction: direction, SignedEfficiency: 1.0),
            DepthImbalance: null,
            NiftyFwd5: null, NiftyFwd10: niftyFwd10, NiftyFwd20: null,
            Mfe5: null, Mae5: null, Mfe10: null, Mae10: null, Mfe20: null, Mae20: null,
            TickDensityDelta: null, TickVelocityDelta: null, PriceEfficiencyDelta: null, ChurnDelta: null);

    [Fact]
    public void ComputeThreshold_ReturnsMedianOfEachFeature()
    {
        var date = new DateOnly(2026, 9, 22);
        var samples = new List<TickActivityAnalyzer.Sample>
        {
            MakeSample(date, 0, tickVelocity: 1, priceEfficiency: 10, direction: 1, niftyFwd10: 0.01),
            MakeSample(date, 1, tickVelocity: 2, priceEfficiency: 20, direction: 1, niftyFwd10: 0.01),
            MakeSample(date, 2, tickVelocity: 3, priceEfficiency: 30, direction: 1, niftyFwd10: 0.01),
        };

        var threshold = TickActivityAnalyzer.ComputeThreshold(samples);

        Assert.Equal(2.0, threshold.ActivityMedian);
        Assert.Equal(20.0, threshold.EfficiencyMedian);
    }

    [Fact]
    public void ComputeThreshold_EmptyPopulation_ReturnsZeroZero()
    {
        var threshold = TickActivityAnalyzer.ComputeThreshold([]);

        Assert.Equal(0.0, threshold.ActivityMedian);
        Assert.Equal(0.0, threshold.EfficiencyMedian);
    }

    [Fact]
    public void MeanReversionSplit_SplitsHighActivityHighEfficiencyBarsByNetMoveDirection()
    {
        var date = new DateOnly(2026, 9, 22);
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(ActivityMedian: 5, EfficiencyMedian: 5);
        var samples = new List<TickActivityAnalyzer.Sample>
        {
            // Below threshold on activity -- excluded from the HighAct+HighEff cell entirely.
            MakeSample(date, 0, tickVelocity: 1, priceEfficiency: 10, direction: 1, niftyFwd10: 0.05),
            // High+High, positive NetMove: one reverses down (Fwd10<0), one continues up (Fwd10>0).
            MakeSample(date, 1, tickVelocity: 10, priceEfficiency: 10, direction: 1, niftyFwd10: -0.01),
            MakeSample(date, 2, tickVelocity: 10, priceEfficiency: 10, direction: 1, niftyFwd10: -0.02),
            MakeSample(date, 3, tickVelocity: 10, priceEfficiency: 10, direction: 1, niftyFwd10: 0.01),
            // High+High, negative NetMove: two reverse up (Fwd10>0), one continues down.
            MakeSample(date, 4, tickVelocity: 10, priceEfficiency: 10, direction: -1, niftyFwd10: 0.01),
            MakeSample(date, 5, tickVelocity: 10, priceEfficiency: 10, direction: -1, niftyFwd10: 0.02),
            MakeSample(date, 6, tickVelocity: 10, priceEfficiency: 10, direction: -1, niftyFwd10: -0.01),
        };

        var stat = TickActivityAnalyzer.MeanReversionSplit(samples, "test", threshold, s => s.NiftyFwd10);

        Assert.Equal(3, stat.NPos);
        Assert.Equal(3, stat.NNeg);
        Assert.NotNull(stat.PosProbUp);
        Assert.NotNull(stat.NegProbUp);
        Assert.Equal(100.0 / 3.0, stat.PosProbUp!.Value, precision: 6); // 1 of 3 positive-NetMove bars has Fwd10>0
        Assert.Equal(200.0 / 3.0, stat.NegProbUp!.Value, precision: 6); // 2 of 3 negative-NetMove bars has Fwd10>0
    }

    [Fact]
    public void MeanReversionSplit_EmptyCell_ReturnsNullRatesNotDivideByZero()
    {
        var date = new DateOnly(2026, 9, 22);
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(ActivityMedian: 5, EfficiencyMedian: 5);
        // Only below-threshold bars -- the HighAct+HighEff cell is empty entirely.
        var samples = new List<TickActivityAnalyzer.Sample>
        {
            MakeSample(date, 0, tickVelocity: 1, priceEfficiency: 1, direction: 1, niftyFwd10: 0.01),
            MakeSample(date, 1, tickVelocity: 1, priceEfficiency: 1, direction: -1, niftyFwd10: -0.01),
        };

        var stat = TickActivityAnalyzer.MeanReversionSplit(samples, "empty", threshold, s => s.NiftyFwd10);

        Assert.Equal(0, stat.NPos);
        Assert.Equal(0, stat.NNeg);
        Assert.Null(stat.PosProbUp);
        Assert.Null(stat.NegProbUp);
        Assert.Null(stat.PosMeanFwd);
        Assert.Null(stat.NegMeanFwd);
    }
}
