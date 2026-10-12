using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Npt1M1;
using NiftySignal.Npt1M1.Data;
using NiftySignal.Persistence;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Live;

// Usage: Npt1M1Parity --date 2026-09-22 --package <dir with nifty_<date>_bars_wide.csv and nifty_<date>_session_state.csv> --history <volume_history_dev.csv>
//        --connection-file <appsettings.Local.json> [--connection-name RecordedSource] [--database niftysignal_vm_copy] [--compute-history]
// The connection string is read from the file and never printed. Dates after 2026-10-01 are sealed and refused.
var a = ParseArgs(args);
var day = DateOnly.ParseExact(a["date"], "yyyy-MM-dd", CultureInfo.InvariantCulture);
if (day > new DateOnly(2026, 10, 1))
{
    Console.Error.WriteLine("REFUSED: sessions after 2026-10-01 are sealed out-of-sample data.");
    return 2;
}

var package = a["package"];
var wide = Directory.GetFiles(package, "nifty_*_bars_wide.csv").Single();
var state = Directory.GetFiles(package, "nifty_*_session_state.csv").Single();
var history = VolumeHistory.ReadCsv(a["history"]).Where(r => r.Date < day).ToList();

var json = JsonDocument.Parse(File.ReadAllText(a["connection-file"]));
var cs = json.RootElement.GetProperty("ConnectionStrings").GetProperty(a.GetValueOrDefault("connection-name", "RecordedSource")).GetString()!;
var csb = new NpgsqlConnectionStringBuilder(cs) { Database = a.GetValueOrDefault("database", "niftysignal_vm_copy") };
var options = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(csb.ConnectionString).Options;
await using var db = new NiftySignalDbContext(options);
var reader = new Npt1TickReader();

var future = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future).OrderBy(i => i.ExpiryDate).FirstAsync();
var optionRows = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice != null).OrderBy(i => i.ExpiryDate).ThenBy(i => i.StrikePrice).ToListAsync();
var weekly = optionRows.Select(x => x.ExpiryDate!.Value).Distinct().OrderBy(x => x).First();
var nearest = optionRows.Where(x => x.ExpiryDate == weekly).ToList();
var descriptors = nearest.Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, i.ExpiryDate!.Value, i.LotSize)).ToList();
var tickSizes = nearest.ToDictionary(i => i.Token, i => i.TickSize > 0m ? i.TickSize : 0.05m, StringComparer.Ordinal);
var tokens = descriptors.Select(d => d.Token).Append(future.Token).Distinct(StringComparer.Ordinal).ToArray();
var futureInstrument = new FutureInstrument(future.Token, future.TradingSymbol, future.ExpiryDate ?? day, future.LotSize);

var failures = new List<string>();
void Check(bool ok, string what)
{
    if (!ok)
    {
        failures.Add(what);
    }
}

// optional: recompute the prior-session volumes from the ticks and compare with the research CSV (ports the volume-history computation)
if (a.ContainsKey("compute-history"))
{
    foreach (var h in history.Where(h => h.FuturesTicks > 0))
    {
        var fut = new FutureInstrument(h.FutureToken, h.FutureSymbol, h.Expiry, h.LotSize);
        var rows = await reader.ReadWindowAsync(db, new[] { h.FutureToken }, h.Date, M1Time.IstTime(h.Date, 15, 35), CancellationToken.None);
        var c = VolumeHistory.Compute(fut, h.Date, rows);
        Check(c.OpeningVolume == h.OpeningVolume && c.WindowVolume == h.WindowVolume && c.CloseVolume == h.CloseVolume && c.OpeningCoverageMinutes == h.OpeningCoverageMinutes && c.BaselineEligible == h.BaselineEligible,
            $"volume history {h.Date}: computed {c.OpeningVolume}/{c.WindowVolume}/{c.CloseVolume} vs reference {h.OpeningVolume}/{h.WindowVolume}/{h.CloseVolume}");
        Console.WriteLine($"history {h.Date}: opening {c.OpeningVolume} window {c.WindowVolume} close {c.CloseVolume} eligible {c.BaselineEligible} (reference {h.OpeningVolume}/{h.WindowVolume}/{h.CloseVolume})");
    }
}

