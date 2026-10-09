// CP5 recorded-session engineering validation (NOT a profitability experiment).
//
// Runs one recorded session through (A) the historical adapter and (B) the non-hosted live-paper adapter over a fake recorded poll source, using the
// SAME translator and trading engine, with plumbing strategies only, and compares them channel by channel. The tick database is only READ: the
// connection is opened with default_transaction_read_only=on, rows are streamed by keyset page (a session is never materialised), nothing is written.
//
// Environment (connection strings are never printed):
//   NSE_RECORDED_SOURCE   read-only tick database (the NiftySignal tick database copy)
//   NSE_RUN_DAY           trade date, yyyy-MM-dd (required)
//   NSE_RUN_MODE          both (default) | hist | live
//   NSE_OUT               output directory (default artifacts/trading-run-diff)
//   NSE_PAGE_SIZE         rows per database page (default 20000)
//   NSE_POLL_SCRIPT       mixed (default) | coarse | tiny | delayed | overlap
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Persistence;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var sourceCs = Env("NSE_RECORDED_SOURCE");
var dayText = Env("NSE_RUN_DAY");
if (sourceCs.Length == 0 || !DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
{
    Console.Error.WriteLine("Set NSE_RECORDED_SOURCE (read-only tick database) and NSE_RUN_DAY (yyyy-MM-dd).");
    return 2;
}

var mode = Env("NSE_RUN_MODE", "both");
var outDir = Env("NSE_OUT", Path.Combine("artifacts", "trading-run-diff"));
var pageSize = int.Parse(Env("NSE_PAGE_SIZE", "20000"), CultureInfo.InvariantCulture);
var scriptName = Env("NSE_POLL_SCRIPT", "mixed");
Directory.CreateDirectory(outDir);

var ist = TimeSpan.FromHours(5.5);
DateTimeOffset U(int h, int m, int s = 0) => new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m, s)), ist).ToUniversalTime();
var open = U(9, 15);
var cutoff = U(9, 30);
var sessionEnd = U(15, 35);

var csb = new NpgsqlConnectionStringBuilder(sourceCs) { Options = "-c default_transaction_read_only=on", CommandTimeout = 1800 };
var dbOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(csb.ConnectionString).Options;
var report = new StringBuilder();
void Say(string line = "")
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

// ------------------------------------------------------------------ instrument metadata of the trade date (effective-date)
List<Instrument> instruments;
await using (var db = new NiftySignalDbContext(dbOptions))
{
    instruments = await db.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY").ToListAsync();
}

var future = instruments.Where(i => i.InstrumentType == InstrumentType.Future).OrderBy(i => i.ExpiryDate).FirstOrDefault();
var optionRows = instruments.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice != null).OrderBy(i => i.ExpiryDate).ThenBy(i => i.StrikePrice).ToList();
if (future is null || optionRows.Count == 0)
{
    Console.Error.WriteLine($"No NIFTY future / options instrument snapshot for {day:yyyy-MM-dd}.");
    return 3;
}

var weekly = optionRows.Select(o => o.ExpiryDate!.Value).Distinct().OrderBy(x => x).First();
var nearest = optionRows.Where(o => o.ExpiryDate == weekly).ToList();
var descriptors = nearest
    .Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, i.ExpiryDate!.Value, i.LotSize))
    .ToList();
var tokens = descriptors.Select(d => d.Token).Append(future.Token).Distinct(StringComparer.Ordinal).ToArray();
var tickSizes = nearest.ToDictionary(i => i.Token, i => i.TickSize, StringComparer.Ordinal);

// ------------------------------------------------------------------ row stream (read-only, keyset paged by id)
long idHintLow;
long idHintHigh;
await using (var db = new NiftySignalDbContext(dbOptions))
{
    var lowTime = open.AddMinutes(-1);
    var highTime = U(23, 0);
    var first = await db.Ticks.AsNoTracking().Where(t => t.ReceivedAt >= lowTime).OrderBy(t => t.ReceivedAt).Select(t => (long?)t.Id).FirstOrDefaultAsync();
    var last = await db.Ticks.AsNoTracking().Where(t => t.ReceivedAt <= highTime).OrderByDescending(t => t.ReceivedAt).Select(t => (long?)t.Id).FirstOrDefaultAsync();
    if (first is null || last is null)
    {
        Console.Error.WriteLine("No ticks found around the session.");
        return 3;
    }

    // The id range is only a performance hint for the keyset scan; the row filters below are the same timestamp predicates the Host reader applies.
    idHintLow = Math.Max(0, first.Value - 200_000);
    idHintHigh = last.Value + 200_000;
}

