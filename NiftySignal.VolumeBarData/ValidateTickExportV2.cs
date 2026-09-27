using System.Globalization;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-27 one-off validation tool for a V2 export (<see cref="TickExporterV2"/>) before more
/// sessions get exported. Read-only against files, never against the live database. Runs
/// <see cref="OptionTickReaderV2"/>'s ordering/volume checks and
/// <see cref="OptionOrderFlowCalculations"/>'s trade-detection/classification/mismatch-recompute
/// against every token in the given day, and reports totals -- this is pipeline validation, not
/// the Options Order Flow experiment itself (no Pattern A/B, no windows, no correlation, no
/// Sections 8+).
/// </summary>
public static class ValidateTickExportV2
{
    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: validate-ticks-v2 <date:yyyy-MM-dd> [--in=research-ticks-v2]");
            return 1;
        }
        var date = args[1];
        var inDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks-v2";
        var dayDir = Path.Combine(inDir, date);
        if (!Directory.Exists(dayDir))
        {
            Console.WriteLine($"No directory found: {dayDir}");
            return 1;
        }

        var completePath = Path.Combine(dayDir, "_complete.json");
        if (File.Exists(completePath))
        {
            Console.WriteLine("_complete.json:");
            Console.WriteLine(await File.ReadAllTextAsync(completePath));
            Console.WriteLine();
        }

        var manifest = await OptionTickReaderV2.LoadManifestAsync(dayDir);
        Console.WriteLine($"Manifest: {manifest.Count} instruments.");

        long totalTicks = 0, totalOrderingViolations = 0, totalNegativeDeltas = 0;
        long totalTradeEvents = 0, totalBuy = 0, totalSell = 0, totalUnknown = 0, totalMismatches = 0;
        long totalWithDepthBeyondL1 = 0;
        var perTokenRows = new List<string>();

        foreach (var m in manifest)
        {
            var path = Path.Combine(dayDir, $"{m.Token}.ndjson");
            if (!File.Exists(path)) { Console.WriteLine($"  MISSING FILE for token {m.Token} ({m.TradingSymbol})"); continue; }

            var ticks = OptionTickReaderV2.LoadFile(path);
            totalTicks += ticks.Count;
            totalWithDepthBeyondL1 += ticks.Count(t => t.Depth is not null && (t.Depth.Bid2Qty != 0 || t.Depth.Ask2Qty != 0));

            var orderingViolations = OptionTickReaderV2.ValidateOrdering(ticks);
            totalOrderingViolations += orderingViolations.Count;

            var volReport = OptionTickReaderV2.ValidateCumulativeVolume(ticks);
            totalNegativeDeltas += volReport.NegativeDeltaCount;

            var events = OptionOrderFlowCalculations.DetectTradeEvents(ticks, out var negDeltasFromDetect);
            if (negDeltasFromDetect != volReport.NegativeDeltaCount)
            {
                Console.WriteLine($"  WARNING token {m.Token}: negative-delta count mismatch between ValidateCumulativeVolume ({volReport.NegativeDeltaCount}) and DetectTradeEvents ({negDeltasFromDetect})");
            }
            var flow = OptionOrderFlowCalculations.AggregateContractFlow(events);
            var mismatches = OptionOrderFlowCalculations.CountAggressorMismatches(events);

            totalTradeEvents += events.Count;
            totalBuy += flow.AggressiveBuyVolume;
            totalSell += flow.AggressiveSellVolume;
            totalUnknown += flow.UnknownVolume;
            totalMismatches += mismatches;

            perTokenRows.Add($"  {m.Token,-8} {m.InstrumentType,-8} {m.OptionType,-5} {(m.StrikePrice?.ToString("0") ?? "-"),8} ticks={ticks.Count,7} " +
                $"orderingViolations={orderingViolations.Count} negDeltas={volReport.NegativeDeltaCount,4} " +
                $"trades={events.Count,6} buy={flow.AggressiveBuyVolume,8} sell={flow.AggressiveSellVolume,8} unknown={flow.UnknownVolume,8} " +
                $"ofi={(flow.TradeOFI is null ? "null" : flow.TradeOFI.Value.ToString("0.000")),6} coverage={flow.ClassificationCoverage:0.000} mismatches={mismatches}");

            if (orderingViolations.Count > 0)
            {
                Console.WriteLine($"  token {m.Token}: ordering violations (showing up to 5): {string.Join("; ", orderingViolations.Take(5))}");
            }
        }

        Console.WriteLine();
        foreach (var row in perTokenRows) { Console.WriteLine(row); }

        Console.WriteLine();
        Console.WriteLine("==================== TOTALS ====================");
        Console.WriteLine($"Total ticks read:                 {totalTicks:N0}");
        Console.WriteLine($"Ticks with depth beyond Level 1:  {totalWithDepthBeyondL1:N0} ({100.0 * totalWithDepthBeyondL1 / Math.Max(1, totalTicks):0.00}%)");
        Console.WriteLine($"Ordering violations:               {totalOrderingViolations} (expected 0)");
        Console.WriteLine($"Negative cumulative-volume deltas: {totalNegativeDeltas} (bad data / session resets, never counted as trades)");
        Console.WriteLine($"Total detected trade events:       {totalTradeEvents:N0}");
        Console.WriteLine($"  AggressiveBuy volume:  {totalBuy:N0}");
        Console.WriteLine($"  AggressiveSell volume: {totalSell:N0}");
        Console.WriteLine($"  Unknown volume:        {totalUnknown:N0}");
        var totalClassified = totalBuy + totalSell;
        var totalVolume = totalClassified + totalUnknown;
        Console.WriteLine($"  Day-level classification coverage: {(totalVolume > 0 ? 100.0 * totalClassified / totalVolume : 0):0.00}%");
        Console.WriteLine($"  Day-level TradeOFI (Buy-Sell)/Classified: {(totalClassified > 0 ? ((double)(totalBuy - totalSell) / totalClassified).ToString("0.000") : "null")}");
        Console.WriteLine($"Aggressor-classification mismatches (independent recompute): {totalMismatches} (expected 0)");

        return 0;
    }
}