var sw = Stopwatch.StartNew();
var freezeRows = await reader.ReadWindowAsync(db, tokens, day, M1Time.IstTime(day, 9, 30).AddSeconds(5), CancellationToken.None);
var freeze = M1SessionFreeze.Build(day, weekly, futureInstrument, descriptors, tickSizes, freezeRows, VolumeHistory.Eligible(history));
var refState = ReadState(state);
Console.WriteLine($"freeze: base bar {freeze.Report.BaseBarVolume} (reference {refState.GetValueOrDefault("base_bar_volume")}), opening {freeze.Report.OpeningVolume} (reference {refState.GetValueOrDefault("opening_volume")}), status {freeze.Decision.MethodStatus}, fallback {freeze.Decision.FallbackSource}");
Check(freeze.Report.BaseBarVolume.ToString(CultureInfo.InvariantCulture) == refState.GetValueOrDefault("base_bar_volume"), "base bar volume");
Check(freeze.Report.OpeningVolume.ToString(CultureInfo.InvariantCulture) == refState.GetValueOrDefault("opening_volume"), "opening volume");
Check(freeze.Report.WeeklyExpiry == refState.GetValueOrDefault("weekly_option_expiry"), "weekly expiry");
if (refState.TryGetValue("residual_center_strike", out var rc) && freeze.Report.AnchorCenterStrike is { } ac)
{
    Check(double.Parse(rc, CultureInfo.InvariantCulture) == double.Parse(ac, CultureInfo.InvariantCulture), $"anchor centre {ac} vs {rc}");
}

var producer = new M1BarProducer(freeze);
var readWatch = new Stopwatch();
var offerWatch = new Stopwatch();
var releaseWatch = new Stopwatch();
var engineWatch = new Stopwatch();
var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;

// --trade: the same groups also drive the three NPT1 strategies in the HISTORICAL-PARITY profile (entries until 11:00, terminal close at the last bar end <= 15:00) for the parity check against the frozen research trades.
TradingEngine? engine = null;
EngineLoop? loop = null;
if (a.ContainsKey("trade"))
{
    var paper = new PaperLiveOptions
    {
        Enabled = true,
        Capital = 1_000_000_000_000m,                         // not a gate in the parity run
        BrokeragePerOrder = 0m,
        OptionTickSize = 0.05m,
        Strategies = PaperLiveOptions.Npt1StrategyIds.ToDictionary(id => id, _ => new StrategyToggleOptions { Enabled = true }),
    };
    var strategies = PaperLiveSetup.Npt1Strategies(paper);
    var config = PaperLiveSetup.Npt1Config(paper, strategies);
    config = config with
    {
        Session = SessionConfig.Npt1HistoricalParity1100(),
        Variants = config.Variants.ToDictionary(
            kv => kv.Key,
            kv => kv.Value with { EntryCondition = ((NextCompletedBarEntrySpec)kv.Value.EntryCondition) with { TerminalMode = NextBarTerminalMode.HistoricalLastBarEnd, PolicyLabel = "npt1-hist1100-v1" } }),
    };
    engine = new TradingEngine(config, day, freeze.Freeze.ToContracts(), strategies.ToDictionary(s => s.Family.StrategyId, s => s.Family));
    loop = new EngineLoop(engine, engine.Variants.Schedule.DayStartUtc);
    engine.Start(loop);
}

var probeToken = a.GetValueOrDefault("probe-token");
var probeFrom = a.TryGetValue("probe-from", out var pf) ? TimeSpan.Parse(pf, CultureInfo.InvariantCulture) : TimeSpan.Zero;
var probeTo = a.TryGetValue("probe-to", out var pt) ? TimeSpan.Parse(pt, CultureInfo.InvariantCulture) : TimeSpan.Zero;
(decimal Bid, decimal Ask, DateTimeOffset At)? probeLast = null;