IEnumerable<SourceTick> Stream(DateTimeOffset through)
{
    var queryStart = open.AddMinutes(-1);
    var after = idHintLow;
    while (true)
    {
        List<SourceTick> page;
        using (var db = new NiftySignalDbContext(dbOptions))
        {
            var rows = db.Ticks.AsNoTracking()
                .Where(t => t.Id > after && t.Id <= idHintHigh && tokens.Contains(t.Token)
                    && t.ExchangeTimestamp >= queryStart && t.ReceivedAt >= queryStart
                    && t.ExchangeTimestamp <= through && t.ReceivedAt <= through)
                .OrderBy(t => t.Id)
                .Take(pageSize)
                .Select(t => new
                {
                    t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Volume, t.OpenInterest,
                    Bid = t.Depth == null ? 0m : t.Depth.Bid1Price,
                    Ask = t.Depth == null ? 0m : t.Depth.Ask1Price,
                    BidQty = t.Depth == null ? 0L : t.Depth.Bid1Qty,
                    AskQty = t.Depth == null ? 0L : t.Depth.Ask1Qty,
                })
                .ToList();
            page = rows.Select(t => new SourceTick(t.Token, t.Id, t.ExchangeTimestamp, t.ReceivedAt, t.LastPrice, t.Bid, t.Ask, t.BidQty, t.AskQty, t.Volume, t.OpenInterest)).ToList();
        }

        foreach (var row in page)
        {
            yield return row;
        }

        if (page.Count < pageSize)
        {
            yield break;
        }

        after = page[^1].Id;
    }
}

// ------------------------------------------------------------------ session freeze, reconstructed exactly like AdaptiveSessionCoordinator (same pure helpers)
Say($"Recorded-session validation: trade date {day:yyyy-MM-dd}");
Say($"Future {future.Token} ({future.TradingSymbol}) lot {future.LotSize}; weekly expiry {weekly:yyyy-MM-dd}; option universe {descriptors.Count} contracts; tokens read {tokens.Length}");

var normalizer = new AdaptiveIncrementalTickNormalizer();
var preClean = new List<(string Token, CleanObserverTick Tick)>();
foreach (var row in Stream(cutoff.AddSeconds(5)))
{
    if (normalizer.Process(row.Token, row.ToRaw()) is { } tick && tick.AvailableAt >= open && tick.AvailableAt <= cutoff.AddSeconds(5))
    {
        preClean.Add((row.Token, tick));
    }
}

preClean.Sort((a, b) => a.Tick.AvailableAt != b.Tick.AvailableAt ? a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt) : a.Tick.Id.CompareTo(b.Tick.Id));
var futureTicks = preClean.Where(x => x.Token == future.Token).Select(x => x.Tick).ToList();
if (!futureTicks.Any(x => x.AvailableAt >= cutoff))
{
    Console.Error.WriteLine("The persisted futures stream does not cross 09:30 for this day.");
    return 3;
}

var enricher = new AdaptiveTradeFlowEnricher();
long openingVolume = 0;
foreach (var tick in futureTicks)
{
    var e = enricher.Process(tick);
    if (tick.AvailableAt < cutoff)
    {
        openingVolume += e.TradeVolume;
    }
}

var covered = AdaptiveOpeningCoverage.CoveredMinutes(futureTicks, open, cutoff);
if (covered != 15 || openingVolume <= 0)
{
    Console.Error.WriteLine($"Late-start / partial-opening session (covered minutes {covered}, opening volume {openingVolume}): the median-fallback freeze needs prior observer sessions. Pick another day.");
    return 3;
}

var baseVolume = OpeningVolumeProjectionV1.SelectBaseBarVolume(openingVolume, future.LotSize);
const double RiskFreeRate = 0.065;                                                                                 // Pricing:RiskFreeRate of the Host configuration
OptionResidualAnchor? anchor = null;
var future0930 = futureTicks.Where(x => x.AvailableAt <= cutoff).OrderByDescending(x => x.AvailableAt).ThenByDescending(x => x.Id).Cast<CleanObserverTick?>().FirstOrDefault();
if (future0930 is { } f0 && (cutoff - f0.AvailableAt).TotalSeconds <= 5d)
{
    var wanted = descriptors.Select(d => d.Token).ToHashSet(StringComparer.Ordinal);
    var latest = new Dictionary<string, OptionQuoteSnapshot>(StringComparer.Ordinal);
    foreach (var (token, t) in preClean)
    {
        if (wanted.Contains(token) && t.AvailableAt <= cutoff)
        {
            latest[token] = new OptionQuoteSnapshot(token, t.AvailableAt, t.Last, t.Bid, t.Ask, t.OpenInterest);
        }
    }

    anchor = OptionResidualModel.BuildAnchor(cutoff, weekly, f0.Last, descriptors, latest, RiskFreeRate, 5d);
}

