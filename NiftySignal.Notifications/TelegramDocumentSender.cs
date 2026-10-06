using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NiftySignal.Notifications;

public enum TelegramDocumentOutcome { Sent, Rejected, Uncertain }
public sealed record TelegramDocumentResult(TelegramDocumentOutcome Outcome, long? MessageId = null, int RetryAfterSeconds = 60, string? Error = null);
public interface ITelegramDocumentSender
{
    Task<TelegramDocumentResult> SendAsync(string file, string caption, CancellationToken ct);
}

/// <summary>Returns acknowledged success; never mistakes HTTP success alone for Telegram acceptance.</summary>
public sealed class TelegramDocumentSender(HttpClient http, IOptions<TelegramOptions> options, Uri? validationApi = null) : ITelegramDocumentSender, IDisposable
{
    public async Task<TelegramDocumentResult> SendAsync(string file, string caption, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BotToken) || string.IsNullOrWhiteSpace(options.Value.ChatId))
            return new(TelegramDocumentOutcome.Rejected, Error: "Telegram configuration missing.");
        await using var stream = File.OpenRead(file);
        using var body = new MultipartFormDataContent();
        body.Add(new StringContent(options.Value.ChatId), "chat_id");
        body.Add(new StringContent(caption), "caption");
        var image = new StreamContent(stream);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        body.Add(image, "document", Path.GetFileName(file));
        try
        {
            // Real bot tokens contain ':'. Without the leading '/', "bot123:token/..."
            // is parsed as an absolute URI with scheme "bot123", not an HTTPS path.
            var endpoint = new Uri(validationApi ?? new Uri("https://api.telegram.org/"), $"/bot{options.Value.BotToken}/sendDocument");
            using var response = await http.PostAsync(endpoint, body, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                && response.IsSuccessStatusCode && root.TryGetProperty("result", out var result)
                && result.TryGetProperty("message_id", out var message) && message.TryGetInt64(out var id) && id > 0)
                return new(TelegramDocumentOutcome.Sent, id);
            if (ok.ValueKind == JsonValueKind.False)
            {
                var retry = 60;
                if (root.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var after) && after.TryGetInt32(out var seconds))
                    retry = Math.Clamp(seconds, 1, 86400);
                return new(TelegramDocumentOutcome.Rejected, RetryAfterSeconds: retry, Error: $"Telegram rejected upload (HTTP {(int)response.StatusCode}).");
            }
            return new(TelegramDocumentOutcome.Uncertain, Error: "Telegram upload response did not acknowledge delivery.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // Do not log exception messages: HTTP exceptions may contain the bot token URL.
            return new(TelegramDocumentOutcome.Uncertain, Error: $"Upload outcome unknown ({ex.GetType().Name}); no automatic resend.");
        }
    }
    public void Dispose() => http.Dispose();
}