void Feed(M1Group g)
{
    if (probeToken is not null)
    {
        foreach (var q in g.Quotes.Where(x => x.Event.Token == probeToken).Select(x => x.Event))
        {
            if (q.Bid is > 0m && q.Ask is { } qa && qa >= q.Bid)
            {
                probeLast = (q.Bid.Value, qa, q.AvailableAt);
            }
            else if (q.Bid is null || q.Ask is null)
            {
                Console.WriteLine($"PROBE one-sided quote at {q.AvailableAt.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss.fff} bid={q.Bid} ask={q.Ask}");
            }
        }

        foreach (var b in g.Bars)
        {
            var t = b.Event.AvailableAt.ToOffset(TimeSpan.FromHours(5.5)).TimeOfDay;
            if (t >= probeFrom && t <= probeTo)
            {
                Console.WriteLine($"PROBE bar {b.Event.BarSeq} end {t} last two-sided bid/ask {probeLast?.Bid}/{probeLast?.Ask} age {(probeLast is { } l ? (b.Event.AvailableAt - l.At).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) : "~")} ms");
            }
        }
    }

    if (loop is null)
    {
        return;
    }

    var events = g.Quotes.Select(q => (EngineEvent)q.Event).Concat(g.Bars.Select(b => (EngineEvent)b.Event)).ToArray();
    loop.ProcessGroup(events);
}
var hardEnd = M1Time.IstTime(day, 15, 35);
var bars = new List<M1Bar>();
long afterId = 0;
long rowsRead = 0;
while (true)
{
    readWatch.Start();
    var batch = await reader.ReadAfterIdAsync(db, tokens, afterId, day, 100_000, CancellationToken.None);
    readWatch.Stop();
    if (batch.Count == 0)
    {
        break;
    }

    foreach (var row in batch)
    {
        afterId = row.Id;
        if (row.ExchangeTimestamp > hardEnd || row.ReceivedAt > hardEnd)
        {
            continue;                                       // the research export reads no row after 15:35 IST
        }

        offerWatch.Start();
        producer.Offer(row);
        offerWatch.Stop();
    }

    rowsRead += batch.Count;
    releaseWatch.Start();
    var released = producer.Release();
    releaseWatch.Stop();
    foreach (var g in released)
    {
        bars.AddRange(g.Bars);
        engineWatch.Start();
        Feed(g);
        engineWatch.Stop();
    }
}

foreach (var g in producer.Finish())
{
    bars.AddRange(g.Bars);
    Feed(g);
}

