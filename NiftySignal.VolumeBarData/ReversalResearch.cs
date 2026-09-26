using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;
using NiftySignal.Persistence;

namespace NiftySignal.VolumeBarData;

/// <summary>Isolated, raw-tick research. Never calls the frozen forward runner or writes a database.</summary>
public static class ReversalResearch
{
    public sealed record Print(long Id, DateTimeOffset Time, DateTimeOffset Received, decimal Price,
        decimal Bid, decimal Ask, long BidQty, long AskQty, long Volume = 0);
    public sealed record Reading(DateTimeOffset End, decimal? Average, int Count, decimal? Fast,
        decimal? Slow, decimal? Gap, bool Up, bool Down);
    public sealed record Signal(DateTimeOffset Time, string Side, string Token, string Reason, decimal Gap,
        bool Extended = false, decimal? Forward1 = null, decimal? Forward2 = null, decimal? Forward5 = null);
    public sealed record Trade(string Side, string Token, DateTimeOffset Decision, DateTimeOffset Entry,
        DateTimeOffset ExitDecision, DateTimeOffset Exit, decimal Buy, decimal Sell, int Quantity,
        decimal Gross, decimal Fees, decimal Net, decimal Mfe, decimal Mae, double Seconds, string Reason, long EntryId, long ExitId);
    public sealed record ActionRow(DateTimeOffset Time, string Action, string Token, string Reason);
    public sealed record Simulation(List<Trade> Trades, List<ActionRow> Decisions, int Unresolved);
    public sealed record PatternRow(DateTimeOffset Time, string State, bool StateEntry, bool Full,
        decimal Move, decimal Future, int Index, decimal? Forward1, decimal? Forward2, decimal? Forward4);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static DateTimeOffset At(DateOnly date, int hour, int minute) => new(date.ToDateTime(new TimeOnly(hour, minute)), Ist);

    public static List<Reading> Cadences(IReadOnlyList<Print> ticks, DateTimeOffset start, DateTimeOffset end,
        int fast = 8, int slow = 40, int bucketSeconds = 15)
    {
        var result = new List<Reading>();
        var window = new Queue<decimal>();
        decimal? previous = null;
        var cursor = 0;
        var bucket = TimeSpan.FromSeconds(bucketSeconds);
        for (var left = start; left < end; left += bucket)
        {
            var right = left + bucket;
            decimal sum = 0; int count = 0;
            while (cursor < ticks.Count && ticks[cursor].Time <= right)
            {
                var tick = ticks[cursor++];
                if (tick.Time > left && tick.Price > 0 && tick.Received <= right) { sum += tick.Price; count++; }
            }
            if (count == 0)
            {
                window.Clear(); previous = null;
                result.Add(new(right, null, 0, null, null, null, false, false));
                continue;
            }
            var avg = sum / count;
            window.Enqueue(avg);
            if (window.Count > slow) { window.Dequeue(); }
            decimal? f = window.Count >= fast ? window.TakeLast(fast).Average() : null;
            decimal? s = window.Count == slow ? window.Average() : null;
            decimal? gap = f - s;
            result.Add(new(right, avg, count, f, s, gap, previous <= 0 && gap > 0, previous >= 0 && gap < 0));
            previous = gap;
        }
        return result;
    }

    /// <summary>
    /// 2026-09-26: option price crossover using volume-threshold bars instead of a fixed time
    /// cadence -- reuses NiftySignal.Features.VolumeBarBuilder directly (it's already generic over
    /// whatever token's ticks it's fed; nothing about it is future-specific) rather than writing a
    /// second bar builder. 650 reuses this project's own already-established option-side volume-bar
    /// magnitude (Price_Based_Findings.md's populate-call-depth-imbalance/cvd-proxy defaults), not
    /// a newly-invented number -- a single option token typically trades ~95,000 contracts/session,
    /// so 650 yields roughly 100-150 bars/day for a liquid strike.
    /// </summary>
    public static List<VolumeBar> OptionVolumeBars(IReadOnlyList<Print> ticks, long threshold)
    {
        var builder = new VolumeBarBuilder(threshold);
        var bars = new List<VolumeBar>();
        foreach (var t in ticks)
        {
            var depth = t.Bid > 0 || t.Ask > 0 ? new MarketDepth(t.Bid, t.BidQty, 0, 0, 0, 0, 0, 0, 0, 0, t.Ask, t.AskQty, 0, 0, 0, 0, 0, 0, 0, 0) : null;
            var bar = builder.ApplyTick(t.Time, t.Price, t.Volume, depth, null);
            if (bar is not null) { bars.Add(bar); }
        }
        return bars;
    }

    /// <summary>
    /// Same fast/slow/gap moving-average-crossover math as Cadences, sourced from already-formed
    /// bars (volume bars here) instead of raw ticks bucketed by time -- lets CrossSignals/Simulate
    /// run completely unchanged regardless of which bar construction produced the series.
    /// </summary>
    public static List<Reading> ReadingsFromBars(IReadOnlyList<VolumeBar> bars, int fast, int slow)
    {
        var result = new List<Reading>();
        var window = new Queue<decimal>();
        decimal? previous = null;
        foreach (var bar in bars)
        {
            window.Enqueue(bar.ClosePrice);
            if (window.Count > slow) { window.Dequeue(); }
            decimal? f = window.Count >= fast ? window.TakeLast(fast).Average() : null;
            decimal? s = window.Count == slow ? window.Average() : null;
            decimal? gap = f - s;
            result.Add(new(bar.EndTimestamp, bar.ClosePrice, bar.TickCount, f, s, gap, previous <= 0 && gap > 0, previous >= 0 && gap < 0));
            previous = gap;
        }
        return result;
    }

