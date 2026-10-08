using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver.Commentary;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Notifications;

namespace NiftySignal.Dashboard.Services;

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
        // Defense in depth: never send an event that sits at or after a commentary replay mismatch (state diverged upstream of it).
        var degradedAt = await db.ProjectionHealth.AsNoTracking()
            .Where(x => x.SessionId == ev.SessionId && x.Component == AdaptiveProjectionComponents.Commentary
                && x.Version == ev.CommentaryVersion && x.BarSeq <= ev.BarSeq)
            .OrderBy(x => x.BarSeq).Select(x => (int?)x.BarSeq).FirstOrDefaultAsync(ct);
        if (degradedAt is { } degradedBar)
        {
            job.Status = AdaptiveCommentaryNotificationStatus.Suppressed;
            job.LastError = $"Suppressed: commentary projection degraded upstream at bar {degradedBar}.";
            await db.SaveChangesAsync(ct);
            return;
        }

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
    AdaptiveCommentaryNotificationProcessor processor, IOptionsMonitor<AdaptiveCommentaryTelegramOptions> options,
    IOptionsMonitor<TelegramOptions> telegram, IHostApplicationLifetime lifetime, ILogger<AdaptiveCommentaryNotificationWorker> logger) : BackgroundService
{
    internal const string DisabledReason = "Suppressed: commentary Telegram delivery is disabled or not configured; unsent commentary is never delivered later.";

    /// <summary>While not deliverable, unsent jobs (for example created by a Host whose configuration differs) are re-suppressed this often.</summary>
    static readonly TimeSpan ResuppressInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        bool? lastDeliverable = null;
        var lastSuppressUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-evaluated every cycle so enabling/disabling through configuration takes effect without a restart.
            var deliverable = options.CurrentValue.IsDeliverable(telegram.CurrentValue);
            try
            {
                if (!deliverable)
                {
                    // On start and on every transition to "not deliverable": unsent jobs become terminally non-deliverable, so re-enabling later
                    // can only ever announce FUTURE events, never a stale backlog.
                    if (lastDeliverable != false || DateTimeOffset.UtcNow - lastSuppressUtc >= ResuppressInterval)
                    {
                        await using var db = await factory.CreateDbContextAsync(stoppingToken);
                        var suppressed = await AdaptiveCommentaryOutbox.SuppressAllPendingAsync(db, DisabledReason, stoppingToken);
                        lastSuppressUtc = DateTimeOffset.UtcNow;
                        if (suppressed > 0 || lastDeliverable != false)
                            logger.LogInformation("Adaptive commentary Telegram delivery is not enabled/configured (events are still persisted and shown on the Dashboard); {Count} unsent job(s) suppressed.", suppressed);
                    }
                }
                else
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

                lastDeliverable = deliverable;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Adaptive commentary notification cycle failed ({ErrorType}); market calculations are unaffected.", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
