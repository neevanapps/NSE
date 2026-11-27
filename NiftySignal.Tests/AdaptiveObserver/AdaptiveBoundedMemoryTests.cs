using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using Xunit.Abstractions;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>Bounded-memory processing (audit findings F74, F75): batched or released reads give the same results as holding everything. Synthetic data only.</summary>
public sealed class AdaptiveBoundedMemoryTests(ITestOutputHelper output)
{
    static readonly DateOnly Day = new(2026, 9, 25);
    static readonly DateTimeOffset At = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);   // 09:30 IST

    static NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static Tick Row(long id, string token, DateTimeOffset at, long volume, long? oi = null) => new()
    {
        Id = id, Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000 + id % 50, Volume = volume, OpenInterest = oi,
    };

    [Fact]
    public async Task StartCursor_IsBelowTheLowestIdOfTheFirstReceivedWindow_AndNullWithoutRows()
    {
        await using var source = Source();
        Assert.Null(await AdaptiveSourceTickReader.FindStartCursorAsync(source, ["FUT"], Day, TimeSpan.FromSeconds(10), default));
        // Id 7 is received first, the lower Id 5 two seconds later: both are inside the first window.
        source.Ticks.AddRange(Row(7, "FUT", At, 100), Row(5, "FUT", At.AddSeconds(2), 200), Row(9, "FUT", At.AddSeconds(60), 300), Row(2, "OTHER", At, 1));
        await source.SaveChangesAsync();

        Assert.Equal(4, await AdaptiveSourceTickReader.FindStartCursorAsync(source, ["FUT"], Day, TimeSpan.FromSeconds(10), default));
        Assert.Null(await AdaptiveSourceTickReader.FindStartCursorAsync(source, ["FUT"], Day.AddDays(1), TimeSpan.FromSeconds(10), default));
    }

    [Fact]
    public async Task LiveDepthPoll_ReadsOnlyAsFarAsAsked_ThenTheRest()
    {
        await using var source = Source();
        for (var i = 1; i <= 40; i++) source.Ticks.Add(Row(i, "FUT", At.AddSeconds(i), 1000 + i));
        await source.SaveChangesAsync();
        var gateway = new FuturesDepth5Gateway(Options.Create(new Top5MicrostructureOptions { Enabled = true }), new AdaptiveDepthTickReader(),
            Options.Create(new AdaptiveRecoveryOptions { LiveReadBatchSize = 5 }));
        var feed = new FuturesDepth5Feed();

        await gateway.PollAsync(source, feed, "FUT", Day, At, default, coverReceivedAt: At.AddSeconds(12));
        Assert.Equal(15, feed.LastFetchedId);                         // whole batches, stopping at the first one that covers the instant; no cursor existed at the start

        await gateway.PollAsync(source, feed, "FUT", Day, At, default);
        Assert.Equal(40, feed.LastFetchedId);
    }

    [Fact]
    public async Task LivePollBacklog_ProcessedBatchByBatch_BuildsTheSameBars_AndHoldsOneWindowPlusABatch()
    {
        const int rows = 400;
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        context.Session.WeeklyOptionExpiry = context.WeeklyOptionExpiry;
        context.Session.BaseBarVolume = 100; context.Session.RollingWindowBars = 10; context.Session.LotSize = 65; context.Session.RiskFreeRate = .065;
        context.Session.StrongThreshold = .2;
        context.Session.OptionUniverseJson = JsonSerializer.Serialize(Array.Empty<ObserverOptionInstrument>());

        var sourceName = Guid.NewGuid().ToString();
        var observerName = Guid.NewGuid().ToString();
        NiftySignalDbContext NewSource() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(sourceName).Options);
        AdaptiveObserverDbContext NewObserver() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(observerName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        await using (var db = NewObserver())
        {
            db.Sessions.Add(context.Session);
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = At });
            await db.SaveChangesAsync();
            db.Entry(context.Session).State = EntityState.Detached;
        }

        // Receive order differs a little from Id order (inside the 10 s read window).
        var random = new Random(7);
        var ticks = Enumerable.Range(1, rows).Select(i => Row(i, "FUT", At.AddSeconds(i + random.Next(0, 3)), 1000 + 60L * i)).ToList();

        var whole = new AdaptiveObserverEngine(context.Definition with { BaseBarVolume = 100 }, context.WeeklyOptionExpiry, .065, .2, context.SignalStartUtc, [], null);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var ordered = ticks.OrderBy(t => t.Id).Select(t => normalizer.Process("FUT", AdaptiveSourceTickReader.ToRaw(t))).OfType<CleanObserverTick>()
            .OrderBy(t => t.AvailableAt).ThenBy(t => t.Id);
        foreach (var group in ordered.GroupBy(t => t.AvailableAt)) whole.ProcessAvailabilityGroup(group.Select(t => ("FUT", t)).ToArray());

        var services = AdaptiveRecoveryReliabilityTests.World.Services(NewSource, NewObserver, new AdaptiveRecoveryOptions { LiveReadBatchSize = 25, ReadWindowSeconds = 10 });
        await using var provider = services;
        var worker = (AdaptiveObserverWorker)provider.GetService(typeof(AdaptiveObserverWorker))!;
        worker.MarkHistoricalBootstrapDone(Day);
        var live = await worker.TryStartOrRecoverAsync(Day, At, default);          // nothing to replay yet: the live poll starts with no cursor
        Assert.NotNull(live);
        Assert.Equal(0, live!.LastFetchedRawId);
        await using (var source = NewSource()) { source.Ticks.AddRange(ticks); await source.SaveChangesAsync(); }

        await worker.PollLiveAsync(live, At.AddMinutes(30), default, forceRuntimeFlush: true);

        Assert.Equal(rows, live.LastFetchedRawId);
        Assert.Empty(live.Pending);
        Assert.NotEmpty(whole.FutureBars);
        Assert.Equal(JsonSerializer.Serialize(whole.FutureBars), JsonSerializer.Serialize(live.Engine.FutureBars));
        Assert.Equal(JsonSerializer.Serialize(whole.PartialBar), JsonSerializer.Serialize(live.Engine.PartialBar));
        Assert.True(live.PeakPendingRows <= 70, $"peak {live.PeakPendingRows} of {rows}");     // about 10 s of rows plus one batch of 25
        await using var check = NewObserver();
        Assert.Equal(whole.FutureBars.Count(b => b.IsComplete), await check.FutureBars.CountAsync());
    }

    [Fact]
    public async Task ObservationFinalization_FromReleasedBatches_EqualsAWholeDayLoad()
    {
        const int rows = 30000;                                        // three source batches
        var original = AdaptiveWeak2ObservationServiceTests.Context();
        var options = new[]
        {
            new ObserverOptionInstrument("C", "CALL", OptionType.Call, 23000, original.WeeklyOptionExpiry, 65),
            new ObserverOptionInstrument("P", "PUT", OptionType.Put, 23000, original.WeeklyOptionExpiry, 65),
        };
        var context = original with { Options = options };
        await using var source = Source();
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var start = At;
        DateTimeOffset BarEnd(int seq) => start.AddSeconds(seq == 10 ? 1000 : seq == 11 ? 1005 : seq == 12 ? 1010 : 1100);
        var ticks = new List<Tick>(rows);
        for (var i = 1; i <= rows; i++)
        {
            var token = i % 2 == 0 ? "C" : "P";
            var seconds = i / 10.0;
            // Open interest appears only long before the base bar and once between base and trigger: the first must survive the release of everything older than the base.
            long? oi = i is 11 or 12 ? 500 : i == 10051 ? 540 : null;
            var ask = 125m + (i % 7);
            var tick = Row(i, token, start.AddSeconds(seconds), i, oi);
            tick.Depth = new MarketDepth(ask - 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, ask, 1, 0, 0, 0, 0, 0, 0, 0, 0);
            ticks.Add(tick);
        }

        source.Ticks.AddRange(ticks);
        await source.SaveChangesAsync();
        db.FutureBars.AddRange(new[] { 10, 11, 12, 17 }.Select(seq => new AdaptiveFutureBarRow
        {
            SessionId = 1, BarSeq = seq, StartAvailableAtUtc = BarEnd(seq).AddSeconds(-5), EndAvailableAtUtc = BarEnd(seq), Close = 23000 + seq,
        }));
        AdaptiveWeak2ObservationRow Pending() => new()
        {
            SessionId = 1, StrongBaseBarSeq = 10, Weak1BarSeq = 11, TriggerBarSeq = 12, H5TargetBarSeq = 17, OptionSide = OptionType.Put, TriggerTimestampUtc = BarEnd(12),
            ReversalDirection = -1, OldTrendDirection = 1, Status = AdaptiveObservationStatus.PendingH5, SelectionPolicy = AdaptiveWeak2ObservationService.SelectionPolicyV1,
        };
        db.Weak2Observations.Add(Pending());
        await db.SaveChangesAsync();

        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var all = new Dictionary<string, List<CleanObserverTick>> { ["C"] = [], ["P"] = [] };
        foreach (var tick in ticks)
            if (normalizer.Process(tick.Token, AdaptiveSourceTickReader.ToRaw(tick)) is { } clean) all[tick.Token].Add(clean);
        var expected = Pending();
        AdaptiveWeak2ObservationService.FinalizeFromCleanTicks(context, expected, BarEnd(10), BarEnd(17), 23017 - 23012, all, sessionClosing: false);
        Assert.Equal(AdaptiveObservationStatus.Completed, expected.Status);

        var service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        await service.FinalizeSessionAsync(source, db, context, default);

        var actual = await db.Weak2Observations.AsNoTracking().SingleAsync();
        foreach (var property in typeof(AdaptiveWeak2ObservationRow).GetProperties().Where(p => p.Name != nameof(AdaptiveWeak2ObservationRow.Id)))
            Assert.True(Equals(property.GetValue(expected), property.GetValue(actual)), $"{property.Name}: {property.GetValue(expected)} != {property.GetValue(actual)}");
        Assert.Equal(500L + 500L, actual.PairOiStrongBase);
        Assert.True(service.PeakCachedTicks < rows * 2 / 3, $"peak {service.PeakCachedTicks} of {rows}");
        await service.VerifyCompletedAsync(source, db, context, default);          // the restart verification reads the same way and agrees
    }

    /// <summary>
    /// Env-gated synthetic memory measurement (set NSE_MEMORY_BENCH=1): one full session of generated ticks through the real engine with the full option universe, released as they are processed. It measures what
    /// the engine itself must retain; database read buffers are bounded separately (see the diagnostics asserted above). Not a recovery-time certification.
    /// </summary>
    [Fact]
    public void EngineRetention_OnASyntheticFullSession()
    {
        if (Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH") != "1") return;
        const int seconds = 22500, strikes = 40;
        var open = At.AddMinutes(-15);
        var expiry = Day.AddDays(4);
        var options = Enumerable.Range(0, strikes).SelectMany(k => new[]
        {
            new ObserverOptionInstrument($"C{k}", $"C{k}", OptionType.Call, 22000 + 50 * k, expiry, 65),
            new ObserverOptionInstrument($"P{k}", $"P{k}", OptionType.Put, 22000 + 50 * k, expiry, 65),
        }).ToArray();

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var engine = new AdaptiveObserverEngine(new AdaptiveSessionDefinition(Day, "FUT", "FUT", expiry, 65, AdaptiveFixedClockV1.BarVolume, AdaptiveFixedClockV1.RollingWindowBars),
            expiry, .065, .2, At, options, null, "SPOT", projectStrikes: true, selfAnchorCutoffUtc: At, atmRule: AtmCenterRule.SpotV1);
        long id = 0, ticks = 0, volume = 0, optionVolume = 0;
        var bars = 0;
        var group = new List<(string, CleanObserverTick)>(options.Length + 2);
        for (var s = 0; s < seconds; s++)
        {
            var at = open.AddSeconds(s);
            group.Clear();
            volume += 650;
            var price = 22975 + Math.Sin(s / 300.0) * 80;
            group.Add(("SPOT", new CleanObserverTick(++id, at, at, price, 0, 0, 0, 0, 0, null)));
            group.Add(("FUT", new CleanObserverTick(++id, at, at, price + (s % 2), price - .5, price + .5, 650, 650, volume, 1_000_000 + s)));
            optionVolume += 65;
            foreach (var o in options)
            {
                var value = Math.Max(5, o.OptionType == OptionType.Call ? price - o.Strike + 90 : o.Strike - price + 90);
                group.Add((o.Token, new CleanObserverTick(++id, at, at, value, value - .25, value + .25, 650, 650, optionVolume, 100_000 + s)));
            }

            ticks += group.Count;
            bars += engine.ProcessAvailabilityGroup(group.ToArray()).Count;
        }

        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        var retained = GC.GetTotalMemory(true) - heapBefore;
        var peak = Process.GetCurrentProcess().PeakWorkingSet64;
        var line = $"synthetic engine session: ticks={ticks}, bars={bars}, retained={retained / 1048576.0:0.0} MiB, allocated={allocated / 1048576.0:0.0} MiB, process peak working set={peak / 1048576.0:0.0} MiB, elapsed={timer.Elapsed.TotalSeconds:0.0} s";
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH_OUT") is { Length: > 0 } path) File.WriteAllText(path, line);

        // Checkpoint cost on the end-of-session state (its largest): serialize, compress, restore. The database write is not part of this figure.
        var costs = new List<string>();
        for (var pass = 0; pass < 4; pass++)
        {
            var clock = Stopwatch.StartNew();
            var before = GC.GetTotalAllocatedBytes(true);
            var bytes = ObserverStateSerializer.Serialize(engine);
            var serialized = clock.Elapsed.TotalMilliseconds;
            using var compressed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
            var zipped = clock.Elapsed.TotalMilliseconds;
            var saveAllocated = GC.GetTotalAllocatedBytes(true) - before;
            var restored = ObserverStateSerializer.Deserialize<AdaptiveObserverEngine>(bytes);
            Assert.Equal(engine.FutureBars.Count, restored.FutureBars.Count);
            costs.Add($"checkpoint pass {pass + 1}: raw={bytes.Length / 1048576.0:0.00} MiB, gzip={compressed.Length / 1048576.0:0.00} MiB, serialize={serialized:0} ms, compress={zipped - serialized:0} ms, restore={clock.Elapsed.TotalMilliseconds - zipped:0} ms, allocated by save={saveAllocated / 1048576.0:0.0} MiB");
        }

        foreach (var cost in costs) output.WriteLine(cost);
        if (Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH_OUT") is { Length: > 0 } same2) File.AppendAllText(same2, Environment.NewLine + string.Join(Environment.NewLine, costs));
        GC.KeepAlive(engine);

        long Bytes<T>(Func<int, T> make)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            var list = new List<T>(100_000);
            for (var i = 0; i < 100_000; i++) list.Add(make(i));
            var size = (GC.GetTotalAllocatedBytes(true) - before) / 100_000;
            GC.KeepAlive(list);
            return size;
        }

        var perRow = $"per held row: ObserverTokenTick={Bytes(i => new ObserverTokenTick("12345", new CleanObserverTick(i, open, open, 1, 1, 1, 1, 1, 1, 1)))} B, CleanObserverTick={Bytes(i => new CleanObserverTick(i, open, open, 1, 1, 1, 1, 1, 1, 1))} B";
        output.WriteLine(perRow);
        if (Environment.GetEnvironmentVariable("NSE_MEMORY_BENCH_OUT") is { Length: > 0 } same) File.AppendAllText(same, Environment.NewLine + perRow);
    }
}