    public static Print? Before(IReadOnlyList<Print> ticks, DateTimeOffset time)
    {
        int lo = 0, hi = ticks.Count;
        while (lo < hi) { var m = (lo + hi) / 2; if (ticks[m].Time <= time) { lo = m + 1; } else { hi = m; } }
        while (lo > 0 && ticks[lo - 1].Received > time) { lo--; }
        return lo == 0 ? null : ticks[lo - 1];
    }
    public static bool Valid(Print p, int qty) => p.Bid > 0 && p.Ask >= p.Bid && p.BidQty >= qty && p.AskQty >= qty;
    public static decimal Fees(decimal buy, decimal sell, int qty)
    {
        var turnover = (buy + sell) * qty;
        return Math.Round(sell * qty * .0015m + turnover * (.0003503m + .000001m) * 1.18m + buy * qty * .00003m, 2);
    }
    public static Print? Fill(IReadOnlyList<Print> ticks, DateTimeOffset decision, int qty, bool entry)
    {
        var minimum = decision.AddSeconds(1);
        // Entry expires after one cadence. An exit remains pending, never backdates its fill.
        return ticks.Where(p => p.Time >= minimum && Valid(p, qty))
            .Select(p => p with { Time = p.Received > p.Time ? p.Received : p.Time })
            .Where(p => !entry || p.Time <= decision.AddSeconds(15)).OrderBy(p => p.Time).ThenBy(p => p.Id).FirstOrDefault();
    }
    static bool Fresh(Print? p, DateTimeOffset at) => p is not null && at - p.Time <= TimeSpan.FromSeconds(15);

    public static List<Signal> CrossSignals(Instrument instrument, IReadOnlyList<Print> ticks,
        IReadOnlyList<Reading> readings, string mode)
    {
        var signals = new List<Signal>();
        int? armed = null;
        for (var b = 0; b < readings.Count; b++)
        {
            var r = readings[b];
            if (r.Gap is null || r.Gap <= 0) { armed = null; }
            if (r.Up) { armed = b; }
            var p = Before(ticks, r.End);
            if (!Fresh(p, r.End) || !Valid(p!, instrument.LotSize)) { continue; }
            var hurdle = p!.Ask - p.Bid + instrument.TickSize * 2 + Fees(p.Ask, p.Bid, instrument.LotSize) / instrument.LotSize;
            var fires = mode switch
            {
                "C0" => r.Up,
                "C1" => r.Up && r.Gap >= hurdle,
                "C2" => armed is { } a && b - a < 8 && r.Gap >= hurdle,
                _ => throw new ArgumentException("Unknown mode", nameof(mode))
            };
            if (!fires) { continue; }
            armed = null;
            if (p.Ask < 100 || p.Ask > 150) { continue; }
            signals.Add(new(r.End, instrument.OptionType.ToString(), instrument.Token, mode, r.Gap ?? 0));
        }
        return signals;
    }

    public static Simulation Simulate(DateOnly date, List<Signal> signals, Dictionary<string, Instrument> instruments,
        Dictionary<string, List<Print>> ticks, Func<Signal, IEnumerable<DateTimeOffset>> exitTimes)
    {
        var trades = new List<Trade>(); var decisions = new List<ActionRow>();
        var busyUntil = DateTimeOffset.MinValue; int unresolved = 0;
        foreach (var group in signals.GroupBy(s => s.Time).OrderBy(g => g.Key))
        {
            if (group.Key >= At(date, 15, 0)) { decisions.Add(new(group.Key, "WAIT", "", "Entry cutoff")); continue; }
            if (group.Key <= busyUntil) { decisions.Add(new(group.Key, "WAIT", "", "Position or exit order active")); continue; }
            var selected = group.OrderBy(s => { var p = Before(ticks[s.Token], s.Time)!; return (p.Ask - p.Bid) / p.Ask; })
                .ThenBy(s => s.Token, StringComparer.Ordinal).First();
            var inst = instruments[selected.Token];
            var series = ticks[selected.Token];
            var entry = Fill(series, selected.Time, inst.LotSize, true);
            if (entry is null || entry.Time >= At(date, 15, 0))
            { decisions.Add(new(selected.Time, "WAIT", selected.Token, "No timely valid entry quote")); continue; }
            var exitDecision = exitTimes(selected).Where(t => t > entry.Time && t < At(date, 15, 15))
                .Append(At(date, 15, 15)).Min();
            var exit = Fill(series, exitDecision, inst.LotSize, false);
            if (exit is null)
            {
                unresolved++; busyUntil = At(date, 15, 30);
                decisions.Add(new(exitDecision, "UNRESOLVED", inst.Token, "No valid quote after exit order; P&L unknown")); continue;
            }
            var buy = entry.Ask + inst.TickSize; var sell = exit.Bid - inst.TickSize;
            var path = series.Where(p => p.Time >= entry.Time && p.Time <= exit.Time).ToList();
            var gross = (sell - buy) * inst.LotSize;
            var fees = Fees(buy, sell, inst.LotSize);
            var reason = exitDecision == At(date, 15, 15) ? "ScheduledClose" : "PremiseReversed";
            trades.Add(new(selected.Side, inst.Token, selected.Time, entry.Time, exitDecision, exit.Time, buy, sell,
                inst.LotSize, gross, fees, gross - fees, Math.Max(0, path.Max(p => p.Price) - buy),
                Math.Max(0, buy - path.Min(p => p.Price)), (exit.Time - entry.Time).TotalSeconds, reason, entry.Id, exit.Id));
            decisions.Add(new(selected.Time, "BUY", inst.Token, $"{selected.Reason}; fill {entry.Time:O}; ask+tick {buy}"));
            decisions.Add(new(exitDecision, "EXIT", inst.Token, $"{reason}; fill {exit.Time:O}; bid-tick {sell}"));
            busyUntil = exit.Time;
        }
        return new(trades, decisions, unresolved);
    }

