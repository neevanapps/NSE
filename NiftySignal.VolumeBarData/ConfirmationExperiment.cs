using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-27 Confirmation-variable experiment (user's own explicit spec) -- NOT a trading
/// simulation, NOT a modification of Pattern A/B. Tests whether the underlying market itself
/// begins confirming a Pattern A/B warning, via two independent, individually-evaluated families:
///
///   1. FUTURES PRICE-STRUCTURE: a later completed 13,000-contract bar closes beyond the signal
///      bar's own High/Low (FutureEventBar already exposes real per-tick High/Low -- no derivation
///      needed).
///   2. FUTURES ORDER FLOW: NiftySignal.Features.FutureCvdProxyAccumulator, reused EXACTLY as
///      production already uses it (LiveFeatureEngine) -- a tick's entire volume delta since the
///      previous tick is classified buy-leaning if LastPrice sat at/above the bid-ask midpoint,
///      sell-leaning otherwise; reset per 13K bar (the "cadence" boundary here), read at bar close.
///
/// A THIRD family (Spot-vs-Futures Basis) was specified but could NOT be built: this project's
/// exported research-ticks/ data (see TickExporter.cs) only ever captured Future and Option
/// instruments, never the NIFTY Index (spot) ticks -- confirmed directly against every session's
/// instruments.json. Per the spec's own "do not fabricate" instruction (and the same stop
/// condition it specifies for order-flow if no validated metric existed), this family is reported
/// as BLOCKED by missing prerequisite data, not attempted with a substitute.
///
/// Reuses ReversalResearch.Patterns() (already internal, unchanged) for the frozen Pattern A/B
/// reference and FutureEventBarBuilder (13,000-contract threshold) for the bars -- no new
/// architecture invented.
/// </summary>
public static class ConfirmationExperiment
{
    static readonly HashSet<DateOnly> AllowedSessions =
    [
        new(2026, 9, 4), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
        new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17), new(2026, 9, 18), new(2026, 9, 21),
        new(2026, 9, 22), new(2026, 9, 23),
    ];
    const long VolumeThreshold = 13000;
    const int MaxBarsAhead = 4; // predeclared, matches the existing +1/+2/+4 horizon convention

    sealed record ManifestRow(string Token, string Exchange, string TradingSymbol, string InstrumentType,
        string OptionType, decimal? StrikePrice, DateOnly? ExpiryDate, string Underlying, int LotSize, decimal TickSize);

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine("=== Confirmation family 2 (Order Flow): reused implementation ===");
        Console.WriteLine("Class: NiftySignal.Features.FutureCvdProxyAccumulator (already used in production LiveFeatureEngine).");
        Console.WriteLine("Formula: for each future tick with a valid two-sided depth quote and positive volume delta since the");
        Console.WriteLine("  previous tick, classify the ENTIRE delta as +delta if LastPrice >= (Bid1+Ask1)/2, else -delta.");
        Console.WriteLine("  CadenceNet = sum of signed deltas since the last ResetCadence() call.");
        Console.WriteLine("Meaning: positive CadenceNet = net buy-leaning volume this bar; negative = net sell-leaning volume.");
        Console.WriteLine("Calculated per completed 13,000-contract bar in this experiment (ResetCadence() at each bar boundary,");
        Console.WriteLine("  CadenceNet read at that bar's close) -- the same reset-per-boundary pattern CadencePopulator/LiveFeatureEngine already use, just with the 13K bar as the boundary instead of the original 15s cadence.");
        Console.WriteLine();
        Console.WriteLine("=== Confirmation family 3 (Spot/Futures Basis): BLOCKED ===");
        Console.WriteLine("research-ticks/ exports (TickExporter.cs) only ever captured Future and Option instruments for");
        Console.WriteLine("every session checked -- NIFTY Index (spot) ticks were never exported. Per spec section 10's");
        Console.WriteLine("'do not fabricate' rule, this family is NOT computed. Reported as unavailable, not attempted.");
        Console.WriteLine();

        if (args.Length < 3)
        {
            Console.WriteLine("Usage: confirmation-experiment <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks] [--out=confirmation-raw.csv]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "confirmation-raw.csv";

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
            "TradingDate","Dte","ExistingAtmState","BarIndex","PatternTimestamp",
            "SignalOpen","SignalHigh","SignalLow","SignalClose","ActivityRate","DirectionalRelativeStrength",
            "Forward1","Forward2","Forward4","WindowEndBarIndex","OppositeEntryWithinWindow",
            "StructureConfirmed","StructureBarOffset","StructureTimestamp","StructureCloseAtConfirmation",
            "StructureForward1","StructureForward2","StructureForward4","MoveBeforeStructureConfirmation",
            "OrderFlowConfirmed","OrderFlowBarOffset","OrderFlowTimestamp","OrderFlowValueAtConfirmation",
            "OrderFlowForward1","OrderFlowForward2","OrderFlowForward4","MoveBeforeOrderFlowConfirmation",
        }));

        var totalRows = 0;
        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"Loading {date:yyyy-MM-dd} from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"confirmation-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            await SeedDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            var rowCount = await AppendDayAsync(db, date, sb);
            totalRows += rowCount;
            Console.WriteLine($"  {date:yyyy-MM-dd}: {rowCount} Pattern A/B FullSurface entries.");

            // Full per-bar series (OHLC + signed order flow + Pattern A/B state) for chart forensics
            // -- same underlying computation, just also written out at bar granularity, not only at
            // Pattern entries. Written for every session processed (cheap, harmless for the 10
            // primary sessions too, and directly reusable if the discovery charts need more days later).
            var barPath = Path.Combine(Path.GetDirectoryName(outPath) ?? ".", $"{date:yyyy-MM-dd}_bars.csv");
            await WriteBarSeriesAsync(db, date, barPath);

            await db.Database.EnsureDeletedAsync();
        }
        await File.WriteAllTextAsync(outPath, sb.ToString());
        Console.WriteLine($"Saved {outPath} ({totalRows} total entries across {dates.Count} sessions).");
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
        var futureInst = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate).FirstAsync();
        var futureReceipts = await db.Ticks.Where(t => t.Token == futureInst.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).Select(t => new { t.ExchangeTimestamp, t.ReceivedAt }).ToListAsync();
        var available = ReversalResearch.ComputeAvailability(bars, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
        var patternRows = ReversalResearch.Patterns(chain, ticks, bars, available);

        // Per-bar signed order flow: ONE running FutureFlowAccumulator (for the volume-delta
        // baseline) feeding ONE FutureCvdProxyAccumulator reset at each 13K bar boundary -- exactly
        // the existing CadencePopulator/LiveFeatureEngine pattern, just with the 13K bar as the
        // reset boundary instead of the original 15s cadence.
        var futureTicksRaw = await db.Ticks.Where(t => t.Token == futureInst.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).ToListAsync();
        var cadenceNetByBar = ComputeCadenceNetPerBar(futureTicksRaw, bars);

        static bool Fresh(ReversalResearch.Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);
        decimal? PriceAt(string token, DateTimeOffset time)
        {
            var p = ReversalResearch.Before(ticks[token], time);
            return Fresh(p, time) ? p!.Price : null;
        }
        static double MedianD(List<double> v) { var s = v.Order().ToList(); var n = s.Count; return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; }

        // Only true full-surface, state-entry rows are eligible signal entries (primary population).
        var eligibleEntries = patternRows.Where(p => p.StateEntry && p.Full && p.State is "A" or "B").ToList();
        // "Opposite pattern state entry" boundary uses the same convention P0/P1 already use for
        // their own exit: the next StateEntry of the opposite ATM state, not required to be FullSurface.
        var stateEntries = patternRows.Where(p => p.StateEntry && p.State is "A" or "B").OrderBy(p => p.Index).ToList();

        var count = 0;
        foreach (var pr in eligibleEntries)
        {
            var b0 = pr.Index;
            if (b0 >= bars.Count || bars[b0].IsFinalPartialBar) { continue; }
            var signalBar = bars[b0];
            var opposite = pr.State == "A" ? "B" : "A";
            var nextOpposite = stateEntries.FirstOrDefault(s => s.Index > b0 && s.State == opposite);
            var lastValidBarIdx = bars.FindLastIndex(x => !x.IsFinalPartialBar);
            var windowEnd = Math.Min(b0 + MaxBarsAhead, lastValidBarIdx);
            var oppositeWithinWindow = nextOpposite is not null && nextOpposite.Index <= windowEnd;
            if (oppositeWithinWindow) { windowEnd = Math.Min(windowEnd, nextOpposite!.Index); }

            // --- Activity rate + DirectionalRelativeStrength (same 180s adaptive window as the
            // rest of this research thread, reused for descriptive comparison only). ---
            var firstIndex = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b0, 180);
            var firstBar = bars[firstIndex];
            var windowBarCount = b0 - firstIndex + 1;
            var windowDuration = (signalBar.EndTimestamp - firstBar.StartTimestamp).TotalSeconds;
            var activityRate = windowDuration > 0 ? windowBarCount / windowDuration : (double?)null;

            var atmStrike = strikes.OrderBy(k => Math.Abs(k - signalBar.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);
            var lo = Math.Max(0, idx - 2); var hi = Math.Min(strikes.Count - 1, idx + 2);
            var shifts = new List<double>();
            for (var j = lo; j <= hi; j++)
            {
                var k = strikes[j];
                byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst);
                byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst);
                var ceS = ceInst is not null ? PriceAt(ceInst.Token, firstBar.StartTimestamp) : null;
                var ceE = ceInst is not null ? PriceAt(ceInst.Token, signalBar.EndTimestamp) : null;
                var peS = peInst is not null ? PriceAt(peInst.Token, firstBar.StartTimestamp) : null;
                var peE = peInst is not null ? PriceAt(peInst.Token, signalBar.EndTimestamp) : null;
                if (ceS is > 0 && ceE is not null && peS is > 0 && peE is not null)
                { shifts.Add(Math.Log((double)(peE.Value / peS.Value)) - Math.Log((double)(ceE.Value / ceS.Value))); }
            }
            double? bandShift = shifts.Count == 5 ? MedianD(shifts) : null;
            double? directionalRelativeStrength = bandShift is null ? null : pr.State == "A" ? bandShift : -bandShift;

            // --- Structure confirmation ---
            bool structureConfirmed = false; int? structureOffset = null;
            for (var j = b0 + 1; j <= windowEnd && !structureConfirmed; j++)
            {
                var qualifies = pr.State == "A" ? bars[j].Close < signalBar.Low : bars[j].Close > signalBar.High;
                if (qualifies) { structureConfirmed = true; structureOffset = j - b0; }
            }
            decimal?[] StructureForward(int? offset)
            {
                if (offset is null) { return [null, null, null]; }
                var confIdx = b0 + offset.Value;
                decimal? Fwd(int h) => confIdx + h <= lastValidBarIdx ? bars[confIdx + h].Close - bars[confIdx].Close : null;
                return [Fwd(1), Fwd(2), Fwd(4)];
            }
            var structFwd = StructureForward(structureOffset);
            decimal? moveBeforeStructure = structureOffset is not null ? bars[b0 + structureOffset.Value].Close - signalBar.Close : null;

            // --- Order-flow confirmation ---
            bool flowConfirmed = false; int? flowOffset = null; long? flowValue = null;
            for (var j = b0 + 1; j <= windowEnd && !flowConfirmed; j++)
            {
                if (!cadenceNetByBar.TryGetValue(j, out var net) || net is null) { continue; }
                var qualifies = pr.State == "A" ? net.Value < 0 : net.Value > 0;
                if (qualifies) { flowConfirmed = true; flowOffset = j - b0; flowValue = net.Value; }
            }
            decimal?[] FlowForward(int? offset)
            {
                if (offset is null) { return [null, null, null]; }
                var confIdx = b0 + offset.Value;
                decimal? Fwd(int h) => confIdx + h <= lastValidBarIdx ? bars[confIdx + h].Close - bars[confIdx].Close : null;
                return [Fwd(1), Fwd(2), Fwd(4)];
            }
            var flowFwd = FlowForward(flowOffset);
            decimal? moveBeforeFlow = flowOffset is not null ? bars[b0 + flowOffset.Value].Close - signalBar.Close : null;

            var fields = new List<object?>
            {
                date, dte, pr.State, b0, pr.Time,
                signalBar.Open, signalBar.High, signalBar.Low, signalBar.Close, activityRate, directionalRelativeStrength,
                pr.Forward1, pr.Forward2, pr.Forward4, windowEnd, oppositeWithinWindow,
                structureConfirmed, structureOffset,
                structureOffset is not null ? available[b0 + structureOffset.Value] : (DateTimeOffset?)null,
                structureOffset is not null ? bars[b0 + structureOffset.Value].Close : (decimal?)null,
                structFwd[0], structFwd[1], structFwd[2], moveBeforeStructure,
                flowConfirmed, flowOffset,
                flowOffset is not null ? available[b0 + flowOffset.Value] : (DateTimeOffset?)null,
                flowValue,
                flowFwd[0], flowFwd[1], flowFwd[2], moveBeforeFlow,
            };
            AppendCsvRow(sb, fields);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Feeds every future tick of the day, in order, through ONE FutureFlowAccumulator (volume-delta
    /// baseline) and ONE FutureCvdProxyAccumulator (signed classifier), resetting the classifier at
    /// each 13K bar boundary -- exactly the existing shared-baseline/per-cadence-reset pattern
    /// CadencePopulator/LiveFeatureEngine already use. Returns each bar index's CadenceNet (null if
    /// that bar had no tick with both a two-sided quote and a positive volume delta to classify).
    /// </summary>
    /// <summary>Chart-forensics export only: full per-bar OHLC/order-flow/Pattern-state series, not used by the primary statistics.</summary>
    static async Task WriteBarSeriesAsync(NiftySignalDbContext db, DateOnly date, string path)
    {
        var chainAll = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY"
            && i.InstrumentType == InstrumentType.Option && i.ExpiryDate >= date).ToListAsync();
        var expiry = chainAll.Min(i => i.ExpiryDate)!.Value;
        var chain = chainAll.Where(i => i.ExpiryDate == expiry).OrderBy(i => i.Token).ToDictionary(i => i.Token);
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
        var futureInst = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
            .OrderBy(i => i.ExpiryDate).FirstAsync();
        var futureReceipts = await db.Ticks.Where(t => t.Token == futureInst.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).Select(t => new { t.ExchangeTimestamp, t.ReceivedAt }).ToListAsync();
        var available = ReversalResearch.ComputeAvailability(bars, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
        var patternRows = ReversalResearch.Patterns(chain, ticks, bars, available);
        var patternByIndex = patternRows.ToDictionary(p => p.Index);
        var futureTicksRaw = await db.Ticks.Where(t => t.Token == futureInst.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
            .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).ToListAsync();
        var cadenceNetByBar = ComputeCadenceNetPerBar(futureTicksRaw, bars);

        var sb = new StringBuilder();
        sb.AppendLine("BarIndex,Timestamp,Open,High,Low,Close,CadenceNet,ExistingAtmState,ExistingFullSurfaceState,IsExistingStateEntry");
        for (var b = 0; b < bars.Count; b++)
        {
            if (bars[b].IsFinalPartialBar) { continue; }
            patternByIndex.TryGetValue(b, out var pr);
            AppendCsvRow(sb, new List<object?>
            {
                b, available[b], bars[b].Open, bars[b].High, bars[b].Low, bars[b].Close,
                cadenceNetByBar.TryGetValue(b, out var net) ? net : null,
                pr?.State ?? "Other", pr?.Full ?? false, pr?.StateEntry ?? false,
            });
        }
        await File.WriteAllTextAsync(path, sb.ToString());
    }

    static Dictionary<int, long?> ComputeCadenceNetPerBar(List<Tick> futureTicks, List<FutureEventBar> bars)
    {
        var result = new Dictionary<int, long?>();
        var flow = new FutureFlowAccumulator();
        var cvd = new FutureCvdProxyAccumulator();
        var barIdx = 0;
        foreach (var t in futureTicks)
        {
            while (barIdx < bars.Count && t.ExchangeTimestamp > bars[barIdx].EndTimestamp)
            {
                result[barIdx] = cvd.CadenceNet;
                cvd.ResetCadence();
                barIdx++;
            }
            if (barIdx >= bars.Count) { break; }
            flow.ApplyTick(t.LastPrice, t.Volume);
            if (t.Depth is not null && t.ExchangeTimestamp > bars[barIdx].StartTimestamp)
            {
                cvd.ApplyTick(t.LastPrice, t.Depth, flow.LastVolumeDelta);
            }
        }
        if (barIdx < bars.Count) { result[barIdx] = cvd.CadenceNet; }
        return result;
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
