// Adaptive live-performance validation harness (08-Oct plan, "Live performance contract").
//
// Drives the REAL production live path (AdaptiveObserverWorker.PollLiveAsync / TryStartOrRecoverAsync / CloseCurrentSessionAsync and
// AdaptiveStateRecoveryService) against an ISOLATED writable PostgreSQL cluster. The historical source database is only ever READ (the
// connection is opened with default_transaction_read_only=on and the isolated cluster must be a different host:port). Nothing is sent to
// Telegram (commentary notifications are disabled in this harness), the Dashboard push client is never connected, and nothing is deployed.
//
// Environment (connection strings are never printed):
//   ADAPTIVE_PERF_SOURCE       read-only historical source (the NiftySignal tick database copy)
//   ADAPTIVE_PERF_ADMIN        isolated writable PostgreSQL (any database on it; perf_* databases are created/dropped)
//   ADAPTIVE_PERF_OUT          output directory (default artifacts/adaptive-live-performance)
//   ADAPTIVE_PERF_LABEL        run label used in file names (default "run")
//   ADAPTIVE_PERF_DAYS         "all" (default) or comma list yyyy-MM-dd that get INPUT statistics
//   ADAPTIVE_PERF_LIVE_DAYS    comma list yyyy-MM-dd for live simulation + warm recovery ("auto" picks max-rate / median / lightest)
//   ADAPTIVE_PERF_COLD_DAYS    comma list for cold-recovery-only sessions (default none)
//   ADAPTIVE_PERF_STRESS       "1" (default) to run the 2x-peak sustained stress on the busiest session, "0" to skip
//   ADAPTIVE_PERF_STAGES       comma list of: inputs,live,recovery,dashboard,plans,stress,digest (default all)
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
#if !BASELINE
using NiftySignal.Notifications;
#endif
using NiftySignal.Persistence;

static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var sourceCs = Env("ADAPTIVE_PERF_SOURCE");
var adminCs = Env("ADAPTIVE_PERF_ADMIN");
if (sourceCs.Length == 0 || adminCs.Length == 0)
{
    Console.Error.WriteLine("Set ADAPTIVE_PERF_SOURCE (read-only historical source) and ADAPTIVE_PERF_ADMIN (isolated writable PostgreSQL).");
    return 2;
}

var outDir = Env("ADAPTIVE_PERF_OUT", Path.Combine("artifacts", "adaptive-live-performance"));
var label = Env("ADAPTIVE_PERF_LABEL", "run");
var stages = Env("ADAPTIVE_PERF_STAGES", "inputs,live,recovery,dashboard,plans,stress,digest").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
Directory.CreateDirectory(outDir);

var ist = TimeSpan.FromHours(5.5);
var sourceBuilder = new NpgsqlConnectionStringBuilder(sourceCs) { Options = "-c default_transaction_read_only=on", CommandTimeout = 900 };
var adminBuilder = new NpgsqlConnectionStringBuilder(adminCs) { CommandTimeout = 900 };
if (string.Equals(sourceBuilder.Host, adminBuilder.Host, StringComparison.OrdinalIgnoreCase) && sourceBuilder.Port == adminBuilder.Port)
{
    Console.Error.WriteLine("Refusing to run: ADAPTIVE_PERF_ADMIN must be a different PostgreSQL host:port than the read-only source.");
    return 2;
}

string DbCs(string database) => new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString) { Database = database }.ConnectionString;

async Task RecreateDatabaseAsync(string name)
{
    await using var admin = new NpgsqlConnection(DbCs("postgres"));
    await admin.OpenAsync();
    await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin)) await drop.ExecuteNonQueryAsync();
    await using (var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync();
}

DateTimeOffset U(DateOnly day, int h, int m, int s = 0) => new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m, s)), ist).ToUniversalTime();

// ---------------------------------------------------------------- source discovery (read-only)
var srcOptions = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(sourceBuilder.ConnectionString).Options;
await using var src = new NiftySignalDbContext(srcOptions);
var allDays = await src.Instruments.AsNoTracking().Where(x => x.Underlying == "NIFTY" && x.InstrumentType == InstrumentType.Future)
    .Select(x => x.AsOfDate).Distinct().OrderBy(x => x).ToListAsync();
var reader = new AdaptiveSourceTickReader();

List<DateOnly> ParseDays(string spec) => spec.Length == 0 || spec == "x" ? [] : spec == "all" ? allDays : spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(DateOnly.Parse).ToList();
var inputDays = ParseDays(Env("ADAPTIVE_PERF_DAYS", "all"));

async Task<DayData?> LoadDayAsync(DateOnly day)
{
    var fut = await src.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future).OrderBy(i => i.ExpiryDate).FirstOrDefaultAsync();
    var optRows = await src.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice != null).ToListAsync();
    if (fut is null || optRows.Count == 0) return null;
    var weekly = optRows.Select(x => x.ExpiryDate!.Value).Min();
    var chainRows = optRows.Where(x => x.ExpiryDate == weekly).OrderBy(x => x.StrikePrice).ToList();
    var chain = chainRows.Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, i.ExpiryDate!.Value, i.LotSize)).ToList();
    var tokens = chain.Select(x => x.Token).Append(fut.Token).Distinct().ToArray();
    var raw = await reader.ReadRawSessionAsync(src, day, tokens, U(day, 15, 35), default, includeBeyondThrough: true);
    return new DayData(day, fut, chainRows, weekly, chain, tokens, raw);
}

// ---------------------------------------------------------------- input characteristics
Dictionary<string, object> InputStats(DayData d)
{
    var open = U(d.Day, 9, 15); var close = U(d.Day, 15, 30);
    var normalizer = new AdaptiveIncrementalTickNormalizer();
    var clean = new List<(string Token, CleanObserverTick Tick)>(d.Raw.Count);
    foreach (var (token, tick) in d.Raw)
        if (normalizer.Process(token, tick) is { } c && c.AvailableAt >= open && c.AvailableAt <= close) clean.Add((token, c));

    var seconds = (int)(close - open).TotalSeconds + 1;
    var perSecond = new int[seconds];
    var availGroups = new Dictionary<long, int>();
    foreach (var (_, tick) in clean)
    {
        var idx = (int)(tick.AvailableAt - open).TotalSeconds;
        if (idx >= 0 && idx < seconds) perSecond[idx]++;
        availGroups[tick.AvailableAt.UtcTicks] = availGroups.GetValueOrDefault(tick.AvailableAt.UtcTicks) + 1;
    }

    // 250 ms buckets by ReceivedAt (finer than the second-granularity exchange timestamps).
    var buckets = new Dictionary<long, int>();
    foreach (var (_, t) in d.Raw)
    {
        if (t.ReceivedAt < open || t.ReceivedAt > close) continue;
        var b = (t.ReceivedAt - open).Ticks / (TimeSpan.TicksPerMillisecond * 250);
        buckets[b] = buckets.GetValueOrDefault(b) + 1;
    }

    var sortedSec = perSecond.Select(x => (double)x).OrderBy(x => x).ToList();
    var session = (close - open).TotalSeconds;
    return new()
    {
        ["day"] = d.Day.ToString("yyyy-MM-dd"), ["rawRows"] = d.Raw.Count, ["cleanTicks"] = clean.Count, ["tokens"] = d.Tokens.Length,
        ["ticksPerSecMean"] = Math.Round(clean.Count / session, 2), ["ticksPerSecP50"] = Stats.Percentile(sortedSec, 50), ["ticksPerSecP90"] = Stats.Percentile(sortedSec, 90),
        ["ticksPerSecP95"] = Stats.Percentile(sortedSec, 95), ["ticksPerSecP99"] = Stats.Percentile(sortedSec, 99), ["maxTicksIn1sAvailableAt"] = perSecond.Max(),
        ["maxTicksIn250msReceivedAt"] = buckets.Count == 0 ? 0 : buckets.Values.Max(), ["maxAvailabilityGroupTicks"] = availGroups.Count == 0 ? 0 : availGroups.Values.Max(),
        ["maxRawRowsInOneSecondReceivedAt"] = d.Raw.GroupBy(x => (long)(x.Tick.ReceivedAt - open).TotalSeconds).Select(g => g.Count()).DefaultIfEmpty(0).Max(),
        ["_perSecond"] = perSecond,
    };
}

var report = new Dictionary<string, object> { ["label"] = label, ["generatedUtc"] = DateTimeOffset.UtcNow, ["commit"] = Env("ADAPTIVE_PERF_COMMIT", "unknown"),
    ["machine"] = new { Environment.ProcessorCount, Environment.OSVersion, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, ServerGC = System.Runtime.GCSettings.IsServerGC } };
var inputRows = new List<Dictionary<string, object>>();
var dayCache = new Dictionary<DateOnly, DayData>();
var perSecondByDay = new Dictionary<DateOnly, int[]>();

if (stages.Contains("inputs"))
{
    foreach (var day in inputDays)
    {
        var d = await LoadDayAsync(day);
        if (d is null) continue;
        var stats = InputStats(d);
        perSecondByDay[day] = (int[])stats["_perSecond"]; stats.Remove("_perSecond");
        inputRows.Add(stats);
        Console.WriteLine($"INPUT {day:yyyy-MM-dd} clean={stats["cleanTicks"]} mean/s={stats["ticksPerSecMean"]} p99/s={stats["ticksPerSecP99"]} max1s={stats["maxTicksIn1sAvailableAt"]} max250ms={stats["maxTicksIn250msReceivedAt"]} maxGroup={stats["maxAvailabilityGroupTicks"]}");
    }

    report["inputs"] = inputRows;
    if (inputRows.Count > 0)
    {
        report["inputsAggregate"] = new
        {
            sessions = inputRows.Count,
            maxTicksIn1sAcrossSessions = inputRows.Max(r => Convert.ToInt32(r["maxTicksIn1sAvailableAt"])),
            maxTicksIn250msAcrossSessions = inputRows.Max(r => Convert.ToInt32(r["maxTicksIn250msReceivedAt"])),
            maxAvailabilityGroupAcrossSessions = inputRows.Max(r => Convert.ToInt32(r["maxAvailabilityGroupTicks"])),
            meanTicksPerSecAcrossSessions = Math.Round(inputRows.Average(r => Convert.ToDouble(r["ticksPerSecMean"])), 2),
        };
    }
}

