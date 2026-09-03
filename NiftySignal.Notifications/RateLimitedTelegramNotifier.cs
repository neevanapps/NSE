using System.Collections.Concurrent;

namespace NiftySignal.Notifications;

/// <summary>
/// Per-category cooldown decorator (plan section 12: "an alert storm during a volatile
/// session is how you learn to ignore alerts"). Trade-related categories intentionally
/// have no cooldown here -- MaxTradesPerDay and the entry re-entry gap already bound how
/// often those can legitimately fire; a genuinely fast sequence of trade alerts is real
/// information, not spam. The categories at real risk of firing repeatedly in a short
/// window (a flapping reconnect, a bug toggling the kill switch) get one.
/// </summary>
public sealed class RateLimitedTelegramNotifier(ITelegramNotifier inner, TimeProvider timeProvider) : ITelegramNotifier
{
    static readonly IReadOnlyDictionary<NotificationCategory, TimeSpan> Cooldowns = new Dictionary<NotificationCategory, TimeSpan>
    {
        [NotificationCategory.ConnectionFailure] = TimeSpan.FromMinutes(5),
        [NotificationCategory.KillSwitchToggle] = TimeSpan.FromSeconds(30),
    };

    readonly ConcurrentDictionary<NotificationCategory, DateTimeOffset> _lastSentAt = new();

    public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (Cooldowns.TryGetValue(category, out var cooldown))
        {
            var lastSent = _lastSentAt.GetOrAdd(category, DateTimeOffset.MinValue);
            if (now - lastSent < cooldown)
            {
                return Task.CompletedTask; // suppressed -- still within cooldown
            }

            _lastSentAt[category] = now;
        }

        return inner.SendAsync(category, message, cancellationToken);
    }
}