if (engine is not null && loop is not null)
{
    loop.AdvanceTo(engine.Variants.Schedule.SessionEndUtc);
    loop.Finish(engine.Variants.Schedule.SessionEndUtc);
    var tradesFile = a.GetValueOrDefault("trade-file", "trades.csv");
    var append = File.Exists(tradesFile);
    await using var w = new StreamWriter(tradesFile, append);
    if (!append)
    {
        w.WriteLine("strategy,date,side,strike,token,expiry,signal_utc,entry_utc,entry_ask,exit_utc,exit_bid,qty,gross,fees,net,mae_rs,mfe_rs,exit_reason,signal_bar,entry_pending");
    }

    var opened = engine.Outputs.OfType<PositionOpened>().ToDictionary(o => o.PositionId);
    foreach (var c in engine.Outputs.OfType<PositionClosed>().OrderBy(c => c.OpenedAt))
    {
        var o = opened[c.PositionId];
        var qty = c.EntryQuantity;
        w.WriteLine(string.Join(",",
            o.Variant.StrategyId, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), o.Side == NiftySignal.Trading.Core.Instruments.OptionSide.Call ? "CE" : "PE",
            o.Strike.ToString(CultureInfo.InvariantCulture), o.Token, o.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            o.EntryDecisionAt.ToString("O", CultureInfo.InvariantCulture), o.At.ToString("O", CultureInfo.InvariantCulture), o.AvgEntryPrice.ToString(CultureInfo.InvariantCulture),
            c.At.ToString("O", CultureInfo.InvariantCulture), c.AvgExitPrice.ToString(CultureInfo.InvariantCulture), qty.ToString(CultureInfo.InvariantCulture),
            c.GrossPnl.ToString(CultureInfo.InvariantCulture), c.TotalCosts.ToString(CultureInfo.InvariantCulture), c.NetPnl.ToString(CultureInfo.InvariantCulture),
            ((c.Excursion.Mae ?? 0m) * qty).ToString(CultureInfo.InvariantCulture), ((c.Excursion.Mfe ?? 0m) * qty).ToString(CultureInfo.InvariantCulture),
            c.FinalExitReason, o.EntryBarSeq.ToString(CultureInfo.InvariantCulture), ""));
    }

    if (a.TryGetValue("audit-file", out var auditFile))
    {
        await using var aw = new StreamWriter(auditFile, false);
        foreach (var rec in engine.Outputs.OfType<AuditRecord>())
        {
            aw.WriteLine($"{rec.At.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss.ffffff}|{rec.Variant.StrategyId}|{rec.Stage}|{rec.Outcome}|{rec.Detail}");
        }

        foreach (var rj in engine.Outputs.OfType<EntryRejected>())
        {
            aw.WriteLine($"{rj.At.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss.ffffff}|{rj.Variant.StrategyId}|REJECTION|{rj.Stage}|{rj.Reasons} bar={rj.BarSeq}");
        }
    }

    foreach (var g in engine.Outputs.OfType<PositionClosed>().GroupBy(c => c.Variant.StrategyId).OrderBy(g => g.Key))
    {
        Console.WriteLine($"TRADES {g.Key}: {g.Count()} trades, {g.Count(c => c.NetPnl > 0)} wins, gross {g.Sum(c => c.GrossPnl)}, fees {g.Sum(c => c.TotalCosts)}, net {g.Sum(c => c.NetPnl)}");
    }

    Console.WriteLine($"unresolved at close: {engine.Outputs.OfType<UnresolvedAtClose>().Count()}; opened {engine.Outputs.OfType<PositionOpened>().Count()}");
}

Console.WriteLine($"replayed {rowsRead} rows in {sw.Elapsed.TotalSeconds:0.0}s -> {bars.Count} M1 bars; quarantined {producer.Counters.Quarantined}");
Console.WriteLine($"TIMING db read {readWatch.Elapsed.TotalSeconds:0.0}s | producer offer {offerWatch.Elapsed.TotalSeconds:0.0}s | producer release (groups, adaptive engine) {releaseWatch.Elapsed.TotalSeconds:0.0}s | trading engine (if --trade) {engineWatch.Elapsed.TotalSeconds:0.0}s | process CPU {(Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds:0.0}s | peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MiB");

var reference = ReadWide(wide);
Console.WriteLine($"reference bars: {reference.Count}");
Check(bars.Count == reference.Count, $"bar count {bars.Count} vs reference {reference.Count}");
if (bars.Count != reference.Count)
{
    foreach (var extra in bars.Skip(Math.Min(bars.Count, reference.Count)))
    {
        Console.WriteLine($"EXTRA bar {extra.Frame.BarSeq}: {extra.Frame.StartAvailableAt:O} .. {extra.Frame.EndAvailableAt:O} (reference last end {reference[^1]["end_utc"]})");
    }
}