var freeze = new SessionFreeze(
    new AdaptiveSessionDefinition(day, future.Token, future.TradingSymbol, future.ExpiryDate ?? day, future.LotSize, baseVolume),
    weekly,
    RiskFreeRate,
    null,
    cutoff,
    open,
    descriptors,
    anchor,
    tickSizes,
    OpeningVolumeProjectionV1.ModelVersion + "+harness-reconstructed-freeze");
Say($"Frozen session: opening volume {openingVolume}, base bar volume {baseVolume}, signal start {cutoff:O}, residual anchor {(anchor is null ? "UNAVAILABLE" : $"center {anchor.CenterStrike}, {anchor.Calls.Count} CE + {anchor.Puts.Count} PE components")}, strong threshold unavailable (labels only; bars and readiness do not use it)");

// ------------------------------------------------------------------ configuration (confirmed contract + labelled harness inputs; plumbing strategies only)
var variants = new[]
{
    new VariantSpec { VariantId = "A", StrategyId = "P6", EntryCondition = new ImmediateEntrySpec() },
    new VariantSpec
    {
        VariantId = "B",
        StrategyId = "P4",
        EntryCondition = new AskBelowCandleMaSpec { N = 3, PriceSource = CandlePriceSource.AskClose },
        Exit = new ExitSpec { Tp1Pct = 0.05m, Tp1Proportion = 0.5m, TradeSlPct = 0.10m },
    },
};
var config = HarnessSettings.Config("recorded-session-validation", variants);
IReadOnlyDictionary<string, IBaseStrategy> Strategies() => new Dictionary<string, IBaseStrategy>
{
    ["P6"] = new PlumbingEveryNthBarStrategy("P6", 6, 3),
    ["P4"] = new PlumbingEveryNthBarStrategy("P4", 4, 2),
};

var translatorOptions = new TranslatorOptions { AcceptFromUtc = open, AcceptThroughUtc = sessionEnd };
IReadOnlyList<PollStep> Steps() => scriptName switch
{
    "coarse" => new[] { new PollStep(TimeSpan.FromMilliseconds(250), 1_000_000) },
    "tiny" => new[] { new PollStep(TimeSpan.FromMilliseconds(100), 40) },
    "delayed" => new[] { new PollStep(TimeSpan.FromSeconds(45), 1_000_000) },
    "overlap" => new[] { new PollStep(TimeSpan.FromMilliseconds(300), 1_000_000, Overlap: 5) },
    _ => new[]
    {
        new PollStep(TimeSpan.FromMilliseconds(100), 500),
        new PollStep(TimeSpan.FromSeconds(1), 50),
        new PollStep(TimeSpan.FromSeconds(3), 1_000_000),
        new PollStep(TimeSpan.FromMilliseconds(250), 20),
        new PollStep(TimeSpan.FromSeconds(5), 1_000_000),
    },
};

Say();
Say("Configuration (confirmed contract + labelled harness inputs):");
Say($"  config version  : {config.ConfigVersion}");
Say($"  config hash     : {config.ComputeHash().Value}");
foreach (var p in HarnessSettings.Provenance)
{
    Say("  - " + p);
}

Say($"  variants        : A = Immediate on plumbing P6 (3 valid bars of 6); B = AskBelowCandleMA(N=3, AskClose) on plumbing P4 (2 of 4), TP1 5% x 50%, per-trade SL 10%");
Say($"  risk profile    : {config.Risk.Name}/{config.Risk.Version} (no trade cap, daily SL observation only)");
Say($"  translator      : StableOrderingLag {translatorOptions.StableOrderingLag.TotalSeconds}s, accept [{open:O}, {sessionEnd:O}], seen-id retention {translatorOptions.SeenIdRetention}");
Say($"  retention       : loop {RetentionPolicyText()}, db page {pageSize} rows, poll script '{scriptName}'");
Say($"  horizon         : session end {sessionEnd:O}");
Say("  PLUMBING STRATEGIES ONLY: nothing here is a profitability claim.");