// ---------------------------------------------------------------- selection of simulated days
var liveDays = Env("ADAPTIVE_PERF_LIVE_DAYS", "auto") == "auto"
    ? PickAuto()
    : ParseDays(Env("ADAPTIVE_PERF_LIVE_DAYS"));
var coldDays = ParseDays(Env("ADAPTIVE_PERF_COLD_DAYS"));
List<DateOnly> PickAuto()
{
    if (inputRows.Count == 0) return allDays.Take(1).ToList();
    var ranked = inputRows.Where(r => !ParseDays(Env("ADAPTIVE_PERF_EXCLUDE_DAYS", "x")).Contains(DateOnly.Parse((string)r["day"]))).OrderByDescending(r => Convert.ToInt32(r["maxTicksIn1sAvailableAt"])).ToList();
    var picks = new[] { ranked[0], ranked[ranked.Count / 2], ranked[^1] };
    return picks.Select(r => DateOnly.Parse((string)r["day"])).Distinct().ToList();
}

// Days whose opening coverage is below 15 minutes need prior sessions for the median fallback (the first sessions of the dataset): exclude them from live simulation.
var excluded = ParseDays(Env("ADAPTIVE_PERF_EXCLUDE_DAYS", "x")).ToHashSet();
DateOnly? busiestDay = inputRows.Count == 0 ? null : DateOnly.Parse((string)inputRows.Where(r => !excluded.Contains(DateOnly.Parse((string)r["day"]))).OrderByDescending(r => Convert.ToInt32(r["maxTicksIn1sAvailableAt"])).First()["day"]);

// ---------------------------------------------------------------- isolated databases + DI
const string SourceDb = "perf_source";
var sourceCounter = new SqlCounter();
var observerCounter = new SqlCounter();
var recorder = new StageRecorder(observerCounter);
string currentObserverDb = "perf_observer";

#if !BASELINE
var gateOptions = new AdaptiveCommentaryTelegramOptions { Enabled = false };    // no Telegram, ever, from this harness
#endif
Func<string, DbContextOptions<AdaptiveObserverDbContext>> observerOptions = db => new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
    .UseNpgsql(DbCs(db)).AddInterceptors(new CountingInterceptor(observerCounter)).Options;

async Task EnsureSourceSchemaAsync()
{
    await RecreateDatabaseAsync(SourceDb);
    await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(DbCs(SourceDb)).Options);
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ticks_all (LIKE ticks)");
}

async Task<Staged> StageAsync(DayData d)
{
    await using (var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(DbCs(SourceDb)).Options))
    {
        await db.Database.ExecuteSqlRawAsync("TRUNCATE ticks, instruments, ticks_all RESTART IDENTITY");
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var i in new[] { d.Future }.Concat(d.Options))
            db.Instruments.Add(new Instrument { Token = i.Token, Exchange = i.Exchange, TradingSymbol = i.TradingSymbol, InstrumentType = i.InstrumentType, OptionType = i.OptionType,
                StrikePrice = i.StrikePrice, ExpiryDate = i.ExpiryDate, Underlying = i.Underlying, LotSize = i.LotSize, TickSize = i.TickSize, AsOfDate = i.AsOfDate });
        await db.SaveChangesAsync();
    }

    const int chunk = 20_000;
    for (var offset = 0; offset < d.Raw.Count; offset += chunk)
    {
        await using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(DbCs(SourceDb)).Options);
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var (token, t) in d.Raw.Skip(offset).Take(chunk))
        {
            var depth = t.Bid > 0 || t.Ask > 0 || t.BidQty > 0 || t.AskQty > 0
                ? new MarketDepth((decimal)t.Bid, t.BidQty, 0, 0, 0, 0, 0, 0, 0, 0, (decimal)t.Ask, t.AskQty, 0, 0, 0, 0, 0, 0, 0, 0) : null;
            db.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = t.ExchangeTimestamp, ReceivedAt = t.ReceivedAt, LastPrice = (decimal)t.Last, Volume = t.Volume, OpenInterest = t.OpenInterest, Depth = depth });
        }

        await db.SaveChangesAsync();
    }

    await using var finish = new NpgsqlConnection(DbCs(SourceDb));
    await finish.OpenAsync();
    foreach (var sql in new[] { "INSERT INTO ticks_all SELECT * FROM ticks", "TRUNCATE ticks", "CREATE INDEX IF NOT EXISTS ix_ticks_all_id ON ticks_all (\"Id\")" })
        await using (var cmd = new NpgsqlCommand(sql, finish)) await cmd.ExecuteNonQueryAsync();
    var recv = new List<DateTimeOffset>(d.Raw.Count);
    await using (var cmd = new NpgsqlCommand("SELECT \"ReceivedAt\" FROM ticks_all ORDER BY \"Id\"", finish))
    await using (var rd = await cmd.ExecuteReaderAsync())
        while (await rd.ReadAsync()) recv.Add(rd.GetFieldValue<DateTimeOffset>(0));
    if (recv.Count != d.Raw.Count) throw new InvalidOperationException("Staged tick count differs from the source read.");
    return new Staged(d, recv.ToArray());
}

AdaptiveObserverWorker NewWorker(string observerDb)
{
    // The worker resolves its contexts from scopes; give it a provider whose observer context points at the requested database.
    var s = new ServiceCollection();
    s.AddLogging(b => b.SetMinimumLevel(LogLevel.Error).AddSimpleConsole());
    s.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(DbCs(SourceDb)).AddInterceptors(new CountingInterceptor(sourceCounter)));
    s.AddDbContext<AdaptiveObserverDbContext>(o => o.UseNpgsql(DbCs(observerDb)).AddInterceptors(new CountingInterceptor(observerCounter)));
    s.AddOptions<PricingOptions>();
    s.Configure<DashboardPushOptions>(o => o.HubUrl = "http://127.0.0.1:1/hubs/never-connected");
#if !BASELINE
    s.AddSingleton<IOptionsMonitor<AdaptiveCommentaryTelegramOptions>>(new StaticMonitor<AdaptiveCommentaryTelegramOptions>(gateOptions));
    s.AddSingleton<IOptionsMonitor<TelegramOptions>>(new StaticMonitor<TelegramOptions>(new TelegramOptions { BotToken = "", ChatId = "" }));
    s.AddSingleton<AdaptiveCommentaryNotificationGate>();
#endif
    s.AddSingleton<AdaptiveSourceTickReader>(); s.AddSingleton<AdaptiveHistoricalBootstrapService>(); s.AddSingleton<AdaptiveSessionCoordinator>();
    s.AddSingleton<AdaptiveObserverPersistence>(); s.AddSingleton<AdaptiveSupplementalPersistence>(); s.AddSingleton<AdaptiveCommentaryFrameLoader>();
    s.AddSingleton<AdaptiveCommentaryService>(); s.AddSingleton<AdaptiveWeak2ObservationService>();
    s.AddSingleton<AdaptiveStateRecoveryService>(); s.AddSingleton<AdaptiveEndedSessionRecoveryService>(); s.AddSingleton<DashboardPushClient>();
    s.AddSingleton<AdaptiveObserverWorker>();
    return s.BuildServiceProvider().GetRequiredService<AdaptiveObserverWorker>();
}

async Task ClearTicksAsync()
{
    await using var c = new NpgsqlConnection(DbCs(SourceDb)); await c.OpenAsync();
    await using var cmd = new NpgsqlCommand("TRUNCATE ticks", c); await cmd.ExecuteNonQueryAsync();
}

/// <summary>Makes every staged tick (optionally only those received by <paramref name="upTo"/>) visible in the live ticks table, as a restart would find them.</summary>
async Task LoadTicksAsync(DateTimeOffset? upTo)
{
    await using var c = new NpgsqlConnection(DbCs(SourceDb)); await c.OpenAsync();
    await using var cmd = new NpgsqlCommand(upTo is null ? "TRUNCATE ticks; INSERT INTO ticks SELECT * FROM ticks_all" : "TRUNCATE ticks; INSERT INTO ticks SELECT * FROM ticks_all WHERE \"ReceivedAt\" <= @h", c);
    if (upTo is { } h) cmd.Parameters.AddWithValue("h", h);
    await cmd.ExecuteNonQueryAsync();
}

async Task<string> FreshObserverAsync(string name)
{
    await RecreateDatabaseAsync(name);
    await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(DbCs(name)).Options);
    await db.Database.MigrateAsync();
    return name;
}

// ---------------------------------------------------------------- digests (value equivalence)
async Task<Dictionary<string, object>> DigestAsync(string observerDb)
{
    await using var db = new AdaptiveObserverDbContext(observerOptions(observerDb));
    static string Hash(IEnumerable<object> rows, params string[] ignore)
    {
        var skip = ignore.Concat(["Id"]).ToHashSet();
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            foreach (var p in row.GetType().GetProperties().Where(p => !skip.Contains(p.Name) && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)
                || p.PropertyType == typeof(decimal) || p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateOnly) || Nullable.GetUnderlyingType(p.PropertyType) is not null)))
            {
                var v = p.GetValue(row);
                sb.Append(p.Name).Append('=').Append(v is double dv ? dv.ToString("R") : v?.ToString() ?? "null").Append(';');
            }

            sb.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    var d = new Dictionary<string, object>();
    var bars = await db.FutureBars.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync(); d["futureBars"] = new { n = bars.Count, hash = Hash(bars, "SessionId") };
    var roll = await db.RollingStates.AsNoTracking().OrderBy(x => x.EndBarSeq).ToListAsync(); d["rollingStates"] = new { n = roll.Count, hash = Hash(roll, "SessionId") };
    var bands = await db.OptionBandBars.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.Side).ToListAsync(); d["optionBands"] = new { n = bands.Count, hash = Hash(bands, "SessionId") };
    var resid = await db.OptionResidualBars.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.Variant).ToListAsync(); d["residuals"] = new { n = resid.Count, hash = Hash(resid, "SessionId") };
    var fs = await db.FuturesSupplemental.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync(); d["futuresSupplemental"] = new { n = fs.Count, hash = Hash(fs, "SessionId") };
    var os = await db.OptionsSupplemental.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync(); d["optionsSupplemental"] = new { n = os.Count, hash = Hash(os, "SessionId") };
    var ev = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).ThenBy(x => x.EventIdentity).ToListAsync();
    d["commentaryEvents"] = new { n = ev.Count, hash = Hash(ev, "SessionId", "PreviousEventId", "CreatedAtUtc") };
    var wk = await db.Weak2Observations.AsNoTracking().OrderBy(x => x.TriggerBarSeq).ToListAsync(); d["weak2Observations"] = new { n = wk.Count, hash = Hash(wk, "SessionId") };
    d["projectionHealthRows"] = await db.ProjectionHealth.CountAsync();
    return d;
}

