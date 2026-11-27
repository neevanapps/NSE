// Checkpoint-assisted recovery with Top-5 depth on the future AND the options, over one recorded session (stage "certify").
//
// Three runs of the same recorded day through the production Observer services, on an isolated loopback PostgreSQL cluster (the historical source is only read):
//   R  full replay: a fresh process recovers the whole day, checkpoints off (the reference for values, and the full-session recovery duration);
//   U  uninterrupted live processing, checkpoints off (the reference for publications);
//   C  live processing with checkpoints, the process dropped mid-session, a new process restores a checkpoint, catches up and continues to the close.
// Optionally F: the same restart point with checkpoints off (what a restart costs without them).
//
// Clocks: the replay of live hours is accelerated, so while C processes live polls the checkpoint store is given the SIMULATED session clock - a checkpoint is due after the configured number of seconds of
// market time, as in production. While C recovers after the restart the store is given the wall clock, because a recovery replay runs in real time in production too.
//
// The result says CRITERIA MET only if the day carries five-level depth for the future and the options, covers the session, the restart really continued from a checkpoint (not a full replay), it returned
// to Live within the limit, and every comparison is equal. Whether the day is representative is the owner's statement; synthetic data never certifies anything.

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
using NiftySignal.Persistence;

static class CheckpointCertification
{
    const string SourceDb = "perf_cert_source", Template = "perf_cert_template";
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static async Task<int> RunAsync(NpgsqlConnectionStringBuilder source, NpgsqlConnectionStringBuilder admin, string outDir, string label)
    {
        // The scratch cluster is dropped into and written to: loopback only.
        if (admin.Host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            Console.Error.WriteLine("Refusing to run: ADAPTIVE_PERF_ADMIN must be a loopback PostgreSQL (127.0.0.1, localhost or ::1).");
            return 2;
        }

        if (!DateOnly.TryParse(Env("ADAPTIVE_PERF_CERTIFY_DAY"), out var day))
        {
            Console.Error.WriteLine("Set ADAPTIVE_PERF_CERTIFY_DAY=yyyy-MM-dd (a recorded session with five-level depth for the future and the options).");
            return 2;
        }

        DateTimeOffset At(string time) => new DateTimeOffset(day.ToDateTime(TimeOnly.Parse(time)), Ist).ToUniversalTime();
        var start = At("09:30:05");
        var crashAt = At(Env("ADAPTIVE_PERF_CERTIFY_RESTART_IST", "14:30:00"));
        var horizon = At(Env("ADAPTIVE_PERF_CERTIFY_END_IST", "15:35:30"));
        var downtime = TimeSpan.FromSeconds(int.Parse(Env("ADAPTIVE_PERF_CERTIFY_DOWNTIME_SECONDS", "60")));
        var step = TimeSpan.FromMilliseconds(int.Parse(Env("ADAPTIVE_PERF_CERTIFY_POLL_MS", "1000")));
        var limit = TimeSpan.FromSeconds(int.Parse(Env("ADAPTIVE_PERF_CERTIFY_LIMIT_SECONDS", "300")));
        var interval = int.Parse(Env("ADAPTIVE_PERF_CHECKPOINT_INTERVAL_SECONDS", "60"));
        if (!(start < crashAt && crashAt + downtime < horizon)) { Console.Error.WriteLine("The restart point (plus downtime) must lie between 09:30:05 and the end time."); return 2; }

        string Db(string name) => new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name }.ConnectionString;
        DbContextOptions<AdaptiveObserverDbContext> ObserverOptions(string name) => new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(Db(name)).Options;
        DbContextOptions<NiftySignalDbContext> SourceOptions(string connection) => new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(connection).Options;