    /// <summary>
    /// Hypothesis v1 (2026-09-25), REJECTED -- kept in the record, not deleted. P0/P1's only exit is
    /// waiting for the full opposite-pattern confirmation (or scheduled close) -- at DTE 0/4 this
    /// pool's own MFE/MAE ratio (~2x) shows a real favorable move is usually available, but win rate
    /// is only ~50%. v1 exited once the position cleared its own bare round-trip cost, then gave back
    /// 50% of peak. Result (all 12 validated days, same-entry comparison via
    /// SimulateGivebackFixedEntries): win rate at DTE 0/4 got WORSE (47.6%->38.1%, 50.0%->0.0%), and
    /// MFE collapsed from ~16% to ~2% across every DTE -- the bare-cost floor (~0.3 points) is
    /// negligible next to real favorable moves (~15-20 points), so it fired on noise almost
    /// immediately instead of after a real move. Rejected as tested, per its own stated failure
    /// criteria ("fails if it mainly cuts winners short before they fully develop").
    /// Hypothesis v2 (2026-09-26): same mechanism, but the floor scales with the position's own
    /// premium instead of bare cost -- <paramref name="minPeakFraction"/> of the buy price (or the
    /// bare cost floor, whichever is larger) must be cleared before trailing can trigger. 5% reuses
    /// this project's own established threshold convention from the price-crossover track
    /// (Price_Based_Findings.md) rather than inventing a new number; it is still a first,
    /// stated-as-such starting choice, not fit to this data. Same expected/failure criteria as v1.
    /// IMPORTANT (caught empirically, not assumed): this re-runs the SAME signal-selection/busyUntil
    /// loop as Simulate, so a faster exit frees capital sooner and lets MORE signals become trades --
    /// this measures the exit change AND the opportunity-set effect together (BACKTEST_RULES rule 14's
    /// "complete sequential simulation" step), confirmed directly here (trade count nearly doubled vs
    /// P0/P1 at every DTE on the v1 first run). It is NOT a same-entry comparison by itself --
    /// SimulateGivebackFixedEntries is the same-entry-only counterpart; report both, never just this one.
    /// </summary>
    public static Simulation SimulateGiveback(DateOnly date, List<Signal> signals, Dictionary<string, Instrument> instruments,
        Dictionary<string, List<Print>> ticks, Func<Signal, IEnumerable<DateTimeOffset>> exitTimes, decimal givebackFraction,
        decimal minPeakFraction = 0.05m)
    {
        var trades = new List<Trade>(); var decisions = new List<ActionRow>();
        var busyUntil = DateTimeOffset.MinValue; int unresolved = 0;
        foreach (var group in signals.GroupBy(s => s.Time).OrderBy(g => g.Key))
        {
            if (group.Key >= At(date, 15, 0)) { decisions.Add(new(group.Key, "WAIT", "", "Entry cutoff")); continue; }
            if (group.Key <= busyUntil) { decisions.Add(new(group.Key, "WAIT", "", "Position or exit order active")); continue; }
            var selected = group.OrderBy(s => { var p = Before(ticks[s.Token], s.Time)!; return (p.Ask - p.Bid) / p.Ask; })
                .ThenBy(s => s.Token, StringComparer.Ordinal).First();
            var inst = instruments[selected.Token];
            var series = ticks[selected.Token];
            var entry = Fill(series, selected.Time, inst.LotSize, true);
            if (entry is null || entry.Time >= At(date, 15, 0))
            { decisions.Add(new(selected.Time, "WAIT", selected.Token, "No timely valid entry quote")); continue; }
            var buy = entry.Ask + inst.TickSize;
            var minPeak = Math.Max(Fees(buy, buy, inst.LotSize) / inst.LotSize + inst.TickSize, minPeakFraction * buy);
            var scheduledClose = At(date, 15, 15);
            var oppositeTime = exitTimes(selected).Where(t => t > entry.Time && t < scheduledClose).Append(scheduledClose).Min();

            decimal peak = 0; DateTimeOffset? givebackTime = null;
            foreach (var p in series.Where(p => p.Time > entry.Time && p.Time <= oppositeTime).OrderBy(p => p.Time).ThenBy(p => p.Id))
            {
                var gain = p.Price - buy;
                if (gain > peak) { peak = gain; }
                if (peak >= minPeak && peak - gain >= givebackFraction * peak) { givebackTime = p.Time; break; }
            }
            var exitDecision = givebackTime ?? oppositeTime;
            var exit = Fill(series, exitDecision, inst.LotSize, false);
            if (exit is null)
            {
                unresolved++; busyUntil = At(date, 15, 30);
                decisions.Add(new(exitDecision, "UNRESOLVED", inst.Token, "No valid quote after exit order; P&L unknown")); continue;
            }
            var sell = exit.Bid - inst.TickSize;
            var path = series.Where(p => p.Time >= entry.Time && p.Time <= exit.Time).ToList();
            var gross = (sell - buy) * inst.LotSize;
            var fees = Fees(buy, sell, inst.LotSize);
            var reason = givebackTime is not null ? "GivebackExit" : exitDecision == scheduledClose ? "ScheduledClose" : "PremiseReversed";
            trades.Add(new(selected.Side, inst.Token, selected.Time, entry.Time, exitDecision, exit.Time, buy, sell,
                inst.LotSize, gross, fees, gross - fees, Math.Max(0, path.Max(p => p.Price) - buy),
                Math.Max(0, buy - path.Min(p => p.Price)), (exit.Time - entry.Time).TotalSeconds, reason, entry.Id, exit.Id));
            decisions.Add(new(selected.Time, "BUY", inst.Token, $"{selected.Reason}; fill {entry.Time:O}; ask+tick {buy}"));
            decisions.Add(new(exitDecision, "EXIT", inst.Token, $"{reason}; fill {exit.Time:O}; bid-tick {sell}"));
            busyUntil = exit.Time;
        }
        return new(trades, decisions, unresolved);
    }

