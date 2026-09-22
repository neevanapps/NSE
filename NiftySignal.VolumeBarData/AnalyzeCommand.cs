using Microsoft.EntityFrameworkCore;
using NiftySignal.BacktestData;
using NiftySignal.Features;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// The volume-cadence plan's own "parity/sanity check" step (2026-09-17): reads one day's already
/// -populated <see cref="VolumeBarRow"/> series, ports TrendReversion onto it via
/// <see cref="VolumeBarTrendReversionTracker"/>, and compares the bar-based day-level CVD/depth
/// numbers against the SAME day's existing time-cadence numbers
/// (`NiftySignal.BacktestData.BacktestAnalysisDbContext`) -- not to expect an exact match (the two
/// pipelines bucket differently), but to catch a genuinely broken port before trusting anything
/// built on top of it. CVD is the strong check: its classification is per-TICK, independent of
/// bucketing, so the whole day's cumulative classified volume should be close to identical between
/// the two pipelines regardless of how it's bucketed. Depth imbalance is a softer check (an
/// average of per-tick ratios is inherently bucket-size-sensitive), so only sign and rough
/// magnitude are compared, not a tight tolerance.
/// </summary>
public static class AnalyzeCommand
{
    public static async Task<int> RunAsync(DateOnly asOfDate, int windowBars, long barVolumeThreshold, VolumeBarDbContext volumeBars, BacktestAnalysisDbContext timeCadence)
    {
        var bars = await volumeBars.VolumeBars
            .Where(b => b.AsOfDate == asOfDate && b.BarVolumeThreshold == barVolumeThreshold)
            .OrderBy(b => b.BarIndex)
            .ToListAsync();

        if (bars.Count == 0)
        {
            Console.Error.WriteLine($"No volume bars for {asOfDate:yyyy-MM-dd} at threshold={barVolumeThreshold} -- populate it first.");
            return 1;
        }

        // --- Port TrendReversion onto the bar series ---
        var tracker = new VolumeBarTrendReversionTracker(windowBars);
        var trendReadings = new List<double?>(bars.Count);
        decimal? previousClose = null;
        foreach (var bar in bars)
        {
            double? change = previousClose is { } prev ? (double)(bar.ClosePrice - prev) : null;
            trendReadings.Add(tracker.Observe(change));
            previousClose = bar.ClosePrice;
        }

        var warmedUp = trendReadings.Count(r => r is not null);
        var trendValues = trendReadings.Where(r => r is not null).Select(r => r!.Value).ToList();

        // --- Bar-based day-level aggregates ---
        var barCvdSum = bars.Where(b => b.FutureCvdNet is not null).Sum(b => b.FutureCvdNet!.Value);
        var barCvdBearingCount = bars.Count(b => b.FutureCvdNet is not null);
        var barDepthValues = bars.Where(b => b.FutureDepthImbalance is not null).Select(b => b.FutureDepthImbalance!.Value).ToList();
        var barAvgDepth = barDepthValues.Count > 0 ? barDepthValues.Average() : (double?)null;

        // --- Existing time-cadence day-level numbers, same date ---
        var cadences = await timeCadence.CadenceContexts
            .Where(c => c.AsOfDate == asOfDate)
            .OrderBy(c => c.Timestamp)
            .ToListAsync();

        long? cadenceCvdCumulativeDay = cadences.Count > 0
            ? cadences.Select(c => c.FutureCvdProxyCumulativeDay).Where(v => v is not null).LastOrDefault()
            : null;
        var cadenceDepthValues = cadences.Where(c => c.FutureDepthImbalanceFromLastCadence is not null)
            .Select(c => c.FutureDepthImbalanceFromLastCadence!.Value).ToList();
        var cadenceAvgDepth = cadenceDepthValues.Count > 0 ? cadenceDepthValues.Average() : (double?)null;

        // --- Report ---
        Console.WriteLine($"=== Volume-bar analysis: {asOfDate:yyyy-MM-dd}, threshold={barVolumeThreshold}, TrendReversion window={windowBars} bars ===");
        Console.WriteLine($"Bars: {bars.Count} (avg duration {bars.Average(b => b.DurationSeconds):F1}s)");
        Console.WriteLine($"TrendReversion: warmed up on {warmedUp}/{bars.Count} bars, range [{(trendValues.Count > 0 ? trendValues.Min() : 0):F3}, {(trendValues.Count > 0 ? trendValues.Max() : 0):F3}], avg {(trendValues.Count > 0 ? trendValues.Average() : 0):F3}");
        Console.WriteLine();
        Console.WriteLine("--- Parity check vs existing time cadence (same day) ---");
        Console.WriteLine($"CVD, day-cumulative:   volume-bar sum = {barCvdSum} (from {barCvdBearingCount}/{bars.Count} CVD-bearing bars)  |  time-cadence cumulative = {(cadenceCvdCumulativeDay is { } c1 ? c1.ToString() : "n/a")}");
        if (cadenceCvdCumulativeDay is { } cvdRef && cvdRef != 0)
        {
            var pctDiff = 100.0 * (barCvdSum - cvdRef) / Math.Abs(cvdRef);
            Console.WriteLine($"  -> difference: {pctDiff:F1}% (expect small -- both classify the SAME ticks, just bucketed differently)");
        }

        Console.WriteLine($"DepthImbalance, day avg: volume-bar avg = {(barAvgDepth is { } d1 ? d1.ToString("F4") : "n/a")}  |  time-cadence avg = {(cadenceAvgDepth is { } d2 ? d2.ToString("F4") : "n/a")}");
        Console.WriteLine($"  -> same sign: {(barAvgDepth is { } bd && cadenceAvgDepth is { } cd ? (Math.Sign(bd) == Math.Sign(cd) ? "yes" : "NO -- investigate") : "n/a")} (magnitude will differ -- different bucket sizes average over different tick counts)");
        Console.WriteLine();

        Console.WriteLine("Sample bars (first 5, last 5):");
        foreach (var bar in bars.Take(5).Concat(bars.Skip(Math.Max(0, bars.Count - 5))))
        {
            var idx = bars.IndexOf(bar);
            var trend = trendReadings[idx];
            Console.WriteLine($"  [{bar.BarIndex,4}] {FormatIst(bar.StartTimestamp)}-{FormatIst(bar.EndTimestamp)} ({bar.DurationSeconds,5:F1}s) vol={bar.Volume,5} close={bar.ClosePrice,10} cvd={(bar.FutureCvdNet?.ToString() ?? "null"),6} depth={(bar.FutureDepthImbalance?.ToString("F3") ?? "null"),7} ofi={(bar.OrderFlowImbalance?.ToString("F0") ?? "null"),7} trend={(trend?.ToString("F3") ?? "null"),7}");
        }

        return 0;
    }

    // CadenceContext.Timestamp / VolumeBarRow.StartTimestamp/EndTimestamp are all stored UTC
    // (Npgsql's own requirement -- see CadencePopulator.cs's own comment on this), so printing
    // them directly would show UTC wall-clock time, not the IST everyone reading this output
    // actually thinks in. Same convention NiftySignal.MetricTrials/Program.cs already uses.
    static string FormatIst(DateTimeOffset t) => t.ToOffset(TimeSpan.FromHours(5.5)).ToString("HH:mm:ss");
}
