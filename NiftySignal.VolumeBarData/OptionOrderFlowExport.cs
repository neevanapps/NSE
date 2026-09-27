using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-27 Options Order Flow research track -- the user's own full spec (29 sections). Pure
/// data export (no modeling/correlation here, that happens downstream in Python, same division of
/// labor as OptionFuturesDislocationExport/ConfirmationExperiment). NOT a trading simulation, no
/// Pattern A/B modification, no composite score, no threshold search.
///
/// Reuses, unmodified: <see cref="ReversalResearch.Patterns"/>/<see cref="ReversalResearch.ComputeAvailability"/>
/// (frozen Pattern A/B classification), <see cref="FutureEventBarBuilder"/> (same 13,000-contract
/// clock), <see cref="AdaptiveWindowAnalysis.FindWindowStartIndex"/> (same 180s adaptive window),
/// and the exact ATM+/-2 five-strike selection rule <see cref="BandPercentageExperiment"/> already
/// established (nearest strike to window-end futures Close). Sources ticks from the V2 export
/// (research-ticks-v2/, full top-5 depth) via <see cref="OptionTickReaderV2"/>, and computes trade
/// flow/depth via <see cref="OptionOrderFlowCalculations"/> -- both already unit-tested and
/// validated against a real exported session before this file was written.
///
/// One row per valid 13K/180s observation (same population Patterns() considers, "Other" state
/// included) -- not just Pattern A/B entries -- so standalone (Section 13/14), Pattern-conditional
/// (15/16/17), and lead/lag (18/19) analyses can all be built from ONE per-session CSV in the
/// downstream Python pass, joining on BarIndex.
/// </summary>
public static class OptionOrderFlowExport
{
    // Primary 10-session robustness set. 09-22/23 are discovery-only (exported separately, flagged,
    // analyzed only after primary results, per spec). 09-24/25+ are permanently refused -- OOS.
    static readonly HashSet<DateOnly> PrimarySessions =
    [
        new(2026, 9, 4), new(2026, 9, 8), new(2026, 9, 9), new(2026, 9, 10), new(2026, 9, 11),
        new(2026, 9, 15), new(2026, 9, 16), new(2026, 9, 17), new(2026, 9, 18), new(2026, 9, 21),
    ];
    static readonly HashSet<DateOnly> DiscoverySessions = [new(2026, 9, 22), new(2026, 9, 23)];
    static HashSet<DateOnly> AllowedSessions => [..PrimarySessions, ..DiscoverySessions];