// ---------------------------------------------------------------- memory sampling
static double Mb(long bytes) => Math.Round(bytes / 1048576d, 1);

// ---------------------------------------------------------------- recovery benchmark
async Task<Dictionary<string, object>> RecoveryAsync(DateOnly day, string observerDb, string kind, DateTimeOffset nowUtc)
{
    recorder.Reset();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var heapBefore = GC.GetTotalMemory(true);
    var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
    var allocBefore = GC.GetTotalAllocatedBytes(false);
    var sqlBefore = observerCounter.Snapshot(); var srcBefore = sourceCounter.Snapshot();
    var worker = NewWorker(observerDb);
    worker.MarkHistoricalBootstrapDone(day);
    using var mem = new MemorySampler();
    var sw = Stopwatch.StartNew();
    var live = await worker.TryStartOrRecoverAsync(day, nowUtc, default) ?? throw new InvalidOperationException("Recovery did not start a session.");
    sw.Stop();
    var optionBookStates = OptionBookStates(live);
    var stageMs = recorder.DurationsMs.Where(k => k.Key.StartsWith("recovery.", StringComparison.Ordinal)).ToDictionary(k => k.Key, k => Math.Round(k.Value.Sum(), 1));
    var sqlAfter = observerCounter.Snapshot(); var srcAfter = sourceCounter.Snapshot();
    var result = new Dictionary<string, object>
    {
        ["day"] = day.ToString("yyyy-MM-dd"), ["kind"] = kind, ["totalWallMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1), ["stageMsSummed"] = stageMs,
        ["rawRows"] = live.Pending.Count, ["workingSetPeakMb"] = Mb(mem.PeakWorkingSet), ["managedHeapBeforeMb"] = Mb(heapBefore), ["managedHeapPeakMb"] = Mb(mem.PeakManagedHeap),
        ["managedHeapAfterMb"] = Mb(GC.GetTotalMemory(false)), ["allocatedMb"] = Mb(GC.GetTotalAllocatedBytes(false) - allocBefore),
        ["gcGen0"] = GC.CollectionCount(0) - g0, ["gcGen1"] = GC.CollectionCount(1) - g1, ["gcGen2"] = GC.CollectionCount(2) - g2,
        ["observerSqlCommands"] = sqlAfter.Total - sqlBefore.Total, ["observerSqlWrites"] = sqlAfter.Writes - sqlBefore.Writes, ["sourceSqlCommands"] = srcAfter.Total - srcBefore.Total,
        ["retainedOptionBookStates"] = optionBookStates.States, ["optionBookTokens"] = optionBookStates.Tokens, ["completedBars"] = live.Engine.FutureBars.Count(x => x.IsComplete),
    };
    return result;
}

static (long States, int Tokens) OptionBookStates(object live)
{
    var engine = live.GetType().GetProperty("Engine")!.GetValue(live)!;
    var field = typeof(AdaptiveObserverEngine).GetField("_optionBooks", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var books = (System.Collections.IDictionary)field.GetValue(engine)!;
    long states = 0; foreach (System.Collections.DictionaryEntry kv in books) states += ((FuturesMicrostructureTracker)kv.Value!).UniqueStateCount;
    return (states, books.Count);
}

// ---------------------------------------------------------------- live simulation
async Task<SimResult> SimulateAsync(DayData d, Staged staged, string observerDb, double compression, DateTimeOffset dataStart, DateTimeOffset? ingestCap, DateTimeOffset dataEnd, string name,
    bool closeSession)
{
    recorder.Reset();
    var feeder = new Feeder(staged, DbCs(SourceDb));
    var worker = NewWorker(observerDb);
    worker.MarkHistoricalBootstrapDone(d.Day);
    await feeder.FeedAsync(dataStart, ingestCap);
    var startSw = Stopwatch.StartNew();
    var live = await worker.TryStartOrRecoverAsync(d.Day, dataStart, default) ?? throw new InvalidOperationException("The adaptive session did not start (not enough data at the start time).");
    startSw.Stop();

    var holdbackShift = TimeSpan.FromSeconds(2 * (compression - 1));
    var cycleMs = new List<double>(); var cycleTicks = new List<int>(); var cycleKind = new List<byte>();   // 0 idle, 1 ticks, 2 bar
    var cycleObserverSql = new List<int>(); var cycleObserverWrites = new List<int>(); var cycleSourceSql = new List<int>();
    var maxPending = 0; var maxPendingCycleTicks = 0; var pendingAtEnd = 0;
    var dataNow = dataStart; var realElapsed = TimeSpan.Zero;
    var allocStart = GC.GetTotalAllocatedBytes(false); var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
    using var mem = new MemorySampler();
    var sqlStart = observerCounter.Snapshot(); var srcStart = sourceCounter.Snapshot();
    var wsSamples = new List<(double Hours, double Mb)>();
    using var proc = Process.GetCurrentProcess();
    var cycles = 0; var drainedAt = (TimeSpan?)null; var ingestEndReal = (TimeSpan?)null; var lastPollNow = dataStart;
    var ingestEnd = ingestCap ?? dataEnd;

    while (dataNow < dataEnd)
    {
        var inserted = await feeder.FeedAsync(dataNow, ingestCap);
        if (ingestEndReal is null && dataNow >= ingestEnd) ingestEndReal = realElapsed;
        var before = observerCounter.Snapshot(); var srcBeforeCycle = sourceCounter.Snapshot();
        var barsBefore = live.Engine.FutureBars.Count();
        var sw = Stopwatch.StartNew();
        lastPollNow = dataNow - holdbackShift;
        await worker.PollLiveAsync(live, lastPollNow, default);
        sw.Stop();
        var after = observerCounter.Snapshot(); var srcAfterCycle = sourceCounter.Snapshot();
        var ms = sw.Elapsed.TotalMilliseconds;
        cycleMs.Add(ms); cycleTicks.Add(inserted); cycleObserverSql.Add((int)(after.Total - before.Total)); cycleObserverWrites.Add((int)(after.Writes - before.Writes));
        cycleSourceSql.Add((int)(srcAfterCycle.Total - srcBeforeCycle.Total));
        cycleKind.Add(live.Engine.FutureBars.Count() != barsBefore ? (byte)2 : inserted > 0 ? (byte)1 : (byte)0);
        maxPending = Math.Max(maxPending, live.Pending.Count);
        if (inserted > maxPendingCycleTicks) maxPendingCycleTicks = inserted;
        var step = TimeSpan.FromMilliseconds(ms + 250);
        realElapsed += step;
        dataNow += step * compression;
        cycles++;
        if (ingestCap is { } cap2 && dataNow >= ingestEnd && drainedAt is null && live.Pending.Count == 0 && feeder.IngestComplete(cap2))
            drainedAt = realElapsed - (ingestEndReal ?? realElapsed);
        if (cycles % 4000 == 0) { proc.Refresh(); wsSamples.Add((realElapsed.TotalHours, Mb(proc.WorkingSet64))); }
    }

    pendingAtEnd = live.Pending.Count;
    var barCount = cycleKind.Count(x => x == 2);
    if (closeSession) await worker.CloseCurrentSessionAsync(live, default);
    var sqlEnd = observerCounter.Snapshot(); var srcEnd = sourceCounter.Snapshot();
    var secondsReal = Math.Max(1d, realElapsed.TotalSeconds);
    double[] Select(byte kind, IReadOnlyList<double> values) => values.Where((_, i) => cycleKind[i] == kind).ToArray();
    var costs = cycleMs;
    var sqlD = cycleObserverSql.Select(x => (double)x).ToList();
    var ticksPerCycle = cycleTicks.Select(x => (double)x).ToList();
    var periodMs = cycleMs.Select(x => x + 250).ToList();
    // regression of cost on ticks per cycle (least squares) gives the per-tick processing cost b; stable iff b * rate < 1 s/s.
    double meanX = ticksPerCycle.Average(), meanY = costs.Average();
    var sxx = ticksPerCycle.Sum(x => (x - meanX) * (x - meanX)); var sxy = ticksPerCycle.Zip(costs, (x, y) => (x - meanX) * (y - meanY)).Sum();
    var slopeMsPerTick = sxx > 0 ? sxy / sxx : 0d;
    var interceptMs = meanY - slopeMsPerTick * meanX;
    var totalTicks = cycleTicks.Sum();
    var summary = new Dictionary<string, object>
    {
        ["name"] = name, ["day"] = d.Day.ToString("yyyy-MM-dd"), ["compression"] = compression, ["cycles"] = cycles, ["simulatedRealSeconds"] = Math.Round(secondsReal, 1),
        ["dataSecondsCovered"] = Math.Round((dataNow - dataStart).TotalSeconds, 1), ["ticksProcessed"] = totalTicks, ["completedBars"] = barCount,
        ["startupRecoveryAtStartMs"] = Math.Round(startSw.Elapsed.TotalMilliseconds, 1),
        ["pollMsAll"] = Stats.Summary(costs), ["pollMsIdle"] = Stats.Summary(Select(0, costs)), ["pollMsWithTicks"] = Stats.Summary(Select(1, costs)), ["pollMsWithBar"] = Stats.Summary(Select(2, costs)),
        ["cyclesByKind"] = new { idle = cycleKind.Count(x => x == 0), ticksNoBar = cycleKind.Count(x => x == 1), withBar = cycleKind.Count(x => x == 2) },
        ["cyclePeriodMs"] = Stats.Summary(periodMs), ["cyclePeriodOverBudgetPct"] = Math.Round(100d * periodMs.Count(x => x > 275) / periodMs.Count, 3),
        ["ticksPerCycle"] = Stats.Summary(ticksPerCycle), ["inputTicksPerRealSecond"] = Math.Round(totalTicks / secondsReal, 1),
        ["processingCapacityTicksPerSecond"] = slopeMsPerTick > 0 ? Math.Round(1000d / slopeMsPerTick, 0) : double.PositiveInfinity,
        ["costModel"] = new { fixedMsPerPoll = Math.Round(interceptMs, 3), msPerTick = Math.Round(slopeMsPerTick, 5) },
        ["observerSqlPerPoll"] = new { idle = Stats.Summary(Select(0, sqlD)), withTicks = Stats.Summary(Select(1, sqlD)), withBar = Stats.Summary(Select(2, sqlD)) },
        ["observerSqlWritesPerPollIdle"] = Stats.Summary(Select(0, cycleObserverWrites.Select(x => (double)x).ToList())),
        ["sourceSqlPerPollIdle"] = Stats.Summary(Select(0, cycleSourceSql.Select(x => (double)x).ToList())),
        ["observerSqlTotal"] = sqlEnd.Total - sqlStart.Total, ["observerWritesTotal"] = sqlEnd.Writes - sqlStart.Writes, ["sourceSqlTotal"] = srcEnd.Total - srcStart.Total,
        ["observerWritesPerSecond"] = Math.Round((sqlEnd.Writes - sqlStart.Writes) / secondsReal, 3), ["observerCommandsPerSecond"] = Math.Round((sqlEnd.Total - sqlStart.Total) / secondsReal, 3),
        ["maxPendingTicks"] = maxPending, ["pendingAtEnd"] = pendingAtEnd, ["maxTicksInOneCycle"] = maxPendingCycleTicks,
        ["drainSecondsAfterIngestEnd"] = drainedAt is { } dr ? Math.Round(dr.TotalSeconds, 2) : (object)"n/a",
        ["stages"] = recorder.Report(),
        ["memory"] = new { workingSetPeakMb = Mb(mem.PeakWorkingSet), managedHeapPeakMb = Mb(mem.PeakManagedHeap), allocatedGb = Math.Round((GC.GetTotalAllocatedBytes(false) - allocStart) / 1073741824d, 2),
            gcGen0 = GC.CollectionCount(0) - g0, gcGen1 = GC.CollectionCount(1) - g1, gcGen2 = GC.CollectionCount(2) - g2, workingSetSamples = wsSamples.Select(x => new { hours = Math.Round(x.Hours, 2), mb = x.Mb }) },
        ["retainedOptionBookStates"] = OptionBookStates(live).States,
    };

    // commentary stage by outcome: no event / ordinary lifecycle event / Telegram-eligible event (jobs are suppressed in this harness, so an eligible
    // event costs a few extra commands in production for the outbox row)
    {
        await using var cdb = new AdaptiveObserverDbContext(observerOptions(observerDb));
        var events = await cdb.CommentaryEvents.AsNoTracking().Select(x => new { x.BarSeq, x.ShouldNotifyTelegram }).ToListAsync();
        var byBar = events.GroupBy(x => x.BarSeq).ToDictionary(g => g.Key, g => g.Any(x => x.ShouldNotifyTelegram));
        var classes = new Dictionary<string, List<(double Ms, double Sql)>> { ["noMaterialEvent"] = new(), ["lifecycleEvent"] = new(), ["telegramEligibleEvent"] = new() };
        foreach (var (bar, stages2) in recorder.PerBar)
        {
            if (!stages2.TryGetValue("bar.commentary", out var c)) continue;
            var key = byBar.TryGetValue(bar, out var eligible) ? (eligible ? "telegramEligibleEvent" : "lifecycleEvent") : "noMaterialEvent";
            classes[key].Add(c);
        }

        summary["commentaryByOutcome"] = classes.ToDictionary(k => k.Key, k => (object)new { bars = k.Value.Count, ms = Stats.Summary(k.Value.Select(x => x.Ms)), sql = Stats.Summary(k.Value.Select(x => x.Sql)) });
    }

    // per-completed-bar pipeline distribution
    var barTotals = recorder.PerBar.Select(kv => kv.Value.TryGetValue("bar", out var b) ? b.Ms : double.NaN).Where(x => !double.IsNaN(x)).ToList();
    summary["completedBarPipelineMs"] = Stats.Summary(barTotals);
    return new SimResult(summary, observerDb, lastPollNow);
}

// ---------------------------------------------------------------- mid-session ABRUPT restart-continuation validation
// RUN A: one uninterrupted production live path. RUN B: the same session, but at a chosen point the whole process state is destroyed WITHOUT any
// graceful close (worker, engine, normalizer, pending buffer, trackers, commentary service, notification gate, DbContexts, DI container, pooled
// connections), a completely new service provider / worker is built, and TryStartOrRecoverAsync reconstructs everything from the persisted source
// ticks and the adaptive database only; live polling then continues to the same horizon. Final persisted state must be identical.
(AdaptiveObserverWorker Worker, ServiceProvider Provider) NewWorkerWithProvider(string observerDb)
{
    var s = new ServiceCollection();
    s.AddLogging(b => b.SetMinimumLevel(LogLevel.Error).AddSimpleConsole());
    s.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(DbCs(SourceDb)).AddInterceptors(new CountingInterceptor(sourceCounter)));
    s.AddDbContext<AdaptiveObserverDbContext>(o => o.UseNpgsql(DbCs(observerDb)).AddInterceptors(new CountingInterceptor(observerCounter)));
    s.AddOptions<PricingOptions>();
    s.Configure<DashboardPushOptions>(o => o.HubUrl = "http://127.0.0.1:1/hubs/never-connected");
#if !BASELINE
    s.AddSingleton<IOptionsMonitor<AdaptiveCommentaryTelegramOptions>>(new StaticMonitor<AdaptiveCommentaryTelegramOptions>(new AdaptiveCommentaryTelegramOptions { Enabled = false }));
    s.AddSingleton<IOptionsMonitor<TelegramOptions>>(new StaticMonitor<TelegramOptions>(new TelegramOptions { BotToken = "", ChatId = "" }));
    s.AddSingleton<AdaptiveCommentaryNotificationGate>();
#endif
    s.AddSingleton<AdaptiveSourceTickReader>(); s.AddSingleton<AdaptiveHistoricalBootstrapService>(); s.AddSingleton<AdaptiveSessionCoordinator>();
    s.AddSingleton<AdaptiveObserverPersistence>(); s.AddSingleton<AdaptiveSupplementalPersistence>(); s.AddSingleton<AdaptiveCommentaryFrameLoader>();
    s.AddSingleton<AdaptiveCommentaryService>(); s.AddSingleton<AdaptiveWeak2ObservationService>();
    s.AddSingleton<AdaptiveStateRecoveryService>(); s.AddSingleton<AdaptiveEndedSessionRecoveryService>(); s.AddSingleton<DashboardPushClient>();
    s.AddSingleton<AdaptiveObserverWorker>();
    var provider = s.BuildServiceProvider();
    return (provider.GetRequiredService<AdaptiveObserverWorker>(), provider);
}

async Task BuildPriorsTemplateAsync(DateOnly day, string template)
{
    await FreshObserverAsync(template);
    await using var obs = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(DbCs(template)).Options);
    // The real production bootstrap, against the real (read-only) source, writing only into the isolated observer database.
    await new AdaptiveHistoricalBootstrapService(reader, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveHistoricalBootstrapService>.Instance)
        .EnsurePriorSessionsAsync(src, obs, day, default);
}

