using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
using NiftySignal.Host;
using NiftySignal.Host.PaperEngineV2;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.Persistence;
using NiftySignal.Tests.Trading.Data;

namespace NiftySignal.Tests.PaperEngineV2;

sealed class ReplayFactAttribute : FactAttribute
{
    public const string SourceVariable = "NSE_V2_REPLAY_SOURCE";

    public ReplayFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(SourceVariable) is not { Length: > 0 } || !PostgresTestDatabase.Available)
            Skip = $"{SourceVariable} (read-only recorded-tick database) and {PostgresTestDatabase.Variable} are required for the recorded-data Host replay.";
    }
}

sealed class ReplayClock : TimeProvider
{
    DateTimeOffset _sim;
    long _stamp = global::System.Diagnostics.Stopwatch.GetTimestamp();

    public void Set(DateTimeOffset sim) { _sim = sim; Interlocked.Exchange(ref _stamp, global::System.Diagnostics.Stopwatch.GetTimestamp()); }

    /// <summary>Simulated time of the last poll plus the REAL time elapsed since: V2's own processing time is part of the measured lag.</summary>
    public override DateTimeOffset GetUtcNow() => _sim + global::System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _stamp));
}

sealed class ReplayTelegram : ITelegramTextSender
{
    public List<string> Texts { get; } = [];

    public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct)
    {
        lock (Texts) Texts.Add(text);
        return Task.FromResult(new TelegramDocumentResult(TelegramDocumentOutcome.Sent, Texts.Count));
    }
}

