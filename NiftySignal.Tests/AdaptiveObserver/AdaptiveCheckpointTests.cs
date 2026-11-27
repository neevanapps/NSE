using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Durable Observer checkpoints (audit finding F73): a recovery that continues from a checkpoint persists the same bars, metrics, sidecars, observations and publications as one that replays the day without
/// interruption. Synthetic data with split volume bars, a moving ATM strike, active Top-5 depth on the future and the options, duplicates and receive-order disorder; in-memory databases.
/// </summary>
[Collection("adaptive-postgres")]
public sealed class AdaptiveCheckpointTests
{
    static readonly DateOnly Day = new(2026, 9, 25);
    static readonly DateOnly Expiry = Day.AddDays(4);
    static readonly DateTimeOffset At0 = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);   // 09:30 IST
    const int Seconds = 240;
    static readonly DateTimeOffset End = At0.AddSeconds(Seconds + 60);

    static List<ObserverOptionInstrument> Chain() => Enumerable.Range(0, 9).SelectMany(k => new[] { OptionType.Call, OptionType.Put }
        .Select(side => new ObserverOptionInstrument((side == OptionType.Call ? "C" : "P") + k, "OPT" + k, side, 22800 + 50 * k, Expiry, 65))).ToList();

    static List<Tick> Ticks()
    {
        var random = new Random(11);
        var ticks = new List<Tick>();
        var volume = new Dictionary<string, long>();
        long id = 1;
        MarketDepth Depth(decimal mid)
        {
            long Q() => 10 + random.Next(200);
            return new(mid - .5m, Q(), mid - .55m, Q(), mid - .6m, Q(), mid - .65m, Q(), mid - .7m, Q(), mid + .5m, Q(), mid + .55m, Q(), mid + .6m, Q(), mid + .65m, Q(), mid + .7m, Q());
        }

        for (var second = 0; second < Seconds; second++)
        {
            var spot = 22920 + second * 0.75 + random.Next(-6, 7);                     // crosses several 50-point strikes
            void Add(string token, decimal price, long traded, MarketDepth? depth)
            {
                volume[token] = volume.GetValueOrDefault(token, 1000) + traded;
                var received = At0.AddSeconds(second).AddMilliseconds(random.Next(0, 900));
                ticks.Add(new Tick
                {
                    Id = id++, Token = token, Exchange = token == "NIFTYSPOT" ? Exchange.Nse : Exchange.Nfo, ExchangeTimestamp = At0.AddSeconds(second), ReceivedAt = received,
                    LastPrice = price, Volume = volume[token], OpenInterest = 100000 + random.Next(500), Depth = depth,
                });
            }

            Add("NIFTYSPOT", (decimal)spot, 0, null);
            foreach (var option in Chain())
            {
                if (second > 0 && random.Next(3) == 0) continue;
                var model = AdaptiveResearchPricing.Price(option.OptionType, spot + 10, option.Strike, TimeToExpiry.YearsUntilExpiry(Expiry, At0.AddSeconds(second)), .065, .18);
                var price = Math.Max(1m, Math.Round((decimal)model * 20) / 20 + random.Next(-2, 3) * .05m);
                Add(option.Token, price, random.Next(0, 40), Depth(price));
            }

            var future = Math.Round((decimal)spot + 10 + random.Next(-3, 4));
            // Occasionally one update trades several bar volumes (a split-volume event).
            Add("FUT", future, second % 17 == 5 ? 370 : random.Next(0, 90), Depth(future));
            if (second % 29 == 3)
            {
                var repeat = ticks[^1];
                ticks.Add(new Tick { Id = id++, Token = repeat.Token, Exchange = repeat.Exchange, ExchangeTimestamp = repeat.ExchangeTimestamp, ReceivedAt = repeat.ReceivedAt.AddMilliseconds(1), LastPrice = repeat.LastPrice, Volume = repeat.Volume, OpenInterest = repeat.OpenInterest, Depth = repeat.Depth });
            }
        }

        return ticks;
    }

    static AdaptiveObserverSessionContext Context()
    {
        var c = AdaptiveWeak2ObservationServiceTests.Context();
        c.Session.RiskFreeRate = .065;
        c.Session.AtmRuleVersion = AtmCenterRules.SpotV1Version;
        c.Session.WeeklyOptionExpiry = Expiry;
        c.Session.BaseBarVolume = 100; c.Session.RollingWindowBars = 10; c.Session.LotSize = 65; c.Session.StrongThreshold = .2;
        c.Session.OptionUniverseJson = JsonSerializer.Serialize(Chain());
        return c with { Options = Chain(), WeeklyOptionExpiry = Expiry };
    }

    sealed class World
    {
        readonly string _source = Guid.NewGuid().ToString();
        public NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(_source).Options);

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            await using var source = world.Source();
            source.Instruments.Add(new Instrument { Token = "NIFTYSPOT", Exchange = Exchange.Nse, TradingSymbol = "Nifty 50", InstrumentType = InstrumentType.Index, Underlying = "NIFTY", AsOfDate = Day });
            source.Ticks.AddRange(Ticks());
            await source.SaveChangesAsync();
            return world;
        }
    }

    /// <summary>The observer database on the real server (publications are PostgreSQL statements); disposable.</summary>
    sealed class Observer : IAsyncDisposable
    {
        TempAdaptiveDatabase _temp = null!;
        // Pooled: the Host opens many short-lived contexts per bar, and a new server connection each time dominates the run.
        public AdaptiveObserverDbContext Db() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseNpgsql(new Npgsql.NpgsqlConnectionStringBuilder(_temp.ConnectionString) { Pooling = true }.ConnectionString).AddInterceptors(Saves).Options);
        public SaveHook Saves { get; } = new();
        /// <summary>Warnings and errors of every Host built on this database, quoted by failing assertions.</summary>
        public Problems Log { get; } = new();

        public static async Task<Observer> CreateAsync()
        {
            var observer = new Observer { _temp = await TempAdaptiveDatabase.CreateAsync("niftysignal_adaptive_obs_test_") };
            await observer._temp.MigrateAsync();
            await using var db = observer.Db();
            var context = Context();
            db.Sessions.Add(context.Session);
            await db.SaveChangesAsync();
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id, LastHeartbeatUtc = At0 });
            await db.SaveChangesAsync();
            return observer;
        }

        public ValueTask DisposeAsync() => _temp.DisposeAsync();
    }

    static AdaptiveRecoveryOptions Recovery(bool checkpoints) => new()
    {
        CheckpointsEnabled = checkpoints, CheckpointIntervalSeconds = 0, CheckpointRetention = 3, LiveReadBatchSize = 150, ReadWindowSeconds = 20,
    };

    sealed class Problems : ILoggerProvider, ILogger
    {
        readonly List<string> _lines = [];
        public override string ToString() { lock (_lines) return string.Join(Environment.NewLine, _lines.TakeLast(12)); }
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (level >= LogLevel.Warning) lock (_lines) _lines.Add($"{level} {format(state, ex)} {ex?.Message}");
        }
        public void Dispose() { }
    }

    sealed class SaveHook : SaveChangesInterceptor
    {
        public Action<DbContext>? After { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            After?.Invoke(data.Context!);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>The Host's own services, as a fresh process would build them.</summary>
    static ServiceProvider Host(World world, Observer observer, bool checkpoints, AdaptiveRecoveryOptions? recovery = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddProvider(observer.Log));
        services.AddScoped(_ => world.Source());
        services.AddScoped(_ => observer.Db());
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(recovery ?? Recovery(checkpoints)));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new Top5MicrostructureOptions { Enabled = true, OptionsEnabled = true }));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new DashboardPushOptions { HubUrl = "http://127.0.0.1:1/hub" }));
        services.AddSingleton<IOptionsMonitor<PricingOptions>>(new Monitor());
        services.AddSingleton<AdaptiveSourceTickReader>();
        services.AddSingleton<AdaptiveDepthTickReader>();
        services.AddSingleton<FuturesDepth5Gateway>();
        services.AddSingleton<AdaptiveHistoricalBootstrapService>();
        services.AddSingleton(sp => new AdaptiveSessionCoordinator(sp.GetRequiredService<AdaptiveSourceTickReader>(), sp.GetRequiredService<IOptionsMonitor<PricingOptions>>(),
            sp.GetRequiredService<ILogger<AdaptiveSessionCoordinator>>()));
        services.AddSingleton<AdaptiveObserverPersistence>();
        services.AddSingleton<AdaptiveSupplementalPersistence>();
        services.AddSingleton<AdaptiveSessionSupplementalService>();
        services.AddSingleton<AdaptiveWeak2ObservationService>();
        services.AddSingleton<AdaptiveCheckpointStore>();
        services.AddSingleton<AdaptiveStateRecoveryService>();
        services.AddSingleton<AdaptiveEndedSessionRecoveryService>();
        services.AddSingleton<DashboardPushClient>();
        services.AddSingleton<AdaptiveObserverWorker>();
        return services.BuildServiceProvider();
    }

    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }

    static async Task<(AdaptiveRecoveryResult Result, long TicksProcessed)> Recover(World world, Observer observer, DateTimeOffset through, bool checkpoints = true, AdaptiveRecoveryOptions? recovery = null)
    {
        await using var host = Host(world, observer, checkpoints, recovery);
        await using var source = world.Source();
        await using var db = observer.Db();
        var result = await host.GetRequiredService<AdaptiveStateRecoveryService>().RecoverAsync(source, db, Context(), through, default);
        await using var check = observer.Db();
        return (result, (await check.Runtime.SingleAsync()).RecoveryTicksProcessed);
    }

    /// <summary>Every persisted market row of the session, without surrogate keys and write instants.</summary>
    static async Task<Dictionary<string, List<string>>> Persisted(Observer observer, AdaptiveRecoveryResult result)
    {
        await using var db = observer.Db();
        await AdaptiveBarPublisher.PublishMissingAsync(db, 1, result.UnverifiedSidecarBars.Count == 0 ? null : result.UnverifiedSidecarBars.Min(), End, default);
        static bool Volatile(System.Reflection.PropertyInfo p) => p.Name == "Id" || (p.PropertyType == typeof(DateTimeOffset) || p.PropertyType == typeof(DateTimeOffset?))
            && new[] { "Created", "Computed", "Attested", "Verified", "Published", "Recorded", "Updated", "Persisted", "Occurred", "Detected" }.Any(p.Name.Contains);
        static async Task<List<string>> Rows<T>(IQueryable<T> query) where T : class
        {
            var properties = typeof(T).GetProperties().Where(p => !Volatile(p)).OrderBy(p => p.Name).ToArray();
            return (await query.AsNoTracking().ToListAsync())
                .Select(row => string.Join('|', properties.Select(p => $"{p.Name}={JsonSerializer.Serialize(p.GetValue(row))}"))).Order(StringComparer.Ordinal).ToList();
        }

        return new()
        {
            ["bars"] = await Rows(db.FutureBars), ["rolling"] = await Rows(db.RollingStates), ["bands"] = await Rows(db.OptionBandBars), ["residuals"] = await Rows(db.OptionResidualBars),
            ["futures"] = await Rows(db.FuturesSupplemental), ["basis"] = await Rows(db.BasisSupplemental), ["options"] = await Rows(db.OptionsSupplemental),
            ["strikes"] = await Rows(db.OptionStrikeBars), ["strikeAttestations"] = await Rows(db.StrikeAttestations), ["closeAtm"] = await Rows(db.BarCloseAtm),
            ["depth5"] = await Rows(db.FuturesDepth5), ["optionsDepth5"] = await Rows(db.OptionsDepth5), ["optionsDepth5Attestations"] = await Rows(db.OptionsDepth5Attestations),
            ["observations"] = await Rows(db.Weak2Observations), ["anchors"] = await Rows(db.ResidualAnchorComponents), ["health"] = await Rows(db.ProjectionHealth),
            ["publications"] = await Rows(db.BarPublications),
        };
    }

    static void AssertSame(Dictionary<string, List<string>> expected, Dictionary<string, List<string>> actual)
    {
        foreach (var (table, rows) in expected)
        {
            Assert.True(rows.Count == actual[table].Count, $"{table}: {rows.Count} rows expected, {actual[table].Count} found");
            for (var i = 0; i < rows.Count; i++)
                Assert.True(rows[i] == actual[table][i], $"{table} row {i} differs: {string.Join(" ## ", rows[i].Split('|').Zip(actual[table][i].Split('|')).Where(p => p.First != p.Second))}");
        }
    }

    static string State(AdaptiveRecoveryResult r) => JsonSerializer.Serialize(new { r.Engine.FutureBars, r.Engine.PartialBar, r.LastFetchedRawId, r.LastProcessedAvailableAt, r.LastProcessedTickId });

    static async Task<(World World, Dictionary<string, List<string>> Rows, AdaptiveRecoveryResult Result, long Ticks)> Reference()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        var (result, ticks) = await Recover(world, observer, End, checkpoints: false);
        var rows = await Persisted(observer, result);
        // The synthetic day really contains what the equivalence has to cover.
        var bars = result.Engine.FutureBars;
        Assert.True(bars.Count > 60);
        Assert.Contains(bars.Zip(bars.Skip(1)), p => p.First.EndAvailableAtUtc == p.Second.EndAvailableAtUtc);   // split volume
        await using var db = observer.Db();
        Assert.True((await db.OptionBandBars.Select(x => x.CenterStrike).Distinct().CountAsync()) >= 3);
        Assert.NotEmpty(rows["depth5"]);
        Assert.NotEmpty(rows["optionsDepth5"]);
        Assert.NotEmpty(rows["strikes"]);
        Assert.NotEmpty(rows["observations"]);
        return (world, rows, result, ticks);
    }

    [Fact]
    public void RestoredEngine_ContinuesIdentically_FromManyIntradayPoints()
    {
        var options = Chain();
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var groups = Ticks().OrderBy(t => t.Id).Select(t => (t.Token, Clean: normalizer.Process(t.Token, AdaptiveSourceTickReader.ToRaw(t)), t.Depth))
            .Where(x => x.Clean is not null).OrderBy(x => x.Clean!.Value.AvailableAt).ThenBy(x => x.Clean!.Value.Id)
            .GroupBy(x => x.Clean!.Value.AvailableAt).Select(g => g.ToArray()).ToList();
        AdaptiveObserverEngine Create() => new(new AdaptiveSessionDefinition(Day, "FUT", "FUT", Expiry, 65, 100), Expiry, .065, .2, At0, options, null, "NIFTYSPOT",
            projectStrikes: true, selfAnchorCutoffUtc: At0.AddSeconds(30), atmRule: AtmCenterRule.SpotV1);
        static string Run(AdaptiveObserverEngine engine, IEnumerable<(string Token, CleanObserverTick? Clean, MarketDepth? Depth)[]> input) =>
            string.Join('\n', input.SelectMany(g => engine.ProcessAvailabilityGroup(g.Select(x => (x.Token, x.Clean!.Value)).ToArray())).Select(p => JsonSerializer.Serialize(p)))
            + JsonSerializer.Serialize(engine.PartialBar);

        var whole = Create();
        var cuts = Enumerable.Range(1, 12).Select(i => i * groups.Count / 13).ToList();
        var restoredFrom = new List<string>();
        var at = 0;
        var midBar = 0;
        foreach (var cut in cuts)
        {
            Run(whole, groups.Skip(at).Take(cut - at));
            at = cut;
            if (whole.PartialBar.AccumulatedVolume > 0) midBar++;
            var restored = ObserverStateSerializer.Deserialize<AdaptiveObserverEngine>(ObserverStateSerializer.Serialize(whole));
            var twin = ObserverStateSerializer.Deserialize<AdaptiveObserverEngine>(ObserverStateSerializer.Serialize(restored));
            var rest = Run(restored, groups.Skip(cut));
            Assert.Equal(rest, Run(twin, groups.Skip(cut)));                 // a restored state can itself be stored and restored
            restoredFrom.Add(rest);
        }

        Assert.True(midBar > 0, "no cut fell inside an unfinished volume bar");
        // An engine that was never stored produces, from each cut, exactly what the restored copy produced.
        for (var i = 0; i < cuts.Count; i++)
        {
            var untouched = Create();
            Run(untouched, groups.Take(cuts[i]));
            Assert.Equal(Run(untouched, groups.Skip(cuts[i])), restoredFrom[i]);
        }
    }

    [PgMigrationFact]
    public async Task RepeatedInterruptedRecoveries_PersistTheSameSessionAsOneUninterruptedReplay()
    {
        var (world, expected, reference, totalTicks) = await Reference();
        await using var observer = await Observer.CreateAsync();
        AdaptiveRecoveryResult result = null!;
        long processed = 0;
        foreach (var second in new[] { 45.0, 101.4, 162.0, 223.7, Seconds + 60 })
        {
            (result, processed) = await Recover(world, observer, At0.AddSeconds(second));
            await using var db = observer.Db();
            Assert.InRange(await db.Checkpoints.CountAsync(), 1, 3);
        }

        Assert.True(processed < totalTicks / 2, $"the last attempt replayed {processed} of {totalTicks} ticks");
        Assert.Equal(State(reference), State(result));
        AssertSame(expected, await Persisted(observer, result));
    }

    [PgMigrationFact]
    public async Task LiveCheckpoints_RestoreAfterARestart_WithTheSameSessionAsAnUninterruptedReplay()
    {
        var (world, expected, reference, totalTicks) = await Reference();
        await using var observer = await Observer.CreateAsync();
        await using (var host = Host(world, observer, checkpoints: true))
        {
            var worker = host.GetRequiredService<AdaptiveObserverWorker>();
            AdaptiveRecoveryResult started;
            await using (var source = world.Source())
            await using (var db = observer.Db())
                started = await host.GetRequiredService<AdaptiveStateRecoveryService>().RecoverAsync(source, db, Context(), At0.AddSeconds(40), default);
            var live = new AdaptiveObserverWorker.LiveState(Context(), started);
            foreach (var second in new[] { 90.0, 150.3, 187.0 }) await worker.PollLiveAsync(live, At0.AddSeconds(second), default);
            await using var check = observer.Db();
            var newest = await check.Checkpoints.OrderByDescending(x => x.Id).FirstAsync();
            Assert.True(newest.ProcessedThroughUtc > At0.AddSeconds(150), "the newest checkpoint was not written by live processing");
        }

        var (result, processed) = await Recover(world, observer, End);
        Assert.True(processed < totalTicks / 2, $"replayed {processed} of {totalTicks} ticks after the restart");
        Assert.Equal(State(reference), State(result));
        AssertSame(expected, await Persisted(observer, result));
    }

    [PgMigrationFact]
    public async Task UnusableCheckpoints_FallBackToAnOlderOne_ThenToAFullReplay()
    {
        var (world, expected, reference, totalTicks) = await Reference();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(200));
        await using (var db = observer.Db())
        {
            var rows = await db.Checkpoints.OrderByDescending(x => x.Id).ToListAsync();
            Assert.Equal(3, rows.Count);
            rows[0].Payload = rows[0].Payload.Reverse().ToArray();                 // damaged
            rows[1].Identity = new string('0', 64);                                 // written by another build or configuration
            await db.SaveChangesAsync();
        }

        var (result, processed) = await Recover(world, observer, End);              // the third is still usable
        Assert.True(processed < totalTicks, $"{processed} of {totalTicks}");
        AssertSame(expected, await Persisted(observer, result));

        await using (var db = observer.Db())
        {
            foreach (var row in await db.Checkpoints.ToListAsync()) row.PayloadSha256 = new string('0', 64);
            await db.SaveChangesAsync();
        }

        var (full, all) = await Recover(world, observer, End);
        Assert.Equal(totalTicks, all);
        Assert.Equal(State(reference), State(full));
        AssertSame(expected, await Persisted(observer, full));
    }

    [PgMigrationFact]
    public async Task ACheckpointWhoseBarsNoLongerMatchPersistence_IsNotUsed_AndPersistedHistoryIsNotOverwritten()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(150));
        await using (var db = observer.Db())
        {
            var bar = await db.FutureBars.SingleAsync(x => x.BarSeq == 3);
            bar.Close += 1;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Recover(world, observer, End));
        Assert.Contains("mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var after = observer.Db();
        Assert.Equal(AdaptiveRuntimeStatus.Rebuilding, (await after.Runtime.SingleAsync()).RuntimeStatus);   // never published as Live
    }

    [PgMigrationFact]
    public async Task ACancelledAttempt_IsNotLive_AndTheNextOneContinuesFromItsCheckpoint()
    {
        var (world, expected, reference, totalTicks) = await Reference();
        await using var observer = await Observer.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var saves = 0;
        observer.Saves.After = db =>
        {
            // Cancel the attempt (as its timeout would) as soon as a checkpoint holding part of the day is durable.
            if (db.ChangeTracker.Entries<AdaptiveCheckpointRow>().Any(x => x.Entity.LastBarSeq >= 60) && ++saves == 1) cancel.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var host = Host(world, observer, checkpoints: true);
            await using var source = world.Source();
            await using var db = observer.Db();
            await host.GetRequiredService<AdaptiveStateRecoveryService>().RecoverAsync(source, db, Context(), End, cancel.Token);
        });
        observer.Saves.After = null;
        await using (var db = observer.Db())
            Assert.NotEqual(AdaptiveRuntimeStatus.Live, (await db.Runtime.SingleAsync()).RuntimeStatus);

        var (result, processed) = await Recover(world, observer, End);
        Assert.True(processed < totalTicks, $"{processed} of {totalTicks}; {observer.Log}");
        Assert.Equal(State(reference), State(result));
        AssertSame(expected, await Persisted(observer, result));
    }

    [PgMigrationFact]
    public async Task ACheckpointWhoseSidecarRowsHaveGone_IsNotUsed_AndTheReplayRestoresThem()
    {
        var (world, expected, _, totalTicks) = await Reference();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(200));
        await using (var db = observer.Db())
            Assert.Equal(1, await db.FuturesDepth5.Where(x => x.BarSeq == 2).ExecuteDeleteAsync());

        var (result, processed) = await Recover(world, observer, End);
        Assert.Equal(totalTicks, processed);
        AssertSame(expected, await Persisted(observer, result));
    }

    [PgMigrationFact]
    public async Task AReplayThatFailsAfterARestore_DiscardsThatCheckpoint()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(150));
        await Recover(world, observer, End, checkpoints: false);                      // persists the later bars
        long newest;
        await using (var db = observer.Db())
        {
            newest = await db.Checkpoints.MaxAsync(x => x.Id);
            var last = await db.FutureBars.OrderByDescending(x => x.BarSeq).FirstAsync();
            last.Close += 1;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recover(world, observer, End));
        await using var after = observer.Db();
        Assert.False(await after.Checkpoints.AnyAsync(x => x.Id >= newest));
        // The failed attempt saved checkpoints of its own while replaying: none of that pushed out the older ones it falls back to.
        Assert.Equal(2, await after.Checkpoints.CountAsync());
        Assert.NotEqual(AdaptiveRuntimeStatus.Live, (await after.Runtime.SingleAsync()).RuntimeStatus);
    }

    [PgMigrationFact]
    public async Task AVerificationThatFailsAfterARestore_DiscardsThatCheckpoint_SoItIsNotRestoredAgain()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(150));
        await using (var db = observer.Db())
        {
            // A persisted bar the source cannot reproduce: every replayed bar matches, only the closing verification can see it.
            var extra = await db.FutureBars.AsNoTracking().OrderByDescending(x => x.BarSeq).FirstAsync();
            extra.Id = 0; extra.BarSeq += 10_000;
            db.FutureBars.Add(extra);
            await db.SaveChangesAsync();
        }

        long newest;
        await using (var db = observer.Db()) newest = await db.Checkpoints.MaxAsync(x => x.Id);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Recover(world, observer, End));
        Assert.Contains("exceed reconstructed source history", error.Message);
        await using var after = observer.Db();
        Assert.False(await after.Checkpoints.AnyAsync(x => x.Id >= newest));   // the restored one and any written by the failed attempt
        Assert.NotEqual(AdaptiveRuntimeStatus.Live, (await after.Runtime.SingleAsync()).RuntimeStatus);
    }

    /// <summary>The recovery options exactly as the Host and the Observer bind them from configuration.</summary>
    static AdaptiveRecoveryOptions Configured(int retention)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Adaptive:Recovery:CheckpointRetention"] = retention.ToString(), ["Adaptive:Recovery:CheckpointIntervalSeconds"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddAdaptiveObserver(configuration);
        using var provider = services.BuildServiceProvider();
        var bound = provider.GetRequiredService<IOptions<AdaptiveRecoveryOptions>>().Value;
        Assert.Equal(retention, bound.CheckpointRetention);
        var test = Recovery(checkpoints: true);                       // the harness's small read slices; the retention is the configured one
        test.CheckpointRetention = bound.CheckpointRetention;
        return test;
    }

    static async Task<List<long>> CheckpointIds(Observer observer)
    {
        await using var db = observer.Db();
        return await db.Checkpoints.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
    }

    [PgMigrationFact]
    public async Task TheConfiguredRetention_ControlsHowManyCheckpointsArePersisted_AndNothingElseIsRemoved()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        var (first, _) = await Recover(world, observer, At0.AddSeconds(200), recovery: Configured(2));
        Assert.Equal(2, (await CheckpointIds(observer)).Count);
        var rows = await Persisted(observer, first);

        await Recover(world, observer, At0.AddSeconds(200), recovery: Configured(5));
        var raised = (await CheckpointIds(observer)).Count;
        Assert.InRange(raised, 3, 5);
        var (last, _) = await Recover(world, observer, At0.AddSeconds(200), recovery: Configured(1));
        Assert.Single(await CheckpointIds(observer));
        AssertSame(rows, await Persisted(observer, last));
    }

    [PgMigrationFact]
    public async Task Retention_KeepsTheNewestPerIdentity_HoldsTheFallbackWhileARestoreIsUnverified_AndCleansUpInBoundedSteps()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(200));
        await using var host = Host(world, observer, checkpoints: true);
        var store = host.GetRequiredService<AdaptiveCheckpointStore>();
        var original = await CheckpointIds(observer);
        Assert.Equal(3, original.Count);
        await using var db = observer.Db();
        var identity = (await db.Checkpoints.AsNoTracking().Select(x => x.Identity).Distinct().ToListAsync()).Single();
        var state = (await store.FindAsync(db, 1, identity, End, default))!.Value.State;

        // During an unverified restore from the newest: it and everything older stay, the saves of the attempt are limited among themselves.
        for (var i = 0; i < 4; i++) Assert.True(await store.TrySaveAsync(db, identity, state, default, original[^1]));
        var held = await CheckpointIds(observer);
        Assert.Equal(6, held.Count);
        Assert.Equal(original, held.Take(3));
        await store.PruneAsync(db, 1, identity, default);                                  // the restore is verified
        Assert.Equal(held.Skip(3), await CheckpointIds(observer));

        // Rows of another build do not take the places of usable ones, and are limited separately (newest first).
        var other = new string('0', 64);
        for (var i = 0; i < 5; i++) Assert.True(await store.TrySaveAsync(db, other, state, default));
        var all = await CheckpointIds(observer);
        Assert.Equal(6, all.Count);
        Assert.Equal(held.Skip(3), all.Take(3));
        Assert.NotNull(await store.FindAsync(db, 1, identity, End, default));

        // A large excess goes over several cleanups, oldest first.
        var template = await db.Checkpoints.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync(x => x.Identity == identity);
        for (var i = 0; i < 45; i++) { template.Id = 0; db.Checkpoints.Add(template); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
        await store.PruneAsync(db, 1, identity, default);
        Assert.Equal(6 + 45 - AdaptiveCheckpointStore.MaxRemovedPerCleanup, (await CheckpointIds(observer)).Count);
        await store.PruneAsync(db, 1, identity, default);
        await store.PruneAsync(db, 1, identity, default);
        Assert.Equal(6, (await CheckpointIds(observer)).Count);
        Assert.Equal(all.Skip(3), (await CheckpointIds(observer)).Take(3));                 // the other identity's newest three are still there
    }

    [PgMigrationFact]
    public async Task ADamagedCheckpointIsRemoved_OnesThatMayBeUsableElsewhereAreKept()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(200));
        await using var host = Host(world, observer, checkpoints: true);
        var store = host.GetRequiredService<AdaptiveCheckpointStore>();
        var ids = await CheckpointIds(observer);
        await using var db = observer.Db();
        var identity = (await db.Checkpoints.AsNoTracking().FirstAsync()).Identity;
        var newest = await db.Checkpoints.SingleAsync(x => x.Id == ids[2]);
        newest.Payload = newest.Payload.Reverse().ToArray();
        (await db.Checkpoints.SingleAsync(x => x.Id == ids[1])).Identity = new string('0', 64);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(ids[0], (await store.FindAsync(db, 1, identity, End, default))!.Value.Id);
        Assert.Equal(new[] { ids[0], ids[1] }, await CheckpointIds(observer));

        await db.FuturesDepth5.Where(x => x.BarSeq == 2).ExecuteDeleteAsync();              // rows the remaining state relies on are gone
        Assert.Null(await store.FindAsync(db, 1, identity, End, default));
        Assert.Equal(new[] { ids[0], ids[1] }, await CheckpointIds(observer));             // not usable now, but not damaged
    }

    [PgMigrationFact]
    public async Task ACheckpointRemovedWhileARestoreIsReadingIt_IsPassedOverForAnOlderOne()
    {
        var world = await World.CreateAsync();
        await using var observer = await Observer.CreateAsync();
        await Recover(world, observer, At0.AddSeconds(200));
        await using var host = Host(world, observer, checkpoints: true);
        var store = host.GetRequiredService<AdaptiveCheckpointStore>();
        var ids = await CheckpointIds(observer);
        string identity, connection;
        await using (var db = observer.Db())
        {
            identity = (await db.Checkpoints.AsNoTracking().FirstAsync()).Identity;
            connection = db.Database.GetConnectionString()!;
        }

        var remover = new RemoveBeforeFirstPayloadRead(async () =>
        {
            await using var other = observer.Db();
            await other.Checkpoints.Where(x => x.Id >= ids[1]).ExecuteDeleteAsync();      // the cleanup of someone else, between the listing and the load
        });
        await using var reading = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(connection).AddInterceptors(remover).Options);
        var found = await store.FindAsync(reading, 1, identity, End, default);
        Assert.True(remover.Fired);
        Assert.Equal(ids[0], found!.Value.Id);
    }

    sealed class RemoveBeforeFirstPayloadRead(Func<Task> remove) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("\"Payload\"", StringComparison.Ordinal)) { Fired = true; await remove(); }
            return result;
        }
    }

    [Fact]
    public void Serializer_RefusesWhatItCannotRestore()
    {
        Assert.Throws<ObserverStateIncompatibleException>(() => ObserverStateSerializer.Serialize(new Holder(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))));
        Assert.Throws<ObserverStateIncompatibleException>(() => ObserverStateSerializer.Serialize(new Holder(new System.Text.StringBuilder())));
        Assert.Throws<ObserverStateIncompatibleException>(() => ObserverStateSerializer.Serialize(new Holder((Func<int>)(() => 1))));
        var shared = new List<int> { 1, 2 };
        var copy = ObserverStateSerializer.Deserialize<Pair>(ObserverStateSerializer.Serialize(new Pair(shared, shared)));
        Assert.Same(copy.First, copy.Second);
        Assert.Equal(shared, copy.First);
        var bytes = ObserverStateSerializer.Serialize(new Pair(shared, shared));
        bytes[0]++;
        Assert.Throws<ObserverStateIncompatibleException>(() => ObserverStateSerializer.Deserialize<Pair>(bytes));
    }

    sealed record Holder(object Value);
    sealed record Pair(List<int> First, List<int> Second);
}