async Task CloneObserverAsync(string template, string name)
{
    NpgsqlConnection.ClearAllPools();
    await using var admin = new NpgsqlConnection(DbCs("postgres"));
    await admin.OpenAsync();
    await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin)) await drop.ExecuteNonQueryAsync();
    await using (var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE {template}", admin)) await create.ExecuteNonQueryAsync();
}

var volatileColumns = new HashSet<string> {"Id", "LastHeartbeatUtc", "LastRecoveryStartedUtc", "LastRecoveryCompletedUtc", "LastRecoveryReconciledBars", "UpdatedAtUtc", "DetectedAtUtc" };

async Task<Dictionary<string, List<string>>> DumpAsync(string observerDb, long sessionId)
{
    await using var db = new AdaptiveObserverDbContext(observerOptions(observerDb));
    List<string> Rows<T>(IEnumerable<T> rows, params string[] only) where T : class
    {
        var props = typeof(T).GetProperties().Where(p => !volatileColumns.Contains(p.Name) && (only.Length == 0 || only.Contains(p.Name))
            && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string) || p.PropertyType == typeof(decimal)
                || p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateOnly) || Nullable.GetUnderlyingType(p.PropertyType) is not null)).ToArray();
        return rows.Select(r => string.Join(';', props.Select(p => p.Name + "=" + (p.GetValue(r) is double dv ? dv.ToString("R") : p.GetValue(r)?.ToString() ?? "null")))).ToList();
    }

    var d = new Dictionary<string, List<string>>
    {
        ["Sessions(frozen definition)"] = Rows(await db.Sessions.AsNoTracking().Where(x => x.Id == sessionId).ToListAsync()),
        ["FutureBars"] = Rows(await db.FutureBars.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ToListAsync()),
        ["RollingStates"] = Rows(await db.RollingStates.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.EndBarSeq).ToListAsync()),
        ["OptionBandBars"] = Rows(await db.OptionBandBars.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ThenBy(x => x.Side).ToListAsync()),
        ["OptionResidualBars"] = Rows(await db.OptionResidualBars.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ThenBy(x => x.Variant).ToListAsync()),
        ["ResidualAnchorComponents"] = Rows(await db.ResidualAnchorComponents.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.Side).ThenBy(x => x.Strike).ToListAsync()),
        ["Weak2Observations"] = Rows(await db.Weak2Observations.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.TriggerBarSeq).ToListAsync()),
        ["FuturesSupplemental"] = Rows(await db.FuturesSupplemental.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ThenBy(x => x.MetricsVersion).ToListAsync()),
        ["OptionsSupplemental"] = Rows(await db.OptionsSupplemental.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ThenBy(x => x.MetricsVersion).ToListAsync()),
        ["ProjectionHealth"] = Rows(await db.ProjectionHealth.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ToListAsync()),
        ["CommentaryEvents"] = Rows(await db.CommentaryEvents.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ThenBy(x => x.EventIdentity).ToListAsync()),
        ["CommentaryRuntime"] = Rows(await db.CommentaryRuntime.AsNoTracking().Where(x => x.SessionId == sessionId).ToListAsync()),
        ["CommentaryNotificationJobs"] = Rows(await db.CommentaryNotificationJobs.AsNoTracking().ToListAsync()),
        ["Runtime(deterministic fields)"] = Rows(await db.Runtime.AsNoTracking().Where(x => x.SessionId == sessionId).ToListAsync(),
            "LastCompletedBarSeq", "CurrentPartialBarVolume", "CurrentPartialBarStartedAtUtc", "LastProcessedSourceAvailableAtUtc", "LastProcessedSourceTickId", "RuntimeStatus"),
    };

    // Derived-only metrics evaluated from the persisted rows exactly as the Dashboard / commentary frame do.
    var bars = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.BarSeq).ToListAsync();
    d["Derived: Urgency"] = bars.Select(b => $"{b.BarSeq}:{AdaptiveMetricMath.Urgency(b.Volume, b.DurationSeconds)?.ToString("R") ?? "null"}").ToList();
    var anchors = await db.ResidualAnchorComponents.AsNoTracking().Where(x => x.SessionId == sessionId).ToListAsync();
    var bases = new ResidualBases(anchors.Where(x => x.Side == OptionType.Call).Sum(x => x.Price0930), anchors.Where(x => x.Side == OptionType.Put).Sum(x => x.Price0930));
    var residuals = await db.OptionResidualBars.AsNoTracking().Where(x => x.SessionId == sessionId && x.Variant == ResidualVariant.AtmPlusMinus2).OrderBy(x => x.BarSeq).ToListAsync();
    d["Derived: residual adjacent deltas"] = AdaptiveResidualProjection.Derive(residuals.Select(x => new ResidualReadingPoint(x.BarSeq, x.IsAvailable, x.CEResidual, x.PEResidual,
        x.CEResidualPct, x.PEResidualPct, x.DirectionalResidualPct)), bases).Select(x => $"{x.BarSeq}:{x.AdjacentDirectionalResidualDelta?.ToString("R")}:{x.CeResidualDeltaPct?.ToString("R")}:{x.PeResidualDeltaPct?.ToString("R")}:{x.StraddleResidualPct?.ToString("R")}").ToList();
    return d;
}

