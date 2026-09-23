namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "Experiment 6 -- Standalone Mean-Reversion Strategy +
/// Gates" task -- see docs/VOLUME_BAR_FINDINGS.md's dated section). Snapshot of already-existing,
/// already-populated per-bar market-state features at a qualifying bar's own entry, used as GATE
/// CANDIDATES for Experiment 6's mean-reversion strategy. Every field here is read directly from
/// <see cref="VolumeBarRow"/>/<see cref="Features.TickActivityFeatures"/> -- no new indicator, no
/// new database column, per the task's own "reuse only existing available features" instruction.
/// Pure function, no DB access, no look-ahead (every field is read from the entry bar itself or an
/// earlier bar in the same day, never a future one).
/// </summary>
public static class GateFeatureExtractor
{
    /// <summary>
    /// One qualifying bar's gate-feature snapshot. <see cref="TickVelocityExcess"/>/<see cref="PriceEfficiencyExcess"/>
    /// are the entry bar's own TickVelocity/PriceEfficiency MINUS the locked pooled threshold median
    /// (Group E "signal strength" candidates) -- positive means "further above the qualifying median
    /// than a typical qualifying bar," i.e. a stronger exhaustion reading, not a new metric.
    /// </summary>
    public sealed record GateFeatures(
        double? DepthImbalance, double? Ofi, long? CvdNet, double? VwapRelPct,
        long? OiAtClose, long? OiChangeFromPrev, double DurationSeconds, int TickCount,
        double? TickDensity, double? TickVelocityExcess, double? PriceEfficiencyExcess,
        double? Churn, double AbsNetMovePct);

    /// <summary>
    /// Builds one qualifying bar's gate-feature snapshot from its own entry bar
    /// (<paramref name="dayBars"/>[<paramref name="barIndex"/>]) and the immediately preceding bar
    /// (for the OI-change candidate). <paramref name="dayBars"/> must be the same day's futures
    /// bars, 0-indexed/contiguous/ascending -- the same contract
    /// <see cref="Experiment5OptionTradeSimulator.BuildTradeAsync"/> already requires.
    /// </summary>
    public static GateFeatures Build(
        IReadOnlyList<VolumeBarRow> dayBars, int barIndex,
        TickActivityAnalyzer.ActivityEfficiencyThreshold threshold)
    {
        var b = dayBars[barIndex];
        var f = Features.TickActivityFeatures.Compute(b.OpenPrice, b.HighPrice, b.LowPrice, b.ClosePrice, b.Volume, b.TickCount, b.DurationSeconds);

        long? oiChange = barIndex > 0 && b.OpenInterestAtClose is not null && dayBars[barIndex - 1].OpenInterestAtClose is not null
            ? b.OpenInterestAtClose - dayBars[barIndex - 1].OpenInterestAtClose
            : null;

        double? vwapRel = b.VwapAtClose is { } vwap && vwap > 0 && b.ClosePrice > 0
            ? (double)(b.ClosePrice - (decimal)vwap) / (double)b.ClosePrice * 100.0
            : null;

        var absNetMovePct = b.OpenPrice > 0 ? (double)Math.Abs(b.ClosePrice - b.OpenPrice) / (double)b.OpenPrice * 100.0 : 0.0;

        return new GateFeatures(
            b.FutureDepthImbalance, b.OrderFlowImbalance, b.FutureCvdNet, vwapRel,
            b.OpenInterestAtClose, oiChange, b.DurationSeconds, b.TickCount,
            f.TickDensity,
            f.TickVelocity is { } tv ? tv - threshold.ActivityMedian : null,
            f.PriceEfficiency is { } pe ? pe - threshold.EfficiencyMedian : null,
            f.Churn, absNetMovePct);
    }
}
