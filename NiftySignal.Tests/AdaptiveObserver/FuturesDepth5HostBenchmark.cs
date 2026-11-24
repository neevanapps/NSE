using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Env-gated, read-only Host-recovery benchmark on LOCAL data (skipped unless NS_DEPTH5_HOST is "off" or "on"). It runs the real <see cref="AdaptiveStateRecoveryService"/> over one day of the future token with the Top-5 flag
/// off or on (one mode per process so peak working set is clean); the observer database is in-memory, nothing is written to any real database.
///   NS_DEPTH5_HOST=off|on  NS_DEPTH5_DAY=2026-10-29  NS_DEPTH5_FUT=61471  NS_DEPTH5_BARVOL=20000  NS_DEPTH5_CONN="Host=localhost;Database=niftysignal_vm_copy;Username=research_ro"  NS_DEPTH5_OUT=result.txt
/// In "on" mode a second pass over the same feed measures per-bar completion latency and the coverage / bridging statistics.
/// </summary>
public sealed class FuturesDepth5HostBenchmark
{
    [Fact]
    public async Task Benchmark()
    {
        var mode = Environment.GetEnvironmentVariable("NS_DEPTH5_HOST");
        if (mode is not ("off" or "on")) return;
        var day = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_DAY") ?? "2026-10-29");
        var fut = Environment.GetEnvironmentVariable("NS_DEPTH5_FUT") ?? "61471";
        var barVol = long.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_BARVOL") ?? "20000");
        var conn = Environment.GetEnvironmentVariable("NS_DEPTH5_CONN") ?? "Host=localhost;Database=niftysignal_vm_copy;Username=research_ro";
        var through = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 40)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();

        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(conn, o => o.CommandTimeout(1800)).Options);
        await using var observer = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var at = open.AddMinutes(30);
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = day, ModelVersion = OpeningVolumeProjectionV1.ModelVersion, SourceBranch = "bench", SourceCommitSha = "bench", BuildUtc = at,
            FutureToken = fut, FutureSymbol = "FUT", FutureExpiry = day.AddDays(14), OpeningWindowStartUtc = at.AddMinutes(-15), OpeningWindowEndUtc = at,
            OpeningVolume = 1000, EstimatorName = nameof(OpeningVolumeProjectionV1), CreatedAtUtc = at,
        };
        var context = new AdaptiveObserverSessionContext(session, new AdaptiveSessionDefinition(day, fut, "FUT", day.AddDays(14), 65, barVol), day.AddDays(7), [], null, at);
        observer.Sessions.Add(session);
        observer.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = 1, LastHeartbeatUtc = at });
        await observer.SaveChangesAsync();

        var gateway = new FuturesDepth5Gateway(Microsoft.Extensions.Options.Options.Create(new Top5MicrostructureOptions { Enabled = mode == "on" }), new AdaptiveDepthTickReader());
        var service = new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), NullLogger<AdaptiveStateRecoveryService>.Instance, depth5: gateway);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseHeap = GC.GetTotalMemory(true);
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var gen0 = GC.CollectionCount(0); var gen2Before = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration();
        var sw = Stopwatch.StartNew();
        var result = await service.RecoverAsync(source, observer, context, through, default);
        sw.Stop();
        var bars = result.Engine.FutureBars.Where(b => b.IsComplete).ToList();
        var retained = GC.GetTotalMemory(true) - baseHeap;
        var process = Process.GetCurrentProcess();
        var core = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(bars))))[..16];
        var line = $"host_mode={mode} day={day:yyyy-MM-dd} bars={bars.Count} recovery_seconds={sw.Elapsed.TotalSeconds:F2} alloc_mb={(GC.GetTotalAllocatedBytes(true) - allocBefore) / 1048576d:F0} retained_mb_after={retained / 1048576d:F1} " +
                   $"peak_working_set_mb={process.PeakWorkingSet64 / 1048576d:F0} gen0={GC.CollectionCount(0) - gen0} gen2={GC.CollectionCount(2) - gen2Before} gc_pause_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F0} " +
                   $"last_fetched_id={result.LastFetchedRawId} last_tick_id={result.LastProcessedTickId} core_hash={core} depth_feed_pending={result.Depth5Feed?.PendingCount}";
        var outPath = Environment.GetEnvironmentVariable("NS_DEPTH5_OUT");
        if (outPath is not null) await File.AppendAllTextAsync(outPath, line + Environment.NewLine);

        if (mode != "on") return;

        // Second pass (measurement only): per-bar completion latency and coverage / bridging statistics from the same feed.
        var feed = await gateway.RecoverFeedAsync(source, day, fut, open, default);
        var tracker = new FuturesDepth5Tracker();
        var latencies = new List<double>();
        FuturesDepth5Bar? sum = null;
        double usable = 0, stale = 0, invalid = 0, superseded = 0, noDepth = 0, unobserved = 0, bridgedGap = 0, bridgedAbs = 0, directAbs = 0;
        long direct = 0, bridged = 0, resets = 0, snapshots = 0, markers = 0, invalidSnaps = 0;
        var gaps = new List<double>();
        foreach (var bar in bars.OrderBy(b => b.BarSeq))
        {
            foreach (var o in feed.DrainThrough(bar.EndAvailableAtUtc)) tracker.Observe(o);
            var t0 = Stopwatch.GetTimestamp();
            var d = tracker.Complete(bar.StartAvailableAtUtc, bar.EndAvailableAtUtc);
            latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            usable += d.Depth5UsableSeconds; stale += d.Depth5StaleSeconds; invalid += d.Depth5InvalidSeconds; superseded += d.Depth5SupersededSeconds; noDepth += d.Depth5NoDepthSeconds; unobserved += d.Depth5UnobservedSeconds;
            direct += d.Ofi5DirectTransitions; bridged += d.Ofi5BridgedTransitions; resets += d.Ofi5BaselineResets; snapshots += d.SnapshotEvents; markers += d.SupersededMarkers; invalidSnaps += d.InvalidSnapshots;
            bridgedGap += d.Ofi5BridgedGapSeconds;
            if (d.Ofi5BridgedTransitions > 0) gaps.Add(d.Ofi5BridgedGapSeconds / d.Ofi5BridgedTransitions);
            sum = d;
        }

        latencies.Sort();
        static double Pct(List<double> s, double p) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        var total = usable + stale + invalid + superseded + noDepth + unobserved;
        gaps.Sort();
        var stats = $"depth_pass bars={bars.Count} complete_ms_p50={Pct(latencies, .5):F3} p99={Pct(latencies, .99):F3} max={(latencies.Count > 0 ? latencies[^1] : 0):F3} late_events={tracker.LateEvents} repeats_ignored={tracker.RepeatsIgnored} retained_events_end={tracker.RetainedEvents} " +
                    $"snapshots={snapshots} markers={markers} invalid_snapshots={invalidSnaps} ofi5_direct={direct} ofi5_bridged={bridged} bridged_pct={(direct + bridged > 0 ? 100d * bridged / (direct + bridged) : 0):F1} baseline_resets={resets} " +
                    $"bridged_gap_s_mean={(bridged > 0 ? bridgedGap / bridged : 0):F3} bridged_gap_per_bar_p50={Pct(gaps, .5):F3} p99={Pct(gaps, .99):F3} max={(gaps.Count > 0 ? gaps[^1] : 0):F3} " +
                    $"coverage_total_s={total:F0} usable={100 * usable / total:F2}% stale={100 * stale / total:F2}% invalid={100 * invalid / total:F2}% superseded={100 * superseded / total:F2}% nodepth={100 * noDepth / total:F2}% unobserved={100 * unobserved / total:F2}%";
        if (outPath is not null) await File.AppendAllTextAsync(outPath, stats + Environment.NewLine);
        GC.KeepAlive(sum);
    }
}
