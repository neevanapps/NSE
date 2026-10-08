using System.Net;
using System.Text;
using System.Text.Json;
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
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Notifications;

namespace NiftySignal.Tests.Dashboard;

/// <summary>08-Oct plan Slice 4C: Dashboard panel, Telegram policy/outbox and the text sender.</summary>
public sealed class AdaptiveCommentaryDeliveryTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 8, 4, 30, 0, TimeSpan.Zero);

    // ---- pure policy ----

    static CommentaryEvent Event(CommentaryEventType type, EventBias bias, CommentaryLifecycle lifecycle, EventBias previous, int bar = 20) =>
        CommentaryEvaluatorProxy.Make(type, bias, lifecycle, previous, bar);

    [Fact]
    public void TelegramEligibility_IsLimitedToConfirmedFlippedAndActualBiasChange()
    {
        Assert.Equal((true, "Confirmed"), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.Confirmed, EventBias.Neutral, EventBias.Long));
        Assert.Equal((true, "Flipped"), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.Flipped, EventBias.Short, EventBias.Long));
        Assert.Equal((true, "BiasChanged"), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.New, EventBias.Short, EventBias.Neutral));
        Assert.Equal((false, null), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.New, EventBias.Neutral, EventBias.Short));          // ordinary New expansion
        Assert.Equal((false, null), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.Resolved, EventBias.Short, EventBias.Neutral));       // resolution alone
        Assert.Equal((false, null), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.Strengthening, EventBias.Short, EventBias.Short));
        Assert.Equal((false, null), CommentaryEvaluator.TelegramPolicy(CommentaryLifecycle.Weakening, EventBias.Long, EventBias.Long));
    }

    [Fact]
    public void Cooldown_AppliesToNonBypassClassesOnly_AndEveryV1EligibleClassBypassesIt()
    {
        var last = new LastCommentaryNotification(CommentaryEventType.SellerExpansion, EventBias.Short, 20);
        // The three V1 eligible reasons bypass the cooldown even for the identical event type and bias one bar later.
        foreach (var reason in new[] { "Confirmed", "Flipped", "BiasChanged" })
            Assert.True(CommentaryNotificationPolicy.ShouldEnqueue(Event(CommentaryEventType.SellerExpansion, EventBias.Short, CommentaryLifecycle.New, EventBias.Short, 21) with
            { ShouldNotifyTelegram = true, NotificationReason = reason }, last));
        // A hypothetical future class (no bypass reason) is throttled for the same type + bias for 5 bars, then allowed; a different event is never throttled.
        var other = Event(CommentaryEventType.SellerExpansion, EventBias.Short, CommentaryLifecycle.New, EventBias.Short, 24) with { ShouldNotifyTelegram = true, NotificationReason = "Other" };
        Assert.False(CommentaryNotificationPolicy.ShouldEnqueue(other, last));
        Assert.True(CommentaryNotificationPolicy.ShouldEnqueue(other with { BarSeq = 25 }, last));
        Assert.True(CommentaryNotificationPolicy.ShouldEnqueue(other with { EventType = CommentaryEventType.LongLiquidation }, last));
        Assert.False(CommentaryNotificationPolicy.ShouldEnqueue(other with { ShouldNotifyTelegram = false }, null));
        Assert.Equal(5, CommentaryNotificationPolicy.MinimumBarsBetweenSameEventTelegram);
    }

    [Fact]
    public void Message_CarriesTheRenderedCommentary_IsBoundedAndNeverSaysConfidence()
    {
        var text = CommentaryNotificationPolicy.FormatMessage(new DateOnly(2026, 10, 8), 16, T0.AddMinutes(48), "Seller rejection confirmed.\nBias LONG | Regime TRANSITION | EvidenceAgreement HIGH.");
        Assert.StartsWith("NIFTY adaptive commentary | 2026-10-08 | bar 16 | 10:48:00 IST", text);
        Assert.Contains("Seller rejection confirmed.", text);
        Assert.Contains("not a probability or a trade signal", text);
        Assert.DoesNotContain("onfidence", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(CommentaryNotificationPolicy.FormatMessage(new DateOnly(2026, 10, 8), 1, T0, new string('x', 9000)).Length <= 4000);
    }

    // ---- text sender (acknowledged delivery) ----

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static TelegramTextSender Sender(StubHandler handler) => new(new HttpClient(handler), Options.Create(new TelegramOptions { BotToken = "123456:secret-token", ChatId = "42" }));

    [Fact]
    public async Task TextSender_ReportsSentOnlyWithAnAcknowledgedMessageId_AndUsesTheColonTokenPath()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":777}}")));
        var result = await Sender(handler).SendAsync("hello", default);
        Assert.Equal(TelegramDocumentOutcome.Sent, result.Outcome); Assert.Equal(777, result.MessageId);
        Assert.Equal("/bot123456:secret-token/sendMessage", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("api.telegram.org", handler.Request.RequestUri.Host);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("42", body.RootElement.GetProperty("chat_id").GetString()); Assert.Equal("hello", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task TextSender_ClassifiesRefusalAsRejected_AndAnythingUnacknowledgedAsUncertain_WithoutLeakingTheToken()
    {
        var refused = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{\"ok\":false,\"parameters\":{\"retry_after\":17}}")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Rejected, refused.Outcome); Assert.Equal(17, refused.RetryAfterSeconds);

        var ok200NoAck = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, ok200NoAck.Outcome); Assert.Null(ok200NoAck.MessageId);

        var garbage = await Sender(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, "not json")))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, garbage.Outcome);

        var broken = await Sender(new StubHandler(_ => throw new HttpRequestException("POST https://api.telegram.org/bot123456:secret-token/sendMessage failed"))).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Uncertain, broken.Outcome);
        Assert.DoesNotContain("secret-token", broken.Error);

        var missing = await new TelegramTextSender(new HttpClient(new StubHandler(_ => throw new InvalidOperationException())), Options.Create(new TelegramOptions { BotToken = "", ChatId = "" })).SendAsync("x", default);
        Assert.Equal(TelegramDocumentOutcome.Rejected, missing.Outcome);
    }

    // ---- outbox processor ----

    static AdaptiveObserverDbContext Database() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    static async Task<(AdaptiveObserverDbContext Db, AdaptiveCommentaryEventRow Event, AdaptiveCommentaryNotificationJobRow Job)> OutboxWithOneJob()
    {
        var db = Database();
        db.Sessions.Add(new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = new DateOnly(2026, 10, 8), ModelVersion = "adaptive-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0,
            FutureToken = "FUT", FutureSymbol = "FUT", FutureExpiry = new DateOnly(2026, 10, 29), OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0,
            EstimatorName = "V1", CreatedAtUtc = T0,
        });
        var ev = new AdaptiveCommentaryEventRow
        {
            SessionId = 1, TradeDate = new DateOnly(2026, 10, 8), BarSeq = 16, OccurredAtUtc = T0.AddMinutes(48), EventType = CommentaryEventType.SellerRejectionConfirmed,
            EventBias = EventBias.Long, MarketRegime = MarketRegime.Transition, Lifecycle = CommentaryLifecycle.Confirmed, EvidenceAgreement = EvidenceAgreement.High,
            Severity = CommentarySeverity.High, PreviousBias = EventBias.Neutral, BiasChanged = true, PrimaryEvidenceJson = "[]", ConfirmationEvidenceJson = "[]",
            ContradictionEvidenceJson = "[]", DataQualityJson = "[]", RenderedCommentary = "Seller rejection confirmed.\nBias LONG | Regime TRANSITION | EvidenceAgreement HIGH.",
            ShouldNotifyTelegram = true, NotificationReason = "Confirmed", CommentaryVersion = "commentary-v1", EventIdentity = "1:16:SellerRejectionConfirmed:Confirmed:Long",
        };
        db.CommentaryEvents.Add(ev); await db.SaveChangesAsync();
        var job = new AdaptiveCommentaryNotificationJobRow { EventId = ev.Id, Status = AdaptiveCommentaryNotificationStatus.Pending, CreatedAtUtc = T0, NextAttemptUtc = T0 };
        db.CommentaryNotificationJobs.Add(job); await db.SaveChangesAsync();
        return (db, ev, job);
    }

    sealed class FakeSender(Func<string, TelegramDocumentResult> respond, Func<Task>? beforeReturn = null) : ITelegramTextSender
    {
        public List<string> Sent { get; } = [];
        public async Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            if (beforeReturn is not null) await beforeReturn();
            return respond(text);
        }
    }

    [Fact]
    public async Task Outbox_MarksSendingDurablyBeforeSending_ThenSentWithTheAcknowledgedMessageId_AndNeverResends()
    {
        var (db, _, job) = await OutboxWithOneJob();
        await using var _ = db;
        AdaptiveCommentaryNotificationStatus? statusWhileSending = null;
        var sender = new FakeSender(_ => new(TelegramDocumentOutcome.Sent, 555),
            async () => statusWhileSending = (await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync()).Status);
        var processor = new AdaptiveCommentaryNotificationProcessor(sender);

        await processor.ProcessOneAsync(db, T0.AddHours(1), default);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Sending, statusWhileSending);        // durable before the send completed
        var sent = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Sent, sent.Status); Assert.Equal(555, sent.TelegramMessageId);
        Assert.NotNull(sent.SentAtUtc); Assert.Equal(1, sent.Attempts);
        Assert.Contains("Seller rejection confirmed.", Assert.Single(sender.Sent));

        await processor.ProcessOneAsync(db, T0.AddHours(2), default);                           // nothing pending: never resent
        await AdaptiveCommentaryNotificationProcessor.RecoverInterruptedAsync(db, default);     // restart: Sent stays Sent
        await processor.ProcessOneAsync(db, T0.AddHours(3), default);
        Assert.Single(sender.Sent);
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Sent, (await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(job.Id, sent.Id);
    }

    [Fact]
    public async Task Outbox_UncertainOrThrowingSenders_BecomeDeliveryUncertain_AndAreNeverRetriedAutomatically()
    {
        foreach (var throwing in new[] { false, true })
        {
            var (db, _, _) = await OutboxWithOneJob();
            await using var _ = db;
            ITelegramTextSender sender = throwing
                ? new ThrowingSender()
                : new FakeSender(_ => new(TelegramDocumentOutcome.Uncertain, Error: "Telegram response did not acknowledge delivery."));
            var processor = new AdaptiveCommentaryNotificationProcessor(sender);
            await processor.ProcessOneAsync(db, T0.AddHours(1), default);
            var job = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
            Assert.Equal(AdaptiveCommentaryNotificationStatus.DeliveryUncertain, job.Status);
            Assert.Null(job.TelegramMessageId); Assert.Null(job.SentAtUtc);
            if (throwing) Assert.DoesNotContain("secret", job.LastError);

            var second = new FakeSender(_ => new(TelegramDocumentOutcome.Sent, 1));
            await new AdaptiveCommentaryNotificationProcessor(second).ProcessOneAsync(db, T0.AddDays(1), default);
            Assert.Empty(second.Sent);                                                           // an ambiguous job is never blindly resent
        }
    }

    sealed class ThrowingSender : ITelegramTextSender
    {
        public Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct) =>
            throw new HttpRequestException("https://api.telegram.org/bot123:secret/sendMessage failed", new IOException("reset"));
    }

    [Fact]
    public async Task Outbox_DefiniteRefusalIsRetriedAfterRetryAfter_AndHoldsOtherJobsMeanwhile()
    {
        var (db, ev, _) = await OutboxWithOneJob();
        await using var _ = db;
        db.CommentaryEvents.Add(new AdaptiveCommentaryEventRow
        {
            SessionId = 1, TradeDate = ev.TradeDate, BarSeq = 17, OccurredAtUtc = ev.OccurredAtUtc.AddMinutes(1), EventType = CommentaryEventType.SellerExpansion, EventBias = EventBias.Short,
            MarketRegime = MarketRegime.Bearish, Lifecycle = CommentaryLifecycle.Flipped, EvidenceAgreement = EvidenceAgreement.Medium, Severity = CommentarySeverity.High,
            PreviousBias = EventBias.Long, PrimaryEvidenceJson = "[]", ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]",
            RenderedCommentary = "second", ShouldNotifyTelegram = true, NotificationReason = "Flipped", CommentaryVersion = "commentary-v1", EventIdentity = "1:17:SellerExpansion:Flipped:Short",
        });
        await db.SaveChangesAsync();
        db.CommentaryNotificationJobs.Add(new AdaptiveCommentaryNotificationJobRow { EventId = db.CommentaryEvents.Local.Last().Id, Status = AdaptiveCommentaryNotificationStatus.Pending, CreatedAtUtc = T0, NextAttemptUtc = T0 });
        await db.SaveChangesAsync();

        var refusing = new FakeSender(_ => new(TelegramDocumentOutcome.Rejected, RetryAfterSeconds: 30, Error: "Telegram rejected message (HTTP 429)."));
        var now = T0.AddHours(1);
        await new AdaptiveCommentaryNotificationProcessor(refusing, new FixedClock(now)).ProcessOneAsync(db, now, default);
        var first = await db.CommentaryNotificationJobs.AsNoTracking().OrderBy(x => x.Id).FirstAsync();
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Pending, first.Status); Assert.Equal(now.AddSeconds(30), first.NextAttemptUtc);

        var other = new FakeSender(_ => new(TelegramDocumentOutcome.Sent, 9));
        await new AdaptiveCommentaryNotificationProcessor(other, new FixedClock(now)).ProcessOneAsync(db, now.AddSeconds(10), default);
        Assert.Empty(other.Sent);                                                                // chat-wide hold: the second job waits
        await new AdaptiveCommentaryNotificationProcessor(other, new FixedClock(now)).ProcessOneAsync(db, now.AddSeconds(31), default);
        Assert.Single(other.Sent);                                                               // after retry_after the same (oldest) job goes out
        Assert.Equal(AdaptiveCommentaryNotificationStatus.Sent, (await db.CommentaryNotificationJobs.AsNoTracking().OrderBy(x => x.Id).FirstAsync()).Status);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Outbox_InterruptedSendBecomesDeliveryUncertainOnRestart()
    {
        var (db, _, job) = await OutboxWithOneJob();
        await using var _ = db;
        job.Status = AdaptiveCommentaryNotificationStatus.Sending; await db.SaveChangesAsync();
        await AdaptiveCommentaryNotificationProcessor.RecoverInterruptedAsync(db, default);
        var recovered = await db.CommentaryNotificationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(AdaptiveCommentaryNotificationStatus.DeliveryUncertain, recovered.Status);
        Assert.Contains("confirm delivery", recovered.LastError);
    }

    // ---- enqueue from the commentary service ----

    sealed record Spec(int Seq, int Flow, long? RollOi, double Bar, double Roll);

    static async Task SeedBars(AdaptiveObserverDbContext db, params Spec[] specs)
    {
        foreach (var s in specs)
        {
            var end = T0.AddMinutes(s.Seq); var start = end.AddMinutes(-1);
            db.FutureBars.Add(new()
            {
                SessionId = 1, BarSeq = s.Seq, StartAvailableAtUtc = start, EndAvailableAtUtc = end, Volume = 1000, TradeUpdates = 5, Open = 23000, High = 23010, Low = 22990,
                Close = 23000 + s.Bar, DurationSeconds = 60, BarPriceDisplacement = s.Bar, StrictDelta = s.Flow * 100, EnrichedDelta = s.Flow * 110,
            });
            if (s.Seq >= 10)
                db.RollingStates.Add(new()
                {
                    SessionId = 1, StartBarSeq = s.Seq - 9, EndBarSeq = s.Seq, StartAvailableAtUtc = start.AddMinutes(-9), EndAvailableAtUtc = end, PriceDisplacement = s.Roll,
                    StrictDelta = s.Flow * 1000, EnrichedDelta = s.Flow * 1100, OiChange = s.RollOi, Efficiency = .5, RollingStrictAbsDeltaChange = 0, State = AdaptiveStateKind.Normal,
                });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Service_EnqueuesAJobOnlyForNewlyInsertedEligibleEvents_AndNeverRequeuesOnReplay()
    {
        var (db, _, _) = await OutboxWithOneJob();
        await using var _ = db;
        db.CommentaryNotificationJobs.RemoveRange(db.CommentaryNotificationJobs); db.CommentaryEvents.RemoveRange(db.CommentaryEvents);
        (await db.Sessions.SingleAsync()).BaseBarVolume = 1000;                                 // bars below are exact-volume bars (readiness gate)
        await db.SaveChangesAsync();
        await SeedBars(db,
            [.. Enumerable.Range(1, 9).Select(i => new Spec(i, -1, 50, -2, -5)),
             new(10, -1, 50, -2, -5),     // SellerExpansion New            : not eligible (Neutral -> Short from silence)
             new(11, 1, -5, 2, 5),        // ShortCovering                  : Flipped (Short -> Long)               => queued
             new(12, -1, 50, 0.5, -5),    // SellerAbsorption               : Long -> Neutral through an event      => queued (BiasChanged)
             new(13, 0, 0, 0, 0)]);       // nothing material               : Resolved                              => not queued
        var service = new AdaptiveCommentaryService(new AdaptiveCommentaryFrameLoader(), NullLogger<AdaptiveCommentaryService>.Instance);
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        await service.ProcessThroughAsync(db, session, 13, default);

        var events = await db.CommentaryEvents.AsNoTracking().OrderBy(x => x.BarSeq).ToListAsync();
        Assert.Equal([CommentaryLifecycle.New, CommentaryLifecycle.Flipped, CommentaryLifecycle.New, CommentaryLifecycle.Resolved], events.Select(x => x.Lifecycle));
        Assert.Equal([false, true, true, false], events.Select(x => x.ShouldNotifyTelegram));
        var jobs = await db.CommentaryNotificationJobs.AsNoTracking().OrderBy(x => x.EventId).ToListAsync();
        Assert.Equal([events[1].Id, events[2].Id], jobs.Select(x => x.EventId));
        Assert.All(jobs, j => Assert.Equal(AdaptiveCommentaryNotificationStatus.Pending, j.Status));
        Assert.Equal(12, (await db.CommentaryRuntime.AsNoTracking().SingleAsync()).LastTelegramBarSeq);

        // Replaying everything (checkpoint lost) verifies the events and queues nothing new.
        db.CommentaryRuntime.RemoveRange(db.CommentaryRuntime); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await service.ProcessThroughAsync(db, session, 13, default);
        Assert.Equal(2, await db.CommentaryNotificationJobs.CountAsync());
        Assert.Equal(4, await db.CommentaryEvents.CountAsync());
        Assert.Equal(0, service.MismatchCount);
    }

    // ---- Dashboard panel ----

    static ServiceCollection PanelServices(IDbContextFactory<AdaptiveObserverDbContext> factory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(factory);
        services.AddSingleton<AdaptiveObserverDataService>();
        services.AddSingleton<AdaptiveCommentaryDataService>();
        return services;
    }

    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    static async Task<string> Render(ServiceCollection services)
    {
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<CommentaryPanel>()).ToHtmlString());
        return WebUtility.HtmlDecode(html);
    }

    [Fact]
    public async Task Panel_ShowsTheLatestFiveEventsNewestFirst_WithBiasRegimeLifecycleAndEvidenceAgreement()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        await using (var db = new AdaptiveObserverDbContext(options))
        {
            db.Sessions.Add(new AdaptiveSessionStateRow
            {
                Id = 1, TradeDate = today, ModelVersion = OpeningVolumeProjectionV1.ModelVersion, SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "F", FutureSymbol = "F",
                FutureExpiry = today, OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0, EstimatorName = "V1", CreatedAtUtc = T0,
            });
            for (var i = 1; i <= 7; i++)
                db.CommentaryEvents.Add(new AdaptiveCommentaryEventRow
                {
                    SessionId = 1, TradeDate = today, BarSeq = 10 + i, OccurredAtUtc = T0.AddMinutes(i), EventType = i % 2 == 0 ? CommentaryEventType.SellerExpansion : CommentaryEventType.BuyerExpansion,
                    EventBias = i % 2 == 0 ? EventBias.Short : EventBias.Long, MarketRegime = MarketRegime.Bearish, Lifecycle = CommentaryLifecycle.New,
                    EvidenceAgreement = EvidenceAgreement.High, Severity = CommentarySeverity.Medium, PreviousBias = EventBias.Neutral, PrimaryEvidenceJson = "[]",
                    ConfirmationEvidenceJson = "[]", ContradictionEvidenceJson = "[]", DataQualityJson = "[]", CommentaryVersion = "commentary-v1", EventIdentity = $"1:{10 + i}:x:New:y",
                    RenderedCommentary = $"Title {i} started.\nBias X | Regime Y | EvidenceAgreement HIGH.\nMeaning {i}.\nEvidence: Strict Δ -{i}.",
                });
            db.CommentaryRuntime.Add(new AdaptiveCommentaryRuntimeRow { SessionId = 1, LastEvaluatedBarSeq = 17, CurrentRegime = MarketRegime.Bearish, CurrentBias = EventBias.Short, CommentaryVersion = "commentary-v1" });
            await db.SaveChangesAsync();
        }

        var html = await Render(PanelServices(new Factory(options)));
        Assert.Contains("Live Market Commentary", html);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "class=\"commentary-event\"").Count);
        Assert.True(html.IndexOf("Meaning 7.", StringComparison.Ordinal) < html.IndexOf("Meaning 3.", StringComparison.Ordinal));   // newest first
        Assert.DoesNotContain("Meaning 2.", html);                                                                                    // older than the latest five
        Assert.Contains("Regime BEARISH", html); Assert.Contains("evaluated through bar 17", html);
        Assert.Contains("EvidenceAgreement HIGH", html);
        Assert.Contains("not a probability or a validated signal", html);
        Assert.DoesNotContain("onfidence", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bias X | Regime Y", html);                       // the header chips replace the first two rendered lines
    }

    [Fact]
    public async Task Panel_WithoutASessionOrEvents_ShowsAnEmptyStateAndNeverFails()
    {
        var options = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var html = await Render(PanelServices(new Factory(options)));
        Assert.Contains("No commentary events yet today.", html);
    }
}

/// <summary>Builds a minimal event for pure policy tests without running the evaluator.</summary>
static class CommentaryEvaluatorProxy
{
    public static CommentaryEvent Make(CommentaryEventType type, EventBias bias, CommentaryLifecycle lifecycle, EventBias previous, int bar) => new(
        1, new DateOnly(2026, 10, 8), bar, DateTimeOffset.UnixEpoch, type, bias, MarketRegime.Neutral, lifecycle, EvidenceAgreement.Low, CommentarySeverity.Info,
        previous, previous != bias, [], [], [], [], false, null, CommentaryEvaluator.Version, "text");
}
