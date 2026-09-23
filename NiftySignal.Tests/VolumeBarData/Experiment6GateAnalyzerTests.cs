using NiftySignal.Domain.Enums;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22, Experiment 6 (docs/VOLUME_BAR_FINDINGS.md's dated "Experiment 6" section): coverage
/// for <see cref="GateFeatureExtractor.Build"/> (gate-feature snapshot arithmetic) and
/// <see cref="Experiment6GateAnalyzer"/>'s distribution/gate-evaluation/equity-curve logic, against
/// small hand-computed synthetic fixtures.
/// </summary>
public class Experiment6GateAnalyzerTests
{
    static VolumeBarRow Bar(int index, decimal open, decimal close, long? oi, double? vwap) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = index,
        BarVolumeThreshold = 650,
        StartTimestamp = new DateTimeOffset(2026, 9, 8, 4, 0, index, TimeSpan.Zero),
        EndTimestamp = new DateTimeOffset(2026, 9, 8, 4, 0, index + 1, TimeSpan.Zero),
        OpenPrice = open,
        HighPrice = Math.Max(open, close) + 1m,
        LowPrice = Math.Min(open, close) - 1m,
        ClosePrice = close,
        Volume = 650,
        TickCount = 20,
        DurationSeconds = 10,
        OpenInterestAtClose = oi,
        VwapAtClose = vwap,
        FutureDepthImbalance = 0.2,
        OrderFlowImbalance = -0.1,
        FutureCvdNet = 500,
    };

    [Fact]
    public void Build_OiChangeAndVwapRel_MatchHandComputation()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, open: 100m, close: 101m, oi: 1000, vwap: 100.0),
            Bar(1, open: 101m, close: 103m, oi: 1200, vwap: 100.0), // OI change = 1200-1000=200; VWAP rel = (103-100)/103*100
        };
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0);

        var f = GateFeatureExtractor.Build(bars, 1, threshold);

        Assert.Equal(200, f.OiChangeFromPrev);
        Assert.Equal((103.0 - 100.0) / 103.0 * 100.0, f.VwapRelPct!.Value, 6);
        Assert.Equal(0.2, f.DepthImbalance);
        Assert.Equal(-0.1, f.Ofi);
        Assert.Equal(500, f.CvdNet);
    }

    [Fact]
    public void Build_FirstBarOfDay_OiChangeIsNullNotFabricated()
    {
        var bars = new List<VolumeBarRow> { Bar(0, open: 100m, close: 101m, oi: 1000, vwap: 100.0) };
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0);

        var f = GateFeatureExtractor.Build(bars, 0, threshold);

        Assert.Null(f.OiChangeFromPrev);
    }

    static Experiment5OptionTradeSimulator.OptionTrade Trade(DateOnly date, int barIndex, decimal netPnl) => new(
        date, barIndex, 650, 10, "Mid(10:00-13:30)", 4, false, 1, OptionType.Put, 20000m,
        new DateTimeOffset(2026, 9, 8, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 8, 5, 5, 0, TimeSpan.Zero),
        100m, 100m + netPnl / 130m, netPnl / 130m, netPnl, netPnl, 0.0, null, null, 0m, 0m, 0m, 0m, 5.0);

    [Fact]
    public void Evaluate_GateKeepingOnlyPositiveDepthImbalance_ReportsRetentionCorrectly()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, 100m, 101m, 1000, 100.0),
            Bar(1, 101m, 102m, 1000, 100.0),
        };
        var byDay = new Dictionary<DateOnly, List<VolumeBarRow>> { [new DateOnly(2026, 9, 8)] = bars };
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0);

        // Trade 0 is a winner (net +50), trade 1 is a loser (net -30). Both bars have the SAME
        // DepthImbalance (0.2, from the Bar() helper), so a gate on DepthImbalance>0 keeps both --
        // this deliberately tests the retention bookkeeping, not a real separating gate.
        var trades = new List<Experiment5OptionTradeSimulator.OptionTrade>
        {
            Trade(new DateOnly(2026, 9, 8), 0, 50m),
            Trade(new DateOnly(2026, 9, 8), 1, -30m),
        };

        var gated = Experiment6GateAnalyzer.Attach(trades, byDay, threshold);
        var result = Experiment6GateAnalyzer.Evaluate("DepthImbalance>0", gated, g => g.Features.DepthImbalance > 0);

        Assert.Equal(2, result.BaselineCount);
        Assert.Equal(2, result.Kept);
        Assert.Equal(0, result.Removed);
        Assert.Equal(100.0, result.PctProfitableBaseRetained);
        Assert.Equal(0.0, result.PctLosingBaseRemoved);
    }

    [Fact]
    public void Evaluate_GateKeepingOnlyTheWinner_ReportsFullWinnerRetentionAndFullLoserRemoval()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, 100m, 101m, 1000, 100.0), // OiChangeFromPrev = null (first bar of day)
            Bar(1, 101m, 102m, 1000, 100.0), // OiChangeFromPrev = 1000-1000 = 0
        };
        var byDay = new Dictionary<DateOnly, List<VolumeBarRow>> { [new DateOnly(2026, 9, 8)] = bars };
        var threshold = new TickActivityAnalyzer.ActivityEfficiencyThreshold(0, 0);

        var trades = new List<Experiment5OptionTradeSimulator.OptionTrade>
        {
            Trade(new DateOnly(2026, 9, 8), 0, 50m), // winner, OiChangeFromPrev=null
            Trade(new DateOnly(2026, 9, 8), 1, -30m), // loser, OiChangeFromPrev=0
        };

        var gated = Experiment6GateAnalyzer.Attach(trades, byDay, threshold);
        // Gate: keep only bars whose OiChangeFromPrev is null -- selects bar 0 (the winner) only,
        // since bar 1's OiChangeFromPrev is a real (non-null) 0. This isolates the winner via the
        // fixture's own OI construction, purely to exercise the retention arithmetic below.
        var result = Experiment6GateAnalyzer.Evaluate("OiChangeFromPrev is null", gated, g => g.Features.OiChangeFromPrev is null);

        Assert.Equal(1, result.Kept);
        Assert.Equal(100.0, result.PctProfitableBaseRetained); // the winner was kept
        Assert.Equal(100.0, result.PctLosingBaseRemoved); // the loser was removed
    }

    [Fact]
    public void BuildEquityCurve_AccumulatesGrossCostsNetInTradeOrder()
    {
        var trades = new List<Experiment5OptionTradeSimulator.OptionTrade>
        {
            Trade(new DateOnly(2026, 9, 8), 0, 50m), // net=50, gross set so gross-net = "costs"; Trade() sets GrossPnlLot=netPnl too in this fixture, so costs=0 here for simplicity
            Trade(new DateOnly(2026, 9, 8), 1, -30m),
        };

        var curve = Experiment6GateAnalyzer.BuildEquityCurve(trades);

        Assert.Equal(2, curve.Count);
        Assert.Equal(1, curve[0].TradeNumber);
        Assert.Equal(50m, curve[0].CumulativeNet);
        Assert.Equal(20m, curve[1].CumulativeNet); // 50 + (-30)
        Assert.Equal(0m, curve[1].CumulativeCosts); // fixture sets Gross==Net (no cost modeled in the helper)
    }
}
