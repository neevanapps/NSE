using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Notifications;

namespace NiftySignal.Host;

/// <summary>
/// Decides, from the configuration contract shared with the Dashboard (<see cref="AdaptiveCommentaryTelegramOptions"/> + <see cref="TelegramOptions"/>),
/// whether live commentary may create Telegram notification jobs. Telegram disabled or unconfigured means events are still persisted and shown on
/// the Dashboard but NO job is created, and every transition to that state (and the first evaluation after a start) makes unsent jobs terminally
/// <see cref="AdaptiveCommentaryNotificationStatus.Suppressed"/>, so re-enabling can only ever announce FUTURE events, never a stale backlog.
/// The classification of market events itself never depends on this configuration.
/// </summary>
public sealed class AdaptiveCommentaryNotificationGate(
    IOptionsMonitor<AdaptiveCommentaryTelegramOptions> commentaryTelegram,
    IOptionsMonitor<TelegramOptions> telegram,
    ILogger<AdaptiveCommentaryNotificationGate> logger)
{
    public const string DisabledReason = "Suppressed: commentary Telegram delivery is disabled or not configured; unsent commentary is never delivered later.";

    bool? _lastDeliverable;

    public bool IsDeliverable => commentaryTelegram.CurrentValue.IsDeliverable(telegram.CurrentValue);

    public async Task<CommentaryNotificationMode> SyncAsync(AdaptiveObserverDbContext observer, CancellationToken ct)
    {
        var deliverable = IsDeliverable;
        if (!deliverable && _lastDeliverable != false)
        {
            try
            {
                var suppressed = await AdaptiveCommentaryOutbox.SuppressAllPendingAsync(observer, DisabledReason, ct);
                logger.LogInformation("Adaptive commentary notifications are disabled/unconfigured; {Count} unsent job(s) suppressed.", suppressed);
                _lastDeliverable = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Outbox maintenance must never reach the core observer; it is retried on the next completed bar.
                logger.LogWarning(ex, "Could not suppress unsent commentary jobs while notifications are disabled.");
                try { observer.ChangeTracker.Clear(); } catch (Exception) { /* nothing more to protect */ }
            }
        }
        else if (deliverable)
        {
            _lastDeliverable = true;
        }

        return deliverable ? CommentaryNotificationMode.EnqueueFinalBar : CommentaryNotificationMode.Suppress;
    }
}
