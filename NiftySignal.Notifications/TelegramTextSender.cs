using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NiftySignal.Notifications;

public interface ITelegramTextSender
{
    Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct);
}

/// <summary>
/// Acknowledged text delivery for the durable commentary outbox. Mirrors <see cref="TelegramDocumentSender"/>: it reports Sent only when
/// Telegram acknowledges with a message id, Rejected when Telegram definitively refuses (safe to retry later), and Uncertain otherwise
/// (never resent automatically). Exception messages are never surfaced because HTTP errors can contain the bot token URL.
/// </summary>
public sealed class TelegramTextSender(HttpClient http, IOptions<TelegramOptions> options, Uri? validationApi = null) : ITelegramTextSender, IDisposable
{
    public async Task<TelegramDocumentResult> SendAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken) || string.IsNullOrWhiteSpace(options.Value.ChatId))
            return new(TelegramDocumentOutcome.Rejected, Error: "Telegram configuration missing.");
        try
        {
            // Real bot tokens contain ':'; the leading '/' keeps "bot123:token/..." an HTTPS path, not a URI scheme.
            var endpoint = new Uri(validationApi ?? new Uri("https://api.telegram.org/"), $"/bot{options.Value.BotToken}/sendMessage");
            using var content = new StringContent(JsonSerializer.Serialize(new { chat_id = options.Value.ChatId, text }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && response.IsSuccessStatusCode
                && root.TryGetProperty("result", out var result) && result.TryGetProperty("message_id", out var message)
                && message.TryGetInt64(out var id) && id > 0)
                return new(TelegramDocumentOutcome.Sent, id);
            if (ok.ValueKind == JsonValueKind.False)
            {
                var retry = 60;
                if (root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var after) && after.TryGetInt32(out var seconds))
                    retry = Math.Clamp(seconds, 1, 86400);
                return new(TelegramDocumentOutcome.Rejected, RetryAfterSeconds: retry, Error: $"Telegram rejected message (HTTP {(int)response.StatusCode}).");
            }

            return new(TelegramDocumentOutcome.Uncertain, Error: "Telegram response did not acknowledge delivery.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return new(TelegramDocumentOutcome.Uncertain, Error: $"Send outcome unknown ({ex.GetType().Name}); no automatic resend.");
        }
    }

    public void Dispose() => http.Dispose();
}