/// <summary>
/// RECORDED-DATA replay of the real Host live path (AdaptiveObserverWorker.TryStartOrRecoverAsync / PollLiveAsync / CloseCurrentSessionAsync with the PaperEngineV2Bridge attached) over one recorded session from the local
/// tick copy. The source database is only READ (read-only connection); every write goes to disposable databases created and dropped by this test (unless NSE_V2_REPLAY_KEEP=1, for the Dashboard validation). Nothing
/// is fabricated: bars, sidecars, VIX and quotes come from the recorded ticks through the production code; the 30 strategies are test configurations whose results are whatever the data produces.
/// </summary>
[Collection("PostgresTradingTests")]
public class HostReplayTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    static string Env(string name, string fallback = "") => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    static DateTimeOffset U(DateOnly day, int h, int m, int s = 0) => new DateTimeOffset(day.ToDateTime(new TimeOnly(h, m, s)), Ist).ToUniversalTime();

    static string DbCs(string database) => new NpgsqlConnectionStringBuilder(PostgresTestDatabase.ConnectionString!) { Database = database }.ConnectionString;

    static async Task RecreateAsync(string name)
    {
        await using var admin = new NpgsqlConnection(DbCs("postgres"));
        await admin.OpenAsync();
        foreach (var sql in new[] { $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", $"CREATE DATABASE \"{name}\"" })
            await using (var cmd = new NpgsqlCommand(sql, admin)) await cmd.ExecuteNonQueryAsync();
    }

    static async Task DropAsync(string name)
    {
        await using var admin = new NpgsqlConnection(DbCs("postgres"));
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        await cmd.ExecuteNonQueryAsync();
    }

    sealed record Staged(DateOnly Day, string SourceDb);

    /// <summary>Copies one session's instruments and ticks from the read-only recorded source into a disposable database (server-side text COPY, identity re-assigned in the original order).</summary>
    static async Task<Staged> StageAsync(string sourceCs, DateOnly day, string targetDb)
    {
        var srcBuilder = new NpgsqlConnectionStringBuilder(sourceCs) { Options = "-c default_transaction_read_only=on", CommandTimeout = 900 };
        await RecreateAsync(targetDb);
        await using (var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(DbCs(targetDb)).Options)) await db.Database.EnsureCreatedAsync();

        await using var src = new NpgsqlConnection(srcBuilder.ConnectionString); await src.OpenAsync();
        await using var dst = new NpgsqlConnection(DbCs(targetDb)); await dst.OpenAsync();
        async Task<string> Columns(NpgsqlConnection c, string table, bool skipId)
        {
            await using var cmd = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema='public' AND table_name=@t ORDER BY ordinal_position", c);
            cmd.Parameters.AddWithValue("t", table);
            var list = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) if (!(skipId && r.GetString(0) == "Id")) list.Add("\"" + r.GetString(0) + "\"");
            return string.Join(",", list);
        }

        async Task Copy(string table, string where, string orderBy)
        {
            var sc = await Columns(src, table, skipId: true);
            var dc = await Columns(dst, table, skipId: true);
            if (sc != dc) throw new InvalidOperationException($"column mismatch for {table}: {sc} vs {dc}");
            using var export = await src.BeginTextExportAsync($"COPY (SELECT {sc} FROM {table} WHERE {where} ORDER BY {orderBy}) TO STDOUT");
            using var import = await dst.BeginTextImportAsync($"COPY {table} ({dc}) FROM STDIN");
            var buffer = new char[1 << 16]; int n;
            while ((n = await export.ReadAsync(buffer, 0, buffer.Length)) > 0) await import.WriteAsync(buffer, 0, n);
        }

        await Copy("instruments", $"\"AsOfDate\" = DATE '{day:yyyy-MM-dd}'", "\"Id\"");
        var from = U(day, 3, 0).UtcDateTime; var to = U(day, 23, 59).UtcDateTime;
        await Copy("ticks", $"\"ReceivedAt\" >= TIMESTAMPTZ '{from:yyyy-MM-dd HH:mm:ss}+00' AND \"ReceivedAt\" < TIMESTAMPTZ '{to:yyyy-MM-dd HH:mm:ss}+00'", "\"Id\"");
        return new Staged(day, targetDb);
    }

    /// <summary>The Host's service graph for the adaptive worker (as the production performance harness builds it) with an optional V2 bridge.</summary>
    static (AdaptiveObserverWorker Worker, PaperEngineV2Bridge? Bridge, ServiceProvider Services) BuildHost(string sourceDb, string observerDb, string? pev2Db, ReplayClock clock, string? strategyDir, ReplayTelegram telegram, bool alerts)
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
        s.AddDbContext<NiftySignalDbContext>(o => o.UseNpgsql(DbCs(sourceDb)));
        s.AddDbContext<AdaptiveObserverDbContext>(o => o.UseNpgsql(DbCs(observerDb)));
        s.AddOptions<PricingOptions>();
        s.Configure<DashboardPushOptions>(o => o.HubUrl = "http://127.0.0.1:1/hubs/never-connected");
        s.AddSingleton<AdaptiveSourceTickReader>(); s.AddSingleton<AdaptiveHistoricalBootstrapService>(); s.AddSingleton(sp => new AdaptiveSessionCoordinator(sp.GetRequiredService<AdaptiveSourceTickReader>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<PricingOptions>>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdaptiveSessionCoordinator>>(), NiftySignal.AdaptiveObserver.AdaptiveClockMode.Fixed13650));
        s.AddSingleton<AdaptiveObserverPersistence>(); s.AddSingleton<AdaptiveSupplementalPersistence>(); s.AddSingleton<AdaptiveWeak2ObservationService>();
        s.AddSingleton<AdaptiveStateRecoveryService>(); s.AddSingleton<AdaptiveEndedSessionRecoveryService>(); s.AddSingleton<DashboardPushClient>();
        s.AddSingleton<AdaptiveSessionSupplementalService>();
        if (pev2Db is not null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:V2"] = DbCs(pev2Db), ["PaperEngineV2:Enabled"] = "true", ["PaperEngineV2:ConnectionStringName"] = "V2", ["PaperEngineV2:StrategyDirectory"] = strategyDir,
                ["PaperEngineV2:QueueCapacity"] = "64", ["PaperEngineV2:MaxProcessingLagSeconds"] = "5", ["PaperEngineV2:FramesToClearGap"] = "3", ["PaperEngineV2:TerminalRetryUntil"] = "15:25",
                ["PaperEngineV2:BrokeragePerOrder"] = "0", ["PaperEngineV2:AlertPollIntervalMs"] = "100", ["PaperEngineV2:CostScheduleVersion"] = "npt1-frozen-fees-v1",
                ["PaperEngineV2:Alerts:Opened"] = alerts.ToString(), ["PaperEngineV2:Alerts:Closed"] = alerts.ToString(), ["PaperEngineV2:Alerts:OrderRejected"] = alerts.ToString(), ["PaperEngineV2:Alerts:ExecutionFailure"] = alerts.ToString(),
            };
            s.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            s.AddSingleton<ITelegramTextSender>(telegram);
            s.AddSingleton(sp => new PaperEngineV2Bridge(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<ILogger<PaperEngineV2Bridge>>(), telegram, clock));
        }

        s.AddSingleton<AdaptiveObserverWorker>();
        var sp = s.BuildServiceProvider();
        return (sp.GetRequiredService<AdaptiveObserverWorker>(), pev2Db is null ? null : sp.GetRequiredService<PaperEngineV2Bridge>(), sp);
    }

    static string WriteStrategies(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "v2-replay-strategies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 30 ENABLED configurations with different thresholds, windows and exits over the real summary metrics; what each does on the recorded session is whatever the data produces.
        var windows = new[] { "5", "10", "50" };          // the metric windows are 5 / 10 / 50 / 100 / Day
        for (var i = 1; i <= count; i++)
        {
            var id = $"R{i:00}";
            var thr = 2 + i % 7;                                  // entry threshold (percent) 3..8
            var win = windows[i % windows.Length];
            File.WriteAllText(Path.Combine(dir, id + ".json"), ConfigTests.Mutate(o =>
            {
                o["strategyId"] = id;
                o["execution"]!["fillQuote"] = "AsOfEligibleBarEnd";
                o["fees"]!["schedule"] = "npt1-frozen-fees-v1";
                o["entry"] = JsonNode.Parse($$"""
                    { "buyCall": { "op": "AND", "items": [ { "metric": "opt.atm.strict.dir:pct", "window": "{{win}}", "cmp": ">", "value": {{thr}} }, { "metric": "fut.strict:pct", "window": "5", "cmp": ">", "value": 0 } ] },
                      "buyPut":  { "op": "AND", "items": [ { "metric": "opt.atm.strict.dir:pct", "window": "{{win}}", "cmp": "<", "value": -{{thr}} }, { "metric": "fut.strict:pct", "window": "5", "cmp": "<", "value": 0 } ] } }
                    """);
                o["exit"]!["conditions"] = JsonNode.Parse($$"""{ "op": "OR", "items": [ { "metric": "opt.atm.strict.dir:pct", "window": "{{win}}", "cmp": "<", "value": {{(i % 2 == 0 ? "-1" : "0")}} } ] }""");
                o["quantity"]!["lots"] = 1 + i % 3;
            }));
        }

        return dir;
    }

    static async Task WaitIdleAsync(PaperEngineV2Bridge bridge)
    {
        long Handled(NiftySignal.PaperEngineV2.Runtime.V2RuntimeSnapshot s) => s.Consumer is { } c ? c.Processed + c.Duplicates + c.Rejected : 0;
        for (var i = 0; i < 6000; i++)
        {
            var a = bridge.Snapshot!;
            if (a.NoticesAccepted == a.FramesBuilt + a.InputLoadFailures && (a.Handoff?.QueueDepth ?? 0) == 0)
            {
                await Task.Delay(15);
                var b = bridge.Snapshot!;
                if (Handled(a) == Handled(b) && b.NoticesAccepted == a.NoticesAccepted && (b.Handoff?.QueueDepth ?? 0) == 0) return;
            }
            else await Task.Delay(5);
        }

        throw new TimeoutException("V2 did not go idle");
    }

    sealed record Run(int Polls, int Bars, int MaxBarsInOnePoll, long SimulatedMs, string ObserverDigest, Dictionary<string, string> Extra);

    static async Task<Run> ReplayAsync(Staged staged, string observerDb, string? pev2Db, string? strategyDir, ReplayTelegram telegram, bool alerts, ReplayClock clock)
    {
        await RecreateAsync(observerDb);
        await using (var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(DbCs(observerDb)).Options)) await db.Database.MigrateAsync();
        var (worker, bridge, services) = BuildHost(staged.SourceDb, observerDb, pev2Db, clock, strategyDir, telegram, alerts);
        await using var _ = services;
        if (bridge is not null) await bridge.StartAsync(default);
        var day = staged.Day;
        worker.MarkHistoricalBootstrapDone(day);
        var start = U(day, 9, 31);
        clock.Set(start);
        var live = await worker.TryStartOrRecoverAsync(day, start, default) ?? throw new InvalidOperationException("The adaptive session did not start from the recorded data.");
        bridge?.OnSessionStarted(live);          // the worker's ExecuteAsync does this at session start; the replay drives the same internal entry points
        var polls = 0; var barsTotal = 0; var maxBars = 0;
        var end = U(day, 15, 31);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var now = start.AddSeconds(1); now <= end; now = now.AddSeconds(1))
        {
            clock.Set(now);
            var before = live.Engine.FutureBars.Count(b => b.IsComplete);
            await worker.PollLiveAsync(live, now, default);
            polls++;
            var made = live.Engine.FutureBars.Count(b => b.IsComplete) - before;
            if (made > 0)
            {
                barsTotal += made; maxBars = Math.Max(maxBars, made);
                if (bridge is not null) await WaitIdleAsync(bridge);          // the replay does not outrun V2: its simulated lag is real queueing + processing time
            }
        }

        await worker.CloseCurrentSessionAsync(live, default);
        clock.Set(U(day, 15, 40));          // the recorded close flushes events stamped after the last poll; wall time is never behind an event, so the replay clock must not be either
        if (bridge is not null) await WaitIdleAsync(bridge);
        var extra = new Dictionary<string, string>();
        if (bridge is not null && pev2Db is not null)
        {
            await using var ds = NpgsqlDataSource.Create(DbCs(pev2Db));
            for (var i = 0; i < 1200; i++)          // let the dispatcher drain the durable outbox (fake sender)
            {
                await using var c = ds.CreateCommand("SELECT count(*) FROM pev2.pev2_alert_outbox WHERE status IN (0, 5)");
                if (Convert.ToInt64(await c.ExecuteScalarAsync()) == 0) break;
                await Task.Delay(100);
            }
        }

        if (bridge is not null) { await bridge.StopAsync(default); }
        return new Run(polls, barsTotal, maxBars, sw.ElapsedMilliseconds, await DigestAsync(observerDb), extra);
    }

    /// <summary>Order-stable digest of everything the Observer persisted (core bars, option bars, sidecars): equal digests mean V2 changed nothing the Observer or NPT1 consume.</summary>
    static async Task<string> DigestAsync(string observerDb)
    {
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(DbCs(observerDb)).Options);
        var sb = new StringBuilder();
        foreach (var b in await db.FutureBars.AsNoTracking().OrderBy(x => x.SessionId).ThenBy(x => x.BarSeq).ToListAsync())
            sb.Append(CultureInfo.InvariantCulture, $"F|{b.BarSeq}|{b.Open:R}|{b.High:R}|{b.Low:R}|{b.Close:R}|{b.Volume}|{b.StartAvailableAtUtc:O}|{b.EndAvailableAtUtc:O}|{b.StrictDelta}|{b.EnrichedDelta}|{b.OiOpen}|{b.OiClose}\n");
        foreach (var o in await db.OptionStrikeBars.AsNoTracking().OrderBy(x => x.SessionId).ThenBy(x => x.BarSeq).ThenBy(x => x.Token).ThenBy(x => x.MetricsVersion).ToListAsync())
            sb.Append(CultureInfo.InvariantCulture, $"O|{o.BarSeq}|{o.Token}|{o.MetricsVersion}|{o.PriceStart:R}|{o.PriceEnd:R}|{o.TotalQuantity}|{o.StrictDelta}|{o.NetOfi:R}\n");
        foreach (var s in await db.FuturesSupplemental.AsNoTracking().OrderBy(x => x.SessionId).ThenBy(x => x.BarSeq).ThenBy(x => x.MetricsVersion).ToListAsync())
            sb.Append(CultureInfo.InvariantCulture, $"S|{s.BarSeq}|{s.MetricsVersion}|{s.TobTimeWeighted:R}|{s.MicroDevTimeWeighted:R}|{s.Ofi:R}\n");
        foreach (var s in await db.Sessions.AsNoTracking().OrderBy(x => x.Id).ToListAsync())
            sb.Append(CultureInfo.InvariantCulture, $"X|{s.TradeDate}|{s.ModelVersion}|{s.AtmRuleVersion}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    [ReplayFact]
    public async Task RecordedSession_FlowsThroughTheRealHostPath_To_Ledger_Outbox_And_Dashboard_WithNpt1Parity()
    {
        var sourceCs = Env(ReplayFactAttribute.SourceVariable);
        var day = DateOnly.Parse(Env("NSE_V2_REPLAY_DAY", "2026-09-07"));
        var strategies = int.Parse(Env("NSE_V2_REPLAY_STRATEGIES", "30"), CultureInfo.InvariantCulture);
        var keep = Env("NSE_V2_REPLAY_KEEP") == "1";
        var tag = Guid.NewGuid().ToString("N")[..8];
        string srcDb = $"v2e2e_{tag}_src", obsA = $"v2e2e_{tag}_obs_a", obsB = $"v2e2e_{tag}_obs_b", pevDb = $"v2e2e_{tag}_pev2";
        var report = new StringBuilder();
        try
        {
            var staged = await StageAsync(sourceCs, day, srcDb);
            await RecreateAsync(pevDb);
            await using (var pev = NpgsqlDataSource.Create(DbCs(pevDb)))
            await using (var create = pev.CreateCommand(File.ReadAllText(Path.Combine(TradingModelTests.RepoRoot(), "NiftySignal.PaperEngineV2.Data", "Sql", "pev2-schema.idempotent.sql")))) await create.ExecuteNonQueryAsync();

            // run A: V2 absent (the NPT1 / Observer baseline)
            var baseline = await ReplayAsync(staged, obsA, null, null, new ReplayTelegram(), false, new ReplayClock());
            // run B: V2 enabled with 30 strategies and alerts (fake Telegram sender)
            var telegram = new ReplayTelegram();
            var dir = WriteStrategies(strategies);
            var clock = new ReplayClock();
            var withV2 = await ReplayAsync(staged, obsB, pevDb, dir, telegram, true, clock);

            Assert.Equal(baseline.ObserverDigest, withV2.ObserverDigest);              // NPT1 / Observer parity: V2 changed nothing the Observer persisted
            Assert.True(withV2.Bars > 100, $"only {withV2.Bars} bars completed");

            await using var pev2 = NpgsqlDataSource.Create(DbCs(pevDb));
            async Task<long> Scalar(string sql) { await using var c = pev2.CreateCommand(sql); return Convert.ToInt64(await c.ExecuteScalarAsync()); }
            var frames = await Scalar("SELECT count(*) FROM pev2.pev2_frames");
            var disp = await Scalar("SELECT count(*) FROM pev2.pev2_dispositions");
            var allowed = await Scalar("SELECT count(*) FROM pev2.pev2_dispositions WHERE entries_allowed");
            var gaps = await Scalar("SELECT count(*) FROM pev2.pev2_gaps");
            var maxLag = await Scalar("SELECT coalesce(max(processing_lag_ms),0) FROM pev2.pev2_dispositions");
            var store = new PostgresEventStore(pev2);
            var events = await store.ReadAsync(U(day, 3, 0), U(day, 23, 59), null, PostgresEventStore.MaxRead, default);
            var placed = events.OfType<EntryOrderPlaced>().ToList(); var filled = events.OfType<EntryFilled>().ToList(); var exits = events.OfType<ExitFilled>().ToList(); var rej = events.OfType<EntryRejected>().ToList();

            Assert.Equal(strategies, await Scalar("SELECT count(*) FROM pev2.pev2_strategy_configs"));          // every generated file was accepted by the strict validator
            Assert.Equal(frames, disp);
            Assert.True(frames > 0);
            // causality: every fill used a quote that became available strictly after its decision, with the all-or-none full quantity
            var byIntent = placed.ToDictionary(p => p.IntentId, StringComparer.Ordinal);
            foreach (var f in filled)
            {
                var p = byIntent[f.IntentId];
                Assert.True(f.QuoteAvailableAt > p.Decision.AvailableAt, $"{f.StrategyId} fill at/before decision");
                Assert.Equal(p.Quantity, f.Quantity);
            }

            // determinism / recovery: replaying the durable journal into fresh engines reproduces the stored ledger exactly
            var journal = new PostgresFrameJournal(pev2);
            var sessionId = await Scalar("SELECT session_id FROM pev2.pev2_frames LIMIT 1");
            var replay = await ReplayLedgerAsync(journal, sessionId, dir, pev2);
            var rep = await LedgerReconciler.ReconcileAsync(replay, store, U(day, 3, 0), U(day, 23, 59), default);
            Assert.True(rep.Clean, $"ledger reconcile: missing={rep.MissingInStore} extra={rep.ExtraInStore} different={rep.Different}");

            // alerts: one outbox row per alertable event (opened/closed/rejected/failure), all delivered through the fake sender, none duplicated
            var alertable = filled.Count + exits.Count + rej.Count + events.OfType<StrategyFaulted>().Count() + events.OfType<PositionUnresolved>().Count();
            var outbox = await Scalar("SELECT count(*) FROM pev2.pev2_alert_outbox");
            var sent = await Scalar("SELECT count(*) FROM pev2.pev2_alert_outbox WHERE status = 1");
            var expired = await Scalar("SELECT count(*) FROM pev2.pev2_alert_outbox WHERE status = 4");
            Assert.Equal(alertable, outbox);
            Assert.Equal(outbox, sent + expired);

            // Dashboard read model over the same database: strategies, trades, realised P&L agree with the ledger
            using var svc = new PaperEngineV2DataService(DbCs(pevDb));
            var view = await svc.LoadAsync(U(day, 15, 35), default);
            Assert.True(view.Health.SchemaOk);
            Assert.Equal(strategies, view.Strategies.Count);
            Assert.Equal(exits.Count, view.Trades.Count);
            Assert.Equal(exits.Sum(e => e.NetPnl), view.Strategies.Sum(s => s.RealizedNetToday ?? 0m));

            report.AppendLine($"day={day:yyyy-MM-dd} strategies={strategies}");
            report.AppendLine($"observer: bars={withV2.Bars} maxBarsInOnePoll={withV2.MaxBarsInOnePoll} polls={withV2.Polls} parityDigestEqual=True digest={withV2.ObserverDigest[..16]}");
            report.AppendLine($"v2: frames={frames} dispositions={disp} entriesAllowedFrames={allowed} gaps={gaps} maxLagMs={maxLag}");
            report.AppendLine($"ledger: orders={placed.Count} fills={filled.Count} exits={exits.Count} rejected={rej.Count} totalNet={exits.Sum(e => e.NetPnl):0.00} totalFees={exits.Sum(e => e.Fees) + filled.Sum(f => f.Fees):0.00}");
            report.AppendLine($"mfe/mae recorded on {exits.Count} exits; rejection reasons: {string.Join(", ", rej.GroupBy(r => r.Reason).Select(g => $"{g.Key}={g.Count()}"))}");
            report.AppendLine($"alerts: outbox={outbox} sent={sent} expired={expired} fakeSenderTexts={telegram.Texts.Count}");
            report.AppendLine($"dashboard: strategies={view.Strategies.Count} trades={view.Trades.Count} rejections={view.Rejections.Count} failures={view.Failures.Count}");
            var lagP = new List<long>(); await using (var lc = pev2.CreateCommand("SELECT processing_lag_ms FROM pev2.pev2_dispositions ORDER BY 1")) await using (var lr = await lc.ExecuteReaderAsync()) while (await lr.ReadAsync()) lagP.Add(lr.GetInt64(0));
            var qsw = System.Diagnostics.Stopwatch.StartNew(); var qview = await svc.LoadAsync(U(day, 15, 35), default); qsw.Stop();
            var ps = System.Diagnostics.Process.GetCurrentProcess();
            report.AppendLine($"lag ms: p50={lagP[lagP.Count / 2]} p95={lagP[(int)(lagP.Count * 0.95)]} max={lagP[^1]}; dashboard LoadAsync (warm) {qsw.ElapsedMilliseconds} ms; test process peak working set {ps.PeakWorkingSet64 / 1048576} MB, managed heap {GC.GetTotalMemory(false) / 1048576} MB");
            report.AppendLine($"replay wall time: baseline {baseline.SimulatedMs / 1000.0:0.0}s, with V2 {withV2.SimulatedMs / 1000.0:0.0}s");
            if (keep) report.AppendLine($"KEPT databases: {srcDb}, {obsA}, {obsB}, {pevDb}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "paper-v2-host-replay.txt"), report.ToString());
            if (!keep) foreach (var db in new[] { srcDb, obsA, obsB, pevDb }) await DropAsync(db);
        }
    }

    static async Task<IReadOnlyList<PaperEvent>> ReplayLedgerAsync(PostgresFrameJournal journal, long sessionId, string strategyDir, NpgsqlDataSource db)
    {
        var docs = Directory.EnumerateFiles(strategyDir, "*.json").OrderBy(x => x, StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
        var set = NiftySignal.PaperEngineV2.Core.Config.StrategySet.Load(docs);
        var sink = new ListSink();
        var schedule = NiftySignal.Trading.Core.Costs.NseIndexOptionsCostSchedules.Npt1FrozenV1(new NiftySignal.Trading.Core.Costs.FlatBrokerage { Unit = NiftySignal.Trading.Core.Costs.BrokerageUnit.PerOrder, Amount = 0m });
        var lots = new RecordedLots(db);
        var engine = new PaperEngine(set.Active, new ExecutionOptions(new TimeOnly(15, 25), [schedule], lots), sink);
        var consumer = new FrameConsumer(null, new HandoffOptions(64, TimeSpan.FromSeconds(5), 3), journal, engine, TimeProvider.System);
        await consumer.RecoverFromJournalAsync(sessionId, default);
        return sink.Events;
    }

    sealed class RecordedLots(NpgsqlDataSource db) : ILotSizeSource
    {
        public int? LotSize(DateOnly tradeDate, string token) => 65;          // NIFTY lot size on the recorded dates (instrument master: 65 on every as-of date, checked in the 6c source-dependent investigation)
    }
}