    public static async Task<int> RunAsync(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: option-order-flow-export <fromDate:yyyy-MM-dd> <toDate:yyyy-MM-dd> [--in=research-ticks-v2] [--out=option-order-flow-raw.csv]");
            return 1;
        }
        var from = DateOnly.ParseExact(args[1], "yyyy-MM-dd");
        var to = DateOnly.ParseExact(args[2], "yyyy-MM-dd");
        var ticksDir = args.FirstOrDefault(a => a.StartsWith("--in=", StringComparison.Ordinal)) is { } i ? i[5..] : "research-ticks-v2";
        var outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal)) is { } o ? o[6..] : "option-order-flow-raw.csv";

        var dates = Directory.Exists(ticksDir)
            ? Directory.GetDirectories(ticksDir)
                .Select(d => (Dir: d, Date: DateOnly.ParseExact(Path.GetFileName(d), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .Where(x => x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList()
            : [];
        var rejected = dates.Where(x => !AllowedSessions.Contains(x.Date)).ToList();
        if (rejected.Count > 0)
        {
            Console.WriteLine("Refusing: Options Order Flow experiment is restricted to the 10 primary sessions " +
                "(2026-09-04/08/09/10/11/15/16/17/18/21) plus 2026-09-22/23 as discovery-only. " +
                $"Rejected (includes anything at/after 2026-09-24, permanently OOS): {string.Join(',', rejected.Select(x => x.Date))}.");
            return 1;
        }

        Console.WriteLine("Depth aggregation method: time-weighted average DepthImbalance (predeclared -- " +
            "feasible with the V2 tick stream's own timestamps, per OptionOrderFlowCalculations.TimeWeightedAverageDepthImbalance, " +
            "already unit-tested; not chosen based on this run's results).");
        Console.WriteLine("Aggressor classification: predeclared TradePrice-vs-pre-trade-book rule (OptionOrderFlowCalculations.ClassifyAggressor); " +
            "no tick-rule fallback, inside-spread trades left Unknown.");

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', HeaderFields));
        int totalRows = 0;
        foreach (var (dayDir, date) in dates)
        {
            Console.WriteLine($"Processing {date:yyyy-MM-dd} ({(DiscoverySessions.Contains(date) ? "DISCOVERY REFERENCE ONLY" : "primary")}) from {dayDir} ...");
            await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
                .UseInMemoryDatabase($"oof-{date:yyyyMMdd}-{Guid.NewGuid()}").Options);
            var (manifest, ticksByToken) = await LoadDayAsync(db, dayDir, date);
            db.ChangeTracker.Clear();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            var isDiscovery = DiscoverySessions.Contains(date);
            var rows = await BuildDayAsync(db, date, manifest, ticksByToken, isDiscovery);
            foreach (var f in rows) { sb.AppendLine(string.Join(',', f.Select(Fmt))); }
            totalRows += rows.Count;
            Console.WriteLine($"  {date:yyyy-MM-dd}: {rows.Count} observations.");
            await db.Database.EnsureDeletedAsync();
        }
        await File.WriteAllTextAsync(outPath, sb.ToString());
        Console.WriteLine($"Saved {outPath} ({totalRows} total rows).");
        return 0;
    }

    static readonly string[] HeaderFields =
    [
        "TradingDate", "IsDiscovery", "Dte", "BarIndex", "Timestamp",
        "WindowStartTimestamp", "WindowEndTimestamp", "WindowDurationSeconds", "WindowBarCount", "ActivityRate",
        "ExistingAtmState", "ExistingFullSurfaceState", "ExistingStateEntry", "Move", "Forward1", "Forward2", "Forward4",
        "DirectionalRelativeStrength",
        "FiveStrikeTokenMappingAvailable", "ValidCeFlowStrikeCount", "ValidPeFlowStrikeCount",
        "MedianClassificationCoverage", "MinClassificationCoverage", "TotalClassifiedVolume", "TotalUnknownVolume",
        "BandCETradeOFI", "BandPETradeOFI", "RelativeOptionTradeFlow",
        "ValidCeDepthStrikeCount", "ValidPeDepthStrikeCount",
        "BandCEDepthImbalance", "BandPEDepthImbalance", "RelativeOptionDepth",
    ];

    static string Fmt(object? v) => v switch
    {
        null => "",
        DateTimeOffset dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool bo => bo.ToString(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>First index whose ExchangeTimestamp is &gt;= t (ticks assumed sorted ascending, per TickExporterV2's own deterministic export order).</summary>
    static int LowerBound(List<OptionTickV2> ticks, DateTimeOffset t)
    {
        int lo = 0, hi = ticks.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (ticks[mid].ExchangeTimestamp < t) { lo = mid + 1; } else { hi = mid; } }
        return lo;
    }

    /// <summary>First index whose ExchangeTimestamp is &gt; t.</summary>
    static int UpperBound(List<OptionTickV2> ticks, DateTimeOffset t)
    {
        int lo = 0, hi = ticks.Count;
        while (lo < hi) { var mid = (lo + hi) / 2; if (ticks[mid].ExchangeTimestamp <= t) { lo = mid + 1; } else { hi = mid; } }
        return lo;
    }

    /// <summary>
    /// One extra tick of lookback before the window is included so the FIRST real trade inside the
    /// window still has a pre-trade book/previous-cumulative-volume to diff against (that lookback
    /// tick is itself before the window, so this is never a look-ahead) -- trade events are then
    /// filtered back down to [windowStart, windowEnd] by the caller.
    /// </summary>
    static List<OptionTickV2> WindowSliceWithLookback(List<OptionTickV2> ticks, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var startIdx = LowerBound(ticks, windowStart);
        var endIdxExclusive = UpperBound(ticks, windowEnd);
        var sliceStart = startIdx > 0 ? startIdx - 1 : 0;
        var length = Math.Max(0, endIdxExclusive - sliceStart);
        return length == 0 ? [] : ticks.GetRange(sliceStart, length);
    }

    static decimal? PriceAt(List<OptionTickV2> ticks, DateTimeOffset time)
    {
        var idx = UpperBound(ticks, time) - 1;
        if (idx < 0) { return null; }
        var p = ticks[idx];
        return time - p.ExchangeTimestamp <= TimeSpan.FromSeconds(15) ? p.LastPrice : null; // same 15s freshness policy as ReversalResearch's own private Fresh().
    }

    static double Median(List<double> v)
    {
        var s = v.Order().ToList(); var n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
    }

    public static async Task<List<object?[]>> BuildDayAsync(
        NiftySignalDbContext db, DateOnly date, List<OptionTickReaderV2.ManifestRow> manifest,
        Dictionary<string, List<OptionTickV2>> ticksByToken, bool isDiscovery)
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

        // ReversalResearch.Patterns() needs the ReversalResearch.Print shape (Level-1 only) -- built
        // straight from the same V2 OptionTickV2 lists already loaded, no second file read.
        var printsByToken = new Dictionary<string, List<ReversalResearch.Print>>();
        foreach (var inst in chain.Values)
        {
            printsByToken[inst.Token] = (ticksByToken.TryGetValue(inst.Token, out var tks) ? tks : [])
                .Where(t => t.LastPrice > 0)
                .Select(t => new ReversalResearch.Print(t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                    t.Depth?.Bid1Price ?? 0, t.Depth?.Ask1Price ?? 0, t.Depth?.Bid1Qty ?? 0, t.Depth?.Ask1Qty ?? 0, t.Volume)).ToList();
        }

        var bars = await FutureEventBarBuilder.BuildDayAsync(db, date, 13000, CancellationToken.None);
        var futureFirst = manifest.First(m => m.InstrumentType == "Future");
        var futureReceipts = (ticksByToken.TryGetValue(futureFirst.Token, out var futTicks) ? futTicks : [])
            .Select(t => (t.ExchangeTimestamp, t.ReceivedAt)).ToList();
        var available = ReversalResearch.ComputeAvailability(bars, futureReceipts, start);
        var patternRows = ReversalResearch.Patterns(chain, printsByToken, bars, available);

        var rows = new List<object?[]>();
        foreach (var pr in patternRows)
        {
            var b = pr.Index;
            if (b >= bars.Count || bars[b].IsFinalPartialBar) { continue; } // same skip Patterns() itself applies.
            var firstIndex = AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b, 180);
            var first = bars[firstIndex];
            var current = bars[b];
            var windowStart = first.StartTimestamp;
            var windowEnd = current.EndTimestamp;

            // EXACT same ATM+/-2 rule BandPercentageExperiment/OptionFuturesDislocationExport already
            // use: nearest listed strike to the window-end futures Close.
            var atmStrike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            var idx = strikes.IndexOf(atmStrike);
            var fiveStrikeAvailable = idx >= 2 && idx + 2 < strikes.Count;

            var ceOfis = new List<double?>(); var peOfis = new List<double?>();
            var ceDepths = new List<double?>(); var peDepths = new List<double?>();
            var coverages = new List<double>();
            long totalClassified = 0, totalUnknown = 0;
            var ceLogs = new List<double>(); var peLogs = new List<double>(); var shifts = new List<double>();

            if (fiveStrikeAvailable)
            {
                for (var j = idx - 2; j <= idx + 2; j++)
                {
                    var k = strikes[j];
                    if (!byStrikeSide.TryGetValue((k, OptionType.Call), out var ceInst) || !byStrikeSide.TryGetValue((k, OptionType.Put), out var peInst))
                    { fiveStrikeAvailable = false; break; }

                    foreach (var (inst, ofis, depths) in new[] { (ceInst, ceOfis, ceDepths), (peInst, peOfis, peDepths) })
                    {
                        var tokTicks = ticksByToken.TryGetValue(inst.Token, out var tl) ? tl : [];
                        var slice = WindowSliceWithLookback(tokTicks, windowStart, windowEnd);
                        var events = OptionOrderFlowCalculations.DetectTradeEvents(slice, out _)
                            .Where(e => e.ExchangeTimestamp >= windowStart).ToList();
                        var flow = OptionOrderFlowCalculations.AggregateContractFlow(events);
                        ofis.Add(flow.TradeOFI);
                        if (flow.TotalTradedVolume > 0) { coverages.Add(flow.ClassificationCoverage); }
                        totalClassified += flow.ClassifiedVolume;
                        totalUnknown += flow.UnknownVolume;

                        var snapshots = slice.Where(t => t.Depth is not null).Select(t => (t.ExchangeTimestamp, t.Depth)).ToList();
                        depths.Add(OptionOrderFlowCalculations.TimeWeightedAverageDepthImbalance(snapshots, windowStart, windowEnd));
                    }

                    var ceS = PriceAt(ticksByToken.TryGetValue(ceInst.Token, out var ceT) ? ceT : [], windowStart);
                    var ceE = PriceAt(ticksByToken.TryGetValue(ceInst.Token, out var ceT2) ? ceT2 : [], windowEnd);
                    var peS = PriceAt(ticksByToken.TryGetValue(peInst.Token, out var peT) ? peT : [], windowStart);
                    var peE = PriceAt(ticksByToken.TryGetValue(peInst.Token, out var peT2) ? peT2 : [], windowEnd);
                    if (ceS is > 0 && ceE is not null) { ceLogs.Add(Math.Log((double)(ceE.Value / ceS.Value))); }
                    if (peS is > 0 && peE is not null) { peLogs.Add(Math.Log((double)(peE.Value / peS.Value))); }
                    if (ceS is > 0 && ceE is not null && peS is > 0 && peE is not null)
                    { shifts.Add(Math.Log((double)(peE.Value / peS.Value)) - Math.Log((double)(ceE.Value / ceS.Value))); }
                }
            }

            double? bandCeOfi = null, bandPeOfi = null, relFlow = null, bandCeDepth = null, bandPeDepth = null, relDepth = null;
            int validCeFlow = 0, validPeFlow = 0, validCeDepth = 0, validPeDepth = 0;
            if (fiveStrikeAvailable)
            {
                validCeFlow = ceOfis.Count(v => v is not null); validPeFlow = peOfis.Count(v => v is not null);
                validCeDepth = ceDepths.Count(v => v is not null); validPeDepth = peDepths.Count(v => v is not null);
                bandCeOfi = OptionOrderFlowCalculations.MedianOfBand(ceOfis);
                bandPeOfi = OptionOrderFlowCalculations.MedianOfBand(peOfis);
                if (bandCeOfi is not null && bandPeOfi is not null) { relFlow = bandPeOfi - bandCeOfi; }
                bandCeDepth = OptionOrderFlowCalculations.MedianOfBand(ceDepths);
                bandPeDepth = OptionOrderFlowCalculations.MedianOfBand(peDepths);
                if (bandCeDepth is not null && bandPeDepth is not null) { relDepth = bandPeDepth - bandCeDepth; }
            }

            double? dirStrength = null;
            if (shifts.Count == 5 && ceLogs.Count == 5 && peLogs.Count == 5)
            {
                var medShift = Median(shifts); var medCe = Median(ceLogs); var medPe = Median(peLogs);
                var priceBandState = pr.Move > 0 && medCe < 0 && medPe > 0 ? "A" : pr.Move < 0 && medCe > 0 && medPe < 0 ? "B" : "Other";
                dirStrength = priceBandState == "A" ? medShift : priceBandState == "B" ? -medShift : null;
            }

            var windowBarCount = b - firstIndex + 1;
            var windowDuration = (windowEnd - windowStart).TotalSeconds;
            var activityRate = windowDuration > 0 ? windowBarCount / windowDuration : (double?)null;
            double? medCov = coverages.Count > 0 ? Median(coverages) : null;
            double? minCov = coverages.Count > 0 ? coverages.Min() : null;

            rows.Add(new object?[]
            {
                date, isDiscovery, dte, b, pr.Time,
                windowStart, windowEnd, windowDuration, windowBarCount, activityRate,
                pr.State, pr.Full, pr.StateEntry, pr.Move, pr.Forward1, pr.Forward2, pr.Forward4,
                dirStrength,
                fiveStrikeAvailable, validCeFlow, validPeFlow,
                medCov, minCov, totalClassified, totalUnknown,
                bandCeOfi, bandPeOfi, relFlow,
                validCeDepth, validPeDepth,
                bandCeDepth, bandPeDepth, relDepth,
            });
        }
        return rows;
    }

    /// <summary>
    /// Loads the manifest and every instrument's full V2 tick list (used both for DB-seeding, so
    /// ReversalResearch.Patterns()/FutureEventBarBuilder work unchanged, and directly for order-flow
    /// calculations -- one file read per token, not two).
    /// </summary>
    public static async Task<(List<OptionTickReaderV2.ManifestRow> Manifest, Dictionary<string, List<OptionTickV2>> TicksByToken)>
        LoadDayAsync(NiftySignalDbContext db, string dayDir, DateOnly date)
    {
        var manifest = await OptionTickReaderV2.LoadManifestAsync(dayDir);
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

        var ticksByToken = new Dictionary<string, List<OptionTickV2>>();
        foreach (var m in manifest)
        {
            var path = Path.Combine(dayDir, $"{m.Token}.ndjson");
            var ticks = File.Exists(path) ? OptionTickReaderV2.LoadFile(path) : [];
            ticksByToken[m.Token] = ticks;

            var exchange = Enum.Parse<Exchange>(m.Exchange);
            var batch = new List<Tick>(8192);
            foreach (var t in ticks)
            {
                batch.Add(new Tick
                {
                    Id = t.Id, Token = m.Token, Exchange = exchange, ExchangeTimestamp = t.ExchangeTimestamp,
                    ReceivedAt = t.ReceivedAt, LastPrice = t.LastPrice, Volume = t.Volume, OpenInterest = t.OpenInterest, Depth = t.Depth
                });
                if (batch.Count == 8192) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); batch.Clear(); }
            }
            if (batch.Count > 0) { db.Ticks.AddRange(batch); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
        }
        return (manifest, ticksByToken);
    }
}
