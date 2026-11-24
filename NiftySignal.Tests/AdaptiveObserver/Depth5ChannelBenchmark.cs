using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Env-gated, read-only benchmark of the S3 depth channel against the current reader path on LOCAL data. Skipped (passes trivially) unless NS_DEPTH5_BENCH is "current" or "depth".
/// Run one mode per process so peak working set is not polluted:
///   NS_DEPTH5_BENCH=current|depth  NS_DEPTH5_DAY=2026-10-29  NS_DEPTH5_TOKENS=a,b,c  NS_DEPTH5_CONN="Host=localhost;Database=niftysignal_vm_copy;Username=research_ro"  NS_DEPTH5_OUT=result.txt
/// The connection string carries no password (Npgsql resolves it from the user's own pgpass). Nothing is written to any database.
/// </summary>
public sealed class Depth5ChannelBenchmark
{
    [Fact]
    public async Task Benchmark()
    {
        var mode = Environment.GetEnvironmentVariable("NS_DEPTH5_BENCH");
        if (mode is not ("current" or "depth")) return;
        var day = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_DAY") ?? "2026-10-29");
        var tokens = (Environment.GetEnvironmentVariable("NS_DEPTH5_TOKENS") ?? throw new InvalidOperationException("NS_DEPTH5_TOKENS")).Split(',');
        var conn = Environment.GetEnvironmentVariable("NS_DEPTH5_CONN") ?? "Host=localhost;Database=niftysignal_vm_copy;Username=research_ro";
        var through = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 40)), TimeSpan.FromHours(5.5)).ToUniversalTime();

        var options = new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(conn, o => o.CommandTimeout(1800)).Options;
        await using var db = new NiftySignalDbContext(options);
        db.Database.SetCommandTimeout(1800);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseHeap = GC.GetTotalMemory(true);
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var gen2Before = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();
        long rows = 0, kept = 0, checksum = 0;
        long retainedAtEnd;
        var events = "";
        double firstRowMs = 0;

        if (mode == "current")
        {
            var clean = await new AdaptiveSourceTickReader().ReadCleanSessionAsync(db, day, tokens, through, default);
            kept = clean.Count;
            foreach (var c in clean) checksum += c.Tick.Id;
            rows = kept;                                                              // the current path does not expose the raw row count; clean rows are what the engine consumes
            retainedAtEnd = GC.GetTotalMemory(true) - baseHeap;
            GC.KeepAlive(clean);
        }
        else
        {
            var normalizer = new AdaptiveDepthNormalizer();
            var buffer = new Depth5OrderingBuffer();
            var last = new Dictionary<string, Depth5Book?>();
            long transitions = 0;
            long snapshots = 0, superseded = 0, noDepth = 0;
            double avail = 0, span = 0;
            var prevEvent = new Dictionary<string, (DateTimeOffset At, bool Valid)>();
            await foreach (var row in new AdaptiveDepthTickReader().StreamSessionAsync(db, day, tokens, through, default))
            {
                if (rows == 0) firstRowMs = sw.Elapsed.TotalMilliseconds;
                rows++;
                if (normalizer.Process(row) is { } obs) buffer.Add(obs);
                var now = Depth5Mapping.AvailableAt(row.ExchangeTimestamp, row.ReceivedAt);
                foreach (var o in buffer.Release(now))
                {
                    kept++;
                    checksum += o.Id;
                    if (o.Kind == Depth5EventKind.Snapshot) snapshots++; else if (o.Kind == Depth5EventKind.L1Superseded) superseded++; else noDepth++;
                    if (prevEvent.TryGetValue(o.Token, out var pe))
                    {
                        var dt = (o.AvailableAt - pe.At).TotalSeconds;
                        span += dt;
                        if (pe.Valid) avail += Math.Min(5d, dt);
                    }

                    prevEvent[o.Token] = (o.AvailableAt, o.Kind == Depth5EventKind.Snapshot && o.Book is { IsValid: true });
                    if (o.Book is { IsValid: true } b)
                    {
                        if (last.TryGetValue(o.Token, out var p) && p is not null && Depth5Metrics.Ofi5Contribution(p, b) is not null) transitions++;
                        last[o.Token] = b;
                    }
                    else if (o.Kind != Depth5EventKind.L1Superseded) last[o.Token] = null;      // default policy: a superseded marker does not break the baseline; invalid/no-depth does
                }
            }

            foreach (var o in buffer.ReleaseAll()) { kept++; checksum += o.Id; if (o.Kind == Depth5EventKind.Snapshot) snapshots++; else if (o.Kind == Depth5EventKind.L1Superseded) superseded++; else noDepth++; }
            checksum += transitions * 1_000_003;
            events = $"snapshots={snapshots} l1_superseded={superseded} no_depth={noDepth} ofi5_transitions={transitions} book_available_fraction_of_event_span={(span > 0 ? avail / span : 0):P1}";
            retainedAtEnd = GC.GetTotalMemory(true) - baseHeap;
        }

        sw.Stop();
        var process = Process.GetCurrentProcess();
        var line = $"mode={mode} day={day:yyyy-MM-dd} tokens={tokens.Length} rows={rows} emitted={kept} seconds={sw.Elapsed.TotalSeconds:F2} rows_per_s={(rows / sw.Elapsed.TotalSeconds):F0} " +
                   $"first_row_ms={firstRowMs:F0} alloc_mb={(GC.GetTotalAllocatedBytes(true) - allocBefore) / 1048576d:F0} retained_mb_at_end={retainedAtEnd / 1048576d:F1} " +
                   $"peak_working_set_mb={process.PeakWorkingSet64 / 1048576d:F0} gen2_gcs={GC.CollectionCount(2) - gen2Before} checksum={checksum} {events}";
        var outPath = Environment.GetEnvironmentVariable("NS_DEPTH5_OUT");
        if (outPath is not null) await File.AppendAllTextAsync(outPath, line + Environment.NewLine);
    }
}
