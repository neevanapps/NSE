using NiftySignal.Domain.Entities;

namespace NiftySignal.Backtest;

/// <summary>
/// Pure computation over a list of closed trades -- no live/backtest distinction here,
/// which is the point: the same builder reports on real paper trades once the pipeline is
/// live, and on backtest-replayed trades today, from the exact same PaperTrade rows.
/// </summary>
public static class PerformanceReportBuilder
{
    public static PerformanceReport Build(IReadOnlyList<PaperTrade> trades)
    {
        var closed = trades
            .Where(t => t.ExitTime is not null && t.NetPnl is not null)
            .OrderBy(t => t.ExitTime)
            .ToList();

        if (closed.Count == 0)
        {
            return new PerformanceReport(0, 0, null, null, 0, 0, 0, 0, 0, 0, null, 0);
        }

        var wins = closed.Where(t => t.NetPnl!.Value > 0).ToList();
        var losses = closed.Where(t => t.NetPnl!.Value < 0).ToList();

        var winRate = 100.0 * wins.Count / closed.Count;
        var avgWin = wins.Count == 0 ? 0m : wins.Average(t => t.NetPnl!.Value);
        var avgLoss = losses.Count == 0 ? 0m : losses.Average(t => t.NetPnl!.Value);

        var grossWin = wins.Sum(t => t.NetPnl!.Value);
        var grossLoss = Math.Abs(losses.Sum(t => t.NetPnl!.Value));
        var profitFactor = grossLoss == 0 ? 0.0 : (double)(grossWin / grossLoss);

        var netPnl = closed.Sum(t => t.NetPnl!.Value);
        var (maxDrawdown, maxDrawdownPct) = ComputeMaxDrawdown(closed);

        var spanDays = Math.Max(1.0, (closed[^1].ExitTime!.Value - closed[0].EntryTime).TotalDays);

        return new PerformanceReport(
            TotalTrades: closed.Count,
            WinRatePct: winRate,
            RollingWinRate20Pct: RollingWinRate(closed, 20),
            RollingWinRate50Pct: RollingWinRate(closed, 50),
            AverageWin: avgWin,
            AverageLoss: avgLoss,
            ProfitFactor: profitFactor,
            NetPnl: netPnl,
            MaxDrawdown: maxDrawdown,
            MaxDrawdownPct: maxDrawdownPct,
            SharpeRatio: ComputeSharpeLikeRatio(closed),
            TradesPerDay: closed.Count / spanDays);
    }

    /// <summary>
    /// Tercile split by VixAtEntry (plan section 9). Returns null rather than a
    /// misleadingly-precise report when too few trades carry VIX data to form three
    /// meaningful groups -- there's no VIX data source wired up yet, so this is null in
    /// practice until one exists, not because the bucketing logic is unfinished.
    /// </summary>
    public static IReadOnlyList<VixRegimeBucket>? BuildVixRegimeBuckets(IReadOnlyList<PaperTrade> trades)
    {
        var withVix = trades
            .Where(t => t.VixAtEntry is not null && t.ExitTime is not null && t.NetPnl is not null)
            .OrderBy(t => t.VixAtEntry!.Value)
            .ToList();

        if (withVix.Count < 3)
        {
            return null;
        }

        var n = withVix.Count;
        var lowEnd = n / 3;
        var midEnd = 2 * n / 3;

        return
        [
            ToBucket("Low VIX", withVix.Take(lowEnd)),
            ToBucket("Mid VIX", withVix.Skip(lowEnd).Take(midEnd - lowEnd)),
            ToBucket("High VIX", withVix.Skip(midEnd)),
        ];
    }

    static VixRegimeBucket ToBucket(string name, IEnumerable<PaperTrade> trades)
    {
        var list = trades.ToList();
        return new VixRegimeBucket(
            name,
            list.Count,
            list.Sum(t => t.NetPnl!.Value),
            list.Count == 0 ? 0 : 100.0 * list.Count(t => t.NetPnl!.Value > 0) / list.Count);
    }

    static double? RollingWinRate(List<PaperTrade> orderedByExit, int window)
    {
        if (orderedByExit.Count < window)
        {
            return null;
        }

        var last = orderedByExit.TakeLast(window).ToList();
        return 100.0 * last.Count(t => t.NetPnl!.Value > 0) / window;
    }

    static (decimal MaxDrawdown, double MaxDrawdownPct) ComputeMaxDrawdown(List<PaperTrade> orderedByExit)
    {
        // Drawdown measured against cumulative net P&L (an equity curve starting at 0),
        // not absolute account balance -- consistent with how the dashboard's equity
        // curve is computed. MaxDrawdownPct stays 0 while the curve has never cleared 0
        // (peak <= 0): "percent off a peak that was never positive" isn't a meaningful number.
        decimal equity = 0, peak = 0, maxDrawdown = 0;
        var maxDrawdownPct = 0.0;

        foreach (var trade in orderedByExit)
        {
            equity += trade.NetPnl!.Value;
            if (equity > peak)
            {
                peak = equity;
            }

            var drawdown = peak - equity;
            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }

            if (peak > 0)
            {
                var drawdownPct = (double)(drawdown / peak) * 100;
                if (drawdownPct > maxDrawdownPct)
                {
                    maxDrawdownPct = drawdownPct;
                }
            }
        }

        return (maxDrawdown, maxDrawdownPct);
    }

    /// <summary>
    /// Mean/stddev of per-trade net P&amp;L, not a finance-textbook annualized Sharpe --
    /// that needs a risk-free rate and consistent-period returns, which don't map cleanly
    /// onto irregularly-timed discretionary trades. Useful as a relative comparability
    /// metric between ruleset versions, which is what plan section 10 actually needs it
    /// for ("every tuning run must be recorded... otherwise you'll unknowingly repeat and
    /// cherry-pick").
    /// </summary>
    static double? ComputeSharpeLikeRatio(List<PaperTrade> closed)
    {
        if (closed.Count < 2)
        {
            return null;
        }

        var pnls = closed.Select(t => (double)t.NetPnl!.Value).ToList();
        var mean = pnls.Average();
        var variance = pnls.Sum(p => (p - mean) * (p - mean)) / (pnls.Count - 1);
        var stdDev = Math.Sqrt(variance);

        return stdDev < 1e-9 ? null : mean / stdDev;
    }
}
