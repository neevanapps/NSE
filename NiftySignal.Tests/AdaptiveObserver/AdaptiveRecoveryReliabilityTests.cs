using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>Recovery reliability (production mandate sections 9-12): bounded reads with unchanged ordering, progress, time limit, visible failure, true heartbeat time. Synthetic data, in-memory databases.</summary>
public sealed class AdaptiveRecoveryReliabilityTests
{
    static readonly DateOnly Day = new(2026, 9, 25);
    static readonly DateTimeOffset Open = new(2026, 9, 25, 3, 45, 0, TimeSpan.Zero);   // 09:15 IST
    static readonly DateTimeOffset At = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);      // 09:30 IST, the fixture's signal start

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 37)]
    [InlineData(3, 120)]
    [InlineData(4, 90000)]
    public async Task SlicedRecoveryStream_EqualsWholeDayReadNormalizeAndSort(int seed, int windowSeconds)
    {
        await using var source = Source();
        var random = new Random(seed);
        string[] tokens = ["A", "B", "C"];
        var last = new Dictionary<string, Tick>();
        var at = Open.AddSeconds(-90);                       // includes rows before the day's query start and before the observation start
        for (var id = 1; id <= 1500; id++)
        {
            at = at.AddMilliseconds(random.Next(0, 900));
            if (id == 700) at = at.AddMinutes(20);           // a silent gap: several empty slices
            var token = tokens[random.Next(tokens.Length)];
            // Receive time slightly out of Id order (less than the smallest slice); exchange time in whole seconds, sometimes later than receipt, so AvailableAt ties occur.
            var received = at.AddMilliseconds(-random.Next(0, 3000));
            var exchange = DateTimeOffset.FromUnixTimeSeconds(received.ToUnixTimeSeconds() + random.Next(-2, 4));
            var duplicate = random.Next(5) == 0 && last.ContainsKey(token);
            var tick = duplicate
                ? new Tick { Id = id, Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = last[token].ExchangeTimestamp, ReceivedAt = received, LastPrice = last[token].LastPrice, Volume = last[token].Volume }
                : new Tick { Id = id, Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = exchange, ReceivedAt = received, LastPrice = 100 + random.Next(50), Volume = id * 10 };
            last[token] = tick;
            source.Ticks.Add(tick);
        }

        await source.SaveChangesAsync();
        var reader = new AdaptiveSourceTickReader();
        // Close to the end of the data, as in a live restart: only the last stretch is later than the cutoff.
        var through = at.AddSeconds(-60);

        var raw = await reader.ReadRawSessionAsync(source, Day, tokens, through, default, includeBeyondThrough: true);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<ObserverTokenTick>();
        var pending = new List<ObserverTokenTick>();
        foreach (var item in raw)
            if (normalizer.Process(item.Token, item.Tick) is { } tick && tick.AvailableAt >= Open)
                (tick.AvailableAt <= through ? clean : pending).Add(new ObserverTokenTick(item.Token, tick));
        clean = clean.OrderBy(x => x.Tick.AvailableAt).ThenBy(x => x.Tick.Id).ToList();

        var stream = new AdaptiveRecoveryTickStream(reader, source, Day, tokens, Open, through, TimeSpan.FromSeconds(windowSeconds));
        var streamed = new List<ObserverTokenTick>();
        DateTimeOffset? previousBatchEnd = null;
        while (await stream.NextAsync(default) is { } batch)
        {
            if (batch.Count == 0) continue;
            // An availability group is never split across two batches.
            Assert.True(previousBatchEnd is null || batch[0].Tick.AvailableAt > previousBatchEnd);
            previousBatchEnd = batch[^1].Tick.AvailableAt;
            streamed.AddRange(batch);
        }

        Assert.NotEmpty(clean);
        Assert.NotEmpty(pending);
        Assert.Equal(clean, streamed);
        Assert.Equal(pending, stream.Pending);
        Assert.Equal(raw.Max(x => x.Tick.Id), stream.LastRawId);
        Assert.Equal(raw.Count, stream.RawRows);
        Assert.Null(await stream.NextAsync(default));
        if (windowSeconds <= 37) Assert.True(stream.PeakBufferedRows < raw.Count / 4, $"peak {stream.PeakBufferedRows} of {raw.Count}");
        foreach (var token in tokens)
        {
            var repeat = raw.Last(x => x.Token == token).Tick with { Id = 99999 };
            Assert.Null(stream.Normalizer.Process(token, repeat));       // same duplicate memory is handed to live polling
        }
    }

    [Fact]
    public async Task SlicedRecoveryStream_FailsClosedWhenIdOrderDisorderExceedsTheReadAhead()
    {
        await using var source = Source();
        source.Ticks.AddRange(Row(1, Open.AddSeconds(100), 1000), Row(2, Open.AddSeconds(1), 1100));
        await source.SaveChangesAsync();
        var stream = new AdaptiveRecoveryTickStream(new(), source, Day, ["FUT"], Open, Open.AddMinutes(10), TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => { while (await stream.NextAsync(default) is not null) { } });
        Assert.Contains("ReadWindowSeconds", error.Message);
    }

    [Fact]
    public async Task SuccessfulRecovery_PublishesProgressAndClearsThePreviousFailure()
    {
        await using var world = await World.CreateAsync();
        await using var source = world.NewSource();
        await using var db = world.NewObserver();
        var runtime = await db.Runtime.SingleAsync();
        runtime.RuntimeStatus = AdaptiveRuntimeStatus.RecoveryFailed; runtime.RecoveryFailureCount = 2; runtime.LastError = "earlier failure";
        await db.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;

        await world.Recovery().RecoverAsync(source, db, world.Context, At.AddMinutes(1), default, TimeSpan.FromMinutes(5));

        var row = await world.NewObserver().Runtime.SingleAsync();
        Assert.Equal(AdaptiveRuntimeStatus.Live, row.RuntimeStatus);
        Assert.Equal("complete", row.RecoveryPhase);
        Assert.Equal(13, row.RecoveryTicksProcessed);
        Assert.Equal(12, row.RecoveryBarsReplayed);
        Assert.Equal(0, row.RecoveryPersistedBarsAtStart);
        Assert.Equal(At.AddSeconds(12), row.RecoveryReplayedThroughUtc);
        Assert.Equal(0, row.RecoveryFailureCount);
        Assert.Null(row.LastError);
        Assert.True(row.LastHeartbeatUtc >= before);
        Assert.True(row.LastRecoveryProgressUtc >= before);
    }

    [Fact]
    public async Task ExceededTimeLimit_IsReportedAsTimeout_AndCallerCancellationIsNot()
    {
        await using var world = await World.CreateAsync();
        await using var source = world.NewSource();
        await using var db = world.NewObserver();
        var error = await Assert.ThrowsAsync<AdaptiveRecoveryTimeoutException>(() => world.Recovery().RecoverAsync(source, db, world.Context, At.AddMinutes(1), default, TimeSpan.Zero));
        Assert.Contains("limit", error.Message);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await using var other = world.NewObserver();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Recovery().RecoverAsync(source, other, world.Context, At.AddMinutes(1), cancelled.Token, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task WorkerRecordsAFailedRecovery_InsteadOfLeavingItRebuilding()
    {
        await using var world = await World.CreateAsync();
        await using (var db = world.NewObserver())
        {
            // A persisted bar the source cannot rebuild: recovery must refuse.
            db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId = world.Context.Session.Id, BarSeq = 999, StartAvailableAtUtc = At, EndAvailableAtUtc = At });
            await db.SaveChangesAsync();
        }

        await using var services = world.Services(new AdaptiveRecoveryOptions());
        var worker = services.GetRequiredService<AdaptiveObserverWorker>();
        worker.MarkHistoricalBootstrapDone(Day);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.TryStartOrRecoverAsync(Day, At.AddMinutes(1), default));
            var row = await world.NewObserver().Runtime.SingleAsync();
            Assert.Equal(AdaptiveRuntimeStatus.RecoveryFailed, row.RuntimeStatus);
            Assert.Equal(attempt, row.RecoveryFailureCount);
            Assert.Contains("exceed reconstructed source history", row.LastError);
        }
    }

    [Fact]
    public async Task WorkerRecordsATimedOutRecovery()
    {
        await using var world = await World.CreateAsync();
        await using var services = world.Services(new AdaptiveRecoveryOptions { TimeLimitSeconds = 0 });
        var worker = services.GetRequiredService<AdaptiveObserverWorker>();
        worker.MarkHistoricalBootstrapDone(Day);
        await Assert.ThrowsAsync<AdaptiveRecoveryTimeoutException>(() => worker.TryStartOrRecoverAsync(Day, At.AddMinutes(1), default));
        var row = await world.NewObserver().Runtime.SingleAsync();
        Assert.Equal(AdaptiveRuntimeStatus.RecoveryFailed, row.RuntimeStatus);
        Assert.Contains("limit", row.LastError);
    }

    [Fact]
    public async Task LivePoll_DrainsABacklogInBatches_AndStampsTheHeartbeatWhenItIsWritten()
    {
        await using var world = await World.CreateAsync();
        var clock = new FixedClock(At.AddHours(3));
        await using var services = world.Services(new AdaptiveRecoveryOptions { LiveReadBatchSize = 3 }, clock);
        var worker = services.GetRequiredService<AdaptiveObserverWorker>();
        worker.MarkHistoricalBootstrapDone(Day);
        var live = await worker.TryStartOrRecoverAsync(Day, At.AddMinutes(1), default);
        Assert.NotNull(live);
        await using (var source = world.NewSource())
        {
            for (var i = 13; i <= 40; i++) source.Ticks.Add(Row(i + 1, At.AddSeconds(i + 60), 1000 + 100 * i));
            await source.SaveChangesAsync();
        }

        await worker.PollLiveAsync(live!, At.AddMinutes(5), default, forceRuntimeFlush: true);

        Assert.Equal(41, live!.LastFetchedRawId);
        Assert.Empty(live.Pending);
        var row = await world.NewObserver().Runtime.SingleAsync();
        Assert.Equal(40, row.LastCompletedBarSeq);
        Assert.Equal(clock.GetUtcNow(), row.LastHeartbeatUtc);       // the write instant, not the poll's start instant
    }

    [Fact]
    public async Task LiveDepthPoll_DrainsEveryRowInBatches()
    {
        await using var source = Source();
        for (var i = 1; i <= 20; i++) source.Ticks.Add(Row(i, At.AddSeconds(i), 1000 + i));
        await source.SaveChangesAsync();
        var gateway = new FuturesDepth5Gateway(Options.Create(new Top5MicrostructureOptions { Enabled = true }), new AdaptiveDepthTickReader(),
            Options.Create(new AdaptiveRecoveryOptions { LiveReadBatchSize = 3 }));
        var feed = new FuturesDepth5Feed();
        feed.Ingest([new DepthRawRow("FUT", 1, At.AddSeconds(1), At.AddSeconds(1), null)], At);     // an existing cursor, so the batched queries are used

        await gateway.PollAsync(source, feed, "FUT", Day, At, default);

        Assert.Equal(20, feed.LastFetchedId);
    }

    [Fact]
    public void RecoveryText_ShowsProgressWithoutInventingATotal_AndFailureLoudly()
    {
        var now = At.AddMinutes(10);
        var row = new AdaptiveObserverRuntimeRow
        {
            RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding, LastHeartbeatUtc = now, LastRecoveryStartedUtc = now.AddSeconds(-125), LastRecoveryProgressUtc = now.AddSeconds(-2),
            RecoveryPhase = "replaying", RecoveryBarsReplayed = 120, RecoveryPersistedBarsAtStart = 300, RecoveryTicksProcessed = 45000, RecoveryReplayedThroughUtc = At,
        };
        var text = ObserverRecoveryText.Describe(row, now);
        Assert.Contains("elapsed 2m 5s", text);
        Assert.Contains("120 bars replayed (300 persisted before restart)", text);
        Assert.Contains("last progress 2s ago", text);
        Assert.DoesNotContain("%", text);

        row.RuntimeStatus = AdaptiveRuntimeStatus.RecoveryFailed; row.RecoveryFailureCount = 3; row.LastError = "boom";
        text = ObserverRecoveryText.Describe(row, now);
        Assert.StartsWith("RECOVERY FAILED (consecutive failures: 3)", text);
        Assert.Contains("boom", text);
    }

    static Tick Row(long id, DateTimeOffset at, long volume) => new()
    {
        Id = id, Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000 + id, Volume = volume,
    };

    static NiftySignalDbContext Source(string? name = null) => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>One frozen session with 13 future ticks (12 bars of 100 contracts) in shared in-memory databases.</summary>
    internal sealed class World : IAsyncDisposable
    {
        readonly string _source = Guid.NewGuid().ToString();
        readonly string _observer = Guid.NewGuid().ToString();
        public required AdaptiveObserverSessionContext Context { get; init; }

        public NiftySignalDbContext NewSource() => Source(_source);

        public AdaptiveObserverDbContext NewObserver() => new(ObserverOptions().Options);

        DbContextOptionsBuilder<AdaptiveObserverDbContext> ObserverOptions() => new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(_observer).ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

        public AdaptiveStateRecoveryService Recovery() => new(new(), new AdaptiveObserverPersistence(Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveWeak2ObservationService>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveStateRecoveryService>.Instance);

        public static async Task<World> CreateAsync()
        {
            var context = AdaptiveWeak2ObservationServiceTests.Context();
            context.Session.WeeklyOptionExpiry = context.WeeklyOptionExpiry;
            context.Session.BaseBarVolume = 100; context.Session.RollingWindowBars = 10; context.Session.LotSize = 65; context.Session.RiskFreeRate = .065;
            context.Session.StrongThreshold = .2;
            context.Session.OptionUniverseJson = JsonSerializer.Serialize(Array.Empty<ObserverOptionInstrument>());
            var world = new World { Context = context };
            await using var db = world.NewObserver();
            db.Sessions.Add(context.Session);
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = At });
            await db.SaveChangesAsync();
            db.Entry(context.Session).State = EntityState.Detached;
            await using var source = world.NewSource();
            for (var i = 0; i <= 12; i++) source.Ticks.Add(Row(i + 1, At.AddSeconds(i), 1000 + 100 * i));
            await source.SaveChangesAsync();
            return world;
        }

        public ServiceProvider Services(AdaptiveRecoveryOptions options, TimeProvider? clock = null) => Services(NewSource, NewObserver, options, clock);

        /// <summary>The Host's observer worker wired to the given databases.</summary>
        internal static ServiceProvider Services(Func<NiftySignalDbContext> source, Func<AdaptiveObserverDbContext> observer, AdaptiveRecoveryOptions options, TimeProvider? clock = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
            services.AddScoped(_ => source());
            services.AddScoped(_ => observer());
            services.AddSingleton(Options.Create(options));
            services.AddSingleton(Options.Create(new DashboardPushOptions { HubUrl = "http://127.0.0.1:1/hub" }));
            services.AddSingleton<IOptionsMonitor<PricingOptions>>(new Monitor());
            if (clock is not null) services.AddSingleton(clock);
            services.AddSingleton<AdaptiveSourceTickReader>();
            services.AddSingleton<AdaptiveHistoricalBootstrapService>();
            services.AddSingleton(sp => new AdaptiveSessionCoordinator(sp.GetRequiredService<AdaptiveSourceTickReader>(), sp.GetRequiredService<IOptionsMonitor<PricingOptions>>(),
                sp.GetRequiredService<ILogger<AdaptiveSessionCoordinator>>()));
            services.AddSingleton<AdaptiveObserverPersistence>();
            services.AddSingleton<AdaptiveSupplementalPersistence>();
            services.AddSingleton<AdaptiveWeak2ObservationService>();
            services.AddSingleton<AdaptiveStateRecoveryService>();
            services.AddSingleton<AdaptiveEndedSessionRecoveryService>();
            services.AddSingleton<DashboardPushClient>();
            services.AddSingleton<AdaptiveObserverWorker>();
            return services.BuildServiceProvider();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        sealed class Monitor : IOptionsMonitor<PricingOptions>
        {
            public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
            public PricingOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
        }
    }
}
