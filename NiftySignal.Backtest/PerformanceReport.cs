namespace NiftySignal.Backtest;

/// <summary>The metrics named in plan section 9, computed over a set of closed paper trades.</summary>
public sealed record PerformanceReport(
    int TotalTrades,
    double WinRatePct,
    double? RollingWinRate20Pct,
    double? RollingWinRate50Pct,
    decimal AverageWin,
    decimal AverageLoss,
    double ProfitFactor,
    decimal NetPnl,
    decimal MaxDrawdown,
    double MaxDrawdownPct,
    double? SharpeRatio,
    double TradesPerDay);

/// <summary>Performance bucketed by India VIX tercile at entry (plan section 9). Null VixAtEntry trades are excluded -- there's no VIX data source wired up yet, so this will report empty until one exists.</summary>
public sealed record VixRegimeBucket(string Regime, int TradeCount, decimal NetPnl, double WinRatePct);