    /// <summary>
    /// The true same-entry-only counterpart to SimulateGiveback (BACKTEST_RULES rule 14's first
    /// step): reuses each baseline trade's OWN already-fixed entry (same token, same entry time, same
    /// buy fill) verbatim -- no signal re-selection, no busyUntil, so trade count can never differ
    /// from the baseline's. Only the exit rule changes. Skips (rather than counts as unresolved) a
    /// baseline trade whose exit can't be re-filled under the new rule, since this is a diagnostic
    /// comparison on fixed entries, not a standalone tradeable simulation.
    /// </summary>
    public static Simulation SimulateGivebackFixedEntries(DateOnly date, List<Trade> baseline, Dictionary<string, Instrument> instruments,
        Dictionary<string, List<Print>> ticks, Func<Signal, IEnumerable<DateTimeOffset>> exitTimes, decimal givebackFraction,
        decimal minPeakFraction = 0.05m)
    {
        var trades = new List<Trade>(); var decisions = new List<ActionRow>();
        var scheduledClose = At(date, 15, 15);
        foreach (var b in baseline)
        {
            var inst = instruments[b.Token];
            var series = ticks[b.Token];
            var buy = b.Buy;
            var minPeak = Math.Max(Fees(buy, buy, inst.LotSize) / inst.LotSize + inst.TickSize, minPeakFraction * buy);
            var pseudoSignal = new Signal(b.Decision, b.Side, b.Token, "SameEntry", 0);
            var oppositeTime = exitTimes(pseudoSignal).Where(t => t > b.Entry && t < scheduledClose).Append(scheduledClose).Min();

            decimal peak = 0; DateTimeOffset? givebackTime = null;
            foreach (var p in series.Where(p => p.Time > b.Entry && p.Time <= oppositeTime).OrderBy(p => p.Time).ThenBy(p => p.Id))
            {
                var gain = p.Price - buy;
                if (gain > peak) { peak = gain; }
                if (peak >= minPeak && peak - gain >= givebackFraction * peak) { givebackTime = p.Time; break; }
            }
            var exitDecision = givebackTime ?? oppositeTime;
            var exit = Fill(series, exitDecision, inst.LotSize, false);
            if (exit is null) { continue; }
            var sell = exit.Bid - inst.TickSize;
            var path = series.Where(p => p.Time >= b.Entry && p.Time <= exit.Time).ToList();
            var gross = (sell - buy) * inst.LotSize;
            var fees = Fees(buy, sell, inst.LotSize);
            var reason = givebackTime is not null ? "GivebackExit" : exitDecision == scheduledClose ? "ScheduledClose" : "PremiseReversed";
            trades.Add(new(b.Side, b.Token, b.Decision, b.Entry, exitDecision, exit.Time, buy, sell,
                inst.LotSize, gross, fees, gross - fees, Math.Max(0, path.Max(p => p.Price) - buy),
                Math.Max(0, buy - path.Min(p => p.Price)), (exit.Time - b.Entry).TotalSeconds, reason, b.EntryId, exit.Id));
        }
        return new(trades, decisions, 0);
    }

    public static object Summary(IEnumerable<Trade> source)
    {
        var t = source.OrderBy(t => t.Exit).ToList();
        decimal equity = 0, peak = 0, drawdown = 0;
        foreach (var row in t) { equity += row.Net; peak = Math.Max(peak, equity); drawdown = Math.Max(drawdown, peak - equity); }
        var profit = t.Sum(x => Math.Max(0, x.Net)); var loss = t.Sum(x => Math.Max(0, -x.Net));
        return new { Count = t.Count, Net = equity, PF = loss > 0 ? profit / loss : (decimal?)null,
            WinPercent = t.Count > 0 ? 100m * t.Count(x => x.Net > 0) / t.Count : 0, Drawdown = drawdown,
            BestTrade = t.Count > 0 ? t.Max(x => x.Net) : 0, NetWithoutBestTrade = t.Count > 0 ? equity - t.Max(x => x.Net) : 0,
            MeanMfePoints = t.Count > 0 ? t.Average(x => x.Mfe) : 0, MeanMaePoints = t.Count > 0 ? t.Average(x => x.Mae) : 0,
            MeanSeconds = t.Count > 0 ? t.Average(x => x.Seconds) : 0 };
    }

