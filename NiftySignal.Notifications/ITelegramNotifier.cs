namespace NiftySignal.Notifications;

public interface ITelegramNotifier
{
    Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken);
}
