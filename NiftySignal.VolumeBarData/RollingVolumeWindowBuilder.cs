namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-18, user's own framing: instead of a big fixed bar (e.g. 2600) waiting for its full
/// volume to accumulate from empty before producing a single reading -- which can take several
/// minutes and leaves the simulator (and a live system) blind the whole time -- build it out of
/// smaller, already-populated sub-bars (e.g. 650, the project's base unit) and slide a window
/// across them. A 2600-wide window is 4 consecutive 650 sub-bars; a NEW window is emitted every
/// time one more sub-bar completes, not only once all 4 have re-accumulated from scratch. Result:
/// a reading roughly every ~650-volume's worth of time instead of every ~2600's worth, at the
/// cost of consecutive windows sharing most of their underlying sub-bars (a 4-wide window slides
/// by 1 each time, so adjacent windows overlap 75%) -- real statistical non-independence, flagged
/// here rather than hidden, since the percentile-ranking entry gate this feeds
/// (<c>TradeSimulator</c>) assumes reasonably independent samples the way fixed/sequential bars
/// naturally are.
///
/// Deliberately NOT built as a new IO-touching accumulator -- operates purely on already-persisted
/// <see cref="VolumeBarRow"/> sub-bars (loaded once by the caller), combining their own stored
/// fields rather than re-deriving anything from raw ticks. The sub-bars it consumes are assumed to
/// all share the same <see cref="VolumeBarRow.BarVolumeThreshold"/> (the base unit) and be
/// contiguous, same-day, ordered by <see cref="VolumeBarRow.BarIndex"/> -- exactly what a query
/// against one already-populated threshold's rows already returns.
/// </summary>
public static class RollingVolumeWindowBuilder
{
    /// <param name="subBars">Same-day, same-threshold sub-bars, already ordered by BarIndex.</param>
    /// <param name="windowSubBarCount">How many consecutive sub-bars make up one window (e.g. 4 for a 2600-equivalent window built from 650 sub-bars).</param>
    /// <param name="logicalThreshold">The window's own nominal size (e.g. 2600) -- recorded on every emitted row's <see cref="VolumeBarRow.BarVolumeThreshold"/> so a consumer can tell which "logical" bar size these rolling windows represent, distinct from the underlying sub-bar size.</param>
    public static List<VolumeBarRow> BuildRollingWindows(IReadOnlyList<VolumeBarRow> subBars, int windowSubBarCount, long logicalThreshold)
    {
        if (windowSubBarCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSubBarCount), windowSubBarCount, "Window width must be a positive number of sub-bars.");
        }

        var windows = new List<VolumeBarRow>();
        for (var end = windowSubBarCount - 1; end < subBars.Count; end++)
        {
            var start = end - windowSubBarCount + 1;
            windows.Add(Combine(subBars, start, end, windows.Count, logicalThreshold));
        }

        return windows;
    }

    static VolumeBarRow Combine(IReadOnlyList<VolumeBarRow> subBars, int start, int end, int windowIndex, long logicalThreshold)
    {
        var oldest = subBars[start];
        var newest = subBars[end];

        decimal high = oldest.HighPrice;
        decimal low = oldest.LowPrice;
        long volume = 0;
        double durationSeconds = 0;
        long? cvdNet = null;
        double? ofiNet = null;
        var depthWeightedSum = 0.0;
        var depthWeight = 0.0;
        var tobWeightedSum = 0.0;
        var tobWeight = 0.0;

        for (var i = start; i <= end; i++)
        {
            var bar = subBars[i];
            high = Math.Max(high, bar.HighPrice);
            low = Math.Min(low, bar.LowPrice);
            volume += bar.Volume;
            durationSeconds += bar.DurationSeconds;

            if (bar.FutureCvdNet is { } cvd)
            {
                cvdNet = (cvdNet ?? 0) + cvd;
            }

            if (bar.OrderFlowImbalance is { } ofi)
            {
                ofiNet = (ofiNet ?? 0) + ofi;
            }

            // Volume-weighted average of each sub-bar's own already-averaged ratio -- an honest
            // approximation of the tick-level average over the whole window (sub-bars only store
            // their own final average, not the raw per-tick sum/count needed to recombine exactly).
            if (bar.FutureDepthImbalance is { } depth)
            {
                depthWeightedSum += depth * bar.Volume;
                depthWeight += bar.Volume;
            }

            if (bar.TopOfBookImbalance is { } tob)
            {
                tobWeightedSum += tob * bar.Volume;
                tobWeight += bar.Volume;
            }
        }

        return new VolumeBarRow
        {
            AsOfDate = newest.AsOfDate,
            BarIndex = windowIndex,
            BarVolumeThreshold = logicalThreshold,
            StartTimestamp = oldest.StartTimestamp,
            EndTimestamp = newest.EndTimestamp,
            DurationSeconds = durationSeconds,
            OpenPrice = oldest.OpenPrice,
            HighPrice = high,
            LowPrice = low,
            ClosePrice = newest.ClosePrice,
            Volume = volume,
            OpenInterestAtClose = newest.OpenInterestAtClose,
            VwapAtClose = newest.VwapAtClose,
            FutureCvdNet = cvdNet,
            FutureDepthImbalance = depthWeight > 0 ? depthWeightedSum / depthWeight : null,
            OrderFlowImbalance = ofiNet,
            TopOfBookImbalance = tobWeight > 0 ? tobWeightedSum / tobWeight : null,
        };
    }
}
