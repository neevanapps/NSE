using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Notifications;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Explicit opt-in for sending commentary to Telegram. Commentary events are always persisted and shown on the Dashboard; messages are only
/// sent when this is enabled AND Dashboard Telegram settings exist. Default is off so a deployment never starts messaging a real account by surprise.
/// </summary>
public sealed class AdaptiveCommentaryTelegramOptions
{
    public const string SectionName = "AdaptiveCommentaryTelegram";
    public bool Enabled { get; set; }
}

/// <summary>
/// Durable commentary outbox delivery (08-Oct plan section 61), mirroring the screenshot outbox: the job is marked Sending and saved before the
/// first byte leaves, Sent jobs are never resent, a Telegram acknowledgement is required for Sent, an interrupted or unacknowledged send becomes
/// DeliveryUncertain and is never retried automatically, and a definitive Telegram refusal is retried after its retry_after.
/// </summary>
public sealed class AdaptiveCommentaryNotificationProcessor(ITelegramTextSender sender, TimeProvider? clock = null)
{
    public static async Task RecoverInterruptedAsync(AdaptiveObserverDbContext db, CancellationToken ct)
    {
        foreach (var job in await db.CommentaryNotificationJobs.Where(x => x.Status == AdaptiveCommentaryNotificationStatus.Sending).ToListAsync(ct))
        {
            job.Status = AdaptiveCommentaryNotificationStatus.DeliveryUncertain;
            job.LastError = "Interrupted send; confirm delivery in Telegram before any manual retry.";
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ProcessOneAsync(AdaptiveObserverDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Telegram retry_after is chat-wide: do not immediately send a different queued job.
        if (await db.CommentaryNotificationJobs.AnyAsync(x => x.Status == AdaptiveCommentaryNotificationStatus.Pending && x.NextAttemptUtc > now
            && x.LastError != null && x.LastError.StartsWith("Telegram rejected"), ct)) return;
        var job = await db.CommentaryNotificationJobs.Where(x => x.Status == AdaptiveCommentaryNotificationStatus.Pending && x.NextAttemptUtc <= now)
            .OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (job is null) return;
        var ev = await db.CommentaryEvents.AsNoTracking().SingleAsync(x => x.Id == job.EventId, ct);
        var text = CommentaryNotificationPolicy.FormatMessage(ev.TradeDate, ev.BarSeq, ev.OccurredAtUtc, ev.RenderedCommentary);

        job.Status = AdaptiveCommentaryNotificationStatus.Sending;
        job.Attempts++;
        await db.SaveChangesAsync(ct);                       // durable before any byte leaves
        TelegramDocumentResult delivery;
        try { delivery = await sender.SendAsync(text, ct); }
        catch (Exception ex)
        {
            // Exception messages can contain the bot URL and token; type names are safe.
            var types = ex.GetType().Name;
            for (var inner = ex.InnerException; inner is not null && types.Length < 300; inner = inner.InnerException) types += $" -> {inner.GetType().Name}";
            delivery = new(TelegramDocumentOutcome.Uncertain, Error: $"Sender interrupted ({types}); delivery uncertain; no automatic resend.");
        }

        var acknowledgedAt = (clock ?? TimeProvider.System).GetUtcNow();
        job.Status = delivery.Outcome switch
        {
            TelegramDocumentOutcome.Sent => AdaptiveCommentaryNotificationStatus.Sent,
            TelegramDocumentOutcome.Rejected => AdaptiveCommentaryNotificationStatus.Pending,
            _ => AdaptiveCommentaryNotificationStatus.DeliveryUncertain,
        };
        job.TelegramMessageId = delivery.MessageId;
        job.SentAtUtc = delivery.Outcome == TelegramDocumentOutcome.Sent ? acknowledgedAt : null;
        job.NextAttemptUtc = acknowledgedAt.AddSeconds(delivery.RetryAfterSeconds);
        job.LastError = delivery.Error is { Length: > 512 } e ? e[..512] : delivery.Error;
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

public sealed class AdaptiveCommentaryNotificationWorker(IDbContextFactory<AdaptiveObserverDbContext> factory,
    AdaptiveCommentaryNotificationProcessor processor, IOptions<AdaptiveCommentaryTelegramOptions> options,
    IOptions<TelegramOptions> telegram, IHostApplicationLifetime lifetime, ILogger<AdaptiveCommentaryNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { logger.LogInformation("Adaptive commentary Telegram delivery is not enabled (events are still persisted and shown on the Dashboard)."); return; }
        if (string.IsNullOrWhiteSpace(telegram.Value.BotToken) || string.IsNullOrWhiteSpace(telegram.Value.ChatId))
        { logger.LogError("Adaptive commentary Telegram delivery requires Dashboard Telegram configuration; nothing will be sent."); return; }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                await db.Database.OpenConnectionAsync(stoppingToken);
                // Connection-scoped PostgreSQL lease: two Dashboards cannot deliver the same outbox.
                var held = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(73063006) AS \"Value\"").SingleAsync(stoppingToken);
                if (held)
                {
                    try
                    {
                        await AdaptiveCommentaryNotificationProcessor.RecoverInterruptedAsync(db, stoppingToken);
                        await processor.ProcessOneAsync(db, DateTimeOffset.UtcNow, stoppingToken);
                    }
                    finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(73063006)", CancellationToken.None); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Adaptive commentary notification cycle failed ({ErrorType}); market calculations are unaffected.", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
