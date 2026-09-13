using NiftySignal.Backtest;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.Backtest;

public class PerformanceReportBuilderTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 1, 9, 30, 0, TimeSpan.Zero);

    static PaperTrade ClosedTrade(int hoursFromStart, decimal netPnl, double? vixAtEntry = null) => new()
    {
        InstrumentToken = "12345",
        TradingSymbol = "NIFTY25SEP26C25000",
        StrategyId = StrategyId.LegacyComposite,
        Direction = EntryDirection.Bullish,
        EntryTime = Start.AddHours(hoursFromStart),
        EntryPrice = 175m,
        Quantity = 130,
        EntryScore = 60,
        ExitTime = Start.AddHours(hoursFromStart).AddMinutes(30),
        ExitPrice = 175m + netPnl,
        ExitReason = ExitReason.ScoreDecay,
        NetPnl = netPnl,
        VixAtEntry = vixAtEntry,
        RulesetVersion = "v1",
        ScoreWeightsVersion = "v1",
    };

    static PaperTrade OpenTrade(int hoursFromStart) => new()
    {
        InstrumentToken = "12345",
        TradingSymbol = "NIFTY25SEP26C25000",
        StrategyId = StrategyId.LegacyComposite,
        Direction = EntryDirection.Bullish,
        EntryTime = Start.AddHours(hoursFromStart),
        EntryPrice = 175m,
        Quantity = 130,
        EntryScore = 60,
        RulesetVersion = "v1",
        ScoreWeightsVersion = "v1",
    };

    [Fact]
    public void Build_ReturnsZeroedReport_WhenNoTradesGiven()
    {
        var report = PerformanceReportBuilder.Build([]);

        Assert.Equal(0, report.TotalTrades);
        Assert.Equal(0, report.WinRatePct);
        Assert.Null(report.RollingWinRate20Pct);
        Assert.Null(report.SharpeRatio);
    }

    [Fact]
    public void Build_ExcludesOpenTrades_FromEveryMetric()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 500), OpenTrade(1) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(1, report.TotalTrades);
    }

    [Fact]
    public void Build_ComputesWinRate_AsPercentOfProfitableTrades()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 500), ClosedTrade(1, -300), ClosedTrade(2, 200), ClosedTrade(3, -100) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(50.0, report.WinRatePct);
    }

    [Fact]
    public void Build_ComputesAverageWinAndAverageLoss_Separately()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 400), ClosedTrade(1, 600), ClosedTrade(2, -100), ClosedTrade(3, -300) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(500m, report.AverageWin);
        Assert.Equal(-200m, report.AverageLoss);
    }

    [Fact]
    public void Build_ComputesProfitFactor_AsGrossWinOverGrossLoss()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 800), ClosedTrade(1, 200), ClosedTrade(2, -400) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(2.5, report.ProfitFactor, 1e-9);
    }

    [Fact]
    public void Build_ComputesNetPnl_AsSumOfAllClosedTrades()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 500), ClosedTrade(1, -200), ClosedTrade(2, 300) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(600m, report.NetPnl);
    }

    [Fact]
    public void Build_RollingWinRate20_IsNull_WithFewerThan20ClosedTrades()
    {
        var trades = Enumerable.Range(0, 19).Select(i => ClosedTrade(i, 100)).ToList();

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Null(report.RollingWinRate20Pct);
    }

    [Fact]
    public void Build_RollingWinRate20_ComputesOverOnlyTheMostRecent20Trades()
    {
        // First 30 trades are losses, last 20 are wins -- the rolling window should only
        // "see" the last 20 (all wins), even though the overall win rate is much lower.
        var losses = Enumerable.Range(0, 30).Select(i => ClosedTrade(i, -100));
        var wins = Enumerable.Range(30, 20).Select(i => ClosedTrade(i, 100));
        var trades = losses.Concat(wins).ToList();

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(100.0, report.RollingWinRate20Pct);
        Assert.True(report.WinRatePct < 100.0);
    }

    [Fact]
    public void Build_MaxDrawdown_TracksTheWorstDeclineFromAnEquityPeak()
    {
        // Equity curve: 0 -> 200 -> 400 -> 100 -> 250. Peak 400, trough 100 -> drawdown 300 (75%).
        var trades = new List<PaperTrade> { ClosedTrade(0, 200), ClosedTrade(1, 200), ClosedTrade(2, -300), ClosedTrade(3, 150) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Equal(300m, report.MaxDrawdown);
        Assert.Equal(75.0, report.MaxDrawdownPct, 1e-9);
    }

    [Fact]
    public void Build_SharpeRatio_IsNull_WithFewerThanTwoClosedTrades()
    {
        var report = PerformanceReportBuilder.Build([ClosedTrade(0, 500)]);

        Assert.Null(report.SharpeRatio);
    }

    [Fact]
    public void Build_SharpeRatio_IsNull_WhenEveryTradeHasIdenticalPnl()
    {
        // Zero variance -- dividing by a ~0 stddev would be nonsense, not a huge ratio.
        var trades = Enumerable.Range(0, 5).Select(i => ClosedTrade(i, 100)).ToList();

        var report = PerformanceReportBuilder.Build(trades);

        Assert.Null(report.SharpeRatio);
    }

    [Fact]
    public void Build_SharpeRatio_IsPositive_WhenAverageReturnIsPositive()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 500), ClosedTrade(1, 300), ClosedTrade(2, -100) };

        var report = PerformanceReportBuilder.Build(trades);

        Assert.NotNull(report.SharpeRatio);
        Assert.True(report.SharpeRatio > 0);
    }

    [Fact]
    public void BuildVixRegimeBuckets_ReturnsNull_WhenFewerThanThreeTradesCarryVixData()
    {
        var trades = new List<PaperTrade> { ClosedTrade(0, 100, vixAtEntry: 15), ClosedTrade(1, 100, vixAtEntry: null) };

        var buckets = PerformanceReportBuilder.BuildVixRegimeBuckets(trades);

        Assert.Null(buckets);
    }

    [Fact]
    public void BuildVixRegimeBuckets_ExcludesTradesWithoutVixData_ButStillBucketsTheRest()
    {
        var trades = new List<PaperTrade>
        {
            ClosedTrade(0, 100, vixAtEntry: 12),
            ClosedTrade(1, 100, vixAtEntry: 15),
            ClosedTrade(2, 100, vixAtEntry: 25),
            ClosedTrade(3, 100, vixAtEntry: null), // excluded
        };

        var buckets = PerformanceReportBuilder.BuildVixRegimeBuckets(trades);

        Assert.NotNull(buckets);
        Assert.Equal(3, buckets.Sum(b => b.TradeCount));
    }

    [Fact]
    public void BuildVixRegimeBuckets_PartitionsIntoLowMidHigh_BySortedVixValue()
    {
        var trades = new List<PaperTrade>
        {
            ClosedTrade(0, 100, vixAtEntry: 10),  // low
            ClosedTrade(1, -50, vixAtEntry: 12),  // low
            ClosedTrade(2, 200, vixAtEntry: 18),  // mid
            ClosedTrade(3, -80, vixAtEntry: 20),  // mid
            ClosedTrade(4, 300, vixAtEntry: 28),  // high
            ClosedTrade(5, -20, vixAtEntry: 30),  // high
        };

        var buckets = PerformanceReportBuilder.BuildVixRegimeBuckets(trades)!;

        Assert.Equal(3, buckets.Count);
        Assert.Equal("Low VIX", buckets[0].Regime);
        Assert.Equal(2, buckets[0].TradeCount);
        Assert.Equal(50m, buckets[0].NetPnl); // 100 + -50
        Assert.Equal("Mid VIX", buckets[1].Regime);
        Assert.Equal(120m, buckets[1].NetPnl); // 200 + -80
        Assert.Equal("High VIX", buckets[2].Regime);
        Assert.Equal(280m, buckets[2].NetPnl); // 300 + -20
    }
}
