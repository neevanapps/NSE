using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-27 raw-data exporter for the "Option-Futures Dislocation" experiment (user's own explicit
/// spec) -- NOT a trading experiment, NOT a modification of Pattern A/B. Reuses the exact frozen
/// architecture already validated in this research thread: 13,000-contract futures bars
/// (FutureEventBarBuilder), the 180-second adaptive window (AdaptiveWindowAnalysis.FindWindowStartIndex),
/// ATM/ATM+/-2 selection and the existing Pattern A/B classification (ReversalResearch.Patterns(),
/// already `internal`, unchanged). All statistical work (leave-one-session-out Huber regression,
/// dislocation scoring) happens downstream in Python against this CSV -- this module only produces
/// the per-bar raw variables listed in the spec's section 3, nothing derived beyond that.
/// </summary>
public static class OptionFuturesDislocationExport
{
    // 10 primary sessions + 2 discovery-reference sessions, per spec. 09-24/25 (or later) refused.
    static readonly HashSet<DateOnly> AllowedSessions =
    [
        new(2026, 9, 4), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
        new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17), new(2026, 9, 18), new(2026, 9, 21),
        new(2026, 9, 22), new(2026, 9, 23),
    ];
    const long VolumeThreshold = 13000;

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: option-futures-dislocation-export <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=dislocation-raw.csv]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "dislocation-raw.csv";

        var dates = Directory.Exists(ticksDir)
            ? Directory.GetDirectories(ticksDir)
                .Select(d => (Dir: d, Date: DateOnly.ParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList()
            : [];
        var rejected = dates.Where(x => !AllowedSessions.Contains(x.Date)).ToList();
        if (rejected.Count > 0)
        {
            Console.WriteLine($"Refusing: restricted to the 10 primary + 2 discovery sessions only. Rejected: {string.Join(',', rejected.Select(x => x.Date))}.");
            return 1;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', new[]
        {
            "TradingDate","Timestamp","Dte","BarIndex",
            "WindowStartTimestamp","WindowEndTimestamp","WindowDurationSeconds","WindowBarCount","ActualWindowVolume",
            "FuturesStartPrice","FuturesEndPrice","FuturesChangePoints","FuturesLogReturn","AbsFuturesLogReturn","ActivityRate",
            "MedianCELogReturn","MedianPELogReturn","BandRelativeShift","ValidCEStrikeCount","ValidPEStrikeCount",
            "ExistingAtmState","ExistingFullSurfaceState","IsExistingStateEntry",
            "Forward1","Forward2","Forward4",
        }));

        var totalRows = 0;
        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"Loading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"dislocation-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            var rowCount = await AppendDayAsync(db, date, sb);
            totalRows += rowCount;
            Console.WriteLine($"  {date:yyyy-MM-dd}: {rowCount} observations.");
            await db.Database.EnsureDeletedAsync();
        }
        await File.WriteAllTextAsync(outPath, sb.ToString());
        Console.WriteLine($"Saved {outPath} ({totalRows} total rows across {dates.Count} sessions).");
        return 0;
    }

    static async Task<int> AppendDayAsync(NiftySignalDbContext db, DateOnly date, StringBuilder sb)
    {
        var chainAll = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY"
            && i.InstrumentType == InstrumentType.Option && i.ExpiryDate >= date).ToListAsync();
        var expiry = chainAll.Min(i => i.ExpiryDate)!.Value;
        var dte = expiry.DayNumber - date.DayNumber;
        var chain = chainAll.Where(i => i.ExpiryDate == expiry).OrderBy(i => i.Token).ToDictionary(i => i.Token);
        var strikes = chain.Values.Select(i => i.StrikePrice!.Value).Distinct().Order().ToList();
        var byStrikeSide = chain.Values.Where(i => i.StrikePrice is not null)
            .ToDictionary(i => (i.StrikePrice!.Value, i.OptionType), i => i);

        var start = ReversalResearch.At(date, 9, 15).ToUniversalTime();
        var end = ReversalResearch.At(date, 15, 30).ToUniversalTime();
        var ticks = new Dictionary<string, List<ReversalResearch.Print>>();
        foreach (var inst in chain.Values)
        {
            var token = inst.Token;
            ticks[token] = await db.Ticks.Where(t => t.Token == token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end && t.LastPrice > 0)
                .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id)
                .Select(t => new ReversalResearch.Print(t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                    t.Depth == null ? 0 : t.Depth.Bid1Price, t.Depth == null ? 0 : t.Depth.Ask1Price,
                    t.Depth == null ? 0 : t.Depth.Bid1Qty, t.Depth == null ? 0 : t.Depth.Ask1Qty, t.Volume)).ToListAsync();
        }

        var bars = await FutureEventBarBuilder.BuildDayAsync(db, date, VolumeThreshold, CancellationToken.None);
        var futureFirst = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate).FirstAsync();
        var futureReceipts = await db.Ticks.Where(t => t.Token == futureFirst.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).Select(t => new { t.ExchangeTimestamp, t.ReceivedAt }).ToListAsync();
        var available = ReversalResearch.ComputeAvailability(bars, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
        var patternRows = ReversalResearch.Patterns(chain, ticks, bars, available);

        static bool Fresh(ReversalResearch.Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);
        decimal? PriceAt(string token, DateTimeOffset time)
        {
            var p = ReversalResearch.Before(ticks[token], time);
            return Fresh(p, time) ? p!.Price : null;
        }
        static double MedianD(List<double> v) { var s = v.Order().ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }

        var count = 0;
        foreach (var pr in patternRows)
        {
            var b = pr.Index;
            if (b >= bars.Count || bars[b].IsFinalPartialBar) { continue; }
            var firstIndex = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b, 180);
            var first = bars[firstIndex];
            var current = bars[b];
            var atmStrike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);
            var lo = Math.Max(0, idx - 2); var hi = Math.Min(strikes.Count - 1, idx + 2);

            var ceLogs = new List<double>(); var peLogs = new List<double>(); var shifts = new List<double>();
            var ceValid = 0; var peValid = 0;
            for (var j = lo; j <= hi; j++)
            {
                var k = strikes[j];
                byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst);
                byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst);
                var ceS = ceInst is not null ? PriceAt(ceInst.Token, first.StartTimestamp) : null;
                var ceE = ceInst is not null ? PriceAt(ceInst.Token, current.EndTimestamp) : null;
                var peS = peInst is not null ? PriceAt(peInst.Token, first.StartTimestamp) : null;
                var peE = peInst is not null ? PriceAt(peInst.Token, current.EndTimestamp) : null;
                if (ceS is > 0 && ceE is not null) { ceLogs.Add(Math.Log((double)(ceE.Value / ceS.Value))); ceValid++; }
                if (peS is > 0 && peE is not null) { peLogs.Add(Math.Log((double)(peE.Value / peS.Value))); peValid++; }
                if (ceS is > 0 && ceE is not null && peS is > 0 && peE is not null)
                { shifts.Add(Math.Log((double)(peE.Value / peS.Value)) - Math.Log((double)(ceE.Value / ceS.Value))); }
            }

            double? medCe = ceLogs.Count == 5 ? MedianD(ceLogs) : null;
            double? medPe = peLogs.Count == 5 ? MedianD(peLogs) : null;
            double? shift = shifts.Count == 5 ? MedianD(shifts) : null;

            var windowBarCount = b - firstIndex + 1;
            var windowDuration = (current.EndTimestamp - first.StartTimestamp).TotalSeconds;
            var windowVolume = bars.Skip(firstIndex).Take(windowBarCount).Sum(x => x.Volume);
            var futLogReturn = first.Open > 0 ? Math.Log((double)(current.Close / first.Open)) : (double?)null;

            var fields = new List<object?>
            {
                date, pr.Time, dte, b,
                first.StartTimestamp, current.EndTimestamp, windowDuration, windowBarCount, windowVolume,
                first.Open, current.Close, pr.Move, futLogReturn, futLogReturn is not null ? Math.Abs(futLogReturn.Value) : null,
                windowDuration > 0 ? windowBarCount / windowDuration : (double?)null,
                medCe, medPe, shift, ceValid, peValid,
                pr.State, pr.Full, pr.StateEntry,
                pr.Forward1, pr.Forward2, pr.Forward4,
            };
            AppendCsvRow(sb, fields);
            count++;
        }
        return count;
    }

    static void AppendCsvRow(StringBuilder sb, List<object?> fields)
    {
        string Fmt(object? v) => v switch
        {
            null => "",
            DateTimeOffset dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            bool bo => bo.ToString(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
        sb.AppendLine(string.Join(',', fields.Select(Fmt)));
    }

    static async Task SeedDayAsync(NiftySignalDbContext db, string dayDir, DateOnly date)
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
                if (batch.Count == 8192) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); batch.Clear(); }
            }
            if (batch.Count > 0) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
        }
    }
}
