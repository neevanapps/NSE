using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22, Experiment 5 (docs/VOLUME_BAR_FINDINGS.md's dated "Experiment 5" section): coverage
/// for <see cref="OptionTradeQualityStats.Compute"/> -- hand-computed expected values for win rate,
/// profit factor, expectancy, max drawdown, and top-K concentration against small synthetic P&amp;L
/// sequences, same "prefer a separable, unit-testable pure function" discipline this project's other
/// trade-quality helpers already follow.
/// </summary>
public class OptionTradeQualityStatsTests
{
    [Fact]
    public void Compute_EmptySequence_ReturnsZeroedStatsWithoutDividingByZero()
    {
        var st = OptionTradeQualityStats.Compute([]);

        Assert.Equal(0, st.Count);
        Assert.Null(st.WinRatePct);
        Assert.Null(st.ProfitFactor);
        Assert.Equal(0m, st.NetPnl);
        Assert.Equal(0m, st.MaxDrawdown);
        Assert.Null(st.TopKContributionPct[1]);
    }

    [Fact]
    public void Compute_MixedWinsAndLosses_ComputesStandardMetricsCorrectly()
    {
        // 5 trades in sequence order: +100, -40, +60, -20, +10. Winners: 100,60,10 (avg 56.67).
        // Losers: -40,-20 (avg -30). Net = 110. Gross win=170, gross loss=60 -> PF=170/60=2.8333.
        var pnls = new List<decimal> { 100m, -40m, 60m, -20m, 10m };
        var st = OptionTradeQualityStats.Compute(pnls);

        Assert.Equal(5, st.Count);
        Assert.Equal(60.0, st.WinRatePct); // 3 of 5
        Assert.Equal(110m, st.NetPnl);
        Assert.Equal(22m, st.Expectancy); // 110/5
        Assert.NotNull(st.ProfitFactor);
        Assert.Equal(170.0 / 60.0, st.ProfitFactor!.Value, 4);

        // Cumulative curve: 100, 60, 120, 100, 110. Peak-to-trough drops seen along the way: 40
        // (peak 100 after trade 1 -> 60 after trade 2) and 20 (peak 120 after trade 3 -> 100 after
        // trade 4) -- the larger of the two, 40, is the max drawdown.
        Assert.Equal(40m, st.MaxDrawdown);

        // Top-1 winner (100) / net(110) = 90.9...%; top-3 (all 3 winners, sum 170) / 110 = 154.5...%.
        Assert.Equal(100.0 / 110.0 * 100.0, st.TopKContributionPct[1]!.Value, 3);
        Assert.Equal(170.0 / 110.0 * 100.0, st.TopKContributionPct[3]!.Value, 3);
    }

    [Fact]
    public void Compute_AllLosers_ProfitFactorIsZeroNotFabricated()
    {
        var st = OptionTradeQualityStats.Compute([-10m, -20m]);

        Assert.Equal(0.0, st.WinRatePct);
        Assert.Equal(0.0, st.ProfitFactor); // grossWin=0, grossLoss>0 -> 0/grossLoss = 0.0, a real (worst-possible) profit factor, not fabricated
        Assert.Null(st.AvgWinner);
        Assert.NotNull(st.AvgLoser);
    }

    [Fact]
    public void Compute_AllWinners_ProfitFactorIsPositiveInfinity()
    {
        var st = OptionTradeQualityStats.Compute([10m, 20m]);

        Assert.Equal(100.0, st.WinRatePct);
        Assert.NotNull(st.ProfitFactor);
        Assert.True(double.IsPositiveInfinity(st.ProfitFactor!.Value));
    }

    [Fact]
    public void Compute_MfeMaeAndHoldingDuration_AveragesAndMediansIgnoreNulls()
    {
        var pnls = new List<decimal> { 10m, -5m, 20m };
        var mfe = new List<double?> { 2.0, null, 4.0 };
        var mae = new List<double?> { 1.0, 3.0, null };
        var hold = new List<double> { 5.0, 10.0, 15.0 };

        var st = OptionTradeQualityStats.Compute(pnls, mfe, mae, hold);

        Assert.Equal(3m, st.MeanMfe); // (2+4)/2
        Assert.Equal(2m, st.MeanMae); // (1+3)/2
        Assert.Equal(10.0, st.AvgHoldingMinutes);
        Assert.Equal(10.0, st.MedianHoldingMinutes);
    }
}