static string HashRows(List<string> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))))[..16];

Dictionary<string, object> Compare(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
{
    var tables = new List<object>(); var allEqual = true; var diffs = new List<string>();
    foreach (var key in a.Keys)
    {
        var equal = a[key].SequenceEqual(b[key]);
        allEqual &= equal;
        tables.Add(new { table = key, rowsA = a[key].Count, rowsB = b[key].Count, hashA = HashRows(a[key]), hashB = HashRows(b[key]), equal });
        if (!equal)
        {
            var max = Math.Max(a[key].Count, b[key].Count);
            for (var i = 0; i < max && diffs.Count < 12; i++)
            {
                var ra = i < a[key].Count ? a[key][i] : "<missing>"; var rb = i < b[key].Count ? b[key][i] : "<missing>";
                if (ra == rb) continue;
                var ca = ra.Split(';').ToDictionary(x => x.Split('=')[0], x => x.Contains('=') ? x[(x.IndexOf('=') + 1)..] : "");
                var cb = rb.Split(';').ToDictionary(x => x.Split('=')[0], x => x.Contains('=') ? x[(x.IndexOf('=') + 1)..] : "");
                diffs.Add($"{key}[{i}]: " + string.Join(", ", ca.Keys.Where(k => !cb.ContainsKey(k) || cb[k] != ca[k]).Select(k => $"{k}: A={ca[k]} B={(cb.TryGetValue(k, out var v) ? v : "<missing>")}")));
            }
        }
    }

    return new() { ["allEqual"] = allEqual, ["tables"] = tables, ["structuralDiff"] = diffs };
}


async Task<(Dictionary<string, List<string>> PreClose, Dictionary<string, List<string>> PostClose, Dictionary<string, object> Info)> RestartRunAsync(
    DayData d, Staged staged, string template, string runDb, CrashCase? crash, DateTimeOffset start, DateTimeOffset horizon)
{
    await CloneObserverAsync(template, runDb);
    await ClearTicksAsync();
    var feeder = new Feeder(staged, DbCs(SourceDb));
    var (worker, provider) = NewWorkerWithProvider(runDb);
    await feeder.FeedAsync(start);
    var live = await worker.TryStartOrRecoverAsync(d.Day, start, default) ?? throw new InvalidOperationException("Session did not start.");
    var info = new Dictionary<string, object> { ["run"] = crash?.Name ?? "uninterrupted" };
    var clock = start; var crashed = false;
    int Complete(AdaptiveObserverWorker.LiveState l) => l.Engine.FutureBars.Count(x => x.IsComplete);
    var pollNow = start;
    while (clock < horizon - TimeSpan.FromSeconds(5))
    {
        await feeder.FeedAsync(clock);
        var barsBefore = Complete(live); pollNow = clock;
        var sw = Stopwatch.StartNew();
        await worker.PollLiveAsync(live, clock, default);
        var cost = sw.Elapsed;
        if (!crashed && crash is not null && crash.When(live, barsBefore, Complete(live), clock))
        {
            crashed = true;
            var baseVolume = live.Context.Session.BaseBarVolume;
            var before = new Dictionary<string, object>
            {
                ["restartPointIst"] = (clock + ist).ToString("HH:mm:ss.fff"), ["completedBarsBeforeCrash"] = Complete(live), ["partialVolume"] = live.Engine.PartialBar.AccumulatedVolume,
                ["baseBarVolume"] = baseVolume, ["partialFractionOfBase"] = Math.Round(live.Engine.PartialBar.AccumulatedVolume / (double)baseVolume, 3), ["pendingTicksLost"] = live.Pending.Count,
                ["rawTicksVisibleInSource"] = feeder.Inserted, ["lastProcessedTickId"] = live.LastProcessedTickId ?? -1,
            };

            // ---- ABRUPT PROCESS DEATH: no CloseCurrentSessionAsync, no flush; every in-memory object is dropped, the DI container and all pooled connections die.
            live = null!; worker = null!; await provider.DisposeAsync(); provider = null!;
            NpgsqlConnection.ClearAllPools();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            var restartAt = clock + TimeSpan.FromSeconds(3);                   // downtime; ticks keep being persisted by ingestion
            await feeder.FeedAsync(restartAt);
            long Count(string table) { using var c = new NpgsqlConnection(DbCs(runDb)); c.Open(); using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", c); return (long)cmd.ExecuteScalar()!; }
            var barsPersisted = Count("adaptive_future_bars"); var futSideBefore = Count("adaptive_futures_supplemental_bars"); var optSideBefore = Count("adaptive_options_supplemental_bars");
            var eventsBefore = Count("adaptive_commentary_events");
            recorder.Reset();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var heapBefore = GC.GetTotalMemory(true); var obsBefore = observerCounter.Snapshot(); var srcBefore = sourceCounter.Snapshot();
            using var mem = new MemorySampler();
            var recoverySw = Stopwatch.StartNew();
            (worker, provider) = NewWorkerWithProvider(runDb);                  // brand-new service provider and worker: nothing carried over
            live = await worker.TryStartOrRecoverAsync(d.Day, restartAt, default) ?? throw new InvalidOperationException("Recovery did not return to Live.");
            recoverySw.Stop();
            string status; long reconciled;
            await using (var chk = new AdaptiveObserverDbContext(observerOptions(runDb)))
            {
                var rt = await chk.Runtime.AsNoTracking().SingleAsync(x => x.SessionId == live.Context.Session.Id);
                status = rt.RuntimeStatus.ToString(); reconciled = rt.LastRecoveryReconciledBars;
            }

            var barsAfter = Count("adaptive_future_bars"); var futSideAfter = Count("adaptive_futures_supplemental_bars"); var optSideAfter = Count("adaptive_options_supplemental_bars");
            var eventsAfter = Count("adaptive_commentary_events"); var obsAfter = observerCounter.Snapshot();
            var stageMs = recorder.DurationsMs.Where(k => k.Key.StartsWith("recovery.", StringComparison.Ordinal)).ToDictionary(k => k.Key, k => Math.Round(k.Value.Sum(), 1));
            before["downtimeSeconds"] = 3;
            info["crash"] = before;
            info["recovery"] = new Dictionary<string, object>
            {
                ["wallMs"] = Math.Round(recoverySw.Elapsed.TotalMilliseconds, 1), ["stageMs"] = stageMs, ["runtimeStatusAfterRecovery"] = status, ["barsVerifiedAgainstPersisted"] = reconciled,
                ["barsPersistedBefore"] = barsPersisted, ["barsPersistedAfter"] = barsAfter,
                ["futuresSidecarsInserted"] = futSideAfter - futSideBefore, ["futuresSidecarsVerified"] = Math.Min(futSideBefore, barsAfter),
                ["optionsSidecarsInserted"] = optSideAfter - optSideBefore, ["optionsSidecarsVerified"] = Math.Min(optSideBefore, barsAfter),
                ["commentaryEventsBefore"] = eventsBefore, ["commentaryEventsAfter"] = eventsAfter, ["commentaryEventsBackfilled"] = eventsAfter - eventsBefore,
                ["pendingTicksRetainedBehindStableLag"] = live.Pending.Count, ["recoveredCompletedBars"] = Complete(live), ["recoveredPartialVolume"] = live.Engine.PartialBar.AccumulatedVolume,
                ["recoveredPartialStartIst"] = live.Engine.PartialBar.StartedAtUtc is { } ps ? (ps + ist).ToString("HH:mm:ss.fff") : "none",
                ["workingSetPeakMb"] = Mb(mem.PeakWorkingSet), ["managedHeapBeforeMb"] = Mb(heapBefore), ["managedHeapPeakMb"] = Mb(mem.PeakManagedHeap),
                ["observerSqlCommands"] = obsAfter.Total - obsBefore.Total, ["sourceSqlCommands"] = sourceCounter.Snapshot().Total - srcBefore.Total,
            };
            clock = restartAt;
            continue;
        }

        clock += cost + TimeSpan.FromMilliseconds(250);
    }

    // Deterministic synchronization point: every tick received by the horizon is visible and one poll at exactly the horizon clock settles the state.
    await feeder.FeedAsync(horizon);
    await worker.PollLiveAsync(live, horizon, default, forceRuntimeFlush: true);
    var sessionId = live.Context.Session.Id;
    info["horizonIst"] = (horizon + ist).ToString("HH:mm:ss");
    info["finalPartialVolume"] = live.Engine.PartialBar.AccumulatedVolume;
    info["finalPartialStartedUtc"] = live.Engine.PartialBar.StartedAtUtc?.ToString("O") ?? "none";
    info["finalCompletedBars"] = Complete(live);
    info["finalPendingTicks"] = live.Pending.Count;
    info["finalPendingFingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', live.Pending
        .OrderBy(x => x.Tick.AvailableAt).ThenBy(x => x.Tick.Id).Select(x => $"{x.Token}:{x.Tick.Id}:{x.Tick.AvailableAt.UtcTicks}:{x.Tick.Last:R}"))))) [..16];
    info["lastProcessedAvailableAtUtc"] = live.LastProcessedAvailableAt?.ToString("O") ?? "none";
    info["lastProcessedTickId"] = live.LastProcessedTickId ?? -1;
    info["lastFetchedRawId"] = live.LastFetchedRawId;
    info["ticksInSource"] = feeder.Inserted;
    var preClose = await DumpAsync(runDb, sessionId);
    await worker.CloseCurrentSessionAsync(live, default);
    var postClose = await DumpAsync(runDb, sessionId);
    await provider.DisposeAsync();
    return (preClose, postClose, info);
}

