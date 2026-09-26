using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-26 exploratory market-structure experiment (user's own explicit spec) -- NOT a trading
/// simulation, and it never touches Pattern A/B's own implementation. It reuses
/// ReversalResearch.BuildDayReportAsync UNCHANGED to source the existing Pattern A/B classification
/// (PatternRow.State/StateEntry/Full/Forward1/2/4) as the frozen reference for every bar of the day,
/// and independently recomputes the SAME futures bars (FutureEventBarBuilder, 13,000-contract
/// threshold) and SAME adaptive-window boundaries (AdaptiveWindowAnalysis.FindWindowStartIndex,
/// 180s) -- both already-public, already-tested building blocks, not a reimplementation -- so the
/// new ATM+/-2 five-strike percentage/log-return representation is evaluated against EXACTLY the
/// same market observations as the existing implementation, deterministically (same threshold, same
/// inputs -> same bars), never a parallel guess at what the reference "must have" computed.
/// Two sessions only, per spec: 2026-09-22 (DTE0) and 2026-09-23 (DTE6), both Design sessions.
/// Read-only; writes a standalone JSON report. Never modifies ReversalResearch.cs.
/// </summary>
public static class BandPercentageExperiment
{
    static readonly HashSet<DateOnly> AllowedSessions = [new(2026, 9, 22), new(2026, 9, 23)];

    public sealed record BandRow(
        DateTimeOffset Time, int Index, string ExistingAtmState, bool ExistingStateEntry, bool ExistingFullSurfaceState,
        decimal Move, decimal Future, decimal? ExistingForward1, decimal? ExistingForward2, decimal? ExistingForward4,
        bool BandAvailable, string BandPctState,
        decimal? BandCeReturnPct, decimal? BandPeReturnPct, decimal? BandRelativeShift, decimal? DirectionalRelativeStrength,
        decimal? CeStartMin, decimal? CeStartMedian, decimal? CeStartMax,
        decimal? PeStartMin, decimal? PeStartMedian, decimal? PeStartMax,
        decimal? RawCePointChangeAtm, decimal? RawPePointChangeAtm, decimal? CeReturnPctAtm, decimal? PeReturnPctAtm);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: band-pct-experiment <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=band-pct-experiment.json]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "band-pct-experiment.json";

        var dates = Directory.Exists(ticksDir)
            ? Directory.GetDirectories(ticksDir)
                .Select(d => (Dir: d, Date: DateOnly.ParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList()
            : [];
        var rejected = dates.Where(x => !AllowedSessions.Contains(x.Date)).ToList();
        if (rejected.Count > 0)
        {
            Console.WriteLine("Refusing: this experiment is restricted to 2026-09-22 and 2026-09-23 only " +
                $"(Design sessions, per spec). Rejected: {string.Join(',', rejected.Select(x => x.Date))}.");
            return 1;
        }

        var byDate = new Dictionary<string, List<BandRow>>();
        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"Loading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"band-pct-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            var rows = await BuildAsync(db, date);
            byDate[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)] = rows;
            Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} observations ({rows.Count(r => r.BandAvailable)} with a full 5-strike band).");
            await db.Database.EnsureDeletedAsync();
        }
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(byDate, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved {outPath}.");
        return 0;
    }

    /// <summary>
    /// Builds the parallel BandRow dataset for one session. Sources Existing* fields from the
    /// UNMODIFIED ReversalResearch.BuildDayReportAsync; independently recomputes the futures bars
    /// and window boundaries via the same public, deterministic building blocks Patterns() itself
    /// uses, so window/ATM alignment with the existing report is exact by construction, not assumed.
    /// </summary>
    public static async Task<List<BandRow>> BuildAsync(NiftySignalDbContext db, DateOnly date)
    {
        var report = await ReversalResearch.BuildDayReportAsync(db, date); // frozen reference, unmodified

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

        // SAME threshold (13,000 contracts) as Patterns() uses internally -- deterministic given the
        // same underlying ticks, so this is guaranteed to be the identical bar series, not a guess.
        var bars = await FutureEventBarBuilder.BuildDayAsync(db, date, 13000, CancellationToken.None);

        // Same freshness policy as ReversalResearch's own private Fresh() (at - p.Time <= 15s) -- a
        // generic tick-quality gate reused throughout that file, not part of Pattern A/B's own logic.
        static bool Fresh(ReversalResearch.Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);
        decimal? PriceAt(string token, DateTimeOffset time)
        {
            var p = ReversalResearch.Before(ticks[token], time);
            return Fresh(p, time) ? p!.Price : null;
        }
        static decimal Median(List<decimal> v)
        {
            var s = v.Order().ToList(); var n = s.Count;
            return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
        }

        var rows = new List<BandRow>();
        foreach (var pr in report.patternRows)
        {
            var b = pr.Index;
            if (b >= bars.Count || bars[b].IsFinalPartialBar) { continue; } // matches Patterns()'s own skip condition
            var first = bars[AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b, 180)];
            var current = bars[b];
            // EXACT same ATM rule Patterns() uses: nearest strike to the window-end futures close.
            var atmStrike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);

            decimal? ceAtmStart = null, ceAtmEnd = null, peAtmStart = null, peAtmEnd = null;
            if (byStrikeSide.TryGetValue((atmStrike, OptionType.Call), out var ceAtmInst))
            { ceAtmStart = PriceAt(ceAtmInst.Token, first.StartTimestamp); ceAtmEnd = PriceAt(ceAtmInst.Token, current.EndTimestamp); }
            if (byStrikeSide.TryGetValue((atmStrike, OptionType.Put), out var peAtmInst))
            { peAtmStart = PriceAt(peAtmInst.Token, first.StartTimestamp); peAtmEnd = PriceAt(peAtmInst.Token, current.EndTimestamp); }
            decimal? rawCe = ceAtmStart is > 0 && ceAtmEnd is not null ? ceAtmEnd - ceAtmStart : null;
            decimal? rawPe = peAtmStart is > 0 && peAtmEnd is not null ? peAtmEnd - peAtmStart : null;
            decimal? ceRetAtm = ceAtmStart is > 0 && ceAtmEnd is not null ? ceAtmEnd / ceAtmStart - 1 : null;
            decimal? peRetAtm = peAtmStart is > 0 && peAtmEnd is not null ? peAtmEnd / peAtmStart - 1 : null;

            var bandAvailable = idx >= 2 && idx + 2 < strikes.Count;
            var ceRets = new List<decimal>(); var peRets = new List<decimal>(); var shifts = new List<decimal>();
            var ceStarts = new List<decimal>(); var peStarts = new List<decimal>();
            if (bandAvailable)
            {
                for (var j = idx - 2; j <= idx + 2; j++)
                {
                    var k = strikes[j];
                    if (!byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst) || !byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst))
                    { bandAvailable = false; break; }
                    var ceS = PriceAt(ceInst.Token, first.StartTimestamp); var ceE = PriceAt(ceInst.Token, current.EndTimestamp);
                    var peS = PriceAt(peInst.Token, first.StartTimestamp); var peE = PriceAt(peInst.Token, current.EndTimestamp);
                    if (ceS is not > 0 || ceE is null || peS is not > 0 || peE is null) { bandAvailable = false; break; }
                    var ceRet = ceE.Value / ceS.Value - 1; var peRet = peE.Value / peS.Value - 1;
                    var ceLog = (decimal)Math.Log((double)(ceE.Value / ceS.Value)); var peLog = (decimal)Math.Log((double)(peE.Value / peS.Value));
                    ceRets.Add(ceRet); peRets.Add(peRet); shifts.Add(peLog - ceLog);
                    ceStarts.Add(ceS.Value); peStarts.Add(peS.Value);
                }
            }

            var bandState = "Other"; decimal? bandShift = null, bandCe = null, bandPe = null, dirStrength = null;
            if (bandAvailable && ceRets.Count == 5)
            {
                bandCe = Median(ceRets); bandPe = Median(peRets); bandShift = Median(shifts);
                bandState = pr.Move > 0 && bandCe < 0 && bandPe > 0 ? "A" : pr.Move < 0 && bandCe > 0 && bandPe < 0 ? "B" : "Other";
                dirStrength = bandState == "A" ? bandShift : bandState == "B" ? -bandShift : null;
            }

            rows.Add(new BandRow(pr.Time, b, pr.State, pr.StateEntry, pr.Full, pr.Move, pr.Future,
                pr.Forward1, pr.Forward2, pr.Forward4, bandAvailable, bandState, bandCe, bandPe, bandShift, dirStrength,
                ceStarts.Count == 5 ? ceStarts.Min() : null, ceStarts.Count == 5 ? Median(ceStarts) : null, ceStarts.Count == 5 ? ceStarts.Max() : null,
                peStarts.Count == 5 ? peStarts.Min() : null, peStarts.Count == 5 ? Median(peStarts) : null, peStarts.Count == 5 ? peStarts.Max() : null,
                rawCe, rawPe, ceRetAtm, peRetAtm));
        }
        return rows;
    }

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    /// <summary>
    /// Same manifest-based seeding FileBackedResearchRunner.SeedDayAsync already uses (that method
    /// is private to its own class), duplicated here rather than changing that file's accessibility --
    /// mechanical tick-loading plumbing, not Pattern A/B logic, so there is no fidelity risk in having
    /// two copies of it.
    /// </summary>
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
