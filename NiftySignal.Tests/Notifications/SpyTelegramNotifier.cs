using NiftySignal.Notifications;

namespace NiftySignal.Tests.Notifications;

public sealed class SpyTelegramNotifier : ITelegramNotifier
{
    public List<(NotificationCategory Category, string Message)> SentMessages { get; } = [];

    public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken)
    {
        SentMessages.Add((category, message));
        return Task.CompletedTask;
    }
}
