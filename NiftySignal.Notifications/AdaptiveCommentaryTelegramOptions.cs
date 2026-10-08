namespace NiftySignal.Notifications;

/// <summary>
/// Explicit opt-in for sending adaptive commentary to Telegram, shared by the Host (which decides whether a notification job may be
/// created) and the Dashboard (which delivers jobs). Commentary events are always persisted and shown on the Dashboard; a notification job
/// exists only while this is enabled AND Telegram credentials are present, so a disabled or unconfigured Telegram can never accumulate a
/// backlog that later announces stale commentary. Default is off.
/// </summary>
public sealed class AdaptiveCommentaryTelegramOptions
{
    public const string SectionName = "AdaptiveCommentaryTelegram";

    public bool Enabled { get; set; }

    /// <summary>True only when commentary notifications are enabled and a bot token and chat id are configured.</summary>
    public bool IsDeliverable(TelegramOptions telegram) =>
        Enabled && !string.IsNullOrWhiteSpace(telegram.BotToken) && !string.IsNullOrWhiteSpace(telegram.ChatId);
}