string RetentionPolicyText() => $"{NiftySignal.Trading.Core.Trading.RetentionPolicy.Default.LoopSeenRetention} / quote history MaxQuoteAge+1m";

TradingRun NewRun(RecorderOptions rec, string runMode)
    => new(freeze, translatorOptions, config, Strategies(), new RunSettings { RunMode = runMode, RunId = "cp5-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + runMode }, rec);

(RunResult Result, TimeSpan Elapsed) Timed(Func<RunResult> run)
{
    var sw = Stopwatch.StartNew();
    var r = run();
    return (r, sw.Elapsed);
}

var times = new Dictionary<string, TimeSpan>();
RunResult Historical(RecorderOptions rec)
{
    var (r, t) = Timed(() => HistoricalAdapter.Run(NewRun(rec, "Historical"), Stream(sessionEnd), new HistoricalOptions { PageSize = 5_000 }));
    times["historical"] = t;
    return r;
}

RunResult Live(RecorderOptions rec)
{
    var (r, t) = Timed(() =>
    {
        using var source = RecordedPollSource.Cycle(Stream(sessionEnd), Steps(), open.AddSeconds(-1));
        return LivePaperAdapter.Run(NewRun(rec, "LivePaper"), source).Run;
    });
    times["livepaper"] = t;
    return r;
}

var exit = 0;
var rec0 = new RecorderOptions { CheckpointEvery = 5_000 };
Say();
if (mode == "hist" || mode == "live")
{
    var r = mode == "hist" ? Historical(rec0) : Live(rec0);
    PrintRun(mode, r);
}
else
{
    var diff = RunDiff.Compare(Historical, Live, new DiffOptions { CheckpointEvery = 5_000 });
    Say(RunDiff.Render(diff));
    Say();
    PrintRun("historical (A)", diff.A);
    PrintRun("live-paper (B)", diff.B);
    exit = diff.Equivalent ? 0 : 1;
}

Say();
foreach (var (name, elapsed) in times.OrderBy(x => x.Key, StringComparer.Ordinal))
{
    Say($"runtime {name,-10}: {elapsed.TotalSeconds:F1} s (last run of that path)");
}

using (var self = Process.GetCurrentProcess())
{
    Say($"process peak working set: {self.PeakWorkingSet64 / (1024d * 1024d):F0} MiB; peak virtual {self.PeakVirtualMemorySize64 / (1024d * 1024d):F0} MiB (whole process, all runs of this invocation)");
}

File.WriteAllText(Path.Combine(outDir, $"trading-run-diff-{day:yyyy-MM-dd}-{mode}-{scriptName}.txt"), report.ToString());
return exit;

void PrintRun(string name, RunResult r)
{
    var c = r.Translator;
    Say($"[{name}]");
    Say($"  rows offered {c.Input}: translated {c.Translated}, per-token payload duplicates {c.PayloadDuplicates}, outside window {c.OutsideWindow}, re-deliveries {c.Redelivered}, quarantined {c.Quarantined} {string.Join(", ", c.QuarantinedByReason.Select(q => q.Key + "=" + q.Value))}; balanced={c.Balanced}");
    Say($"  availability: clamped to exchange stamp {c.ClampedAvailability}, exchange==received {c.ExchangeEqualsReceived}");
    Say($"  groups {c.GroupsReleased}, bars {c.BarsReleased}, max buffered rows {c.MaxBufferedRows}, remembered ids at end {c.SeenIdCount}");
    Say($"  engine: events delivered {r.Loop.EventsDelivered}, duplicates ignored {r.Loop.DuplicatesIgnored}, deadlines fired {r.Loop.DeadlinesFired}, engine quarantines {r.EngineQuarantines}, loop memory at end {r.SeenInLoop} events");
    Say($"  outputs {r.Outputs}, fills {r.Fills}, positions {r.Positions}, UNRESOLVED positions {r.UnresolvedPositions}");
    Say($"  transport (excluded from equivalence): pages {r.Transport.Pages}, polls {r.Transport.Polls}, failed polls {r.Transport.FailedPolls}, heartbeats {r.Transport.Heartbeats}, max batch {r.Transport.MaxBatchRows}, groups after horizon {r.Transport.GroupsAfterHorizon}");
    foreach (var (channel, digest) in r.Channels.OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        Say($"  channel {channel,-14} lines {digest.Count,9}  sha256 {digest.Hash[..16]}");
    }
}
