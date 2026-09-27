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
/// 2026-09-27 pure-visualization research tool (user's own explicit spec) -- NOT a trading
/// experiment, NOT a modification of Pattern A/B. For each of the two allowed Design sessions
/// (2026-09-22, 2026-09-23), builds two independent per-bar datasets:
///   - "Time" cadence: the SAME fixed-time future-bar builder already used for the PT0/PT1
///     experiment (ReversalResearch.BuildTimeBasedFutureBars, 30-second buckets -- reused exactly,
///     no new interval invented).
///   - "Volume" cadence: the SAME 13,000-contract volume-threshold builder (FutureEventBarBuilder)
///     P0/P1 already use.
/// For EACH cadence's own bar series, calls ReversalResearch.Patterns() directly (made `internal`
/// for this purpose, zero logic change -- see the accompanying accessibility-only diff) to get the
/// existing, unmodified Pattern A/B classification (State/StateEntry/Full) for every bar, never a
/// reimplementation of that sign-based rule. On top of that frozen reference, computes the new
/// ATM+/-2 median percentage/log-return band (same construction as BandPercentageExperiment.cs) and
/// chains its median log returns into two synthetic CE/PE strength indices, purely for visual
/// inspection. Writes one wide CSV per (day, cadence) -- 4 files total -- with every individual
/// strike's token/start/end/return retained for independent verification.
/// </summary>
public static class RelativeStrengthVisualization
{
    static readonly HashSet<DateOnly> AllowedSessions = [new(2026, 9, 22), new(2026, 9, 23)];
    const int TimeCadenceBucketSeconds = 30; // Reused verbatim from the existing PT0/PT1 time-bar construction.
    const long VolumeCadenceThreshold = 13000; // Reused verbatim from the existing P0/P1 volume-bar construction.

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public sealed record StrikeLeg(decimal Strike, string? CeToken, string? PeToken,
        decimal? CeStart, decimal? CeEnd, decimal? PeStart, decimal? PeEnd);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: relative-strength-viz <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=.]");
            return 1;
        }
        Console.WriteLine($"Reusing existing fixed-time cadence: BuildTimeBasedFutureBars, {TimeCadenceBucketSeconds}-second buckets " +
            "(the same time-based future-bar construction the PT0/PT1 experiment already uses). " +
            $"Volume cadence: FutureEventBarBuilder, {VolumeCadenceThreshold}-contract threshold (same as P0/P1).");

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
            Console.WriteLine("Refusing: this tool is restricted to 2026-09-22 and 2026-09-23 only (Design sessions, per spec). " +
                $"Rejected: {string.Join(',', rejected.Select(x => x.Date))}.");
            return 1;
        }

        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"\nLoading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"relstrength-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            foreach (var cadenceType in new[] { "time", "volume" })
            {
                var rows = await BuildRowsAsync(db, date, cadenceType);
                var path = Path.Combine(outDir, $"{date:yyyy-MM-dd}_{cadenceType}_relative_strength.csv");
                await File.WriteAllTextAsync(path, ToCsv(rows));
                Console.WriteLine($"  {cadenceType} cadence: {rows.Count} rows -> {path}");
            }
            await db.Database.EnsureDeletedAsync();
        }
        return 0;
    }

    public sealed record Row(
        DateOnly TradingDate, DateTimeOffset Timestamp, string CadenceType,
        DateTimeOffset WindowStartTimestamp, DateTimeOffset WindowEndTimestamp, double WindowDurationSeconds,
        decimal FuturesStartPrice, decimal FuturesEndPrice, decimal FuturesChangePoints, decimal? FuturesReturnPct, double? FuturesLogReturn,
        long BaseBarActualVolume, long RollingContextActualVolume,
        decimal AtmStrike, int ValidCeStrikeCount, int ValidPeStrikeCount,
        decimal? MedianCeReturnPct, decimal? MedianPeReturnPct, double? MedianCeLogReturn, double? MedianPeLogReturn,
        decimal? MinCeReturnPct, decimal? MaxCeReturnPct, decimal? MinPeReturnPct, decimal? MaxPeReturnPct,
        decimal? CeIqr, decimal? PeIqr, double? BandRelativeShift,
        double CeStrengthIndex, double PeStrengthIndex, double? RelativeStrengthIndex, double? LogRelativeStrengthIndex,
        string ExistingAtmState, bool ExistingFullSurfaceState, bool IsExistingStateEntry,
        int BarIndex, List<StrikeLeg> Legs);

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
        // The EXISTING, unmodified Pattern A/B classification -- applied to THIS cadence's own bars.
        var patternRows = ReversalResearch.Patterns(chain, ticks, bars, available);

        static bool Fresh(ReversalResearch.Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);
        decimal? PriceAt(string token, DateTimeOffset time)
        {
            var p = ReversalResearch.Before(ticks[token], time);
            return Fresh(p, time) ? p!.Price : null;
        }
        static decimal Median(List<decimal> v) { var s = v.Order().ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }
        static double MedianD(List<double> v) { var s = v.Order().ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }
        static decimal Pctl(List<decimal> v, double p) { var s = v.Order().ToList(); var k = (s.Count - 1) * p; var f = (int)Math.Floor(k); var c = (int)Math.Ceiling(k); return f == c ? s[f] : s[f] + (s[c] - s[f]) * (decimal)(k - f); }

        var rows = new List<Row>();
        double ceIndex = 100.0, peIndex = 100.0;
        foreach (var pr in patternRows)
        {
            var b = pr.Index;
            if (b >= bars.Count || bars[b].IsFinalPartialBar) { continue; }
            var firstIndex = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b, 180);
            var first = bars[firstIndex];
            var current = bars[b];
            var atmStrike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);

            var legs = new List<StrikeLeg>();
            var ceRets = new List<decimal>(); var peRets = new List<decimal>();
            var ceLogs = new List<double>(); var peLogs = new List<double>(); var shifts = new List<double>();
            var lo = Math.Max(0, idx - 2); var hi = Math.Min(strikes.Count - 1, idx + 2);
            for (var j = lo; j <= hi; j++)
            {
                var k = strikes[j];
                byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst);
                byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst);
                decimal? ceS = null, ceE = null, peS = null, peE = null;
                if (ceInst is not null) { ceS = PriceAt(ceInst.Token, first.StartTimestamp); ceE = PriceAt(ceInst.Token, current.EndTimestamp); }
                if (peInst is not null) { peS = PriceAt(peInst.Token, first.StartTimestamp); peE = PriceAt(peInst.Token, current.EndTimestamp); }
                legs.Add(new(k, ceInst?.Token, peInst?.Token, ceS, ceE, peS, peE));
                if (ceS is > 0 && ceE is not null) { var r = ceE.Value / ceS.Value - 1; ceRets.Add(r); ceLogs.Add(Math.Log((double)(ceE.Value / ceS.Value))); }
                if (peS is > 0 && peE is not null) { var r = peE.Value / peS.Value - 1; peRets.Add(r); peLogs.Add(Math.Log((double)(peE.Value / peS.Value))); }
                if (ceS is > 0 && ceE is not null && peS is > 0 && peE is not null)
                { shifts.Add(Math.Log((double)(peE.Value / peS.Value)) - Math.Log((double)(ceE.Value / ceS.Value))); }
            }

            decimal? medCe = ceRets.Count == 5 ? Median(ceRets) : null;
            decimal? medPe = peRets.Count == 5 ? Median(peRets) : null;
            double? medCeLog = ceLogs.Count == 5 ? MedianD(ceLogs) : null;
            double? medPeLog = peLogs.Count == 5 ? MedianD(peLogs) : null;
            double? shift = shifts.Count == 5 ? MedianD(shifts) : null;

            // Carry-forward when this bar's band isn't fully available (documented assumption --
            // "chain the median log returns chronologically" doesn't specify what to do on a gap;
            // holding the index flat rather than treating a missing observation as a zero return).
            if (medCeLog is not null) { ceIndex *= Math.Exp(medCeLog.Value); }
            if (medPeLog is not null) { peIndex *= Math.Exp(medPeLog.Value); }

            var volumeInWindow = bars.Skip(firstIndex).Take(b - firstIndex + 1).Sum(x => x.Volume);

            rows.Add(new Row(date, pr.Time, cadenceType, first.StartTimestamp, current.EndTimestamp,
                (current.EndTimestamp - first.StartTimestamp).TotalSeconds,
                first.Open, current.Close, pr.Move,
                first.Open != 0 ? current.Close / first.Open - 1 : null,
                first.Open > 0 ? Math.Log((double)(current.Close / first.Open)) : null,
                current.Volume, volumeInWindow,
                atmStrike, ceRets.Count, peRets.Count,
                medCe, medPe, medCeLog, medPeLog,
                ceRets.Count == 5 ? ceRets.Min() : null, ceRets.Count == 5 ? ceRets.Max() : null,
                peRets.Count == 5 ? peRets.Min() : null, peRets.Count == 5 ? peRets.Max() : null,
                ceRets.Count == 5 ? Pctl(ceRets, 0.75) - Pctl(ceRets, 0.25) : null,
                peRets.Count == 5 ? Pctl(peRets, 0.75) - Pctl(peRets, 0.25) : null,
                shift, ceIndex, peIndex,
                ceIndex != 0 ? peIndex / ceIndex : null, ceIndex != 0 ? Math.Log(peIndex / ceIndex) : null,
                pr.State, pr.Full, pr.StateEntry, b, legs));
        }
        return rows;
    }

    static string ToCsv(List<Row> rows)
    {
        var sb = new StringBuilder();
        var header = new List<string>
        {
            "TradingDate","Timestamp","CadenceType","WindowStartTimestamp","WindowEndTimestamp","WindowDurationSeconds",
            "FuturesStartPrice","FuturesEndPrice","FuturesChangePoints","FuturesReturnPct","FuturesLogReturn",
            "BaseBarActualVolume","RollingContextActualVolume",
            "ATMStrike","ValidCEStrikeCount","ValidPEStrikeCount",
            "MedianCEReturnPct","MedianPEReturnPct","MedianCELogReturn","MedianPELogReturn",
            "MinCEReturnPct","MaxCEReturnPct","MinPEReturnPct","MaxPEReturnPct","CEIQR","PEIQR","BandRelativeShift",
            "CEStrengthIndex","PEStrengthIndex","RelativeStrengthIndex","LogRelativeStrengthIndex",
            "ExistingAtmState","ExistingFullSurfaceState","IsExistingStateEntry","BarIndex",
        };
        var labels = new[] { "ATMm2", "ATMm1", "ATM", "ATMp1", "ATMp2" };
        foreach (var lbl in labels)
        {
            header.AddRange(new[]
            {
                $"{lbl}_Strike", $"{lbl}_CEToken", $"{lbl}_CEStart", $"{lbl}_CEEnd", $"{lbl}_CEReturnPct",
                $"{lbl}_PEToken", $"{lbl}_PEStart", $"{lbl}_PEEnd", $"{lbl}_PEReturnPct",
            });
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
            var fields = new List<object?>
            {
                r.TradingDate, r.Timestamp, r.CadenceType, r.WindowStartTimestamp, r.WindowEndTimestamp, r.WindowDurationSeconds,
                r.FuturesStartPrice, r.FuturesEndPrice, r.FuturesChangePoints, r.FuturesReturnPct, r.FuturesLogReturn,
                r.BaseBarActualVolume, r.RollingContextActualVolume,
                r.AtmStrike, r.ValidCeStrikeCount, r.ValidPeStrikeCount,
                r.MedianCeReturnPct, r.MedianPeReturnPct, r.MedianCeLogReturn, r.MedianPeLogReturn,
                r.MinCeReturnPct, r.MaxCeReturnPct, r.MinPeReturnPct, r.MaxPeReturnPct, r.CeIqr, r.PeIqr, r.BandRelativeShift,
                r.CeStrengthIndex, r.PeStrengthIndex, r.RelativeStrengthIndex, r.LogRelativeStrengthIndex,
                r.ExistingAtmState, r.ExistingFullSurfaceState, r.IsExistingStateEntry, r.BarIndex,
            };
            var byStrike = r.Legs.ToDictionary(l => l.Strike);
            var ordered = r.Legs.OrderBy(l => l.Strike).ToList();
            // Pad to exactly 5 legs positioned around ATM (some sessions near the edge of the chain
            // may have fewer than 2 strikes on one side -- leave those columns blank, never fabricate).
            var atmIdx = ordered.FindIndex(l => l.Strike == r.AtmStrike);
            for (var pos = -2; pos <= 2; pos++)
            {
                var legIdx = atmIdx + pos;
                StrikeLeg? leg = legIdx >= 0 && legIdx < ordered.Count ? ordered[legIdx] : null;
                fields.Add(leg?.Strike); fields.Add(leg?.CeToken); fields.Add(leg?.CeStart); fields.Add(leg?.CeEnd);
                fields.Add(leg?.CeStart is > 0 && leg?.CeEnd is not null ? leg.CeEnd / leg.CeStart - 1 : null);
                fields.Add(leg?.PeToken); fields.Add(leg?.PeStart); fields.Add(leg?.PeEnd);
                fields.Add(leg?.PeStart is > 0 && leg?.PeEnd is not null ? leg.PeEnd / leg.PeStart - 1 : null);
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