var compared = 0;
var mismatches = 0;
for (var i = 0; i < Math.Min(bars.Count, reference.Count); i++)
{
    var f = bars[i].Frame;
    var r = reference[i];
    var errs = new List<string>();
    if (f.BarSeq != long.Parse(r["bar_seq"], CultureInfo.InvariantCulture)) errs.Add("bar_seq");
    if (f.StartAvailableAt != DateTimeOffset.Parse(r["start_utc"], CultureInfo.InvariantCulture).ToUniversalTime()) errs.Add($"start {f.StartAvailableAt:O} vs {r["start_utc"]}");
    if (f.EndAvailableAt != DateTimeOffset.Parse(r["end_utc"], CultureInfo.InvariantCulture).ToUniversalTime()) errs.Add($"end {f.EndAvailableAt:O} vs {r["end_utc"]}");
    if (f.Ready != bool.Parse(r["flag_ready"])) errs.Add($"ready {f.Ready} vs {r["flag_ready"]}");
    if (!Same(f.MicroDevTimeWeighted, r["fmicro_microdev_time_weighted"])) errs.Add($"m {f.MicroDevTimeWeighted} vs {r["fmicro_microdev_time_weighted"]}");
    if (!Same(f.MicroDevUsableSeconds, r["fmicro_microdev_usable_s"])) errs.Add($"w {f.MicroDevUsableSeconds} vs {r["fmicro_microdev_usable_s"]}");
    if (!SameLong(f.PeBandContractStrictDelta, r["band_pe_contract_strict_delta"])) errs.Add($"P {f.PeBandContractStrictDelta} vs {r["band_pe_contract_strict_delta"]}");
    if (!Same(f.BandCenterStrike, r["band_center_strike"])) errs.Add($"centre {f.BandCenterStrike} vs {r["band_center_strike"]}");
    compared++;
    if (errs.Count > 0)
    {
        mismatches++;
        if (mismatches <= 15)
        {
            Console.WriteLine($"MISMATCH bar {f.BarSeq}: {string.Join("; ", errs)}");
        }
    }
}

Check(mismatches == 0, $"{mismatches} of {compared} bars differ");
var withM = bars.Count(b => b.Frame.MicroDevTimeWeighted is not null);
var withP = bars.Count(b => b.Frame.PeBandContractStrictDelta is not null);
Console.WriteLine($"compared {compared} bars, {mismatches} mismatches; frames with m: {withM}, with P: {withP}, ready: {bars.Count(b => b.Frame.Ready)}");
Console.WriteLine(failures.Count == 0 ? $"PARITY PASS {day:yyyy-MM-dd}" : "PARITY FAIL " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": " + string.Join(" | ", failures));
return failures.Count == 0 ? 0 : 1;

static bool Same(double? value, string reference)
{
    if (string.IsNullOrEmpty(reference))
    {
        return value is null;
    }

    return value is { } v && Math.Abs(v - double.Parse(reference, CultureInfo.InvariantCulture)) <= 1e-9 * Math.Max(1d, Math.Abs(v));
}

static bool SameLong(long? value, string reference)
    => string.IsNullOrEmpty(reference) ? value is null : value is { } v && v == (long)double.Parse(reference, CultureInfo.InvariantCulture);

static Dictionary<string, string> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
    }

    return d;
}

static Dictionary<string, string> ReadState(string path)
    => File.ReadLines(path).Skip(1).Select(l => Split(l)).Where(c => c.Count >= 3).GroupBy(c => c[0]).ToDictionary(g => g.Key, g => g.First()[2]);

static List<string> Split(string line)
{
    var result = new List<string>();
    var sb = new System.Text.StringBuilder();
    var quoted = false;
    for (var i = 0; i < line.Length; i++)
    {
        var ch = line[i];
        if (ch == '"')
        {
            if (quoted && i + 1 < line.Length && line[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else
            {
                quoted = !quoted;
            }
        }
        else if (ch == ',' && !quoted)
        {
            result.Add(sb.ToString());
            sb.Clear();
        }
        else
        {
            sb.Append(ch);
        }
    }

    result.Add(sb.ToString());
    return result;
}

static List<Dictionary<string, string>> ReadWide(string path)
{
    var lines = File.ReadLines(path).GetEnumerator();
    lines.MoveNext();
    var header = Split(lines.Current);
    var rows = new List<Dictionary<string, string>>();
    while (lines.MoveNext())
    {
        var cells = Split(lines.Current);
        var d = new Dictionary<string, string>(header.Count);
        for (var i = 0; i < header.Count && i < cells.Count; i++)
        {
            d[header[i]] = cells[i];
        }

        rows.Add(d);
    }

    return rows;
}
