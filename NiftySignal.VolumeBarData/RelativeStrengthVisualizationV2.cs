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
/// 2026-09-27 V2 of the CE/PE relative-strength visualization (user's own explicit spec) -- fixes
/// the V1 methodology issue directly: V1 chained the same overlapping 180-second adaptive-window
/// median log return bar-to-bar into a "cumulative" index, which is not a non-overlapping return
/// series at all -- consecutive bars' 180s windows mostly overlap, so the same underlying option
/// move got compounded into the index multiple times, and on DTE0 (many bars/thin premiums) that
/// blew up to astronomical values. This version separates two genuinely different things:
///   (A) INCREMENTAL returns (strictly non-overlapping: PreviousCadenceEnd -> CurrentCadenceEnd) --
///       the only thing ever chained into CEIncrementalIndex/PEIncrementalIndex.
///   (B) ROLLING returns at several fixed, predeclared horizons (60/120/180/300s for time cadence;
///       2/4/8 bars for volume cadence) -- may overlap by construction, used only for local
///       structure, NEVER compounded into an index.
/// Still never touches Pattern A/B: ReversalResearch.Patterns() (already `internal`, unchanged) is
/// called on each cadence's own bars for the reference overlay fields, exactly as V1 did.
/// NOT a trading experiment -- no thresholds, no signals, no P&L, no horizon optimization against
/// future Nifty movement.
/// </summary>
public static class RelativeStrengthVisualizationV2
{
    static readonly HashSet<DateOnly> AllowedSessions = [new(2026, 9, 22), new(2026, 9, 23)];
    const int TimeCadenceBucketSeconds = 30;
    const long VolumeCadenceThreshold = 13000;
    static readonly int[] TimeHorizonSeconds = [60, 120, 180, 300];
    static readonly int[] VolumeHorizonBars = [2, 4, 8];

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: relative-strength-viz-v2 <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=.]");
            return 1;
        }
        Console.WriteLine("Reusing existing fixed-time cadence: BuildTimeBasedFutureBars, 30-second buckets (same as PT0/PT1). " +
            "Volume cadence: FutureEventBarBuilder, 13000-contract threshold (same as P0/P1). " +
            $"Time rolling horizons: {string.Join(',', TimeHorizonSeconds)}s. Volume rolling horizons: {string.Join(',', VolumeHorizonBars)} bars.");

        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : ".";

        var dates = Directory.Exists(ticksDir)
            ? Directory.GetDirectories(ticksDir)
                .Select(d => (Dir: d, Date: DateOnly.ParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList()
            : [];
        var rejected = dates.Where(x => !AllowedSessions.Contains(x.Date)).ToList();
        if (rejected.Count > 0)
        {
            Console.WriteLine($"Refusing: restricted to 2026-09-22/23 only. Rejected: {string.Join(',', rejected.Select(x => x.Date))}.");
            return 1;
        }

        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"\nLoading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"relstrength-v2-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            foreach (var cadenceType in new[] { "time", "volume" })
            {
                var rows = await BuildRowsAsync(db, date, cadenceType);
                var path = Path.Combine(outDir, $"{date:yyyy-MM-dd}_{cadenceType}_relative_strength_v2.csv");
                await File.WriteAllTextAsync(path, ToCsv(rows, cadenceType));
                var durs = cadenceType == "volume" ? DescribeDurations(rows) : "n/a (fixed 30s)";
                Console.WriteLine($"  {cadenceType} cadence: {rows.Count} rows -> {path}");
                if (cadenceType == "volume") { Console.WriteLine($"    Bar/rolling duration summary: {durs}"); }
            }
            await db.Database.EnsureDeletedAsync();
        }
        return 0;
    }

    static string DescribeDurations(List<Row> rows)
    {
        static double Pctl(List<double> v, double p) { var s = v.Order().ToList(); var k = (s.Count - 1) * p; var f = (int)Math.Floor(k); var c = (int)Math.Ceiling(k); return f == c ? s[f] : s[f] + (s[c] - s[f]) * (k - f); }
        var baseDur = rows.Select(r => r.BaseBarDurationSeconds).ToList();
        var d2 = rows.Where(r => r.Rolling2BarsDurationSeconds is not null).Select(r => r.Rolling2BarsDurationSeconds!.Value).ToList();
        var d4 = rows.Where(r => r.Rolling4BarsDurationSeconds is not null).Select(r => r.Rolling4BarsDurationSeconds!.Value).ToList();
        var d8 = rows.Where(r => r.Rolling8BarsDurationSeconds is not null).Select(r => r.Rolling8BarsDurationSeconds!.Value).ToList();
        string Sum(List<double> v) => v.Count == 0 ? "n/a" : $"median={Pctl(v, 0.5):F1}s P25={Pctl(v, 0.25):F1}s P75={Pctl(v, 0.75):F1}s min={v.Min():F1}s max={v.Max():F1}s";
        return $"BaseBar[{Sum(baseDur)}] 2Bars[{Sum(d2)}] 4Bars[{Sum(d4)}] 8Bars[{Sum(d8)}]";
    }

    public sealed record Leg(decimal Strike, string? CeToken, string? PeToken, Dictionary<string, decimal?> CePrices, Dictionary<string, decimal?> PePrices);

    public sealed record Row(
        DateOnly TradingDate, DateTimeOffset Timestamp, string CadenceType, int BarIndex,
        double BaseBarDurationSeconds, double? Rolling2BarsDurationSeconds, double? Rolling4BarsDurationSeconds, double? Rolling8BarsDurationSeconds,
        DateTimeOffset IncrementalStart, DateTimeOffset IncrementalEnd,
        double? IncrementalCeLogReturn, double? IncrementalPeLogReturn, double? IncrementalRelativeShift,
        double CeIncrementalIndex, double PeIncrementalIndex, double? IncrementalLogRelativeIndex,
        decimal FuturesEndPrice, decimal AtmStrike, int ValidCeStrikeCount, int ValidPeStrikeCount,
        Dictionary<string, double?> CeRolling, Dictionary<string, double?> PeRolling, Dictionary<string, double?> RollingShift,
        string ExistingAtmState, bool ExistingFullSurfaceState, bool IsExistingStateEntry,
        List<Leg> Legs, List<string> RefPoints);

    public static async Task<List<Row>> BuildRowsAsync(NiftySignalDbContext db, DateOnly date, string cadenceType)
    {
        var chainAll = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY"
            && i.InstrumentType == InstrumentType.Option && i.ExpiryDate >= date).ToListAsync();
        var expiry = chainAll.Min(i => i.ExpiryDate)!.Value;
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

        var future = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate).FirstAsync();
        var futureReceipts = await db.Ticks.Where(t => t.Token == future.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).Select(t => new { t.ExchangeTimestamp, t.ReceivedAt }).ToListAsync();

        List<FutureEventBar> bars;
        if (cadenceType == "volume")
        {
            bars = await FutureEventBarBuilder.BuildDayAsync(db, date, VolumeCadenceThreshold, CancellationToken.None);
        }
        else
        {
            var futureTicks = await db.Ticks.Where(t => t.Token == future.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end && t.LastPrice > 0)
                .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id)
                .Select(t => new ReversalResearch.Print(t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                    t.Depth == null ? 0 : t.Depth.Bid1Price, t.Depth == null ? 0 : t.Depth.Ask1Price,
                    t.Depth == null ? 0 : t.Depth.Bid1Qty, t.Depth == null ? 0 : t.Depth.Ask1Qty)).ToListAsync();
            bars = ReversalResearch.BuildTimeBasedFutureBars(futureTicks, start, end, TimeCadenceBucketSeconds);
        }

        var available = ReversalResearch.ComputeAvailability(bars, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
        // Existing, unmodified Pattern A/B classification -- reference overlay only.
        var patternRows = ReversalResearch.Patterns(chain, ticks, bars, available);
        var patternByIndex = patternRows.ToDictionary(p => p.Index);

        static bool Fresh(ReversalResearch.Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);
        decimal? PriceAt(string token, DateTimeOffset time)
        {
            var p = ReversalResearch.Before(ticks[token], time);
            return Fresh(p, time) ? p!.Price : null;
        }
        static double MedianD(List<double> v) { var s = v.Order().ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }

        // Reference points (besides "current"): "prev" (incremental, non-overlapping) plus each
        // predeclared rolling horizon, named by its own label for column naming.
        var rollingLabels = cadenceType == "time"
            ? TimeHorizonSeconds.Select(s => $"{s}s").ToArray()
            : VolumeHorizonBars.Select(n => $"{n}Bars").ToArray();
        int NBack(string label) => cadenceType == "time" ? int.Parse(label[..^1]) / TimeCadenceBucketSeconds : int.Parse(label[..^"Bars".Length]);

        var rows = new List<Row>();
        double ceIndex = 100.0, peIndex = 100.0;
        for (var b = 0; b < bars.Count; b++)
        {
            if (bars[b].IsFinalPartialBar) { continue; }
            var current = bars[b];
            var atmStrike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);
            var lo = Math.Max(0, idx - 2); var hi = Math.Min(strikes.Count - 1, idx + 2);

            // "prev" reference point: strictly the immediately preceding bar's own end (non-overlapping
            // by construction); for the very first bar of the day, there is no previous bar, so this
            // falls back to that bar's OWN start -- an explicit, documented edge case, not a silent gap.
            var prevTimestamp = b == 0 ? current.StartTimestamp : bars[b - 1].EndTimestamp;

            var refTimestamps = new Dictionary<string, DateTimeOffset?> { ["prev"] = prevTimestamp };
            foreach (var label in rollingLabels)
            {
                var nBack = NBack(label);
                refTimestamps[label] = b >= nBack ? bars[b - nBack].EndTimestamp : null;
            }

            var legs = new List<Leg>();
            var ceLogsByRef = refTimestamps.Keys.ToDictionary(k => k, _ => new List<double>());
            var peLogsByRef = refTimestamps.Keys.ToDictionary(k => k, _ => new List<double>());
            var shiftByRef = refTimestamps.Keys.ToDictionary(k => k, _ => new List<double>());
            var ceValidCount = 0; var peValidCount = 0;

            for (var j = lo; j <= hi; j++)
            {
                var k = strikes[j];
                byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst);
                byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst);
                var ceCurrent = ceInst is not null ? PriceAt(ceInst.Token, current.EndTimestamp) : null;
                var peCurrent = peInst is not null ? PriceAt(peInst.Token, current.EndTimestamp) : null;
                var cePrices = new Dictionary<string, decimal?> { ["current"] = ceCurrent };
                var pePrices = new Dictionary<string, decimal?> { ["current"] = peCurrent };
                foreach (var (label, ts) in refTimestamps)
                {
                    var ceAt = ts is not null && ceInst is not null ? PriceAt(ceInst.Token, ts.Value) : null;
                    var peAt = ts is not null && peInst is not null ? PriceAt(peInst.Token, ts.Value) : null;
                    cePrices[label] = ceAt; pePrices[label] = peAt;
                    if (ceAt is > 0 && ceCurrent is not null) { ceLogsByRef[label].Add(Math.Log((double)(ceCurrent.Value / ceAt.Value))); }
                    if (peAt is > 0 && peCurrent is not null) { peLogsByRef[label].Add(Math.Log((double)(peCurrent.Value / peAt.Value))); }
                    if (ceAt is > 0 && ceCurrent is not null && peAt is > 0 && peCurrent is not null)
                    { shiftByRef[label].Add(Math.Log((double)(peCurrent.Value / peAt.Value)) - Math.Log((double)(ceCurrent.Value / ceAt.Value))); }
                }
                legs.Add(new(k, ceInst?.Token, peInst?.Token, cePrices, pePrices));
                if (ceCurrent is not null) { ceValidCount++; }
                if (peCurrent is not null) { peValidCount++; }
            }

            double? MedOrNull(List<double> v) => v.Count == 5 ? MedianD(v) : null;
            var incCe = MedOrNull(ceLogsByRef["prev"]); var incPe = MedOrNull(peLogsByRef["prev"]); var incShift = MedOrNull(shiftByRef["prev"]);
            if (incCe is not null) { ceIndex *= Math.Exp(incCe.Value); }
            if (incPe is not null) { peIndex *= Math.Exp(incPe.Value); }

            var ceRolling = rollingLabels.ToDictionary(l => l, l => MedOrNull(ceLogsByRef[l]));
            var peRolling = rollingLabels.ToDictionary(l => l, l => MedOrNull(peLogsByRef[l]));
            var shiftRolling = rollingLabels.ToDictionary(l => l, l => MedOrNull(shiftByRef[l]));

            double? RollingDuration(int nBack) => b >= nBack ? (current.EndTimestamp - bars[b - nBack].EndTimestamp).TotalSeconds : (double?)null;

            var pr = patternByIndex[b];
            rows.Add(new Row(date, current.EndTimestamp, cadenceType, b,
                (current.EndTimestamp - current.StartTimestamp).TotalSeconds,
                cadenceType == "volume" ? RollingDuration(2) : null,
                cadenceType == "volume" ? RollingDuration(4) : null,
                cadenceType == "volume" ? RollingDuration(8) : null,
                prevTimestamp, current.EndTimestamp,
                incCe, incPe, incShift, ceIndex, peIndex, ceIndex != 0 ? Math.Log(peIndex / ceIndex) : null,
                current.Close, atmStrike, ceValidCount, peValidCount,
                ceRolling, peRolling, shiftRolling,
                pr.State, pr.Full, pr.StateEntry, legs, [.. refTimestamps.Keys]));
        }
        return rows;
    }

    static string ToCsv(List<Row> rows, string cadenceType)
    {
        var sb = new StringBuilder();
        var header = new List<string>
        {
            "TradingDate","Timestamp","CadenceType","BarIndex","BaseBarDurationSeconds",
        };
        if (cadenceType == "volume") { header.AddRange(new[] { "Rolling2BarsDurationSeconds", "Rolling4BarsDurationSeconds", "Rolling8BarsDurationSeconds" }); }
        header.AddRange(new[]
        {
            "IncrementalStart","IncrementalEnd","IncrementalCELogReturn","IncrementalPELogReturn","IncrementalRelativeShift",
            "CEIncrementalIndex","PEIncrementalIndex","IncrementalLogRelativeIndex",
            "FuturesEndPrice","ATMStrike","ValidCEStrikeCount","ValidPEStrikeCount",
        });
        var rollingLabels = cadenceType == "time" ? TimeHorizonSeconds.Select(s => $"{s}s").ToArray() : VolumeHorizonBars.Select(n => $"{n}Bars").ToArray();
        foreach (var lbl in rollingLabels) { header.AddRange(new[] { $"CERollingLogReturn_{lbl}", $"PERollingLogReturn_{lbl}", $"RelativeShift_{lbl}" }); }
        header.AddRange(new[] { "ExistingAtmState", "ExistingFullSurfaceState", "IsExistingStateEntry" });

        var strikeLabels = new[] { "ATMm2", "ATMm1", "ATM", "ATMp1", "ATMp2" };
        var refLabels = new[] { "current", "prev" }.Concat(rollingLabels).ToArray();
        foreach (var sl in strikeLabels)
        {
            header.Add($"{sl}_Strike"); header.Add($"{sl}_CEToken"); header.Add($"{sl}_PEToken");
            foreach (var rl in refLabels) { header.Add($"{sl}_CE_At_{rl}"); header.Add($"{sl}_PE_At_{rl}"); }
        }
        sb.AppendLine(string.Join(',', header));

        string Fmt(object? v) => v switch
        {
            null => "",
            DateTimeOffset dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };

        foreach (var r in rows)
        {
            var fields = new List<object?> { r.TradingDate, r.Timestamp, r.CadenceType, r.BarIndex, r.BaseBarDurationSeconds };
            if (cadenceType == "volume") { fields.Add(r.Rolling2BarsDurationSeconds); fields.Add(r.Rolling4BarsDurationSeconds); fields.Add(r.Rolling8BarsDurationSeconds); }
            fields.Add(r.IncrementalStart); fields.Add(r.IncrementalEnd);
            fields.Add(r.IncrementalCeLogReturn); fields.Add(r.IncrementalPeLogReturn); fields.Add(r.IncrementalRelativeShift);
            fields.Add(r.CeIncrementalIndex); fields.Add(r.PeIncrementalIndex); fields.Add(r.IncrementalLogRelativeIndex);
            fields.Add(r.FuturesEndPrice); fields.Add(r.AtmStrike); fields.Add(r.ValidCeStrikeCount); fields.Add(r.ValidPeStrikeCount);
            foreach (var lbl in rollingLabels) { fields.Add(r.CeRolling[lbl]); fields.Add(r.PeRolling[lbl]); fields.Add(r.RollingShift[lbl]); }
            fields.Add(r.ExistingAtmState); fields.Add(r.ExistingFullSurfaceState); fields.Add(r.IsExistingStateEntry);

            var ordered = r.Legs.OrderBy(l => l.Strike).ToList();
            var atmIdx = ordered.FindIndex(l => l.Strike == r.AtmStrike);
            for (var pos = -2; pos <= 2; pos++)
            {
                var legIdx = atmIdx + pos;
                Leg? leg = legIdx >= 0 && legIdx < ordered.Count ? ordered[legIdx] : null;
                fields.Add(leg?.Strike); fields.Add(leg?.CeToken); fields.Add(leg?.PeToken);
                foreach (var rl in refLabels)
                {
                    fields.Add(leg is not null && leg.CePrices.TryGetValue(rl, out var cv) ? cv : null);
                    fields.Add(leg is not null && leg.PePrices.TryGetValue(rl, out var pv) ? pv : null);
                }
            }
            sb.AppendLine(string.Join(',', fields.Select(Fmt)));
        }
        return sb.ToString();
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
