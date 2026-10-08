using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Host;
using NiftySignal.Notifications;
using P = NiftySignal.Tests.AdaptiveObserver.AdaptiveCommentaryPersistenceTests;
using Spec = NiftySignal.Tests.AdaptiveObserver.AdaptiveCommentaryPersistenceTests.Spec;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// Final pre-integration hardening: Telegram disabled/unconfigured must never accumulate a notification backlog, a commentary replay mismatch
/// quarantines downstream rows and jobs, unknown sidecar integrity is never consumed, and the operational runtime cadence.
/// </summary>
public sealed class AdaptiveFinalHardeningTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 30, 0, TimeSpan.Zero);

    sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    sealed record Config(AdaptiveCommentaryNotificationGate Gate, AdaptiveCommentaryTelegramOptions Options, TelegramOptions Telegram);

    static Config NewGate(bool enabled, string token = "bot-token", string chat = "chat")
    {
        var options = new AdaptiveCommentaryTelegramOptions { Enabled = enabled };
        var telegram = new TelegramOptions { BotToken = token, ChatId = chat };
        return new(new AdaptiveCommentaryNotificationGate(new Monitor<AdaptiveCommentaryTelegramOptions>(options), new Monitor<TelegramOptions>(telegram),
            NullLogger<AdaptiveCommentaryNotificationGate>.Instance), options, telegram);
    }

    /// <summary>Bars 1-9 warm-up, 10 Short expansion, 11 flips Long (Telegram-eligible Flipped), 12 flips back Short (Flipped).</summary>
    static Spec[] FlipBars(int through)
    {
        var all = Enumerable.Range(1, 9).Select(i => new Spec(i, -1, 50, -2, -5))
            .Concat([new Spec(10, -1, 50, -2, -5), new Spec(11, 1, -5, 2, 5), new Spec(12, -1, 50, -2, -5)]);
        return all.Where(x => x.Seq <= through).ToArray();
    }

    static async Task LiveBar(AdaptiveObserverDbContext db, AdaptiveCommentaryNotificationGate gate, int bar)
    {
        var mode = await gate.SyncAsync(db, default);
        await P.Service().ProcessThroughAsync(db, P.Session(), bar, mode, default);
    }

    // ------------------------------------------------------------------ A1

    [Fact]
    public async Task Disabled_LiveEligibleEvent_IsPersistedAndShown_ButCreatesNoJob()
    {
        await using var db = await P.Seeded(FlipBars(10));
        var gate = NewGate(enabled: false).Gate;
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        await P.Seed(db, FlipBars(11)[10]);
        await LiveBar(db, gate, 11);

        var flipped = await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 11);
        Assert.Equal(CommentaryLifecycle.Flipped, flipped.Lifecycle); Assert.True(flipped.ShouldNotifyTelegram);   // classification is independent of Telegram configuration
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());
        Assert.Equal(11, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);           // the Dashboard still gets current commentary
    }

    [Fact]
    public async Task Enabled_NewLiveEligibleEvent_CreatesExactlyOneJob()
    {
        await using var db = await P.Seeded(FlipBars(10));
        var gate = NewGate(enabled: true).Gate;
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        await P.Seed(db, FlipBars(11)[10]);
        await LiveBar(db, gate, 11);
        var job = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Pending, job.Status);
        Assert.Equal(11, (await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.Id == job.EventId)).BarSeq);
    }

    [Fact]
    public async Task DisabledThenEnabled_HistoricalEligibleEventsDoNotSuddenlyQueue_OnlyFutureOnesDo()
    {
        await using var db = await P.Seeded(FlipBars(10));
        var cfg = NewGate(enabled: false);
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        await P.Seed(db, FlipBars(11)[10]);
        await LiveBar(db, cfg.Gate, 11);                                  // eligible Flipped while disabled: persisted, no job
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());

        cfg.Options.Enabled = true;                                       // operator enables Telegram
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);     // restart/catch-up after enabling
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);     // even a full rebuild queues nothing
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());

        await P.Seed(db, FlipBars(12)[11]);
        await LiveBar(db, cfg.Gate, 12);                                  // a genuinely new eligible live event now queues
        var job = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(12, (await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.Id == job.EventId)).BarSeq);
    }

    sealed class FakeSender : ITelegramTextSender
    {
        public List<string> Sent { get; } = [];
        public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct) { Sent.Add(text); return Task.FromResult(new TelegramDocumentResult(TelegramDocumentOutcome.Sent, 77)); }
    }

    [Fact]
    public async Task EnabledThenDisabled_APendingJobIsSuppressedAndCanNeverSendLater()
    {
        await using var db = await P.Seeded(FlipBars(10));
        var cfg = NewGate(enabled: true);
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        await P.Seed(db, FlipBars(11)[10]);
        await LiveBar(db, cfg.Gate, 11);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Pending, (await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync()).Status);

        cfg.Options.Enabled = false;                                      // disable
        Assert.Equal(CommentaryNotificationMode.Suppress, await cfg.Gate.SyncAsync(db, default));
        var suppressed = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Suppressed, suppressed.Status);
        Assert.Contains("disabled or not configured", suppressed.LastError);

        cfg.Options.Enabled = true;                                       // re-enable later: the old job is terminal, never delivered
        var sender = new FakeSender();
        var processor = new AdaptiveCommentaryNotificationProcessor(sender);
        await processor.ProcessOneAsync(db, T0.AddHours(5), default);
        Assert.Empty(sender.Sent);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Suppressed, (await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("", "chat")]
    [InlineData("bot-token", "")]
    [InlineData("  ", "  ")]
    public async Task MissingTelegramCredentials_NeverAccumulateABacklog(string token, string chat)
    {
        await using var db = await P.Seeded(FlipBars(10));
        var cfg = NewGate(enabled: true, token, chat);
        Assert.False(cfg.Gate.IsDeliverable);
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        await P.Seed(db, FlipBars(11)[10]);
        await LiveBar(db, cfg.Gate, 11);
        Assert.Empty(await db.CommentaryNotificationJobs.ToListAsync());
        Assert.NotNull(await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 11 && x.ShouldNotifyTelegram));
    }

    [Fact]
    public async Task RestartWhileDisabled_CreatesNoJobs_AndSuppressesAnythingLeftOver()
    {
        await using var db = await P.Seeded(FlipBars(11));
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        var flipped = await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.BarSeq == 11);
        db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow { EventId = flipped.Id, Status = AdaptiveCommentaryNotificationStatus.Pending, CreatedAtUtc = T0, NextAttemptUtc = T0 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var restarted = NewGate(enabled: false);                          // new process: no memory, first evaluation
        Assert.Equal(CommentaryNotificationMode.Suppress, await restarted.Gate.SyncAsync(db, default));
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Suppressed, (await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await db.CommentaryNotificationJobs.CountAsync());
    }

    [Fact]
    public async Task SuppressionNeverTouchesSentSendingOrDeliveryUncertainJobs()
    {
        await using var db = await P.Seeded(FlipBars(10));
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        var statuses = new[] { AdaptiveCommentaryNotificationStatus.Sent, AdaptiveCommentaryNotificationStatus.Sending, AdaptiveCommentaryNotificationStatus.DeliveryUncertain, AdaptiveCommentaryNotificationStatus.Pending };
        var ev = await db.CommentaryEvents.AsNoTracking().FirstAsync();
        foreach (var status in statuses)
        {
            var copy = await db.CommentaryEvents.AsNoTracking().FirstAsync();
            var clone = new AdaptiveCommentaryEventRow
            {
                SessionId = copy.SessionId, TradeDate = copy.TradeDate, BarSeq = 100 + (int)status, OccurredAtUtc = copy.OccurredAtUtc, EventType = copy.EventType, EventBias = copy.EventBias,
                MarketRegime = copy.MarketRegime, Lifecycle = copy.Lifecycle, EvidenceAgreement = copy.EvidenceAgreement, Severity = copy.Severity, PreviousBias = copy.PreviousBias,
                PrimaryEvidenceJson = "[]", ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]", RenderedCommentary = "x",
                CommentaryVersion = copy.CommentaryVersion, EventIdentity = $"commentary-v1:1:{100 + (int)status}:x:x:x", CreatedAtUtc = copy.CreatedAtUtc,
            };
            db.CommentaryEvents.Add(clone); await db.SaveChangesAsync();
            db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow { EventId = clone.Id, Status = status, CreatedAtUtc = T0, NextAttemptUtc = T0, TelegramMessageId = status == AdaptiveCommentaryNotificationStatus.Sent ? 5 : null });
        }
        await db.SaveChangesAsync();

        Assert.Equal(1, await AdaptiveCommentaryOutbox.SuppressAllPendingAsync(db, "off", default));
        var byStatus = await db.CommentaryNotificationJobs.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Status).ToListAsync();
        Assert.Equal([AdaptiveCommentaryNotificationStatus.Sent, AdaptiveCommentaryNotificationStatus.Sending, AdaptiveCommentaryNotificationStatus.DeliveryUncertain, AdaptiveCommentaryNotificationStatus.Suppressed], byStatus);
        Assert.NotNull(ev);
    }

    // ------------------------------------------------------------------ A2 / A3

    static AdaptiveCommentaryEventRow Event(int bar, string version = "commentary-v1") => new()
    {
        SessionId = 1, TradeDate = new DateOnly(2026, 10, 8), BarSeq = bar, OccurredAtUtc = T0.AddMinutes(bar), EventType = CommentaryEventType.SellerExpansion, EventBias = EventBias.Short,
        MarketRegime = MarketRegime.Bearish, Lifecycle = CommentaryLifecycle.Flipped, EvidenceAgreement = EvidenceAgreement.High, Severity = CommentarySeverity.High,
        PreviousBias = EventBias.Long, PrimaryEvidenceJson = "[]", ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]",
        RenderedCommentary = $"Title {bar}.\nBias.\nMeaning of bar {bar}.\nEvidence: none.", ShouldNotifyTelegram = true, NotificationReason = "Flipped",
        CommentaryVersion = version, EventIdentity = $"{version}:1:{bar}:SellerExpansion:Flipped:Short", CreatedAtUtc = T0,
    };

    [Fact]
    public async Task Processor_NeverSendsAnEventAtOrAfterADegradationMarker_ButStillSendsEarlierOnes()
    {
        await using var db = P.Database();
        db.Sessions.Add(P.Session());
        db.CommentaryEvents.AddRange(Event(8), Event(14));
        await db.SaveChangesAsync();
        db.ProjectionHealth.Add(new AdaptiveProjectionHealthRow { SessionId = 1, Component = AdaptiveProjectionComponents.Commentary, Version = "commentary-v1", BarSeq = 10, Detail = "x", DetectedAtUtc = T0 });
        foreach (var e in await db.CommentaryEvents.ToListAsync())
            db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow { EventId = e.Id, Status = AdaptiveCommentaryNotificationStatus.Pending, CreatedAtUtc = T0, NextAttemptUtc = T0 });
        await db.SaveChangesAsync();

        var sender = new FakeSender();
        var processor = new AdaptiveCommentaryNotificationProcessor(sender);
        await processor.ProcessOneAsync(db, T0.AddHours(1), default);     // bar 8 (before the mismatch): sent
        await processor.ProcessOneAsync(db, T0.AddHours(1), default);     // bar 14 (downstream of bar 10): suppressed, not sent
        await processor.ProcessOneAsync(db, T0.AddHours(1), default);
        Assert.Single(sender.Sent); Assert.Contains("bar 8", sender.Sent[0]);
        var late = await db.CommentaryNotificationJobs.AsNoTracking().Join(db.CommentaryEvents, j => j.EventId, e => e.Id, (j, e) => new { j, e.BarSeq }).SingleAsync(x => x.BarSeq == 14);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Suppressed, late.j.Status);
        Assert.Equal("Suppressed: commentary projection degraded upstream at bar 10.", late.j.LastError);
    }

    [Fact]
    public async Task ReplayMismatch_SuppressesDownstreamPendingJobs_LeavesSentAlone_AndNothingRevivesThemAfterReconciliation()
    {
        await using var db = await P.Seeded(P.Scenario());
        var service = P.Service();
        await service.ProcessThroughAsync(db, P.Session(), 16, CommentaryNotificationMode.Suppress, default);
        var events = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Equal([10, 12, 14, 16], events.Select(x => x.BarSeq));
        // Pending jobs for 10, 12 and 14 (at/after the coming mismatch) and an already-Sent job for 16.
        foreach (var e in events)
            db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow
            {
                EventId = e.Id, CreatedAtUtc = T0, NextAttemptUtc = T0,
                Status = e.BarSeq == 16 ? AdaptiveCommentaryNotificationStatus.Sent : AdaptiveCommentaryNotificationStatus.Pending, TelegramMessageId = e.BarSeq == 16 ? 9 : null,
            });
        var original = (await db.CommentaryEvents.FirstAsync(x => x.BarSeq == 10)).RenderedCommentary;
        (await db.CommentaryEvents.FirstAsync(x => x.BarSeq == 10)).RenderedCommentary = "tampered";
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        await service.ProcessThroughAsync(db, P.Session(), 16, CommentaryNotificationMode.EnqueueFinalBar, default);
        var jobs = await db.CommentaryNotificationJobs.AsNoTracking().Join(db.CommentaryEvents, j => j.EventId, e => e.Id, (j, e) => new { j.Status, j.LastError, e.BarSeq }).OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Equal([AdaptiveCommentaryNotificationStatus.Suppressed, AdaptiveCommentaryNotificationStatus.Suppressed, AdaptiveCommentaryNotificationStatus.Suppressed, AdaptiveCommentaryNotificationStatus.Sent],
            jobs.Select(x => x.Status));
        Assert.All(jobs.Take(3), j => Assert.Equal("Suppressed: commentary projection degraded upstream at bar 10.", j.LastError));

        // Manual reconciliation: restore the stored text, remove the marker. Catch-up proceeds; suppressed jobs stay suppressed; nothing new is queued.
        (await db.CommentaryEvents.FirstAsync(x => x.BarSeq == 10)).RenderedCommentary = original;
        db.ProjectionHealth.RemoveRange(db.ProjectionHealth); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await P.Service().ProcessAllCompletedAsync(db, P.Session(), default);
        Assert.Equal(16, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);
        var after = await db.CommentaryNotificationJobs.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Status).ToListAsync();
        Assert.Equal(4, after.Count);
        Assert.Equal(3, after.Count(x => x == AdaptiveCommentaryNotificationStatus.Suppressed));
    }

    [Fact]
    public async Task DegradedDashboard_ShowsOnlyVerifiedEvents_AndReportsTheQuarantine()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        await using (var db = new AdaptiveObserverDbContext(options))
        {
            var session = P.Session(); session.TradeDate = today; session.ModelVersion = OpeningVolumeProjectionV1.ModelVersion;
            db.Sessions.Add(session);
            foreach (var bar in new[] { 4, 10, 12, 14, 16 }) { var e = Event(bar); e.TradeDate = today; db.CommentaryEvents.Add(e); }
            db.CommentaryRuntime.Add(new AdaptiveCommentaryRuntimeRow { SessionId = 1, LastEvaluatedBarSeq = 9, CurrentRegime = MarketRegime.Bearish, CurrentBias = EventBias.Short, CommentaryVersion = "commentary-v1" });
            db.ProjectionHealth.Add(new AdaptiveProjectionHealthRow { SessionId = 1, Component = AdaptiveProjectionComponents.Commentary, Version = "commentary-v1", BarSeq = 10, Detail = "x", DetectedAtUtc = T0 });
            await db.SaveChangesAsync();
        }

        var factory = new Factory(options);
        var model = await new AdaptiveCommentaryDataService(factory).LoadLatestAsync(5);
        Assert.True(model.IsDegraded);
        Assert.Equal([4], model.Events.Select(x => x.BarSeq));            // bars 10-16 stay in PostgreSQL but are not shown as current
        Assert.Equal(9, model.VerifiedThroughBar); Assert.Equal(4, model.QuarantinedCount);

        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(factory);
        services.AddSingleton<AdaptiveObserverDataService>(); services.AddSingleton<AdaptiveCommentaryDataService>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = System.Net.WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<CommentaryPanel>()).ToHtmlString()));
        Assert.Contains("replay mismatch at bar 10", html);
        Assert.Contains("verified only through bar 9", html);
        Assert.Contains("4 later stored event(s) are quarantined", html);
        Assert.Contains("Meaning of bar 4.", html);
        Assert.DoesNotContain("Meaning of bar 12.", html);
    }

    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    // ------------------------------------------------------------------ A4

    [Fact]
    public async Task CommentaryNeverEvaluatesAtOrPastABarWhoseSidecarIntegrityIsUnknown_AndResumesDeterministically()
    {
        await using var capped = await P.Seeded(P.Scenario());
        var service = P.Service();
        Assert.Equal(11, await service.ProcessThroughAsync(capped, P.Session(), 16, CommentaryNotificationMode.EnqueueFinalBar, default, stopBeforeBarSeq: 12));
        Assert.Equal(11, (await capped.CommentaryRuntime.AsNoTracking().SingleAsync()).LastEvaluatedBarSeq);
        Assert.Empty(await capped.CommentaryNotificationJobs.ToListAsync());
        // Once the sidecar is verified the remaining bars are evaluated; the result equals an uninterrupted run.
        Assert.Equal(5, await service.ProcessThroughAsync(capped, P.Session(), 16, CommentaryNotificationMode.Suppress, default));

        await using var straight = await P.Seeded(P.Scenario());
        await P.Service().ProcessThroughAsync(straight, P.Session(), 16, CommentaryNotificationMode.Suppress, default);
        var shape = (AdaptiveObserverDbContext d) => d.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).Select(x => new { x.BarSeq, x.EventType, x.Lifecycle, x.EvidenceAgreement, x.RenderedCommentary }).ToList();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(shape(straight)), System.Text.Json.JsonSerializer.Serialize(shape(capped)));
    }

    [Fact]
    public async Task SidecarIntegrity_IsKnownOnlyForInsertedVerifiedSkippedOrMarkedMismatch_NotForFailure()
    {
        var supplemental = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var package = FirstPackage();
        Assert.NotNull(package.Microstructure);
        // Unusable context: a failed write/verify leaves integrity unknown => PersistAndVerifyAsync is false (commentary must not consume).
        var disposed = P.Database(); await disposed.DisposeAsync();
        Assert.False(await supplemental.PersistAndVerifyAsync(disposed, P.Session(), package, default));
        // Inserted then Verified: integrity established.
        await using var db = P.Database();
        Assert.True(await supplemental.PersistAndVerifyAsync(db, P.Session(), package, default));
        Assert.True(await supplemental.PersistAndVerifyAsync(db, P.Session(), package, default));
        // A mismatch is durably marked (known invalid, consumers treat it as unavailable): integrity is KNOWN, so this is true as well.
        var stored = await db.FuturesSupplemental.SingleAsync(); stored.Ofi = 424242; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.True(await supplemental.PersistAndVerifyAsync(db, P.Session(), package, default));
        Assert.Single(await db.ProjectionHealth.ToListAsync());
        // A package without supplemental metrics has nothing to verify.
        Assert.True(await supplemental.PersistAndVerifyAsync(P.Database(), P.Session(), package with { Microstructure = null, OptionsSupplemental = null }, default));
    }

    static AdaptiveCompletedBarPackage FirstPackage()
    {
        var engine = new AdaptiveObserverEngine(new AdaptiveSessionDefinition(new DateOnly(2026, 10, 8), "FUT", "FUT", new DateOnly(2026, 10, 29), 65, 100),
            new DateOnly(2026, 10, 15), .065, null, T0, [], null);
        var rng = new Random(5); double bid = 22995; long volume = 1000;
        for (var i = 0; i < 400; i++)
        {
            bid += rng.Next(-1, 2); volume += rng.Next(0, 70);
            var at = T0.AddSeconds(i * 1.3);
            var tick = new CleanObserverTick(i + 1, at, at, bid + 0.5, bid, bid + 1 + rng.Next(0, 2), rng.Next(1, 400), rng.Next(1, 400), volume, 5000);
            var packages = engine.ProcessAvailabilityGroup([("FUT", tick)]);
            if (packages.Count > 0) return packages[0];
        }

        throw new InvalidOperationException("No completed bar produced.");
    }

    // ------------------------------------------------------------------ B2

    [Fact]
    public void RuntimeRow_IsPersistedAboutOncePerSecond_NotEveryPoll_ButAlwaysOnCompletionOrClose()
    {
        var flushes = 0; DateTimeOffset? last = null;
        for (var i = 0; i < 4 * 60; i++)                                   // 60 s of empty 250 ms polls
        {
            var now = T0.AddMilliseconds(250 * i);
            if (AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, last, now)) { flushes++; last = now; }
        }

        Assert.InRange(flushes, 59, 61);                                   // ~1/s instead of 240 writes
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, null, T0));                         // first poll
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 1, T0, T0.AddMilliseconds(10)));       // a completed bar flushes immediately
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(true, 0, T0, T0.AddMilliseconds(10)));        // session close / explicit flush
        Assert.False(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, T0, T0.AddMilliseconds(999)));
        Assert.True(AdaptiveObserverWorker.ShouldFlushRuntime(false, 0, T0, T0.AddSeconds(1)));             // source progress is eventually persisted
        Assert.Equal(TimeSpan.FromSeconds(1), AdaptiveObserverWorker.RuntimePersistInterval);
    }

    // ------------------------------------------------------------------ B3

    [Fact]
    public async Task SteadyStateRuntimeRefresh_IsASingleRuntimeReadBySessionId()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var db = new AdaptiveObserverDbContext(options))
        {
            db.Sessions.Add(P.Session());
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = T0, LastCompletedBarSeq = 12, CurrentPartialBarVolume = 345 });
            await db.SaveChangesAsync();
        }

        var service = new AdaptiveObserverDataService(new Factory(options), NullLogger<AdaptiveObserverDataService>.Instance);
        var runtime = await service.LoadRuntimeAsync(1);
        Assert.NotNull(runtime);
        Assert.Equal((12, 345L, AdaptiveRuntimeStatus.Live), (runtime!.LastCompletedBarSeq, runtime.CurrentPartialBarVolume, runtime.RuntimeStatus));
        Assert.Null(await service.LoadRuntimeAsync(99));                   // unknown session: caller falls back to a full reload
    }
}
