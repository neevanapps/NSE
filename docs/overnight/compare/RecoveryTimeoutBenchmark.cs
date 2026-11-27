// Bench-only overlay, NOT part of the release source tree. Candidate only (the baseline has no checkpoints and no time limit).
// Full-table recovery under the production time limit and checkpoint settings, repeated as the worker would repeat it, until it completes or the attempts run out.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;
using Options = Microsoft.Extensions.Options.Options;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class RecoveryTimeoutBenchmark
{
    [Fact]
    public async Task TimedOutAttempts()
    {
        var outPath = Environment.GetEnvironmentVariable("NS_TIMEOUT_OUT");
        if (outPath is null || !AdaptivePg.Available) return;
        var limit = int.Parse(Environment.GetEnvironmentVariable("NS_TIMEOUT_LIMIT") ?? "300");
        var attempts = int.Parse(Environment.GetEnvironmentVariable("NS_TIMEOUT_ATTEMPTS") ?? "5");
        var checkpointsOn = Environment.GetEnvironmentVariable("NS_TIMEOUT_CHECKPOINTS") != "0";
        var mode = Environment.GetEnvironmentVariable("NS_DEPTH5_OPT")!;
        var day = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_DAY")!);
        var fut = Environment.GetEnvironmentVariable("NS_DEPTH5_FUT")!;
        var weekly = DateOnly.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_WEEKLY")!);
        var barVol = long.Parse(Environment.GetEnvironmentVariable("NS_DEPTH5_BARVOL")!);
        var conn = Environment.GetEnvironmentVariable("NS_DEPTH5_CONN")!;
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
        var recovery = Options.Create(new AdaptiveRecoveryOptions { CheckpointsEnabled = checkpointsOn, TimeLimitSeconds = limit });
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var supplemental = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var log = new RecoveryComparisonBenchmark.Lines<AdaptiveStateRecoveryService>();
        var checkpointLog = new RecoveryComparisonBenchmark.Lines<AdaptiveCheckpointStore>();
        var service = new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), persistence,
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), log, supplemental,
            depth5: new FuturesDepth5Gateway(Options.Create(new Top5MicrostructureOptions { Enabled = mode is "futures" or "both", OptionsEnabled = mode == "both" }), new AdaptiveDepthTickReader()),
            recoveryOptions: recovery, checkpoints: new AdaptiveCheckpointStore(persistence, checkpointLog, recovery));

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            log.Messages.Clear(); checkpointLog.Messages.Clear();
            var watch = Stopwatch.StartNew();
            string outcome; int bars = 0; string? hash = null;
            try
            {
                await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(RecoveryComparisonBenchmark.ObserverConnection(temp)).Options);
                var result = await service.RecoverAsync(source, db, context, through, default, TimeSpan.FromSeconds(limit));
                outcome = "completed";
                bars = result.Engine.FutureBars.Count(b => b.IsComplete);
                hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result.Engine.FutureBars))))[..16];
            }
            catch (AdaptiveRecoveryTimeoutException ex) { outcome = "timed out: " + ex.Message; }

            await File.AppendAllTextAsync(outPath, JsonSerializer.Serialize(new
            {
                mode, limit, checkpointsOn, observerPooling = Environment.GetEnvironmentVariable("NS_CMP_POOLING") == "1", attempt, outcome, bars, coreHash = hash, wallSeconds = watch.Elapsed.TotalSeconds,
                storedCheckpoints = await temp.ScalarAsync("SELECT count(*) FROM adaptive_checkpoints"), persistedBars = await temp.ScalarAsync("SELECT count(*) FROM adaptive_future_bars"),
                mismatches = supplemental.MismatchCount, failures = supplemental.FailureCount,
                log = log.Messages.Concat(checkpointLog.Messages).ToArray(),
            }) + Environment.NewLine);
            if (outcome == "completed") return;
            await Task.Delay(TimeSpan.FromSeconds(recovery.Value.TimeoutRetryBackoffSeconds));
        }
    }
}
