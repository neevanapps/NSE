using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-26: exports the exact intermediate dataset ReversalResearch.Cadences already produces
/// -- the 2-minute/10-minute fast/slow moving-average series on one option's own price -- BEFORE
/// CrossSignals/Simulate turn it into trades. Calls the same, already-tested Cadences() function
/// the C0/C1/C2 experiments use (not a reimplementation), so what's exported here is exactly what
/// fed those results. Read-only against already-exported research-ticks files.
/// </summary>
public static class DumpCadences
{
    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dump-cadences <date:yyyy-MM-dd> [token] [--fast=8] [--slow=40] [--bucket=15] [--in=research-ticks] [--out=cadence-dump.csv]");
            return 1;
        }
        var date = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var tokenFilter = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
        var fast = args.FirstOrDefault(a => a.StartsWith("--fast=", StringComparison.Ordinal)) is { } f ? int.Parse(f[7..]) : 8;
        var slow = args.FirstOrDefault(a => a.StartsWith("--slow=", StringComparison.Ordinal)) is { } s ? int.Parse(s[7..]) : 40;
        var bucketSeconds = args.FirstOrDefault(a => a.StartsWith("--bucket=", StringComparison.Ordinal)) is { } b ? int.Parse(b[9..]) : 15;
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "cadence-dump.csv";

        var dayDir = Path.Combine(ticksDir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (!Directory.Exists(dayDir))
        {
            Console.WriteLine($"No exported ticks under {dayDir}.");
            return 1;
        }
        var manifest = JsonSerializer.Deserialize<List<ManifestRow>>(await File.ReadAllTextAsync(Path.Combine(dayDir, "instruments.json")))!;
        var options = manifest.Where(m => m.InstrumentType == "Option" && m.StrikePrice is not null
            && (tokenFilter is null || m.Token == tokenFilter)).ToList();

        var start = ReversalResearch.At(date, 9, 15);
        var end = ReversalResearch.At(date, 15, 30);

        var sb = new StringBuilder();
        sb.AppendLine("Token,OptionType,Strike,BucketEndIST,AvgPrice,FastMA_2min,SlowMA_10min,Gap,Up,Down,AskAtBucket,InFairPriceBand");
        var rowCount = 0;
        foreach (var opt in options)
        {
            var path = Path.Combine(dayDir, $"{opt.Token}.ndjson");
            if (!File.Exists(path)) { continue; }
            var ticks = new List<ReversalResearch.Print>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) { continue; }
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                ticks.Add(new(r[0].GetInt64(), r[1].GetDateTimeOffset(), r[2].GetDateTimeOffset(), r[3].GetDecimal(),
                    r[4].GetDecimal(), r[5].GetDecimal(), r[6].GetInt64(), r[7].GetInt64(), r[8].GetInt64()));
            }
            if (ticks.Count == 0) { continue; }

            var readings = ReversalResearch.Cadences(ticks, start, end, fast, slow, bucketSeconds);
            foreach (var reading in readings)
            {
                var p = ReversalResearch.Before(ticks, reading.End);
                var ask = p is not null && p.Ask > 0 ? p.Ask : (decimal?)null;
                var inBand = ask is >= 100 and <= 150;
                sb.AppendLine(string.Join(',',
                    opt.Token, opt.OptionType, opt.StrikePrice,
                    reading.End.ToOffset(TimeSpan.FromHours(5.5)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    reading.Average?.ToString(CultureInfo.InvariantCulture) ?? "",
                    reading.Fast?.ToString(CultureInfo.InvariantCulture) ?? "",
                    reading.Slow?.ToString(CultureInfo.InvariantCulture) ?? "",
                    reading.Gap?.ToString(CultureInfo.InvariantCulture) ?? "",
                    reading.Up, reading.Down,
                    ask?.ToString(CultureInfo.InvariantCulture) ?? "",
                    inBand));
                rowCount++;
            }
        }
        await File.WriteAllTextAsync(outPath, sb.ToString());
        Console.WriteLine($"dump-cadences: {rowCount:N0} rows across {options.Count} option token(s) for {date:yyyy-MM-dd} " +
            $"(fast={fast} buckets, slow={slow} buckets, bucket={bucketSeconds}s). Saved {outPath}.");
        return 0;
    }
}