        async Task SqlAsync(string database, params string[] statements)
        {
            await using var c = new NpgsqlConnection(Db(database));
            await c.OpenAsync();
            foreach (var sql in statements)
                await using (var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 900 }) await cmd.ExecuteNonQueryAsync();
        }

        var report = new Dictionary<string, object?>
        {
            ["label"] = label, ["day"] = day.ToString("yyyy-MM-dd"), ["commit"] = Env("ADAPTIVE_PERF_COMMIT", "unknown"), ["generatedUtc"] = DateTimeOffset.UtcNow,
            ["restartIst"] = (crashAt + Ist).ToString("HH:mm:ss"), ["downtimeSeconds"] = downtime.TotalSeconds, ["pollStepMs"] = step.TotalMilliseconds,
            ["checkpointIntervalSeconds"] = interval, ["recoveryLimitSeconds"] = limit.TotalSeconds,
            ["machine"] = new { Environment.ProcessorCount, Os = Environment.OSVersion.ToString(), Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, ServerGc = System.Runtime.GCSettings.IsServerGC },
        };
        var failures = new List<string>();

        // ---------------------------------------------------------------- stage the day with its stored depth, unchanged
        await using var original = new NiftySignalDbContext(SourceOptions(source.ConnectionString));
        var instruments = await original.Instruments.AsNoTracking().Where(i => i.AsOfDate == day && i.Underlying == "NIFTY").ToListAsync();
        var future = instruments.Where(i => i.InstrumentType == InstrumentType.Future).OrderBy(i => i.ExpiryDate).FirstOrDefault();
        var optionRows = instruments.Where(i => i.InstrumentType == InstrumentType.Option && i.ExpiryDate != null && i.StrikePrice != null).ToList();
        if (future is null || optionRows.Count == 0) { Console.Error.WriteLine($"No NIFTY future and options recorded for {day:yyyy-MM-dd}."); return 2; }
        var weekly = optionRows.Min(x => x.ExpiryDate!.Value);
        var chain = optionRows.Where(x => x.ExpiryDate == weekly).ToList();
        var spot = instruments.Where(i => i.InstrumentType == InstrumentType.Index).GroupBy(i => i.Token).Select(g => g.First()).ToList();
        var kept = chain.Append(future).Concat(spot.Count == 1 ? spot : []).ToList();
        var tokens = kept.Select(i => i.Token).Distinct().ToArray();

        foreach (var name in new[] { SourceDb, Template })
            await SqlAsync("postgres", $"DROP DATABASE IF EXISTS {name} WITH (FORCE)", $"CREATE DATABASE {name}");
        await using (var db = new NiftySignalDbContext(SourceOptions(Db(SourceDb))))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var i in kept) { i.Id = 0; db.Instruments.Add(i); }
            await db.SaveChangesAsync();
        }

        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
        var to = from.AddDays(1);
        var dayRows = original.Ticks.AsNoTracking().Where(t => tokens.Contains(t.Token) && t.ReceivedAt >= from && t.ReceivedAt < to);
        var received = new List<DateTimeOffset>();
        long futureRows = 0, futureFive = 0, optionRowsSeen = 0, optionFive = 0;
        var optionTokensWithDepth = new HashSet<string>(StringComparer.Ordinal);
        var first = DateTimeOffset.MaxValue; var last = DateTimeOffset.MinValue;
        if (!await dayRows.AnyAsync()) { Console.Error.WriteLine($"No ticks recorded for {day:yyyy-MM-dd}."); return 2; }
        var cursor = await dayRows.MinAsync(t => t.Id) - 1;
        while (true)
        {
            var page = await dayRows.Where(t => t.Id > cursor).OrderBy(t => t.Id).Take(20_000).ToListAsync();
            if (page.Count == 0) break;
            cursor = page[^1].Id;
            await using var db = new NiftySignalDbContext(SourceOptions(Db(SourceDb)));
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (var t in page)
            {
                var five = HasFiveLevels(t.Depth);
                if (t.Token == future.Token) { futureRows++; if (five) futureFive++; }
                else if (chain.Any(o => o.Token == t.Token)) { optionRowsSeen++; if (five) { optionFive++; optionTokensWithDepth.Add(t.Token); } }
                received.Add(t.ReceivedAt);
                if (t.ReceivedAt < first) first = t.ReceivedAt;
                if (t.ReceivedAt > last) last = t.ReceivedAt;
                t.Id = received.Count;                       // renumbered in source Id order, so Ids are dense as the feeder expects
                db.Ticks.Add(t);
            }

            await db.SaveChangesAsync();
        }

        await SqlAsync(SourceDb, "CREATE TABLE ticks_all (LIKE ticks)", "INSERT INTO ticks_all SELECT * FROM ticks", "TRUNCATE ticks", "CREATE INDEX ix_ticks_all_id ON ticks_all (\"Id\")");
        var inputs = new Dictionary<string, object>
        {
            ["ticks"] = received.Count, ["firstIst"] = (first + Ist).ToString("HH:mm:ss"), ["lastIst"] = (last + Ist).ToString("HH:mm:ss"),
            ["futureTicks"] = futureRows, ["futureTicksWithFiveLevels"] = futureFive, ["optionTicks"] = optionRowsSeen, ["optionTicksWithFiveLevels"] = optionFive,
            ["optionTokens"] = chain.Count, ["optionTokensWithFiveLevels"] = optionTokensWithDepth.Count, ["spot"] = spot.Count == 1,
        };
        report["inputs"] = inputs;
        Console.WriteLine("STAGED " + JsonSerializer.Serialize(inputs));
        if (futureFive == 0 || optionFive == 0) failures.Add("the day carries no five-level depth for the future and the options");
        if (first > At("09:20:00") || last < At("15:25:00")) failures.Add("the recorded ticks do not cover 09:20 to 15:25 IST");

        // Prior-session history through the real production bootstrap (reads the source only).
        await using (var obs = new AdaptiveObserverDbContext(ObserverOptions(Template)))
        {
            await obs.Database.MigrateAsync();
            await new AdaptiveHistoricalBootstrapService(new AdaptiveSourceTickReader(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveHistoricalBootstrapService>.Instance)
                .EnsurePriorSessionsAsync(original, obs, day, default);
        }

        var staged = new Staged(new DayData(day, future, chain, weekly, [], tokens, [], spot.Count == 1 ? spot[0] : null), received.ToArray());
        using var recorder = new StageRecorder(new SqlCounter());

        // ---------------------------------------------------------------- one fresh "process"
        (AdaptiveObserverWorker Worker, ServiceProvider Provider, RestoreLog Log) Process(string observer, bool checkpoints, SessionClock clock)
        {
            var log = new RestoreLog();
            var s = new ServiceCollection();
            s.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(log));
            s.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(Db(SourceDb)));
            s.AddDbContext<AdaptiveObserverDbContext>(o => o.UseNpgsql(Db(observer)));
            s.AddOptions<PricingOptions>();
            s.Configure<DashboardPushOptions>(o => o.HubUrl = "http://127.0.0.1:1/hubs/never-connected");
            var recovery = new AdaptiveRecoveryOptions { CheckpointsEnabled = checkpoints, CheckpointIntervalSeconds = interval };
            s.AddSingleton(Options.Create(recovery));
            s.AddSingleton(Options.Create(new Top5MicrostructureOptions { Enabled = true, OptionsEnabled = true }));
            s.AddSingleton<AdaptiveSourceTickReader>(); s.AddSingleton<AdaptiveDepthTickReader>(); s.AddSingleton<FuturesDepth5Gateway>();
            s.AddSingleton<AdaptiveHistoricalBootstrapService>(); s.AddSingleton<AdaptiveSessionCoordinator>(); s.AddSingleton<AdaptiveObserverPersistence>();
            s.AddSingleton<AdaptiveSupplementalPersistence>(); s.AddSingleton<AdaptiveSessionSupplementalService>(); s.AddSingleton<AdaptiveWeak2ObservationService>();
            s.AddSingleton(sp => new AdaptiveCheckpointStore(sp.GetRequiredService<AdaptiveObserverPersistence>(), sp.GetRequiredService<ILogger<AdaptiveCheckpointStore>>(),
                sp.GetRequiredService<IOptions<AdaptiveRecoveryOptions>>(), clock));
            s.AddSingleton<AdaptiveStateRecoveryService>(); s.AddSingleton<AdaptiveEndedSessionRecoveryService>(); s.AddSingleton<DashboardPushClient>(); s.AddSingleton<AdaptiveObserverWorker>();
            var provider = s.BuildServiceProvider();
            return (provider.GetRequiredService<AdaptiveObserverWorker>(), provider, log);
        }

        async Task<(AdaptiveObserverWorker.LiveState Live, int Attempts)> RecoverToLiveAsync(AdaptiveObserverWorker worker, DateTimeOffset now)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return (await worker.TryStartOrRecoverAsync(day, now, default) ?? throw new InvalidOperationException("The session did not start."), attempt);
                }
                catch (AdaptiveRecoveryTimeoutException ex) when (attempt < 10) { Console.WriteLine($"recovery attempt {attempt} timed out: {ex.Message}"); }
            }
        }

        async Task CloneAsync(string name)
        {
            NpgsqlConnection.ClearAllPools();
            await SqlAsync("postgres", $"DROP DATABASE IF EXISTS {name} WITH (FORCE)", $"CREATE DATABASE {name} TEMPLATE {Template}");
        }

        async Task<Dictionary<string, object?>> MeasuredAsync(Func<Task<Dictionary<string, object?>>> work)
        {
            recorder.Reset();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var heap = GC.GetTotalMemory(true);
            using var memory = new MemorySampler();
            var sw = Stopwatch.StartNew();
            var info = await work();
            info["wallSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 2);
            info["peakWorkingSetMb"] = Math.Round(memory.PeakWorkingSet / 1048576d, 1);
            info["managedHeapBeforeMb"] = Math.Round(heap / 1048576d, 1);
            info["peakManagedHeapMb"] = Math.Round(memory.PeakManagedHeap / 1048576d, 1);
            return info;
        }

        // ---- R: full replay of the whole day, checkpoints off
        await CloneAsync("perf_cert_full");
        await SqlAsync(SourceDb, "TRUNCATE ticks", "INSERT INTO ticks SELECT * FROM ticks_all");
        var fullProcess = Process("perf_cert_full", false, new SessionClock());
        AdaptiveObserverWorker.LiveState fullLive = null!;
        report["fullReplay"] = await MeasuredAsync(async () =>
        {
            var (live, attempts) = await RecoverToLiveAsync(fullProcess.Worker, horizon);
            fullLive = live;
            return new() { ["attempts"] = attempts, ["completedBars"] = live.Engine.FutureBars.Count(x => x.IsComplete) };
        });
        var sessionFull = fullLive.Context.Session.Id;
        await fullProcess.Worker.CloseCurrentSessionAsync(fullLive, default);
        var full = await DumpAsync(ObserverOptions("perf_cert_full"), sessionFull);
        await fullProcess.Provider.DisposeAsync();
        Console.WriteLine("R full replay " + JsonSerializer.Serialize(report["fullReplay"]));

        // ---- live processing, optionally dropped at the restart point
        async Task<(Dictionary<string, List<string>> Rows, Dictionary<string, object?> Info)> LiveRunAsync(string name, bool checkpoints, bool crash)
        {
            await CloneAsync(name);
            await SqlAsync(SourceDb, "TRUNCATE ticks");
            var feeder = new Feeder(staged, Db(SourceDb));
            var clock = new SessionClock { Now = start };
            var process = Process(name, checkpoints, clock);
            await feeder.FeedAsync(start);
            var (live, _) = await RecoverToLiveAsync(process.Worker, start);
            recorder.Reset();
            var info = new Dictionary<string, object?>();
            var now = start;
            var crashed = false;
            while (now < horizon)
            {
                now += step;
                if (now > horizon) now = horizon;
                if (crash && !crashed && now >= crashAt)
                {
                    crashed = true;
                    var saves = recorder.DurationsMs.GetValueOrDefault("checkpoint.save") ?? [];
                    info["checkpointSavesBeforeRestart"] = saves.Count;
                    info["checkpointSaveMs"] = Stats.Summary(saves);
                    int publishedBefore; long? newestBytes = null; int stored;
                    await using (var db = new AdaptiveObserverDbContext(ObserverOptions(name)))
                    {
                        publishedBefore = await db.BarPublications.Select(x => (int?)x.BarSeq).MaxAsync() ?? 0;
                        stored = await db.Checkpoints.CountAsync();
                        if (stored > 0) newestBytes = await db.Checkpoints.OrderByDescending(x => x.Id).Select(x => (long)x.Payload.Length).FirstAsync();
                    }

                    info["checkpointsStoredAtRestart"] = stored;
                    info["newestCheckpointBytes"] = newestBytes;
                    info["lastPublishedBarBeforeRestart"] = publishedBefore;

                    // abrupt process death: no close, no flush
                    live = null!;
                    await process.Provider.DisposeAsync();
                    NpgsqlConnection.ClearAllPools();
                    now += downtime;
                    await feeder.FeedAsync(now);
                    var restartAt = now;
                    clock = new SessionClock { Now = restartAt, Wall = true };
                    info["restart"] = await MeasuredAsync(async () =>
                    {
                        process = Process(name, checkpoints, clock);
                        var (recovered, attempts) = await RecoverToLiveAsync(process.Worker, restartAt);
                        live = recovered;
                        await using var db = new AdaptiveObserverDbContext(ObserverOptions(name));
                        var runtime = await db.Runtime.AsNoTracking().SingleAsync(x => x.SessionId == recovered.Context.Session.Id);
                        var restores = recorder.DurationsMs.GetValueOrDefault("checkpoint.restore") ?? [];
                        return new()
                        {
                            ["attempts"] = attempts, ["status"] = runtime.RuntimeStatus.ToString(), ["continuedFromCheckpoint"] = process.Log.Checkpoint is not null, ["checkpointMessage"] = process.Log.Checkpoint,
                            ["ticksReplayed"] = runtime.RecoveryTicksProcessed, ["ticksVisibleAtRestart"] = feeder.Inserted, ["checkpointRestoreMs"] = Math.Round(restores.Sum(), 1),
                            ["checkpointSavesDuringRecovery"] = (recorder.DurationsMs.GetValueOrDefault("checkpoint.save") ?? []).Count,
                            ["completedBars"] = recovered.Engine.FutureBars.Count(x => x.IsComplete),
                        };
                    });
                    clock.Wall = false;
                    Console.WriteLine($"{name} restart " + JsonSerializer.Serialize(info["restart"]));
                    continue;
                }

                clock.Now = now;
                await feeder.FeedAsync(now);
                await process.Worker.PollLiveAsync(live, now, default);
            }

            await feeder.FeedAsync(horizon);
            await process.Worker.PollLiveAsync(live, horizon, default, forceRuntimeFlush: true);
            // Tick accounting: where reading and processing ended, and what is still waiting. A lost or repeated tick changes these or the bars.
            info["end"] = JsonSerializer.Serialize(new
            {
                live.LastFetchedRawId, live.LastProcessedTickId, live.LastProcessedAvailableAt, live.Engine.PartialBar.AccumulatedVolume, live.Engine.PartialBar.StartedAtUtc,
                Pending = string.Join(',', live.Pending.OrderBy(x => x.Tick.AvailableAt).ThenBy(x => x.Tick.Id).Select(x => x.Tick.Id)), Bars = live.Engine.FutureBars.Count(x => x.IsComplete),
            });
            info["ticksFed"] = feeder.Inserted;
            var session = live.Context.Session.Id;
            await process.Worker.CloseCurrentSessionAsync(live, default);
            var rows = await DumpAsync(ObserverOptions(name), session);
            await process.Provider.DisposeAsync();
            return (rows, info);
        }

        var uninterrupted = await LiveRunAsync("perf_cert_live", false, false);
        report["uninterrupted"] = uninterrupted.Info;
        Console.WriteLine("U uninterrupted done");
        var restored = await LiveRunAsync("perf_cert_checkpoint", true, true);
        report["checkpointRestart"] = restored.Info;
        if (Env("ADAPTIVE_PERF_CERTIFY_COMPARE_FULL_REPLAY", "1") == "1")
        {
            var without = await LiveRunAsync("perf_cert_nocheckpoint", false, true);
            report["restartWithoutCheckpoints"] = without.Info;
        }

        // ---------------------------------------------------------------- verdict
        var restart = (Dictionary<string, object?>)restored.Info["restart"]!;
        if (!(bool)restart["continuedFromCheckpoint"]!) failures.Add("the restart did NOT continue from a checkpoint (full-replay fallback)");
        else if ((long)Convert.ToInt64(restart["ticksReplayed"]) >= Convert.ToInt64(restart["ticksVisibleAtRestart"])) failures.Add("the restart replayed as many ticks as the whole day so far");
        if ((string)restart["status"]! != "Live") failures.Add($"the restarted process ended recovery as {restart["status"]}");
        if ((double)restart["wallSeconds"]! > limit.TotalSeconds) failures.Add($"recovery took {restart["wallSeconds"]} s (limit {limit.TotalSeconds} s)");
        if ((string)restored.Info["end"]! != (string)uninterrupted.Info["end"]!) failures.Add("tick accounting at the end differs from the uninterrupted run");

        var parity = new Dictionary<string, object>
        {
            ["checkpointRestartVsFullReplay"] = Compare(full, restored.Rows, publications: false, 0),
            ["checkpointRestartVsUninterrupted"] = Compare(uninterrupted.Rows, restored.Rows, publications: true, Convert.ToInt32(restored.Info["lastPublishedBarBeforeRestart"])),
        };
        report["parity"] = parity;
        foreach (var (name, value) in parity)
        {
            var different = ((List<TableResult>)value).Where(t => !t.Equal).Select(t => t.Table).ToList();
            if (different.Count > 0) failures.Add($"{name}: differences in {string.Join(", ", different)}");
        }

        var bars = restored.Rows["bars"].Count;
        foreach (var table in new[] { "depth5", "optionsDepth5", "optionsDepth5Attestations", "strikes", "strikeAttestations", "closeAtm", "publications" })
            if (restored.Rows[table].Count == 0) failures.Add($"no {table} rows were produced");
        foreach (var table in new[] { "strikeAttestations", "publications" })
            if (restored.Rows[table].Count != bars) failures.Add($"{table}: {restored.Rows[table].Count} rows for {bars} bars");

        report["failures"] = failures;
        report["verdict"] = failures.Count == 0 ? "CRITERIA MET on the supplied day" : "NOT CERTIFIED";
        var path = Path.Combine(outDir, $"certification-{label}-{day:yyyyMMdd}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{report["verdict"]}{(failures.Count == 0 ? "" : ": " + string.Join("; ", failures))}");
        Console.WriteLine($"WROTE {path}");
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>Bid and ask, price and quantity, all five levels present.</summary>
    static bool HasFiveLevels(MarketDepth? d) => d is
    {
        Bid1Price: > 0, Bid1Qty: > 0, Bid2Price: > 0, Bid2Qty: > 0, Bid3Price: > 0, Bid3Qty: > 0, Bid4Price: > 0, Bid4Qty: > 0, Bid5Price: > 0, Bid5Qty: > 0,
        Ask1Price: > 0, Ask1Qty: > 0, Ask2Price: > 0, Ask2Qty: > 0, Ask3Price: > 0, Ask3Qty: > 0, Ask4Price: > 0, Ask4Qty: > 0, Ask5Price: > 0, Ask5Qty: > 0,
    };

    sealed record TableResult(string Table, int RowsA, int RowsB, bool Equal, string HashA, string HashB, List<string> FirstDifferences);

    /// <summary>Row-by-row comparison. Publications: the same bars exactly once; the only accepted difference is a bar published after the restart point arriving as unverified (the recovered-bar rule).</summary>
    static List<TableResult> Compare(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b, bool publications, int publishedBeforeRestart)
    {
        static string Hash(List<string> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))))[..16];
        static int Bar(string row) => int.Parse(row.Split('|').First(p => p.StartsWith("BarSeq=", StringComparison.Ordinal))[7..]);
        var results = new List<TableResult>();
        foreach (var (table, rows) in a)
        {
            var other = b[table];
            var equal = rows.SequenceEqual(other);
            var differences = new List<string>();
            if (table == "publications")
            {
                if (!publications) continue;                  // a pure replay publishes every bar as recovered: not comparable
                equal = rows.Select(Bar).SequenceEqual(other.Select(Bar)) && other.Select(Bar).Distinct().Count() == other.Count;
                foreach (var (x, y) in rows.Zip(other).Where(p => p.First != p.Second))
                {
                    differences.Add($"{x} -> {y}");
                    if (Bar(y) <= publishedBeforeRestart || !y.Contains("Verified=false", StringComparison.Ordinal)) equal = false;
                }
            }
            else if (!equal)
            {
                differences.AddRange(rows.Except(other).Take(3).Select(x => "only A: " + x));
                differences.AddRange(other.Except(rows).Take(3).Select(x => "only B: " + x));
            }

            results.Add(new(table, rows.Count, other.Count, equal, Hash(rows), Hash(other), differences.Take(12).ToList()));
        }

        return results;
    }

    /// <summary>Every persisted market row of the session, without surrogate keys and write instants.</summary>
    static async Task<Dictionary<string, List<string>>> DumpAsync(DbContextOptions<AdaptiveObserverDbContext> options, long sessionId)
    {
        await using var db = new AdaptiveObserverDbContext(options);
        static bool Written(PropertyInfo p) => p.Name == "Id" || (p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?))
            && new[] { "Created", "Computed", "Attested", "Verified", "Published", "Recorded", "Updated", "Persisted", "Occurred", "Detected" }.Any(p.Name.Contains);
        async Task<List<string>> Rows<T>(IQueryable<T> query) where T : class
        {
            var properties = typeof(T).GetProperties().Where(p => !Written(p)).OrderBy(p => p.Name).ToArray();
            var session = typeof(T).GetProperty("SessionId");
            return (await query.AsNoTracking().ToListAsync()).Where(row => session is null || Convert.ToInt64(session.GetValue(row)) == sessionId)
                .Select(row => string.Join('|', properties.Select(p => $"{p.Name}={JsonSerializer.Serialize(p.GetValue(row))}"))).Order(StringComparer.Ordinal).ToList();
        }

        // Prior-session seeds share these tables; only the certified session is compared.
        return new()
        {
            ["bars"] = await Rows(db.FutureBars.Where(x => x.SessionId == sessionId)), ["rolling"] = await Rows(db.RollingStates.Where(x => x.SessionId == sessionId)),
            ["bands"] = await Rows(db.OptionBandBars.Where(x => x.SessionId == sessionId)), ["residuals"] = await Rows(db.OptionResidualBars.Where(x => x.SessionId == sessionId)),
            ["futures"] = await Rows(db.FuturesSupplemental.Where(x => x.SessionId == sessionId)), ["basis"] = await Rows(db.BasisSupplemental.Where(x => x.SessionId == sessionId)),
            ["options"] = await Rows(db.OptionsSupplemental.Where(x => x.SessionId == sessionId)), ["strikes"] = await Rows(db.OptionStrikeBars.Where(x => x.SessionId == sessionId)),
            ["strikeAttestations"] = await Rows(db.StrikeAttestations.Where(x => x.SessionId == sessionId)), ["closeAtm"] = await Rows(db.BarCloseAtm.Where(x => x.SessionId == sessionId)),
            ["depth5"] = await Rows(db.FuturesDepth5.Where(x => x.SessionId == sessionId)), ["optionsDepth5"] = await Rows(db.OptionsDepth5.Where(x => x.SessionId == sessionId)),
            ["optionsDepth5Attestations"] = await Rows(db.OptionsDepth5Attestations.Where(x => x.SessionId == sessionId)),
            ["observations"] = await Rows(db.Weak2Observations.Where(x => x.SessionId == sessionId)), ["anchors"] = await Rows(db.ResidualAnchorComponents.Where(x => x.SessionId == sessionId)),
            ["health"] = await Rows(db.ProjectionHealth.Where(x => x.SessionId == sessionId)), ["publications"] = await Rows(db.BarPublications.Where(x => x.SessionId == sessionId)),
        };
    }

    /// <summary>The checkpoint store's clock: the simulated session instant during accelerated live processing, the wall clock during a recovery.</summary>
    sealed class SessionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public bool Wall { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Wall ? (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency)) : Now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => Wall ? DateTimeOffset.UtcNow : Now;
    }

    /// <summary>Prints errors, and keeps the recovery service's statement that it continued from a checkpoint - the evidence that separates a restore from a full-replay fallback.</summary>
    sealed class RestoreLog : ILoggerProvider
    {
        public string? Checkpoint { get; private set; }
        public ILogger CreateLogger(string category) => new Logger(this, category);
        public void Dispose() { }

        sealed class Logger(RestoreLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
            {
                var text = format(state, exception);
                if (category == typeof(AdaptiveStateRecoveryService).FullName && text.Contains("continues from checkpoint", StringComparison.Ordinal)) { owner.Checkpoint = text; Console.WriteLine(text); }
                if (level >= LogLevel.Error) Console.Error.WriteLine($"{category}: {text} {exception?.Message}");
            }
        }
    }
}