static Dictionary<string, object> LiveState(Dictionary<string, object> i) => new[] { "finalPartialVolume", "finalPartialStartedUtc", "finalPendingTicks", "finalPendingFingerprint", "lastProcessedTickId", "lastProcessedAvailableAtUtc", "finalCompletedBars", "lastFetchedRawId" }
    .ToDictionary(k => k, k => i[k]);

async Task<List<Dictionary<string, object>>> RestartValidationAsync(DayData d, Staged staged)
{
    var results = new List<Dictionary<string, object>>();
    var day = d.Day;
    var template = $"perf_tpl_{day:yyyyMMdd}";
    Console.WriteLine($"RESTART {day:yyyy-MM-dd}: seeding prior-session history with the real production bootstrap ...");
    await BuildPriorsTemplateAsync(day, template);
    var start = U(day, 9, 30, 5); var horizon = U(day, 15, 35, 30);

    if (Env("ADAPTIVE_PERF_RESTART_MODE", "full") == "scan")
    {
        var scanDb = $"perf_scan_{day:yyyyMMdd}";
        await CloneObserverAsync(template, scanDb); await LoadTicksAsync(null);
        var w = NewWorkerWithProvider(scanDb);
        var live = await w.Worker.TryStartOrRecoverAsync(day, U(day, 15, 36), default);
        await using var db = new AdaptiveObserverDbContext(observerOptions(scanDb));
        var session = await db.Sessions.AsNoTracking().SingleAsync(x => x.TradeDate == day && !x.IsHistoricalSeed);
        var weak = await db.Weak2Observations.AsNoTracking().Where(x => x.SessionId == session.Id).OrderBy(x => x.TriggerBarSeq).ToListAsync();
        results.Add(new() { ["day"] = day.ToString("yyyy-MM-dd"), ["mode"] = "scan", ["strongThreshold"] = session.StrongThreshold?.ToString("R") ?? "null", ["weak2Observations"] = weak.Count,
            ["triggerBars"] = weak.Select(x => x.TriggerBarSeq).ToArray(), ["statuses"] = weak.GroupBy(x => x.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()) });
        Console.WriteLine($"SCAN {day:yyyy-MM-dd}: strongThreshold={session.StrongThreshold} weak2={weak.Count} bars=[{string.Join(',', weak.Select(x => x.TriggerBarSeq))}]");
        await w.Provider.DisposeAsync();
        return results;
    }

    // ---- RUN A: uninterrupted
    var a = await RestartRunAsync(d, staged, template, $"perf_restarta_{day:yyyyMMdd}", null, start, horizon);
    Console.WriteLine($"RUN A uninterrupted: bars={a.Info["finalCompletedBars"]} pending={a.Info["finalPendingTicks"]} weak2Rows={a.PostClose["Weak2Observations"].Count}");

    // ---- derive the difficult restart points from the data of run A
    var runA = $"perf_restarta_{day:yyyyMMdd}";
    List<(DateTimeOffset End, int Bars)> multi; List<(DateTimeOffset End, double Seconds, long Volume)> fastest;
    await using (var db = new AdaptiveObserverDbContext(observerOptions(runA)))
    {
        var bars = await db.FutureBars.AsNoTracking().Where(x => x.EndAvailableAtUtc > start.AddMinutes(20) && x.EndAvailableAtUtc < horizon.AddMinutes(-30)).OrderBy(x => x.BarSeq).ToListAsync();
        multi = bars.GroupBy(x => x.EndAvailableAtUtc).Where(g => g.Count() > 1).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
        fastest = bars.Where(x => x.DurationSeconds > 0).OrderBy(x => x.DurationSeconds).Take(3).Select(x => (x.EndAvailableAtUtc, x.DurationSeconds, x.Volume)).ToList();
    }

    var cases = new List<CrashCase>
    {
        new("A: incomplete adaptive bar", "restart when the partial futures volume is 40-60% of BaseBarVolume (mid-bar), after 11:30 IST, with unprocessed ticks still in the ordering buffer",
            (l, bb, bn, now) => now > U(day, 11, 30) && l.Engine.PartialBar.AccumulatedVolume >= 0.4 * l.Context.Session.BaseBarVolume
                && l.Engine.PartialBar.AccumulatedVolume <= 0.6 * l.Context.Session.BaseBarVolume && l.Pending.Count > 0),
        new("B: immediately after a completed bar", "restart on the first poll after 12:30 IST that completed an adaptive bar (bar rows committed, runtime cursor just advanced)",
            (l, bb, bn, now) => now > U(day, 12, 30) && bn > bb),
    };
    if (multi.Count > 0)
    {
        var (end, n) = multi[0];
        cases.Add(new($"C1: before a multi-bar boundary ({n} bars close at {(end + ist):HH:mm:ss} IST)", "restart after the boundary's ticks are fetched but BEFORE they are processed (they sit in the lost ordering buffer)",
            (l, bb, bn, now) => l.Pending.Any(p => p.Tick.AvailableAt == end) && bn == bb));
        cases.Add(new($"C2: after a multi-bar boundary ({n} bars close at {(end + ist):HH:mm:ss} IST)", "restart on the poll that completes the multi-bar boundary", (l, bb, bn, now) => bn - bb >= n));
    }
    else
    {
        var (end, secs, vol) = fastest[0];
        Console.WriteLine($"No timestamp in this session closes more than one exact volume bar; using the fastest-closing (highest-volume-rate) boundary: {(end + ist):HH:mm:ss} IST, {secs}s, volume {vol}.");
        cases.Add(new($"C1: before the highest-volume boundary (bar closing {(end + ist):HH:mm:ss} IST, {secs}s)", "no real multi-bar-close exists; restart before the fastest-closing bar's ticks are processed",
            (l, bb, bn, now) => l.Pending.Any(p => p.Tick.AvailableAt == end) && bn == bb));
        cases.Add(new($"C2: after the highest-volume boundary (bar closing {(end + ist):HH:mm:ss} IST)", "restart on the poll that completes that bar", (l, bb, bn, now) => now > end && bn > bb && l.LastProcessedAvailableAt >= end));
    }

    var weak2Rows = a.PostClose["Weak2Observations"];
    foreach (var c in cases)
    {
        var tag = new string(c.Name.TakeWhile(ch => ch != ':').ToArray()).ToLowerInvariant();
        var b = await RestartRunAsync(d, staged, template, $"perf_restartb{tag}_{day:yyyyMMdd}", c, start, horizon);
        if (!b.Info.ContainsKey("crash")) { results.Add(new() { ["day"] = day.ToString("yyyy-MM-dd"), ["case"] = c.Name, ["error"] = "restart condition never occurred in this session" }); continue; }
        var pre = Compare(a.PreClose, b.PreClose); var post = Compare(a.PostClose, b.PostClose);
        var sameState = a.Info["finalPartialVolume"].Equals(b.Info["finalPartialVolume"]) && a.Info["finalPartialStartedUtc"].Equals(b.Info["finalPartialStartedUtc"]) && a.Info["finalPendingFingerprint"].Equals(b.Info["finalPendingFingerprint"])
            && a.Info["lastProcessedTickId"].Equals(b.Info["lastProcessedTickId"]) && a.Info["lastProcessedAvailableAtUtc"].Equals(b.Info["lastProcessedAvailableAtUtc"]) && a.Info["finalCompletedBars"].Equals(b.Info["finalCompletedBars"]);
        var jobs = b.PostClose["CommentaryNotificationJobs"].Count;
        var health = b.PostClose["ProjectionHealth"].Count;
        results.Add(new()
        {
            ["day"] = day.ToString("yyyy-MM-dd"), ["case"] = c.Name, ["why"] = c.Why, ["crash"] = b.Info["crash"], ["recovery"] = b.Info["recovery"],
            ["equalAtHorizon"] = pre["allEqual"], ["equalAfterClose"] = post["allEqual"], ["liveStateEqualAtHorizon"] = sameState, ["tablesAfterClose"] = post["tables"], ["tablesAtHorizon"] = pre["tables"],
            ["structuralDiff"] = ((List<string>)post["structuralDiff"]).Concat((List<string>)pre["structuralDiff"]).ToList(),
            ["liveStateA"] = LiveState(a.Info), ["liveStateB"] = LiveState(b.Info),
            ["notificationJobs"] = jobs, ["projectionHealthRows"] = health, ["weak2RowsA"] = weak2Rows.Count, ["weak2RowsB"] = b.PostClose["Weak2Observations"].Count,
        });
        Console.WriteLine($"RESTART CASE {c.Name}: horizon-equal={pre["allEqual"]} after-close-equal={post["allEqual"]} liveState-equal={sameState} jobs={jobs} health={health} recoveryMs={((Dictionary<string, object>)b.Info["recovery"])["wallMs"]}");
        foreach (var line in ((List<string>)post["structuralDiff"]).Take(5)) Console.WriteLine("   DIFF " + line);
    }

    // Negative controls: the comparator must flag a one-column change and a missing row (proves the equality results above are not vacuous).
    var mutated = a.PostClose.ToDictionary(k => k.Key, k => k.Value.ToList());
    mutated["FutureBars"][mutated["FutureBars"].Count / 2] = mutated["FutureBars"][mutated["FutureBars"].Count / 2].Replace("Volume=", "Volume=1");
    mutated["OptionBandBars"].RemoveAt(mutated["OptionBandBars"].Count / 3);
    var control = Compare(a.PostClose, mutated);
    Console.WriteLine($"NEGATIVE CONTROL (mutated copy of run A): comparator reports equal={control["allEqual"]} (must be False); {string.Join(" | ", ((List<string>)control["structuralDiff"]).Take(2))}");

    results.Insert(0, new() { ["negativeControlDetected"] = !(bool)control["allEqual"], ["day"] = day.ToString("yyyy-MM-dd"), ["run"] = "A uninterrupted", ["finalCompletedBars"] = a.Info["finalCompletedBars"], ["weak2Rows"] = weak2Rows.Count,
        ["weak2TriggerRows"] = weak2Rows.Take(10).ToList(), ["tables"] = a.PostClose.ToDictionary(k => k.Key, k => new { rows = k.Value.Count, hash = HashRows(k.Value) }), ["multiBarBoundaries"] = multi.Select(m => new { endIst = (m.End + ist).ToString("HH:mm:ss"), m.Bars }).ToList(),
        ["strongThreshold"] = a.PostClose["Sessions(frozen definition)"].FirstOrDefault()?.Split(';').FirstOrDefault(x => x.StartsWith("StrongThreshold="))?.Split('=')[1] ?? "?" });
    return results;
}

