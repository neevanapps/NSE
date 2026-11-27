using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Env-gated Host-recovery benchmark of the S5 options depth sidecar on LOCAL data (skipped unless NS_DEPTH5_OPT is "off", "futures" or "both"). One mode per process so peak working set is clean.
/// Source ticks: read-only (research_ro). Observer database: a throw-away database on the SCRATCH PostgreSQL named by NSE_ADAPTIVE_MIGRATION_TEST_DB (loopback, database "postgres" only), so the core, strike-v3,
/// futures depth5 and options depth5 writes, the attestation and the restart verification all run against a real server, in every mode.
///   NS_DEPTH5_OPT=off|futures|both  NS_DEPTH5_DAY=2026-10-29  NS_DEPTH5_FUT=61471  NS_DEPTH5_WEEKLY=2026-11-03  NS_DEPTH5_BARVOL=20000  NS_DEPTH5_OUT=result.txt
/// The universe is the 20 listed NIFTY strikes nearest the open future price, both sides (40 contracts, the S4 design size).
/// </summary>
public sealed class OptionsDepth5HostBenchmark
{
    [Fact]
    public async Task Benchmark()
    {
        var mode = Environment.GetEnvironmentVariable("NS_DEPTH5_OPT");
        if (mode is not ("off" or "futures" or "both") || !AdaptivePg.Available) return;
        var day = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_DAY") ?? "2026-10-29");
        var fut = Environment.GetEnvironmentVariable("NS_DEPTH5_FUT") ?? "61471";
        var weekly = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_WEEKLY") ?? "2026-11-03");
        var barVol = long.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_BARVOL") ?? "20000");
        var conn = Environment.GetEnvironmentVariable("NS_DEPTH5_CONN") ?? "Host=localhost;Database=niftysignal_vm_copy;Username=research_ro";
        var outPath = Environment.GetEnvironmentVariable("NS_DEPTH5_OUT");
        var through = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 40)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();

        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(conn, o => o.CommandTimeout(1800)).Options);
        var anchor = await source.Ticks.AsNoTracking().Where(t => t.Token == fut && t.ExchangeTimestamp >= open.AddMinutes(15) && t.ExchangeTimestamp < open.AddMinutes(20)).OrderBy(t => t.Id).Select(t => t.LastPrice).FirstAsync();
        var instruments = await source.Instruments.AsNoTracking()
            .Where(i => i.Subscribed && i.ExpiryDate == weekly && i.OptionType != OptionType.None && i.TradingSymbol.StartsWith("NIFTY") && i.AsOfDate <= day)
            .OrderByDescending(i => i.AsOfDate).ToListAsync();
        var picked = instruments.GroupBy(i => i.Token).Select(g => g.First()).ToList();
        var strikes = picked.Select(i => (double)i.StrikePrice!.Value).Distinct().OrderBy(k => Math.Abs(k - (double)anchor)).ThenBy(k => k).Take(20).ToHashSet();
        var universe = picked.Where(i => strikes.Contains((double)i.StrikePrice!.Value))
            .Select(i => new ObserverOptionInstrument(i.Token, i.TradingSymbol, i.OptionType, (double)i.StrikePrice!.Value, weekly, i.LotSize == 0 ? 75 : i.LotSize)).OrderBy(x => x.Token, StringComparer.Ordinal).ToList();

        await using var temp = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_obs_test_");
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
        var service = new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), NullLogger<AdaptiveStateRecoveryService>.Instance, supplemental, depth5: gateway);

        async Task<(AdaptiveRecoveryResult Result, double Seconds)> Recover(string label)
        {
            await using var db = temp.Context();
            var sw = Stopwatch.StartNew();
            var r = await service.RecoverAsync(source, db, context, through, default);
            sw.Stop();
            return (r, sw.Elapsed.TotalSeconds);
        }

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseHeap = GC.GetTotalMemory(true);
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        long g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration();

        var peakManaged = 0L; var sampling = true;
        var sampler = Task.Run(async () => { while (Volatile.Read(ref sampling)) { peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false)); await Task.Delay(50); } });
        var (first, firstSeconds) = await Recover("insert");
        var retained = GC.GetTotalMemory(true) - baseHeap;
        var peakAfterFirst = Process.GetCurrentProcess().PeakWorkingSet64;
        var (second, secondSeconds) = await Recover("verify");                // restart: every bar is replayed through the verify-if-present path

        Volatile.Write(ref sampling, false); await sampler;
        var allTokens = universe.Select(x => x.Token).ToArray();
        var rawFut = await source.Ticks.AsNoTracking().CountAsync(t => t.Token == fut && t.ExchangeTimestamp >= open && t.ExchangeTimestamp <= through);
        var rawOpt = await source.Ticks.AsNoTracking().CountAsync(t => allTokens.Contains(t.Token) && t.ExchangeTimestamp >= open && t.ExchangeTimestamp <= through);
        if (outPath is not null) await File.AppendAllTextAsync(outPath, $"opt_mode={mode} raw_ticks_future={rawFut} raw_ticks_options={rawOpt} peak_managed_mb={peakManaged / 1048576d:F0}{Environment.NewLine}");
        var process = Process.GetCurrentProcess();
        var bars = first.Engine.FutureBars.Count(b => b.IsComplete);
        var sameCore = System.Text.Json.JsonSerializer.Serialize(first.Engine.FutureBars) == System.Text.Json.JsonSerializer.Serialize(second.Engine.FutureBars);
        var coreHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(first.Engine.FutureBars))))[..16];
        var rows = new Dictionary<string, long>();
        foreach (var t in new[] { "adaptive_option_strike_bars", "adaptive_strike_attestation", "adaptive_futures_depth5_bars", "adaptive_options_depth5_bars", "adaptive_options_depth5_attestation", "adaptive_projection_health" })
            rows[t] = await temp.ScalarAsync($"SELECT count(*) FROM {t}");
        var line = $"opt_mode={mode} day={day:yyyy-MM-dd} universe={universe.Count} anchor={anchor} bars={bars} recovery_insert_s={firstSeconds:F2} recovery_verify_s={secondSeconds:F2} alloc_mb={(GC.GetTotalAllocatedBytes(true) - allocBefore) / 1048576d:F0} " +
                   $"retained_mb_after_first={retained / 1048576d:F1} peak_ws_after_first_mb={peakAfterFirst / 1048576d:F0} peak_ws_mb={process.PeakWorkingSet64 / 1048576d:F0} gen0={GC.CollectionCount(0) - g0} gen1={GC.CollectionCount(1) - g1} gen2={GC.CollectionCount(2) - g2} " +
                   $"gc_pause_ms={(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F0} core_hash={coreHash} core_same_on_restart={sameCore} last_id={first.LastFetchedRawId}/{second.LastFetchedRawId} " +
                   $"mismatches={supplemental.MismatchCount} failures={supplemental.FailureCount} pending_retry_opt={supplemental.PendingOptionsDepth5RetryCount} " + string.Join(' ', rows.Select(kv => $"{kv.Key}={kv.Value}"));
        if (outPath is not null) await File.AppendAllTextAsync(outPath, line + Environment.NewLine);

        if (mode != "both") return;

        // ---- standalone measurement pass over the same event path (no DB writes): per-bar completion latency, event throughput, bounded state, coverage statistics.
        var tokens = universe.Select(x => x.Token).ToArray();
        var lazy = gateway.CreateOptionsSource(source, day, tokens, open);
        var tracker = new OptionsDepth5Tracker(universe);
        var completed = first.Engine.FutureBars.Where(b => b.IsComplete).OrderBy(b => b.BarSeq).ToList();
        var latencies = new List<double>();
        var perToken = tokens.ToDictionary(t => t, _ => new double[9]);                       // usable, stale, invalid, superseded, nodepth, unobserved, direct, bridged, resets
        double bridgedGapSum = 0; long events = 0; var peakRetained = 0; var peakPending = 0;
        var pass = Stopwatch.StartNew();
        foreach (var bar in completed)
        {
            await lazy.AdvanceToAsync(bar.EndAvailableAtUtc, default);
            foreach (var e in lazy.Feed.DrainThrough(bar.EndAvailableAtUtc)) { tracker.Observe(e); events++; }
            var t0 = Stopwatch.GetTimestamp();
            var result = tracker.Complete(bar.StartAvailableAtUtc, bar.EndAvailableAtUtc);
            latencies.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            peakRetained = Math.Max(peakRetained, tracker.RetainedEvents);
            peakPending = Math.Max(peakPending, lazy.Feed.PendingCount);
            foreach (var r in result)
            {
                var a = perToken[r.Token]; var d = r.Depth;
                a[0] += d.Depth5UsableSeconds; a[1] += d.Depth5StaleSeconds; a[2] += d.Depth5InvalidSeconds; a[3] += d.Depth5SupersededSeconds; a[4] += d.Depth5NoDepthSeconds; a[5] += d.Depth5UnobservedSeconds;
                a[6] += d.Ofi5DirectTransitions; a[7] += d.Ofi5BridgedTransitions; a[8] += d.Ofi5BaselineResets; bridgedGapSum += d.Ofi5BridgedGapSeconds;
            }
        }

        pass.Stop();
        latencies.Sort();
        static double Pct(List<double> s, double p) => s.Count == 0 ? 0 : s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        var sums = new double[9];
        foreach (var a in perToken.Values) for (var i = 0; i < 9; i++) sums[i] += a[i];
        var total = sums[0] + sums[1] + sums[2] + sums[3] + sums[4] + sums[5];
        var stats = $"options_pass bars={completed.Count} rows_read={lazy.RowsRead} events={events} pass_s={pass.Elapsed.TotalSeconds:F2} rows_per_s={lazy.RowsRead / pass.Elapsed.TotalSeconds:F0} complete_ms_p50={Pct(latencies, .5):F3} p95={Pct(latencies, .95):F3} p99={Pct(latencies, .99):F3} max={(latencies.Count > 0 ? latencies[^1] : 0):F3} " +
                    $"peak_retained_events={peakRetained} peak_pending={peakPending} peak_page_rows={lazy.PeakBufferedRows} late={tracker.LateEvents} repeats_ignored={tracker.RepeatsIgnored} unknown={tracker.UnknownTokenEvents} " +
                    $"direct={sums[6]:F0} bridged={sums[7]:F0} bridged_pct={(sums[6] + sums[7] > 0 ? 100 * sums[7] / (sums[6] + sums[7]) : 0):F1} resets={sums[8]:F0} mean_bridged_gap_s={(sums[7] > 0 ? bridgedGapSum / sums[7] : 0):F4} " +
                    $"usable={100 * sums[0] / total:F2}% stale={100 * sums[1] / total:F2}% invalid={100 * sums[2] / total:F2}% superseded={100 * sums[3] / total:F2}% nodepth={100 * sums[4] / total:F2}% unobserved={100 * sums[5] / total:F2}%";
        if (outPath is not null) await File.AppendAllTextAsync(outPath, stats + Environment.NewLine);

        // per-contract and per-moneyness coverage
        string Money(ObserverOptionInstrument o)
        {
            var itm = o.OptionType == OptionType.Call ? o.Strike < (double)anchor : o.Strike > (double)anchor;
            var dist = Math.Abs(o.Strike - (double)anchor);
            return dist <= 50 ? "ATM" : itm ? "ITM" : "OTM";
        }

        var lines = new List<string>();
        foreach (var grp in universe.GroupBy(Money).OrderBy(g => g.Key))
        {
            var agg = new double[9];
            foreach (var o in grp) for (var i = 0; i < 9; i++) agg[i] += perToken[o.Token][i];
            var tot = agg[0] + agg[1] + agg[2] + agg[3] + agg[4] + agg[5];
            lines.Add($"moneyness={grp.Key} contracts={grp.Count()} usable={100 * agg[0] / tot:F2}% stale={100 * agg[1] / tot:F2}% invalid={100 * agg[2] / tot:F2}% superseded={100 * agg[3] / tot:F2}% unobserved={100 * agg[5] / tot:F2}% direct={agg[6]:F0} bridged={agg[7]:F0} resets={agg[8]:F0}");
        }

        foreach (var o in universe.OrderBy(o => o.OptionType).ThenBy(o => o.Strike))
        {
            var a = perToken[o.Token]; var tot = a[0] + a[1] + a[2] + a[3] + a[4] + a[5];
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"contract={o.TradingSymbol} token={o.Token} side={o.OptionType} strike={o.Strike} {Money(o)} usable={100 * a[0] / tot:F1}% stale={100 * a[1] / tot:F1}% invalid={100 * a[2] / tot:F1}% superseded={100 * a[3] / tot:F1}% unobserved={100 * a[5] / tot:F1}% direct={a[6]:F0} bridged={a[7]:F0} resets={a[8]:F0}"));
        }

        if (outPath is not null) await File.AppendAllLinesAsync(outPath, lines);
    }
}