    public static async Task<int> RunAsync(DbContextOptions<NiftySignalDbContext> options, string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var output = args.Length > 1 ? args[1] : "research-reversal";
        Directory.CreateDirectory(output);
        await using var db = new NiftySignalDbContext(options);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        db.Database.SetCommandTimeout(300);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET default_transaction_read_only = on");
        var dates = await db.Instruments.Where(i => i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Option)
            .Select(i => i.AsOfDate).Distinct().OrderBy(d => d).ToListAsync();
        Console.WriteLine($"Read-only database: {db.Database.GetDbConnection().Database}. Available instrument dates: {string.Join(',', dates)}");
        if (args.Contains("--inventory")) { return 0; }
        if (args.Contains("--snapshot25"))
        {
            var date = new DateOnly(2026, 9, 25);
            var tokens = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY").Select(i => i.Token).ToListAsync();
            var from = At(date, 9, 15).ToUniversalTime(); var to = At(date, 15, 30).ToUniversalTime();
            var summary = await db.Ticks.Where(t => tokens.Contains(t.Token) && t.ExchangeTimestamp >= from && t.ExchangeTimestamp <= to)
                .GroupBy(t => t.Token).Select(g => new { Token = g.Key, Count = g.LongCount(), First = g.Min(t => t.ExchangeTimestamp),
                    Last = g.Max(t => t.ExchangeTimestamp), MaxId = g.Max(t => t.Id), LastReceived = g.Max(t => t.ReceivedAt) }).ToListAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "september25-metadata.json"), JsonSerializer.Serialize(new { CheckedAt = DateTimeOffset.UtcNow, summary }, Json));
            Console.WriteLine($"September 25 metadata only: {summary.Count} tokens, {summary.Sum(s => s.Count):N0} ticks. No strategy evaluated.");
            return 0;
        }
        var requested = args.FirstOrDefault(a => a.StartsWith("--date=", StringComparison.Ordinal));
        var researchDates = dates.Where(d => d >= new DateOnly(2026, 9, 4) && d <= new DateOnly(2026, 9, 23)
            && (requested is null || d == DateOnly.Parse(requested[7..]))).ToList();
        var daily = new List<object>();
        foreach (var date in researchDates)
        {
            Console.WriteLine($"Loading {date:yyyy-MM-dd} raw option ticks...");
            var report = await BuildDayReportAsync(db, date);
            var path = Path.Combine(output, $"{date:yyyy-MM-dd}.json");
            if (File.Exists(path)) { throw new IOException($"Report already exists: {path}; use a fresh run directory"); }
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));
            daily.Add(new { Date = date, report.Dte, Reports = path });
            Console.WriteLine($"Saved {path}; A/B full-surface state entries={report.patternRows.Count(r => r.StateEntry && r.Full)}.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(daily, Json));
        return 0;
    }

    public sealed record DayReport(DateOnly Date, DateOnly Expiry, int Dte, List<object> coverage, List<object> samples,
        List<PatternRow> patternRows, List<PatternRow> referencePatterns, Dictionary<string, object> experiments,
        List<object> traces, string QuoteAge);

    /// <summary>
    /// Everything for one session: cadence coverage/verification samples, C0/C1/C2 and P0/P1
    /// experiments, and full raw-tick trade traces. Pure w.r.t. its DbContext -- takes any
    /// NiftySignalDbContext already populated for this date, live Postgres or a seeded
    /// EF Core InMemoryDatabase (see FileBackedResearchRunner), so this exact logic is what both
    /// paths run, never a parallel reimplementation.
    /// </summary>
    public static async Task<DayReport> BuildDayReportAsync(NiftySignalDbContext db, DateOnly date)
    {
            var chainAll = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY"
                && i.InstrumentType == InstrumentType.Option && i.ExpiryDate >= date).ToListAsync();
            var expiry = chainAll.Min(i => i.ExpiryDate)!.Value;
            var chain = chainAll.Where(i => i.ExpiryDate == expiry).OrderBy(i => i.Token).ToDictionary(i => i.Token);
            var start = At(date, 9, 15).ToUniversalTime(); var end = At(date, 15, 30).ToUniversalTime();
            var ticks = new Dictionary<string, List<Print>>(); var readings = new Dictionary<string, List<Reading>>();
            // 2026-09-26: a second, wider time cadence (30s buckets, fast=10 readings=5min,
            // slow=40 readings=20min) requested alongside the original 15s/8-40 (2min/10min) one,
            // to compare against volume-bar-based construction on equal footing. Same fast:slow
            // ratio family, just a different cadence -- applied identically across every DTE, no
            // per-DTE tuning, per instruction. A first, stated-as-such starting choice at the low
            // end of the requested 5-10min/20-30min ranges, not fit to this data.
            var readingsT = new Dictionary<string, List<Reading>>();
            var coverage = new List<object>(); var samples = new List<object>();
            foreach (var inst in chain.Values)
            {
                var token = inst.Token;
                var list = await db.Ticks.Where(t => t.Token == token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end && t.LastPrice > 0)
                    .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id)
                    .Select(t => new Print(t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                        t.Depth == null ? 0 : t.Depth.Bid1Price, t.Depth == null ? 0 : t.Depth.Ask1Price,
                        t.Depth == null ? 0 : t.Depth.Bid1Qty, t.Depth == null ? 0 : t.Depth.Ask1Qty, t.Volume)).ToListAsync();
                ticks[token] = list; var bars = Cadences(list, start, end); readings[token] = bars;
                readingsT[token] = Cadences(list, start, end, fast: 10, slow: 40, bucketSeconds: 30);
                coverage.Add(new { token, inst.OptionType, inst.StrikePrice, inst.LotSize, Count = list.Count,
                    First = list.FirstOrDefault()?.Time, Last = list.LastOrDefault()?.Time,
                    Empty = bars.Count(b => b.Count == 0), Warm = bars.Count(b => b.Gap is not null),
                    InvalidQuotes = list.Count(p => !Valid(p, inst.LotSize)),
                    ReceiptAfterCadence = list.Count(p => p.Received > start.AddSeconds(Math.Ceiling((p.Time - start).TotalSeconds / 15) * 15)),
                    MaxReceiptLagSeconds = list.Count > 0 ? list.Max(p => (p.Received - p.Time).TotalSeconds) : 0 });
                if (list.Count == 0) { continue; }
                foreach (var index in new[] { 0, 39, 40, 120, 800 })
                {
                    var bar = bars[index]; var raw = list.Where(p => p.Time > bar.End.AddSeconds(-15) && p.Time <= bar.End && p.Received <= bar.End).ToList();
                    decimal? direct = raw.Count > 0 ? raw.Average(p => p.Price) : null;
                    if (direct != bar.Average || raw.Count != bar.Count) { throw new InvalidOperationException("Raw mean mismatch"); }
                    if (samples.Count < 20) { samples.Add(new { token, bar, RawTicks = raw }); }
                }
            }
            Console.WriteLine($"{date}: {ticks.Sum(kv => kv.Value.Count):N0} ticks, {chain.Count} tokens; cadence means verified.");
            var experiments = new Dictionary<string, object>();
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                foreach (var mode in new[] { "C0", "C1", "C2" })
                {
                    var signals = chain.Values.Where(i => i.OptionType == side)
                        .SelectMany(i => CrossSignals(i, ticks[i.Token], readings[i.Token], mode)).ToList();
                    signals = signals.Select(s => Forward(s, ticks[s.Token])).ToList();
                    var simulation = Simulate(date, signals, chain, ticks,
                        s => readings[s.Token].Where(r => r.Down).Select(r => r.End));
                    experiments[$"{mode}-{side}"] = new { Summary = Summary(simulation.Trades), simulation, signals };
                }
                // Same C0/C1/C2 logic, wider 30s/5min/20min cadence (readingsT) instead of the
                // original 15s/2min/10min (readings) -- "CT" = Cadence-Time-variant.
                foreach (var mode in new[] { "C0", "C1", "C2" })
                {
                    var signals = chain.Values.Where(i => i.OptionType == side)
                        .SelectMany(i => CrossSignals(i, ticks[i.Token], readingsT[i.Token], mode)).ToList();
                    signals = signals.Select(s => Forward(s, ticks[s.Token])).ToList();
                    var simulation = Simulate(date, signals, chain, ticks,
                        s => readingsT[s.Token].Where(r => r.Down).Select(r => r.End));
                    experiments[$"{mode}T-{side}"] = new { Summary = Summary(simulation.Trades), simulation, signals };
                }
                // Same C0/C1/C2 logic again, this time on each token's own volume-threshold bars
                // (650, see OptionVolumeBars) instead of any time cadence -- "V" = Volume-bar variant.
                var readingsV = chain.Values.Where(i => i.OptionType == side)
                    .ToDictionary(i => i.Token, i => ReadingsFromBars(OptionVolumeBars(ticks[i.Token], 650), fast: 8, slow: 40));
                foreach (var mode in new[] { "C0", "C1", "C2" })
                {
                    var signals = chain.Values.Where(i => i.OptionType == side)
                        .SelectMany(i => CrossSignals(i, ticks[i.Token], readingsV[i.Token], mode)).ToList();
                    signals = signals.Select(s => Forward(s, ticks[s.Token])).ToList();
                    var simulation = Simulate(date, signals, chain, ticks,
                        s => readingsV[s.Token].Where(r => r.Down).Select(r => r.End));
                    experiments[$"{mode}V-{side}"] = new { Summary = Summary(simulation.Trades), simulation, signals };
                }
            }
            var future = await db.Instruments.Where(i => i.AsOfDate == date && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future)
                .OrderBy(i => i.ExpiryDate).FirstAsync();
            var futureReceipts = await db.Ticks.Where(t => t.Token == future.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end)
                .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id).Select(t => new { t.ExchangeTimestamp, t.ReceivedAt }).ToListAsync();

            var futureBars = await FutureEventBarBuilder.BuildDayAsync(db, date, 13000, CancellationToken.None);
            var available = ComputeAvailability(futureBars, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
            var (patternRows, referencePatterns, patternSignals) = BuildPatternPopulation(chain, ticks, readings, futureBars, available);

            // 2026-09-26: same Pattern A/B detection logic, but the underlying future "bars" are
            // built on a fixed 30-second wall-clock interval instead of the 13,000-contract volume
            // threshold -- comparing bar CONSTRUCTION method, signal/eligibility logic unchanged.
            // Patterns()'s own 180-second window is real-time-based already, so bar granularity here
            // only affects price-sampling resolution within that window, not the window length.
            var futureTicks = await db.Ticks.Where(t => t.Token == future.Token && t.ExchangeTimestamp >= start && t.ExchangeTimestamp <= end && t.LastPrice > 0)
                .OrderBy(t => t.ExchangeTimestamp).ThenBy(t => t.Id)
                .Select(t => new Print(t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice,
                    t.Depth == null ? 0 : t.Depth.Bid1Price, t.Depth == null ? 0 : t.Depth.Ask1Price,
                    t.Depth == null ? 0 : t.Depth.Bid1Qty, t.Depth == null ? 0 : t.Depth.Ask1Qty)).ToListAsync();
            var futureBarsT = BuildTimeBasedFutureBars(futureTicks, start, end, bucketSeconds: 30);
            var availableT = ComputeAvailability(futureBarsT, futureReceipts.Select(r => (r.ExchangeTimestamp, r.ReceivedAt)).ToList(), start);
            var (patternRowsT, referencePatternsT, patternSignalsT) = BuildPatternPopulation(chain, ticks, readings, futureBarsT, availableT);
            var baselineSims = new Dictionary<string, Simulation>();
            foreach (var mode in new[] { "PT0", "PT1" })
            {
                var signals = patternSignalsT.Where(s => mode == "PT0" || !s.Extended).ToList();
                var sim = Simulate(date, signals, chain, ticks, s => patternRowsT.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time));
                experiments[mode] = new { Summary = Summary(sim.Trades), simulation = sim, signals,
                    BySide = sim.Trades.GroupBy(t => t.Side).ToDictionary(g => g.Key, g => Summary(g)) };
            }
            foreach (var mode in new[] { "P0", "P1" })
            {
                var signals = patternSignals.Where(s => mode == "P0" || !s.Extended).ToList();
                var sim = Simulate(date, signals, chain, ticks, s => patternRows.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time));
                baselineSims[mode] = sim;
                experiments[mode] = new { Summary = Summary(sim.Trades), simulation = sim, signals,
                    BySide = sim.Trades.GroupBy(t => t.Side).ToDictionary(g => g.Key, g => Summary(g)) };
            }
            // P4: entry-side filter only, exit UNCHANGED from P0 (both exit redesigns tried
            // earlier -- SimulateGiveback v1/v2 -- failed; the wait-for-opposite-pattern exit
            // remains the best one found). Keeps only signals whose relative-move ratio (Signal.Gap,
            // see BuildPatternPopulation's own comment) clears 0.35 -- the median split from the
            // event-study work on this exact 12-day set landed at 0.23 (A/DTE=0) and 0.44 (B/DTE=4);
            // 0.35 sits between them, applied identically to every DTE/side, not tuned per group.
            // NOT an independently pre-registered threshold -- it's the final packaging of an
            // exploratory lead found on this same data, not a blind validation. Report it as such.
            {
                var signals = patternSignals.Where(s => s.Gap >= 0.35m).ToList();
                var sim = Simulate(date, signals, chain, ticks, s => patternRows.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time));
                experiments["P4"] = new { Summary = Summary(sim.Trades), simulation = sim, signals,
                    BySide = sim.Trades.GroupBy(t => t.Side).ToDictionary(g => g.Key, g => Summary(g)) };
            }
            // P0G/P1G: TRUE same-entry comparison (BACKTEST_RULES rule 14, first step) -- P0/P1's
            // own already-fixed entries, only the exit changes. Trade count is identical to the
            // paired baseline by construction; this isolates the pure exit effect.
            foreach (var (mode, baseline) in new[] { ("P0G", "P0"), ("P1G", "P1") })
            {
                var sim = SimulateGivebackFixedEntries(date, baselineSims[baseline].Trades, chain, ticks,
                    s => patternRows.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time), 0.5m);
                experiments[mode] = new { Summary = Summary(sim.Trades), simulation = sim,
                    BySide = sim.Trades.GroupBy(t => t.Side).ToDictionary(g => g.Key, g => Summary(g)) };
            }
            // P2/P3: FULL sequential re-simulation with the giveback exit (BACKTEST_RULES rule 14,
            // second step) -- same starting signal set as P0/P1, but a faster exit frees capital
            // sooner, so trade count can legitimately differ (opportunity-set effect included).
            // Report alongside P0G/P1G, never in place of it -- see SimulateGiveback's doc comment.
            foreach (var mode in new[] { "P2", "P3" })
            {
                var signals = patternSignals.Where(s => mode == "P2" || !s.Extended).ToList();
                var sim = SimulateGiveback(date, signals, chain, ticks,
                    s => patternRows.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time), 0.5m);
                experiments[mode] = new { Summary = Summary(sim.Trades), simulation = sim, signals,
                    BySide = sim.Trades.GroupBy(t => t.Side).ToDictionary(g => g.Key, g => Summary(g)) };
            }
            // Full raw tick paths for deterministic first, best and worst baseline trades.
            var traceSignals = patternSignals;
            var traceSim = Simulate(date, traceSignals, chain, ticks, s => patternRows.Where(r => r.StateEntry && r.State is "A" or "B" && r.State != s.Side).Select(r => r.Time));
            var crossTraceSignals = chain.Values.SelectMany(i => CrossSignals(i, ticks[i.Token], readings[i.Token], "C0")).ToList();
            var crossTrace = Simulate(date, crossTraceSignals, chain, ticks, s => readings[s.Token].Where(r => r.Down).Select(r => r.End));
            var traces = traceSim.Trades.Take(1).Concat(traceSim.Trades.OrderBy(t => t.Net).Take(1)).Concat(traceSim.Trades.OrderByDescending(t => t.Net).Take(1))
                .Concat(crossTrace.Trades.Take(2))
                .Distinct().Select(t => (object)new { Trade = t, RawTicks = ticks[t.Token].Where(p => p.Time >= t.Decision.AddSeconds(-15) && p.Time <= t.Exit).ToList() }).ToList();
            return new(date, expiry, expiry.DayNumber - date.DayNumber, coverage, samples,
                patternRows, referencePatterns, experiments, traces, "Individual field age unavailable; modeled fills only");
    }

    static Signal Forward(Signal s, List<Print> ticks)
    {
        var p = Before(ticks, s.Time);
        decimal? Return(int minutes)
        {
            var at = s.Time.AddMinutes(minutes); var q = Before(ticks, at);
            return Fresh(q, at) && p is not null && p.Price > 0 ? (q!.Price / p.Price - 1) * 100 : null;
        }
        return s with { Forward1 = Return(1), Forward2 = Return(2), Forward5 = Return(5) };
    }

    /// <summary>
    /// Causal "when could this bar's close actually have been acted on" timestamps -- the later of
    /// the bar's own end and the latest receipt time among all future ticks up to that end. Shared
    /// by both the volume-threshold and time-based bar constructions; the logic itself doesn't care
    /// how a bar was built, only that it has an EndTimestamp.
    /// </summary>
    static List<DateTimeOffset> ComputeAvailability(List<FutureEventBar> bars, List<(DateTimeOffset ExchangeTimestamp, DateTimeOffset ReceivedAt)> futureReceipts, DateTimeOffset start)
    {
        var available = new List<DateTimeOffset>(); var latestReceipt = start; var fc = 0;
        foreach (var bar in bars)
        {
            while (fc < futureReceipts.Count && futureReceipts[fc].ExchangeTimestamp <= bar.EndTimestamp)
            { latestReceipt = latestReceipt > futureReceipts[fc].ReceivedAt ? latestReceipt : futureReceipts[fc].ReceivedAt; fc++; }
            available.Add(latestReceipt > bar.EndTimestamp ? latestReceipt : bar.EndTimestamp);
        }
        return available;
    }

    /// <summary>
    /// Fixed 30-second (or whatever bucketSeconds is) wall-clock future bars -- the time-based
    /// counterpart to FutureEventBarBuilder's 13,000-contract volume threshold, for comparing bar
    /// CONSTRUCTION method on Pattern A/B. A self-contained builder (not a shared/tested class
    /// reused elsewhere) so this doesn't risk FutureEventBarBuilder/TimeBasedBarBuilder's own
    /// existing callers. A bucket with zero ticks is skipped entirely, never fabricated -- same
    /// discipline as Cadences().
    /// </summary>
    internal static List<FutureEventBar> BuildTimeBasedFutureBars(List<Print> futureTicks, DateTimeOffset start, DateTimeOffset end, int bucketSeconds)
    {
        var bars = new List<FutureEventBar>();
        var bucket = TimeSpan.FromSeconds(bucketSeconds);
        var cursor = 0; var eventId = 0;
        for (var left = start; left < end; left += bucket)
        {
            var right = left + bucket;
            decimal? open = null, high = null, low = null, close = null; long volume = 0; var count = 0;
            while (cursor < futureTicks.Count && futureTicks[cursor].Time <= right)
            {
                var t = futureTicks[cursor++];
                if (t.Time <= left) { continue; }
                open ??= t.Price;
                high = high is { } h ? Math.Max(h, t.Price) : t.Price;
                low = low is { } l ? Math.Min(l, t.Price) : t.Price;
                close = t.Price;
                count++;
            }
            if (count == 0) { continue; }
            bars.Add(new(eventId++, DateOnly.FromDateTime(start.Date), left, right, open!.Value, high!.Value, low!.Value, close!.Value,
                volume, null, null, count, false));
        }
        return bars;
    }

    /// <summary>
    /// The full Pattern A/B population (rows, non-causal reference rows, and the eligible tradeable
    /// signals with the exhaustion/extended flag) for one bar series -- shared by both the
    /// volume-threshold and time-based constructions so the signal/eligibility logic itself is
    /// never duplicated between them.
    /// </summary>
    static (List<PatternRow> Rows, List<PatternRow> Reference, List<Signal> Signals) BuildPatternPopulation(
        Dictionary<string, Instrument> chain, Dictionary<string, List<Print>> ticks, Dictionary<string, List<Reading>> readings,
        List<FutureEventBar> bars, List<DateTimeOffset> available)
    {
        var patternRows = Patterns(chain, ticks, bars, available);
        var referencePatterns = Patterns(chain, ticks, bars, bars.Select(b => b.EndTimestamp).ToList(), false);
        var moveByIndex = patternRows.ToDictionary(r => r.Index, r => Math.Abs(r.Move));
        var patternSignals = new List<Signal>();
        foreach (var row in patternRows.Where(r => r.StateEntry && r.Full))
        {
            var side = row.State == "A" ? OptionType.Put : OptionType.Call;
            var eligible = chain.Values.Where(i => i.OptionType == side).Select(i => (i, p: Before(ticks[i.Token], row.Time)))
                .Where(x => Fresh(x.p, row.Time) && Valid(x.p!, x.i.LotSize) && x.p!.Ask >= 100 && x.p.Ask <= 150)
                .OrderBy(x => (x.p!.Ask - x.p.Bid) / x.p.Ask).ThenBy(x => x.i.Token, StringComparer.Ordinal).FirstOrDefault();
            if (eligible.i is null) { continue; }
            var history = readings[eligible.i.Token].Where(r => r.End <= row.Time && r.Slow is not null && r.Average is not null).TakeLast(41).ToList();
            var extended = history.Count < 41 || eligible.p!.Price - history[^1].Slow!.Value > history.Take(40).Max(r => r.Average!.Value - r.Slow!.Value);
            // 2026-09-26: relative-move ratio (this event's own |Move| divided by the trailing
            // 20-bar average |Move| strictly before it, causal) -- stored in Signal.Gap (otherwise
            // unused for pattern signals) so a candidate rule can filter on it without changing the
            // Signal shape. Exploratory lead from the earlier event-study work on this exact 12-day
            // set, not an independently pre-registered threshold -- see P4/P5's own doc comment.
            var trail = Enumerable.Range(Math.Max(0, row.Index - 20), Math.Min(20, row.Index)).Where(moveByIndex.ContainsKey).Select(i => moveByIndex[i]).ToList();
            var ratio = trail.Count >= 5 && trail.Average() > 0 ? Math.Abs(row.Move) / trail.Average() : 0;
            patternSignals.Add(Forward(new(row.Time, row.State, eligible.i.Token, "Pattern" + row.State, ratio, extended), ticks[eligible.i.Token]));
        }
        return (patternRows, referencePatterns, patternSignals);
    }

    static List<PatternRow> Patterns(Dictionary<string, Instrument> chain, Dictionary<string, List<Print>> ticks, List<FutureEventBar> bars, List<DateTimeOffset> available, bool causal = true)
    {
        var result = new List<PatternRow>(); string previous = "Other";
        var strikes = chain.Values.Select(i => i.StrikePrice!.Value).Distinct().Order().ToList();
        for (int b = 0; b < bars.Count; b++)
        {
            var current = bars[b];
            if (current.IsFinalPartialBar) { continue; }
            var first = bars[AdaptiveWindowAnalysis.FindWindowStartIndex(bars, b, 180)];
            var strike = strikes.OrderBy(k => Math.Abs(k - current.Close)).ThenBy(k => k).First();
            decimal? Change(decimal k, OptionType side)
            {
                var inst = chain.Values.FirstOrDefault(i => i.StrikePrice == k && i.OptionType == side);
                if (inst is null) { return null; }
                Print? Lookup(DateTimeOffset time) => causal ? Before(ticks[inst.Token], time)
                    : ticks[inst.Token].LastOrDefault(p => p.Time <= time);
                var a = Lookup(first.StartTimestamp); var z = Lookup(current.EndTimestamp);
                if (causal && (!Fresh(a, first.StartTimestamp) || !Fresh(z, current.EndTimestamp))) { return null; }
                return a is null || z is null ? null : z.Price - a.Price;
            }
            var move = current.Close - first.Open;
            var ce = Change(strike, OptionType.Call); var pe = Change(strike, OptionType.Put);
            var state = current.EndTimestamp - first.StartTimestamp < TimeSpan.FromSeconds(180) ? "Other"
                : move > 0 && ce < 0 && pe > 0 ? "A" : move < 0 && ce > 0 && pe < 0 ? "B" : "Other";
            var idx = strikes.IndexOf(strike);
            bool full = state != "Other" && idx >= 2 && idx + 2 < strikes.Count;
            if (full)
            {
                for (int j = idx - 2; j <= idx + 2; j++)
                {
                    var c = Change(strikes[j], OptionType.Call); var p = Change(strikes[j], OptionType.Put);
                    full &= state == "A" ? c < 0 && p > 0 : c > 0 && p < 0;
                }
            }
            decimal? Fwd(int h) => b + h < bars.Count ? bars[b + h].Close - current.Close : null;
            result.Add(new(available[b], state, state != previous, full, move, current.Close, b, Fwd(1), Fwd(2), Fwd(4)));
            previous = state;
        }
        return result;
    }
}
