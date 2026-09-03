using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NiftySignal.Notifications;

/// <summary>
/// Sends via a raw POST to Telegram's Bot API rather than the official Telegram.Bot NuGet
/// package -- sending a plain text message is a 3-line HTTP call with a stable, versioned
/// API surface (api.telegram.org, not a wrapper library's own method names, which do
/// change release to release), so pulling in a large general-purpose bot framework for
/// this one call would be more dependency than the job needs.
///
/// Never throws: a failed notification is logged, not propagated -- Telegram is a
/// side-channel, and a transient failure to notify must never interrupt the trading
/// pipeline that triggered the notification.
/// </summary>
public sealed class TelegramNotifier(HttpClient http, IOptions<TelegramOptions> options, ILogger<TelegramNotifier> logger)
    : ITelegramNotifier
{
    const string ApiBaseUrl = "https://api.telegram.org";

    public async Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{ApiBaseUrl}/bot{options.Value.BotToken}/sendMessage";
            var body = JsonSerializer.Serialize(new { chat_id = options.Value.ChatId, text = message });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await http.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Telegram notification failed for {Category}: {StatusCode} {Body}",
                    category, response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Telegram notification threw for {Category}", category);
        }
    }
}
