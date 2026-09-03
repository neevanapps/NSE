namespace NiftySignal.Notifications;

/// <summary>Bound from the "Telegram" config section. BotToken belongs in appsettings.Local.json only.</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public required string BotToken { get; set; }

    public required string ChatId { get; set; }
}
