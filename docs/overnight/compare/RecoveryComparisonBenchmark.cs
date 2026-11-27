// Bench-only overlay, NOT part of the release source tree (docs/ is excluded from the source identity).
// Run-RecoveryComparison.ps1 copies it into a scratch COPY of the baseline (78d9b8b) and of the candidate test project. It compiles against both and changes no production code.
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;
using Npgsql;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class RecoveryComparisonBenchmark
{
    internal sealed class Sql : DbCommandInterceptor
    {
        public long Commands, Writes;
        public double ExecuteMs, ReadMs;
        public readonly ConcurrentDictionary<string, DbCommandCopy> Distinct = new();
        readonly object _gate = new();

        void Done(DbCommand command, CommandExecutedEventData data)
        {
            lock (_gate)
            {
                Commands++;
                ExecuteMs += data.Duration.TotalMilliseconds;
                var head = command.CommandText.TrimStart();
                if (head.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || head.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || head.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) Writes++;
            }

            if (Distinct.Count < 12 && !Distinct.ContainsKey(command.CommandText))
                Distinct.TryAdd(command.CommandText, new DbCommandCopy(command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray()));
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        { Done(command, eventData); return ValueTask.FromResult(result); }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        { Done(command, eventData); return ValueTask.FromResult(result); }

        public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
        { Done(command, eventData); return ValueTask.FromResult(result); }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        { lock (_gate) ReadMs += eventData.Duration.TotalMilliseconds; return result; }

        public (long Commands, long Writes, double ExecuteMs, double ReadMs) Take()
        {
            lock (_gate)
            {
                var value = (Commands, Writes, ExecuteMs, ReadMs);
                Commands = 0; Writes = 0; ExecuteMs = 0; ReadMs = 0;
                return value;
            }
        }
    }

    internal sealed record DbCommandCopy(string Text, NpgsqlParameter[] Parameters);

    // Connection opens and the time they take: command interceptors do not include it.
    internal sealed class Opens : DbConnectionInterceptor
    {
        long _count;
        double _ms;
        readonly object _gate = new();

        void Done(ConnectionEndEventData data) { lock (_gate) { _count++; _ms += data.Duration.TotalMilliseconds; } }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Done(eventData);

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        { Done(eventData); return Task.CompletedTask; }

        public (long Count, double Ms) Take()
        {
            lock (_gate) { var value = (_count, _ms); _count = 0; _ms = 0; return value; }
        }
    }

    // The test helper turns pooling off for its temporary database; production uses Npgsql's default (on). NS_CMP_POOLING=1 restores the default.
    internal static string ObserverConnection(TempAdaptiveDatabase database) => Environment.GetEnvironmentVariable("NS_CMP_POOLING") == "1"
        ? new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString
        : database.ConnectionString;

    internal sealed class Lines<T> : ILogger<T>
    {
        public readonly ConcurrentQueue<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) Messages.Enqueue($"{level}: {formatter(state, exception)}"); }
    }

    [Fact]
    public async Task Compare()
    {
        var outPath = Environment.GetEnvironmentVariable("NS_CMP_OUT");
        if (outPath is null || !AdaptivePg.Available) return;
        var label = Environment.GetEnvironmentVariable("NS_CMP_LABEL")!;
        var mode = Environment.GetEnvironmentVariable("NS_DEPTH5_OPT")!;
        var staged = Environment.GetEnvironmentVariable("NS_CMP_STAGED") == "1";
        var day = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_DAY")!);
        var fut = Environment.GetEnvironmentVariable("NS_DEPTH5_FUT")!;
        var weekly = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_WEEKLY")!);
        var barVol = long.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_BARVOL")!);
        var conn = Environment.GetEnvironmentVariable("NS_DEPTH5_CONN")!;
        var through = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 40)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();

        var sourceSql = new Sql();
        var observerSql = new Sql();
        var observerOpens = new Opens();
        var pooled = Environment.GetEnvironmentVariable("NS_CMP_POOLING") == "1";
        NiftySignalDbContext Source(string cs, bool counted) { var o = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(cs, n => n.CommandTimeout(1800)); if (counted) o.AddInterceptors(sourceSql); return new(o.Options); }

        // Same universe selection as OptionsDepth5HostBenchmark.
        await using var original = Source(conn, false);
        var anchor = await original.Ticks.AsNoTracking().Where(t => t.Token == fut && t.ExchangeTimestamp >= open.AddMinutes(15) && t.ExchangeTimestamp < open.AddMinutes(20)).OrderBy(t => t.Id).Select(t => t.LastPrice).FirstAsync();
        var instruments = await original.Instruments.AsNoTracking()
            .Where(i => i.Subscribed && i.ExpiryDate == weekly && i.OptionType != OptionType.None && i.TradingSymbol.StartsWith("NIFTY") && i.AsOfDate <= day)
            .OrderByDescending(i => i.AsOfDate).ToListAsync();
        var picked = instruments.GroupBy(i => i.Token).Select(g => g.First()).ToList();
        var strikes = picked.Select(i => (double)i.StrikePrice!.Value).Distinct().OrderBy(k => Math.Abs(k - (double)anchor)).ThenBy(k => k).Take(20).ToHashSet();
        var universe = picked.Where(i => strikes.Contains((double)i.StrikePrice!.Value))
            .Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, weekly, i.LotSize == 0 ? 75 : i.LotSize)).OrderBy(x => x.Token, StringComparer.Ordinal).ToList();

        // Optional factor isolation: the same rows copied into a small scratch database (original Ids kept).
        TempAdaptiveDatabase? stagedDb = null;
        var sourceCs = conn;
        if (staged)
        {
            stagedDb = await TempAdaptiveDatabase.CreateAsync();
            sourceCs = stagedDb.ConnectionString;
            await using (var create = Source(sourceCs, false)) await create.Database.EnsureCreatedAsync();
            var tokens = universe.Select(x => x.Token).Append(fut).ToArray();
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5)).ToUniversalTime();
            var to = from.AddDays(1);
            var rows = original.Ticks.AsNoTracking().Where(t => tokens.Contains(t.Token) && t.ReceivedAt >= from && t.ReceivedAt < to);
            var cursor = await rows.MinAsync(t => t.Id) - 1;
            while (true)
            {
                var page = await rows.Where(t => t.Id > cursor).OrderBy(t => t.Id).Take(20_000).ToListAsync();
                if (page.Count == 0) break;
                cursor = page[^1].Id;
                await using var write = Source(sourceCs, false);
                write.ChangeTracker.AutoDetectChangesEnabled = false;
                write.Ticks.AddRange(page);
                await write.SaveChangesAsync();
            }

            await using var analyze = new NpgsqlConnection(sourceCs);
            await analyze.OpenAsync();
            await using var command = new NpgsqlCommand("ANALYZE ticks", analyze) { CommandTimeout = 600 };
            await command.ExecuteNonQueryAsync();
        }

        await using var source = Source(sourceCs, true);
        await using var temp = await TempAdaptiveDatabase.CreateAsync();
        await temp.MigrateAsync();
        var at = open.AddMinutes(30);
        var session = new AdaptiveSessionStateRow
        {
            TradeDate = day, ModelVersion = OpeningVolumeProjectionV1.ModelVersion, SourceBranch = "bench", SourceCommitSha = "bench", BuildUtc = at,
            FutureToken = fut, FutureSymbol = "FUT", FutureExpiry = day.AddDays(14), OpeningWindowStartUtc = at.AddMinutes(-15), OpeningWindowEndUtc = at,
            OpeningVolume = 1000, EstimatorName = nameof(OpeningVolumeProjectionV1), CreatedAtUtc = at,
        };
        await using (var seed = temp.Context())
        {
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
            seed.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = session.Id, LastHeartbeatUtc = at });
            await seed.SaveChangesAsync();
        }

        var context = new AdaptiveObserverSessionContext(session, new AdaptiveSessionDefinition(day, fut, "FUT", day.AddDays(14), 65, barVol), weekly, universe, null, at);
        var gateway = new FuturesDepth5Gateway(Microsoft.Extensions.Options.Options.Create(new Top5MicrostructureOptions { Enabled = mode is "futures" or "both", OptionsEnabled = mode == "both" }), new AdaptiveDepthTickReader());
        var supplemental = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var log = new Lines<AdaptiveStateRecoveryService>();
        var service = new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), log, supplemental, depth5: gateway);

        var stages = new ConcurrentDictionary<string, (double Ms, long Count)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "NiftySignal.AdaptiveLive",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a => stages.AddOrUpdate(a.OperationName, (a.Duration.TotalMilliseconds, 1L), (_, v) => (v.Ms + a.Duration.TotalMilliseconds, v.Count + 1)),
        };
        ActivitySource.AddActivityListener(listener);

        string? firstHash = null;
        foreach (var pass in new[] { "insert", "verify" })
        {
            stages.Clear(); sourceSql.Take(); observerSql.Take(); observerOpens.Take(); log.Messages.Clear();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var pause = GC.GetTotalPauseDuration();
            AdaptiveRecoveryResult result;
            var watch = Stopwatch.StartNew();
            await using (var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(ObserverConnection(temp)).AddInterceptors(observerSql, observerOpens).Options))
                result = await service.RecoverAsync(source, db, context, through, default);
            watch.Stop();
            process.Refresh();
            var cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
            var allocatedMb = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d;
            var pauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
            var s = sourceSql.Take(); var o = observerSql.Take(); var opened = observerOpens.Take();

            // Publication cost exists only in the candidate; the baseline has no publisher.
            double? publishMs = null; int? published = null;
            var publisher = typeof(AdaptiveObserverDbContext).Assembly.GetType("NiftySignal.AdaptiveObserverData.AdaptiveBarPublisher")
                ?? typeof(AdaptiveObserverDbContext).Assembly.GetTypes().Concat(typeof(AdaptiveStateRecoveryService).Assembly.GetTypes()).FirstOrDefault(t => t.Name == "AdaptiveBarPublisher");
            if (publisher?.GetMethod("PublishMissingAsync", BindingFlags.Public | BindingFlags.Static) is { } method)
            {
                await using var db = temp.Context();
                var clock = Stopwatch.StartNew();
                published = await (Task<int>)method.Invoke(null, [db, session.Id, null, DateTimeOffset.UtcNow, CancellationToken.None, null])!;
                publishMs = clock.Elapsed.TotalMilliseconds;
            }

            var json = JsonSerializer.Serialize(result.Engine.FutureBars);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))[..16];
            firstHash ??= hash;
            var counts = new Dictionary<string, long>();
            foreach (var t in new[] { "adaptive_option_strike_bars", "adaptive_strike_attestation", "adaptive_futures_depth5_bars", "adaptive_options_depth5_bars", "adaptive_options_depth5_attestation", "adaptive_projection_health" })
                counts[t] = await temp.ScalarAsync($"SELECT count(*) FROM {t}");

            await File.AppendAllTextAsync(outPath, JsonSerializer.Serialize(new
            {
                label, mode, staged, pass, universe = universe.Count, bars = result.Engine.FutureBars.Count(b => b.IsComplete), coreHash = hash, coreSameAsFirstPass = hash == firstHash,
                lastFetchedRawId = result.LastFetchedRawId, unverifiedSidecarBars = result.UnverifiedSidecarBars.Count, mismatches = supplemental.MismatchCount, failures = supplemental.FailureCount,
                wallSeconds = watch.Elapsed.TotalSeconds, cpuSeconds, allocatedMb, gcPauseMs = pauseMs, peakWorkingSetMb = process.PeakWorkingSet64 / 1048576d,
                stages = stages.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => new { ms = x.Value.Ms, count = x.Value.Count }),
                sourceSql = new { s.Commands, s.ExecuteMs, s.ReadMs }, observerSql = new { o.Commands, o.Writes, o.ExecuteMs, o.ReadMs },
                observerPooling = pooled, observerOpens = new { opened.Count, opened.Ms }, publishMs, published, rows = counts, phaseTimings = log.Messages.Where(m => m.Contains("phase timings")).ToArray(),
            }) + Environment.NewLine);
        }

        // Query plans of the source reads (SELECT only, after the timed passes).
        await using var plans = new NpgsqlConnection(sourceCs);
        await plans.OpenAsync();
        foreach (var copy in sourceSql.Distinct.Values.Where(c => c.Text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)))
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + copy.Text, plans) { CommandTimeout = 1800 };
            command.Parameters.AddRange(copy.Parameters.Select(p => p.Clone()).ToArray());
            var text = new List<string> { $"### {label} staged={staged}", copy.Text };
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) text.Add(reader.GetString(0));
            await File.AppendAllLinesAsync(outPath + ".plans.txt", text);
        }

        if (stagedDb is not null) await stagedDb.DisposeAsync();
    }
}