// ---------------------------------------------------------------- run
var liveResults = new List<Dictionary<string, object>>();
var recoveryResults = new List<Dictionary<string, object>>();
var digests = new Dictionary<string, object>();
var dashboardResults = new List<Dictionary<string, object>>();
var stressResults = new List<Dictionary<string, object>>();
var planResults = new List<string>();
var equivalence = new List<Dictionary<string, object>>();

if (stages.Overlaps(["live", "recovery", "dashboard", "stress", "digest"]) && !stages.Contains("restart"))
{
    await EnsureSourceSchemaAsync();
    var simDays = liveDays.Concat(coldDays).Distinct().ToList();
    if (stages.Contains("stress") && Env("ADAPTIVE_PERF_STRESS", "1") == "1" && busiestDay is { } b && !simDays.Contains(b)) simDays.Add(b);
    foreach (var day in simDays.OrderBy(x => x))
    {
        var d = await LoadDayAsync(day);
        if (d is null) { Console.WriteLine($"SKIP {day}: not enough instruments"); continue; }
        var staged = await StageAsync(d);
        Console.WriteLine($"STAGED {day:yyyy-MM-dd}: {d.Raw.Count} ticks, {d.Tokens.Length} tokens");
        var isCold = coldDays.Contains(day) && !liveDays.Contains(day);
        var isLive = liveDays.Contains(day);
        var stressDay = busiestDay == day;

        if (isLive && stages.Contains("live"))
        {
            var observerDb = await FreshObserverAsync($"perf_observer_{day:yyyyMMdd}");
            var endIst = TimeOnly.Parse(Env("ADAPTIVE_PERF_SIM_END_IST", "15:35"));     // shorten for a smoke run; the default simulates the whole session to the 15:35 close
            var fullDay = endIst >= new TimeOnly(15, 35);
            var result = await SimulateAsync(d, staged, observerDb, 1d, U(day, 9, 30, 5), null, U(day, endIst.Hour, endIst.Minute), $"live-1x-{day:yyyyMMdd}", closeSession: fullDay);
            liveResults.Add(result.Summary);
            Console.WriteLine($"LIVE {day:yyyy-MM-dd} cycles={result.Summary["cycles"]} bars={result.Summary["completedBars"]} poll p50/p99/max={Fmt(result.Summary["pollMsAll"])} writes/s={result.Summary["observerWritesPerSecond"]} maxPending={result.Summary["maxPendingTicks"]}");
            if (stages.Contains("digest")) digests[$"live-1x-{day:yyyyMMdd}"] = await DigestAsync(observerDb);

            if (stages.Contains("recovery"))
            {
                // Warm recovery: restart at the end of the day against the fully populated database (verifies every persisted row).
                await LoadTicksAsync(null);
                var rec = await RecoveryAsync(day, observerDb, "warm-verify-full-day", U(day, 15, 40));
                recoveryResults.Add(rec);
                Console.WriteLine($"RECOVERY(warm) {day:yyyy-MM-dd} totalMs={rec["totalWallMs"]} wsPeakMb={rec["workingSetPeakMb"]} heapPeakMb={rec["managedHeapPeakMb"]}");
                if (stages.Contains("digest"))
                {
                    var after = await DigestAsync(observerDb);
                    var same = System.Text.Json.JsonSerializer.Serialize(after) == System.Text.Json.JsonSerializer.Serialize(digests[$"live-1x-{day:yyyyMMdd}"]);
                    equivalence.Add(new() { ["check"] = $"live-vs-restart-replay {day:yyyy-MM-dd}", ["equal"] = same });
                    Console.WriteLine($"EQUIVALENCE live-vs-restart-replay {day:yyyy-MM-dd}: {(same ? "IDENTICAL" : "DIFFERENT")}");
                }
            }

            if (stages.Contains("dashboard")) dashboardResults.Add(await DashboardAsync(observerDb, day, d));
        }

        if (isCold && stages.Contains("recovery"))
        {
            var observerDb = await FreshObserverAsync($"perf_observer_cold_{day:yyyyMMdd}");
            await LoadTicksAsync(null);
            var rec = await RecoveryAsync(day, observerDb, "cold-create-full-day", U(day, 15, 34));
            recoveryResults.Add(rec);
            Console.WriteLine($"RECOVERY(cold) {day:yyyy-MM-dd} totalMs={rec["totalWallMs"]} wsPeakMb={rec["workingSetPeakMb"]} heapPeakMb={rec["managedHeapPeakMb"]}");
        }

        if (stressDay && stages.Contains("stress") && Env("ADAPTIVE_PERF_STRESS", "1") == "1" && perSecondByDay.TryGetValue(day, out var perSecond))
        {
            stressResults.AddRange(await StressAsync(d, staged, perSecond));
        }
    }
}

var restartReports = new List<Dictionary<string, object>>();
if (stages.Contains("restart"))
{
    await EnsureSourceSchemaAsync();
    foreach (var day in ParseDays(Env("ADAPTIVE_PERF_RESTART_DAYS")))
    {
        var d = await LoadDayAsync(day);
        if (d is null) continue;
        var staged = await StageAsync(d);
        restartReports.AddRange(await RestartValidationAsync(d, staged));
    }
}

if (stages.Contains("plans")) planResults.AddRange(await QueryPlansAsync());

report["live"] = liveResults; report["recovery"] = recoveryResults; report["digests"] = digests; report["dashboard"] = dashboardResults; report["stress"] = stressResults;
report["restartContinuation"] = restartReports; report["equivalence"] = equivalence; report["queryPlans"] = planResults;
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
var path = Path.Combine(outDir, $"summary-{label}.json");
await File.WriteAllTextAsync(path, json);
Console.WriteLine($"WROTE {path}");
return equivalence.Any(e => e["equal"] is false) || restartReports.Any(r => r.TryGetValue("equalAfterClose", out var e) && e is false) ? 1 : 0;

static string Fmt(object stats) => stats is Dictionary<string, double> s ? $"{s.GetValueOrDefault("p50")}/{s.GetValueOrDefault("p99")}/{s.GetValueOrDefault("max")}" : "?";

// ---------------------------------------------------------------- sustained 2x peak stress
async Task<List<Dictionary<string, object>>> StressAsync(DayData d, Staged staged, int[] perSecond)
{
    var results = new List<Dictionary<string, object>>();
    var open = U(d.Day, 9, 15);
    var maxRate = perSecond.Max();                       // observed maximum 1-second selected-token rate
    var prefix = new long[perSecond.Length + 1];
    for (var i = 0; i < perSecond.Length; i++) prefix[i + 1] = prefix[i] + perSecond[i];
    // Find the smallest time-compression c for which some window of (60 * c) data-seconds has mean rate * c >= 2 * maxRate (sustained 2x for 60 real seconds).
    int chosenC = 0, bestStart = 0;
    for (var c = 2; c <= 90 && chosenC == 0; c++)
    {
        var len = 60 * c;
        if (len >= perSecond.Length - 1900) break;       // leave 09:15-09:46 as the warm-up so a session exists
        double best = -1; var start = 0;
        for (var s = 1800; s + len < perSecond.Length; s++)
        {
            var mean = (prefix[s + len] - prefix[s]) / (double)len;
            if (mean > best) { best = mean; start = s; }
        }

        if (best * c >= 2d * maxRate) { chosenC = c; bestStart = start; }
    }

    if (chosenC == 0) { Console.WriteLine("STRESS: no sustained window reaches 2x the observed peak with <=90x compression; skipping."); return results; }
    var windowStart = open + TimeSpan.FromSeconds(bestStart);
    var windowEnd = windowStart + TimeSpan.FromSeconds(60 * chosenC);
    var observerDb = await FreshObserverAsync($"perf_observer_stress_{d.Day:yyyyMMdd}");
    await ClearTicksAsync();
    var result = await SimulateAsync(d, staged, observerDb, chosenC, windowStart, windowEnd, windowEnd + TimeSpan.FromSeconds(30 * chosenC), $"stress-{chosenC}x-window-{d.Day:yyyyMMdd}", closeSession: false);
    result.Summary["stressWindow"] = new { windowStartIst = (windowStart + ist).ToString("HH:mm:ss"), windowEndIst = (windowEnd + ist).ToString("HH:mm:ss"), compression = chosenC, observedMax1sRate = maxRate,
        targetInputRate = 2 * maxRate, achievedMeanInputRate = result.Summary["inputTicksPerRealSecond"] };
    results.Add(result.Summary);
    Console.WriteLine($"STRESS c={chosenC}x window {(windowStart + ist):HH:mm:ss}-{(windowEnd + ist):HH:mm:ss}: input/s={result.Summary["inputTicksPerRealSecond"]} (target>={2 * maxRate}) poll max={Fmt(result.Summary["pollMsAll"])} maxPending={result.Summary["maxPendingTicks"]} drainSec={result.Summary["drainSecondsAfterIngestEnd"]}");

    // Equivalence vs deterministic unthrottled replay: a fresh recovery over the same horizon verifies every persisted core row, sidecar and
    // commentary event against the recomputation (throws / mismatch counters on any difference).
    try
    {
        var horizon = result.EndData;                     // the clock of the last live poll: recovery then covers exactly the same ticks
        await LoadTicksAsync(windowEnd);
        var before = await DigestAsync(observerDb);
        var rec = await RecoveryAsync(d.Day, observerDb, "stress-equivalence-replay", horizon);
        var after = await DigestAsync(observerDb);
        var same = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after) && (int)after["projectionHealthRows"] == 0;
        equivalence.Add(new() { ["check"] = $"stress-vs-unthrottled-replay {d.Day:yyyy-MM-dd}", ["equal"] = same, ["recoveryMs"] = rec["totalWallMs"] });
        Console.WriteLine($"EQUIVALENCE stress-vs-unthrottled-replay: {(same ? "IDENTICAL (replay verified every persisted row)" : "DIFFERENT")}");
    }
    catch (Exception ex)
    {
        equivalence.Add(new() { ["check"] = $"stress-vs-unthrottled-replay {d.Day:yyyy-MM-dd}", ["equal"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message });
        Console.WriteLine($"EQUIVALENCE stress FAILED: {ex.GetType().Name}: {ex.Message}");
    }

    return results;
}

