namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22, Experiment 5 -- see docs/VOLUME_BAR_FINDINGS.md's dated
/// "Experiment 5" section). Standard trade-quality metrics (win rate, profit factor, expectancy,
/// drawdown, concentration) computed the same way every other trade-quality table in this project's
/// history has reported them (same formulas as <see cref="TradeSimulator"/>'s own day/period
/// summaries -- win rate = winners/total, profit factor = gross win / gross loss magnitude,
/// expectancy = mean P&amp;L per trade, max drawdown = largest peak-to-trough drop of the CUMULATIVE
/// P&amp;L curve in trade-sequence order) -- reused here rather than reinvented so Experiment 5's
/// numbers stay comparable to every other trade-quality table already in this document. Pure
/// function over an ordered sequence of realized trade P&amp;Ls -- no DB access, no side effects.
/// </summary>
public static class OptionTradeQualityStats
{
    /// <summary>
    /// <paramref name="TopKContributionPct"/> keys are 1/3/5 (item 7's mandatory top-1/3/5-trade
    /// concentration check) -- what share of NET P&amp;L the top-K single WINNING trades (by P&amp;L
    /// descending) contribute, so a result propped up by a handful of large trades reads differently
    /// from a broad, stable distribution, per the task's own explicit instruction.
    /// </summary>
    public sealed record Stats(
        int Count, double? WinRatePct, decimal? AvgWinner, decimal? AvgLoser, decimal? MedianWinner, decimal? MedianLoser,
        double? ProfitFactor, decimal? Expectancy, decimal NetPnl, decimal MaxDrawdown,
        decimal? MeanMfe, decimal? MeanMae, decimal? MedianMfe, decimal? MedianMae,
        double? AvgHoldingMinutes, double? MedianHoldingMinutes,
        IReadOnlyDictionary<int, double?> TopKContributionPct);

    static decimal? Median(List<decimal> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;
    }

    static double? MedianD(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <param name="pnls">Realized P&amp;L per trade, in TRADE-SEQUENCE (chronological entry) order -- required for a meaningful max-drawdown/cumulative-curve computation.</param>
    /// <param name="mfePct">Per-trade MFE, as a percent of entry price (same units <see cref="MaeMfeCalculator"/> reports) -- optional, null entries skipped.</param>
    /// <param name="maePct">Per-trade MAE, same units as <paramref name="mfePct"/>.</param>
    /// <param name="holdingMinutes">Per-trade wall-clock holding duration in minutes.</param>
    public static Stats Compute(
        IReadOnlyList<decimal> pnls, IReadOnlyList<double?>? mfePct = null, IReadOnlyList<double?>? maePct = null,
        IReadOnlyList<double>? holdingMinutes = null)
    {
        var n = pnls.Count;
        if (n == 0)
        {
            return new Stats(0, null, null, null, null, null, null, null, 0m, 0m, null, null, null, null, null, null,
                new Dictionary<int, double?> { [1] = null, [3] = null, [5] = null });
        }

        var winners = pnls.Where(p => p > 0m).OrderBy(p => p).ToList();
        var losers = pnls.Where(p => p < 0m).OrderBy(p => p).ToList();

        var grossWin = winners.Sum();
        var grossLoss = -losers.Sum();
        var net = pnls.Sum();

        double? profitFactor = grossLoss > 0m ? (double)(grossWin / grossLoss) : (grossWin > 0m ? double.PositiveInfinity : null);

        // Max drawdown of the CUMULATIVE P&L curve, in trade-sequence order -- same "largest
        // peak-to-trough drop" definition every other trade-quality table in this project uses.
        var cumulative = 0m;
        var peak = 0m;
        var maxDd = 0m;
        foreach (var p in pnls)
        {
            cumulative += p;
            if (cumulative > peak)
            {
                peak = cumulative;
            }

            var dd = peak - cumulative;
            if (dd > maxDd)
            {
                maxDd = dd;
            }
        }

        var topKContribution = new Dictionary<int, double?>();
        foreach (var k in new[] { 1, 3, 5 })
        {
            if (net == 0m || winners.Count == 0)
            {
                topKContribution[k] = null;
                continue;
            }

            var topK = winners.OrderByDescending(p => p).Take(k).Sum();
            topKContribution[k] = (double)(topK / net) * 100.0;
        }

        var mfeVals = (mfePct ?? []).Where(v => v is not null).Select(v => v!.Value).ToList();
        var maeVals = (maePct ?? []).Where(v => v is not null).Select(v => v!.Value).ToList();
        var holdVals = (holdingMinutes ?? []).Where(v => !double.IsNaN(v)).ToList();

        return new Stats(
            n,
            100.0 * winners.Count / n,
            winners.Count > 0 ? winners.Average() : null,
            losers.Count > 0 ? losers.Average() : null,
            Median(winners),
            Median(losers),
            profitFactor,
            net / n,
            net,
            maxDd,
            mfeVals.Count > 0 ? (decimal)mfeVals.Average() : null,
            maeVals.Count > 0 ? (decimal)maeVals.Average() : null,
            mfeVals.Count > 0 ? (decimal?)MedianD(mfeVals.OrderBy(v => v).ToList()) : null,
            maeVals.Count > 0 ? (decimal?)MedianD(maeVals.OrderBy(v => v).ToList()) : null,
            holdVals.Count > 0 ? holdVals.Average() : null,
            holdVals.Count > 0 ? MedianD(holdVals.OrderBy(v => v).ToList()) : null,
            topKContribution);
    }
}
