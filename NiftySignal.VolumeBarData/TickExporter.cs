using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// Read-only tick-by-tick export of the nearest-expiry option chain + front-month future for one
/// or more sessions, to compact NDJSON files. Exists so research can run entirely against files
/// from an environment with no route to the live database -- never mutates the source database,
/// never touches NiftySignal.Host/NiftySignal.Dashboard, no trading logic here at all.
/// </summary>
public static class TickExporter
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static DateTimeOffset At(DateOnly date, int hour, int minute) => new(date.ToDateTime(new TimeOnly(hour, minute)), Ist);

    public static async Task<int> RunAsync(DbContextOptions<NiftySignalDbContext> options, string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: export-ticks <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--out=research-ticks] [--underlying=NIFTY] [--force]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "research-ticks";
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
        Console.WriteLine($"export-ticks: {dates.Count} session date(s) found for {underlying} in [{from:yyyy-MM-dd},{to:yyyy-MM-dd}].");

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
            var instruments = options_.Concat(futures)
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
                    // Positional array, not named fields, to keep a multi-session export size manageable:
                    // [id, exchangeTimestamp, receivedAt, lastPrice, bid1Price, ask1Price, bid1Qty, ask1Qty, volume, openInterest]
                    var row = new object?[]
                    {
                        t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                        t.Depth?.Bid1Price ?? 0, t.Depth?.Ask1Price ?? 0, t.Depth?.Bid1Qty ?? 0, t.Depth?.Ask1Qty ?? 0,
                        t.Volume, t.OpenInterest
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row));
                    count++;
                }
                totalTicks += count;
                Console.WriteLine($"  {date:yyyy-MM-dd} {inst.Token} ({inst.InstrumentType} {inst.OptionType} {inst.StrikePrice}): {count:N0} ticks");
            }
            await File.WriteAllTextAsync(doneMarker, JsonSerializer.Serialize(new
            {
                Date = date,
                Underlying = underlying,
                NearestExpiry = nearestExpiry,
                InstrumentCount = instruments.Count,
                TotalTicks = totalTicks,
                ExportedAtUtc = DateTimeOffset.UtcNow,
                Schema = "[id, exchangeTimestamp, receivedAt, lastPrice, bid1Price, ask1Price, bid1Qty, ask1Qty, volume, openInterest]"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{date:yyyy-MM-dd}: exported {instruments.Count} instruments, {totalTicks:N0} ticks total -> {dayDir}");
        }
        return 0;
    }
}