// ---------------------------------------------------------------- Dashboard + pinned quote
async Task<Dictionary<string, object>> DashboardAsync(string observerDb, DateOnly day, DayData d)
{
    var counter = new SqlCounter(); var sourceCounterLocal = new SqlCounter();
    var observerFactory = new LambdaFactory<AdaptiveObserverDbContext>(() => new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(DbCs(observerDb)).AddInterceptors(new CountingInterceptor(counter)).Options));
    var sourceFactory = new LambdaFactory<NiftySignalDbContext>(() => new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(DbCs(SourceDb)).AddInterceptors(new CountingInterceptor(sourceCounterLocal)).Options));
    var data = new AdaptiveObserverDataService(observerFactory, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveObserverDataService>.Instance);
    var commentary = new AdaptiveCommentaryDataService(observerFactory);
    var quote = new AdaptiveQuoteAsOfService(sourceFactory, observerFactory);
    await using var db = observerFactory.CreateDbContext();
    var sessionId = await db.Sessions.Select(x => x.Id).FirstAsync();
    var lastBar = await db.FutureBars.Where(x => x.SessionId == sessionId).MaxAsync(x => x.BarSeq);
    var tradeDate = (await db.Sessions.AsNoTracking().FirstAsync()).TradeDate;

    async Task<Dictionary<string, object>> Time(string name, Func<Task> action, int n)
    {
        var times = new List<double>(); var before = counter.Total;
        for (var i = 0; i < n; i++) { var sw = Stopwatch.StartNew(); await action(); times.Add(sw.Elapsed.TotalMilliseconds); }
        return new() { ["name"] = name, ["ms"] = Stats.Summary(times), ["sqlPerCall"] = Math.Round((counter.Total - before) / (double)n, 2) };
    }

    var rows = new List<Dictionary<string, object>>
    {
#if !BASELINE
        await Time("LoadRuntimeAsync (steady-state header refresh, after)", () => data.LoadRuntimeAsync(sessionId), 300),
#endif
        await Time("LoadSnapshotAsync(10)", async () => { await ((Func<Task>)(async () => { await data.LoadSnapshotAsync(10, captureSessionId: sessionId); }))(); }, 100),
        await Time("Commentary LoadLatestAsync(5)", () => commentary.LoadLatestAsync(5), 100),
    };
    // The previous header path (before): find today's session, read runtime, recompute ten-bar readiness. Only comparable on the session's own trade date.
    var legacy = await LegacyHeaderCostAsync(observerFactory, sessionId, counter);
    rows.Add(legacy);
    var quoteTimes = new List<double>(); var srcBefore = sourceCounterLocal.Total; var obsBefore = counter.Total;
    var bars = Enumerable.Range(1, lastBar).Where(x => x % Math.Max(1, lastBar / 40) == 0).ToList();
    foreach (var seq in bars) { var sw = Stopwatch.StartNew(); await quote.LoadAsync(sessionId, seq); quoteTimes.Add(sw.Elapsed.TotalMilliseconds); }
    rows.Add(new() { ["name"] = "AdaptiveQuoteAsOfService.LoadAsync (pinned capture quote resolution)", ["ms"] = Stats.Summary(quoteTimes), ["sourceSqlPerCall"] = Math.Round((sourceCounterLocal.Total - srcBefore) / (double)bars.Count, 2),
        ["observerSqlPerCall"] = Math.Round((counter.Total - obsBefore) / (double)bars.Count, 2) });
    Console.WriteLine($"DASHBOARD {day:yyyy-MM-dd}: " + string.Join(" | ", rows.Select(r => $"{r["name"]} p50={((Dictionary<string, double>)r["ms"])["p50"]}ms")));
    return new() { ["day"] = day.ToString("yyyy-MM-dd"), ["tradeDate"] = tradeDate.ToString(), ["measurements"] = rows };
}

async Task<Dictionary<string, object>> LegacyHeaderCostAsync(LambdaFactory<AdaptiveObserverDbContext> factory, long sessionId, SqlCounter counter)
{
    // Reproduces the 3-query header refresh that ran every 500 ms before the optimisation (session lookup, runtime read, ten-bar readiness).
    var times = new List<double>(); var before = counter.Total;
    for (var i = 0; i < 300; i++)
    {
        var sw = Stopwatch.StartNew();
        await using var db = factory.CreateDbContext();
        var session = await db.Sessions.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();
        var runtime = await db.Runtime.AsNoTracking().SingleAsync(x => x.SessionId == session.Id);
        _ = await db.FutureBars.AsNoTracking().Where(x => x.SessionId == session.Id && x.BarSeq <= runtime.LastCompletedBarSeq && x.BarSeq > runtime.LastCompletedBarSeq - 10).ToListAsync();
        times.Add(sw.Elapsed.TotalMilliseconds);
    }

    return new() { ["name"] = "Legacy header refresh (before: session + runtime + ten-bar readiness)", ["ms"] = Stats.Summary(times), ["sqlPerCall"] = Math.Round((counter.Total - before) / 300d, 2) };
}

// ---------------------------------------------------------------- EXPLAIN (ANALYZE, BUFFERS) on the real source
async Task<List<string>> QueryPlansAsync()
{
    var plans = new List<string>();
    var day = busiestDay ?? allDays.Last();
    var fut = await src.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Future).FirstAsync();
    var optToken = await src.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && i.InstrumentType == InstrumentType.Option).Select(i => i.Token).FirstAsync();
    var tokens = await src.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY" && (i.InstrumentType == InstrumentType.Option || i.InstrumentType == InstrumentType.Future)).Select(i => i.Token).Distinct().ToArrayAsync();
    var lo = U(day, 9, 15).AddMinutes(-1); var hi = U(day, 15, 35); var boundary = U(day, 13, 0);
    var maxId = await src.Ticks.AsNoTracking().Where(t => t.ExchangeTimestamp >= lo && t.ExchangeTimestamp < hi).MaxAsync(t => t.Id);

    async Task<string> Explain(string title, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(sourceBuilder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, conn);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var sb = new StringBuilder(); await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) sb.AppendLine(rd.GetString(0));
        return $"### {title}\n{sb}";
    }

    plans.Add(await Explain("Live read: ReadRawAfterIdAsync (Id > cursor near the end of the day, session tokens)",
        "SELECT t.\"Token\", t.\"Id\", t.\"ExchangeTimestamp\", t.\"ReceivedAt\", t.\"LastPrice\", t.\"Volume\", t.\"OpenInterest\" FROM ticks t WHERE t.\"Id\" > @after AND t.\"Token\" = ANY(@tokens) AND t.\"ExchangeTimestamp\" >= @lo AND t.\"ReceivedAt\" >= @lo AND t.\"ExchangeTimestamp\" < @hi AND t.\"ReceivedAt\" < @hi ORDER BY t.\"Id\"",
        ("after", maxId - 400), ("tokens", tokens), ("lo", lo), ("hi", hi)));
    var dayStart = U(day, 0, 0).AddHours(-5.5);
    plans.Add(await Explain("Pinned quote probe A: greatest eligible ExchangeTimestamp",
        "SELECT t.\"Id\", t.\"ExchangeTimestamp\", t.\"ReceivedAt\" FROM ticks t WHERE t.\"Token\" = @tok AND t.\"LastPrice\" > 0 AND t.\"ExchangeTimestamp\" >= @ds AND t.\"ExchangeTimestamp\" <= @b AND t.\"ReceivedAt\" <= @b ORDER BY t.\"ExchangeTimestamp\" DESC, t.\"Id\" DESC LIMIT 1",
        ("tok", optToken), ("ds", dayStart), ("b", boundary)));
    plans.Add(await Explain("Pinned quote probe B: greatest eligible ReceivedAt within [availability of probe A, boundary]",
        "SELECT t.\"Id\", t.\"ExchangeTimestamp\", t.\"ReceivedAt\" FROM ticks t WHERE t.\"Token\" = @tok AND t.\"LastPrice\" > 0 AND t.\"ExchangeTimestamp\" >= @ds AND t.\"ExchangeTimestamp\" <= @b AND t.\"ReceivedAt\" <= @b AND t.\"ReceivedAt\" >= @floor ORDER BY t.\"ReceivedAt\" DESC, t.\"Id\" DESC LIMIT 1",
        ("tok", optToken), ("ds", dayStart), ("b", boundary), ("floor", boundary.AddSeconds(-3))));
    plans.Add(await Explain("Pinned quote day-open baseline (first tick of the day available by the boundary)",
        "SELECT t.\"LastPrice\" FROM ticks t WHERE t.\"Token\" = @tok AND t.\"ExchangeTimestamp\" >= @ds AND t.\"ExchangeTimestamp\" <= @b AND t.\"ReceivedAt\" <= @b ORDER BY t.\"ExchangeTimestamp\", t.\"Id\" LIMIT 1",
        ("tok", fut.Token), ("ds", dayStart), ("b", boundary)));
    plans.Add(await Explain("Pinned quote probe B on a sparse token with a wide floor (worst case: no recent tick)",
        "SELECT t.\"Id\" FROM ticks t WHERE t.\"Token\" = @tok AND t.\"LastPrice\" > 0 AND t.\"ExchangeTimestamp\" >= @ds AND t.\"ExchangeTimestamp\" <= @b AND t.\"ReceivedAt\" <= @b AND t.\"ReceivedAt\" >= @floor ORDER BY t.\"ReceivedAt\" DESC, t.\"Id\" DESC LIMIT 1",
        ("tok", optToken), ("ds", dayStart), ("b", boundary), ("floor", boundary.AddMinutes(-30))));
    foreach (var p in plans) Console.WriteLine(p.Split('\n').FirstOrDefault() + " | " + p.Split('\n').FirstOrDefault(l => l.Contains("Execution Time")));
    return plans;
}
