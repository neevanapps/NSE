using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Runs ReversalResearch.BuildDayReportAsync against ticks exported by TickExporter, instead of a
/// live database connection -- seeds a fresh EF Core InMemoryDatabase per session (the project's
/// own established convention for DB-backed research/test code, e.g. ReversalResearchTests.cs)
/// and hands it to the SAME report-building method the live-DB path uses, so this is not a parallel
/// reimplementation. Read-only w.r.t. the exported files; never touches the real database.
/// </summary>
public static class FileBackedResearchRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: research-from-files <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=research-file-run]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var inDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "research-file-run";
        Directory.CreateDirectory(outDir);
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

        var dates = Directory.Exists(inDir)
            ? Directory.GetDirectories(inDir)
                .Select(d => (Dir: d, Date: DateOnly.ParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList()
            : [];
        Console.WriteLine($"research-from-files: {dates.Count} exported session(s) found in [{from:yyyy-MM-dd},{to:yyyy-MM-dd}] under {inDir}.");

        var daily = new List<object>();
        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"Loading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"reversal-file-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            var tickCount = await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            Console.WriteLine($"  seeded {tickCount:N0} ticks into in-memory DB.");

            var report = await ReversalResearch.BuildDayReportAsync(db, date);
            var path = Path.Combine(outDir, $"{date:yyyy-MM-dd}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, jsonOpts));
            daily.Add(new { Date = date, report.Dte, Reports = path });
            Console.WriteLine($"Saved {path}; A/B full-surface state entries={report.patternRows.Count(r => r.StateEntry && r.Full)}.");
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(daily, jsonOpts));
        return 0;
    }

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    static async Task<long> SeedDayAsync(NiftySignalDbContext db, string dayDir, DateOnly date)
    {
        var manifest = JsonSerializer.Deserialize<List<ManifestRow>>(await File.ReadAllTextAsync(Path.Combine(dayDir, "instruments.json")))!;
        foreach (var m in manifest)
        {
            db.Instruments.Add(new Instrument
            {
                Token = m.Token, Exchange = Enum.Parse<Exchange>(m.Exchange), TradingSymbol = m.TradingSymbol,
                InstrumentType = Enum.Parse<InstrumentType>(m.InstrumentType), OptionType = Enum.Parse<OptionType>(m.OptionType),
                StrikePrice = m.StrikePrice, ExpiryDate = m.ExpiryDate, Underlying = m.Underlying,
                LotSize = m.LotSize, TickSize = m.TickSize, AsOfDate = date
            });
        }
        await db.SaveChangesAsync();

        long total = 0;
        foreach (var m in manifest)
        {
            var path = Path.Combine(dayDir, $"{m.Token}.ndjson");
            if (!File.Exists(path)) { continue; }
            var exchange = Enum.Parse<Exchange>(m.Exchange);
            var batch = new List<Tick>(8192);
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0) { continue; }
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                var bid = row[4].GetDecimal(); var ask = row[5].GetDecimal();
                var bidQty = row[6].GetInt64(); var askQty = row[7].GetInt64();
                batch.Add(new Tick
                {
                    Id = row[0].GetInt64(), Token = m.Token, Exchange = exchange,
                    ExchangeTimestamp = row[1].GetDateTimeOffset(), ReceivedAt = row[2].GetDateTimeOffset(),
                    LastPrice = row[3].GetDecimal(), Volume = row[8].GetInt64(),
                    OpenInterest = row[9].ValueKind == JsonValueKind.Null ? null : row[9].GetInt64(),
                    Depth = bid == 0 && ask == 0 && bidQty == 0 && askQty == 0 ? null
                        : new MarketDepth(bid, bidQty, 0, 0, 0, 0, 0, 0, 0, 0, ask, askQty, 0, 0, 0, 0, 0, 0, 0, 0)
                });
                total++;
                // ChangeTracker.Clear() after each batch -- without it, EF Core keeps every
                // previously-inserted tick tracked, and change detection cost grows with the
                // total tracked count, making later batches (and every later read query) progressively
                // slower across a multi-million-row day. See the seeding stall this fixed.
                if (batch.Count == 8192) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); batch.Clear(); }
            }
            if (batch.Count > 0) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
        }
        return total;
    }
}
