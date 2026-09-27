using System.Text.Json;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// One tick from a V2 export (<see cref="TickExporterV2"/>), full top-5 depth included. File-based
/// only -- no EF/DB entity involved, since the Options Order Flow experiment (not yet built) is the
/// only thing that will need Pattern A/B's DB-seeded plumbing; this reader exists purely to load and
/// validate the new export so calculation utilities can be built and unit-tested against it first.
/// </summary>
public sealed record OptionTickV2(
    long Id, DateTimeOffset ExchangeTimestamp, DateTimeOffset ReceivedAt,
    decimal LastPrice, MarketDepth? Depth, long Volume, long? OpenInterest);

/// <summary>
/// Reads and validates one token's <c>research-ticks-v2/&lt;date&gt;/&lt;token&gt;.ndjson</c> file
/// against <see cref="TickExporterV2.RowSchema"/>. Deliberately separate from every V1
/// SeedDayAsync-style loader (e.g. <c>ConfirmationExperiment.SeedDayAsync</c>) -- those all
/// hardcode depth levels 2-5 to zero because V1 never exported them; this type is the one place
/// V2's full depth is parsed.
/// </summary>
public static class OptionTickReaderV2
{
    public static List<OptionTickV2> LoadFile(string path)
    {
        var ticks = new List<OptionTickV2>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) { continue; }
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            MarketDepth? depth = new MarketDepth(
                row[4].GetDecimal(), row[5].GetInt64(),
                row[6].GetDecimal(), row[7].GetInt64(),
                row[8].GetDecimal(), row[9].GetInt64(),
                row[10].GetDecimal(), row[11].GetInt64(),
                row[12].GetDecimal(), row[13].GetInt64(),
                row[14].GetDecimal(), row[15].GetInt64(),
                row[16].GetDecimal(), row[17].GetInt64(),
                row[18].GetDecimal(), row[19].GetInt64(),
                row[20].GetDecimal(), row[21].GetInt64(),
                row[22].GetDecimal(), row[23].GetInt64());
            if (depth.TotalBidQty == 0 && depth.TotalAskQty == 0) { depth = null; }
            ticks.Add(new OptionTickV2(
                Id: row[0].GetInt64(),
                ExchangeTimestamp: row[1].GetDateTimeOffset(),
                ReceivedAt: row[2].GetDateTimeOffset(),
                LastPrice: row[3].GetDecimal(),
                Depth: depth,
                Volume: row[24].GetInt64(),
                OpenInterest: row[25].ValueKind == JsonValueKind.Null ? null : row[25].GetInt64()));
        }
        return ticks;
    }

    /// <summary>
    /// Confirms the file is in the exact order the export guarantees (ExchangeTimestamp
    /// non-decreasing, Id strictly increasing as the tie-break/overall sequence key) -- every
    /// downstream calculation (trade detection, pre-trade book lookup) depends on this holding
    /// without re-sorting. Returns a human-readable violation per offending row; empty = clean.
    /// </summary>
    public static List<string> ValidateOrdering(IReadOnlyList<OptionTickV2> ticks)
    {
        var violations = new List<string>();
        for (var i = 1; i < ticks.Count; i++)
        {
            var prev = ticks[i - 1];
            var cur = ticks[i];
            if (cur.ExchangeTimestamp < prev.ExchangeTimestamp)
            {
                violations.Add($"row {i}: ExchangeTimestamp went backwards ({prev.ExchangeTimestamp:O} -> {cur.ExchangeTimestamp:O})");
            }
            if (cur.Id <= prev.Id)
            {
                violations.Add($"row {i}: Id did not strictly increase ({prev.Id} -> {cur.Id})");
            }
        }
        return violations;
    }

    /// <summary>
    /// Flags cumulative-volume irregularities per the spec's "validate session resets and bad
    /// negative deltas" requirement. A negative delta mid-session is bad data (never a legitimate
    /// trade), not something to silently clamp to zero or drop -- callers decide what to do with
    /// the report; this only observes and counts.
    /// </summary>
    public static VolumeIrregularityReport ValidateCumulativeVolume(IReadOnlyList<OptionTickV2> ticks)
    {
        var negativeDeltaCount = 0;
        var negativeDeltaRows = new List<int>();
        for (var i = 1; i < ticks.Count; i++)
        {
            var delta = ticks[i].Volume - ticks[i - 1].Volume;
            if (delta < 0)
            {
                negativeDeltaCount++;
                if (negativeDeltaRows.Count < 20) { negativeDeltaRows.Add(i); }
            }
        }
        return new VolumeIrregularityReport(ticks.Count, negativeDeltaCount, negativeDeltaRows);
    }

    public sealed record VolumeIrregularityReport(int TotalTicks, int NegativeDeltaCount, List<int> SampleOffendingRowIndices);

    public sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<List<ManifestRow>> LoadManifestAsync(string dayDir) =>
        JsonSerializer.Deserialize<List<ManifestRow>>(await File.ReadAllTextAsync(Path.Combine(dayDir, "instruments.json")))!;
}
