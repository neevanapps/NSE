using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-27 V2 tick export -- adds full top-5 bid/ask depth (TickExporter/V1 only ever exported
/// Level 1, per docs/VolumeCandle_0DTE_Findings.md's "Option Relative Strength and Confirmation
/// Findings" section's Section-2 schema inspection). Written as a brand-new file/command so V1
/// (<see cref="TickExporter"/>) and every module built against its `research-ticks/` output are
/// completely unchanged. Read-only against the source database, same as V1 -- never mutates it,
/// never touches NiftySignal.Host/NiftySignal.Dashboard.
///
/// Deterministic tick ordering: ticks are read `ORDER BY ExchangeTimestamp, Id` (Id is the
/// database identity/insert-order surrogate key), same tie-break V1 already uses, and written to
/// file in that exact order -- this is the ordering every V2 reader/calculation must assume and
/// must not silently re-sort.
///
/// Also exports the day's spot (<see cref="InstrumentType.Index"/>) instrument when one exists,
/// so the previously-blocked Spot/Futures Basis confirmation family (docs/VolumeCandle_0DTE_Findings.md's
/// 2026-09-27 findings, item G) can be revisited -- that blocker was the V1 export never selecting
/// Index rows, not the data being genuinely unavailable; <see cref="UnderlyingOptionRelationshipRecorder"/>
/// already queries the exact same row for other, DB-connected research. Guarded the same way that
/// call site guards it: never assumed present, never fabricated if absent for a given day.
/// </summary>
public static class TickExporterV2
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static DateTimeOffset At(DateOnly date, int hour, int minute) => new(date.ToDateTime(new TimeOnly(hour, minute)), Ist);

    /// <summary>
    /// Positional row schema written per tick line. Kept as one authoritative constant string so
    /// the export code, `_complete.json`'s own record of it, and every V2 reader can be checked
    /// against exactly this, rather than three independently-typed hand-copies drifting apart.
    /// </summary>
    public const string RowSchema =
        "[id, exchangeTimestamp, receivedAt, lastPrice, " +
        "bid1Price, bid1Qty, bid2Price, bid2Qty, bid3Price, bid3Qty, bid4Price, bid4Qty, bid5Price, bid5Qty, " +
        "ask1Price, ask1Qty, ask2Price, ask2Qty, ask3Price, ask3Qty, ask4Price, ask4Qty, ask5Price, ask5Qty, " +
        "volume, openInterest]";

    public static async Task<int> RunAsync(DbContextOptions<NiftySignalDbContext> options, string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: export-ticks-v2 <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--out=research-ticks-v2] [--underlying=NIFTY] [--force]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "research-ticks-v2";
        var underlying = args.FirstOrDefault(a => a.StartsWith("--underlying=", StringComparison.Ordinal)) is { } u ? u[13..] : "NIFTY";
        var force = args.Contains("--force");

        await using var db = new NiftySignalDbContext(options);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        db.Database.SetCommandTimeout(600);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET default_transaction_read_only = on");

        var dates = await db.Instruments
            .Where(i => i.Underlying == underlying && i.AsOfDate >= from && i.AsOfDate <= to
                && (i.InstrumentType == InstrumentType.Option || i.InstrumentType == InstrumentType.Future))
            .Select(i => i.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
        Console.WriteLine($"export-ticks-v2: {dates.Count} session date(s) found for {underlying} in [{from:yyyy-MM-dd},{to:yyyy-MM-dd}].");

        foreach (var date in dates)
        {
            var dayDir = Path.Combine(outDir, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var doneMarker = Path.Combine(dayDir, "_complete.json");
            if (File.Exists(doneMarker) && !force)
            {
                Console.WriteLine($"{date:yyyy-MM-dd}: already exported (_complete.json present); skipping. Pass --force to redo.");
                continue;
            }
            Directory.CreateDirectory(dayDir);

            var chainAll = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == underlying
                && i.InstrumentType == InstrumentType.Option && i.ExpiryDate >= date).ToListAsync();
            var nearestExpiry = chainAll.Count > 0 ? chainAll.Min(i => i.ExpiryDate)!.Value : (DateOnly?)null;
            var options_ = nearestExpiry is null ? [] : chainAll.Where(i => i.ExpiryDate == nearestExpiry).ToList();
            var futures = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == underlying
                && i.InstrumentType == InstrumentType.Future).OrderBy(i => i.ExpiryDate).ToListAsync();

            // Spot (Index) may or may not exist for this day -- never assumed, never fabricated
            // if absent, same guard UnderlyingOptionRelationshipRecorder.cs already established
            // for this exact lookup. Excludes InstrumentType.Vix deliberately (see its own doc
            // comment): that's India VIX, not the underlying's own spot price.
            var spot = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == underlying
                && i.InstrumentType == InstrumentType.Index).FirstOrDefaultAsync();
            var spotList = spot is null ? [] : new List<Instrument> { spot };

            var instruments = options_.Concat(futures).Concat(spotList)
                .OrderBy(i => i.InstrumentType).ThenBy(i => i.StrikePrice).ThenBy(i => i.OptionType).ToList();

            var manifest = instruments.Select(i => new
            {
                i.Token,
                Exchange = i.Exchange.ToString(),
                i.TradingSymbol,
                InstrumentType = i.InstrumentType.ToString(),
                OptionType = i.OptionType.ToString(),
                i.StrikePrice,
                i.ExpiryDate,
                i.Underlying,
                i.LotSize,
                i.TickSize
            }).ToList();
            await File.WriteAllTextAsync(Path.Combine(dayDir, "instruments.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var start = At(date, 9, 15).ToUniversalTime();
            var end = At(date, 15, 30).ToUniversalTime();
            long totalTicks = 0;
            long totalWithDepthBeyondL1 = 0;
            foreach (var inst in instruments)
            {
                var path = Path.Combine(dayDir, $"{inst.Token}.ndjson");
                await using var writer = new StreamWriter(path);
                var token = inst.Token;
                var count = 0;
                await foreach (var t in db.Ticks
                    .Where(t => t.Token == token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
                    .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).AsAsyncEnumerable())
                {
                    var d = t.Depth;
                    var row = new object?[]
                    {
                        t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                        d?.Bid1Price ?? 0, d?.Bid1Qty ?? 0, d?.Bid2Price ?? 0, d?.Bid2Qty ?? 0,
                        d?.Bid3Price ?? 0, d?.Bid3Qty ?? 0, d?.Bid4Price ?? 0, d?.Bid4Qty ?? 0,
                        d?.Bid5Price ?? 0, d?.Bid5Qty ?? 0,
                        d?.Ask1Price ?? 0, d?.Ask1Qty ?? 0, d?.Ask2Price ?? 0, d?.Ask2Qty ?? 0,
                        d?.Ask3Price ?? 0, d?.Ask3Qty ?? 0, d?.Ask4Price ?? 0, d?.Ask4Qty ?? 0,
                        d?.Ask5Price ?? 0, d?.Ask5Qty ?? 0,
                        t.Volume, t.OpenInterest
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row));
                    count++;
                    if (d is not null && (d.Bid2Qty != 0 || d.Ask2Qty != 0)) { totalWithDepthBeyondL1++; }
                }
                totalTicks += count;
                Console.WriteLine($"  {date:yyyy-MM-dd} {inst.Token} ({inst.InstrumentType} {inst.OptionType} {inst.StrikePrice}): {count:N0} ticks");
            }
            // Printed explicitly per the "print the exact fields that will be used" mandate --
            // confirms, per session, whether depth beyond Level 1 is actually present in this
            // export rather than assuming the domain type's capability implies populated data.
            Console.WriteLine($"{date:yyyy-MM-dd}: {totalWithDepthBeyondL1:N0} of {totalTicks:N0} ticks carry non-zero depth beyond Level 1.");
            Console.WriteLine(spot is null
                ? $"{date:yyyy-MM-dd}: no {underlying} spot (InstrumentType.Index) instrument found for this day -- not fabricated, simply absent."
                : $"{date:yyyy-MM-dd}: spot instrument found (token {spot.Token}, {spot.TradingSymbol}) and exported.");
            await File.WriteAllTextAsync(doneMarker, JsonSerializer.Serialize(new
            {
                Date = date,
                Underlying = underlying,
                NearestExpiry = nearestExpiry,
                InstrumentCount = instruments.Count,
                TotalTicks = totalTicks,
                TicksWithDepthBeyondLevel1 = totalWithDepthBeyondL1,
                SpotFound = spot is not null,
                SpotToken = spot?.Token,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                SchemaVersion = "v2",
                Schema = RowSchema,
                Ordering = "ORDER BY ExchangeTimestamp, Id (deterministic tie-break on Id)"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{date:yyyy-MM-dd}: exported {instruments.Count} instruments, {totalTicks:N0} ticks total -> {dayDir}");
        }
        return 0;
    }
}
