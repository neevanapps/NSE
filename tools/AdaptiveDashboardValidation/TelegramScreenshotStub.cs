using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Buffers.Binary;

/// <summary>Loopback Bot API emulator. No real Telegram account or token is used in CI.</summary>
sealed class TelegramScreenshotStub : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly Task _loop;
    public ConcurrentQueue<(string Caption, int Width)> Uploads { get; } = new();
    /// <summary>Texts accepted through sendMessage (commentary outbox).</summary>
    public ConcurrentQueue<string> Messages { get; } = new();
    public TelegramScreenshotStub()
    {
        _listener.Prefixes.Add("http://127.0.0.1:5091/"); _listener.Start(); _loop = RunAsync();
    }
    async Task RunAsync()
    {
        try {
            while (_listener.IsListening) {
                var context = await _listener.GetContextAsync();
                using var buffer = new MemoryStream(); await context.Request.InputStream.CopyToAsync(buffer);
                var bytes = buffer.ToArray(); var text = Encoding.UTF8.GetString(bytes);
                var offset = bytes.AsSpan().IndexOf(new byte[] {137,80,78,71,13,10,26,10});
                if (context.Request.Url!.AbsolutePath.EndsWith("/sendMessage"))
                {
                    using var message = JsonDocument.Parse(text);
                    var ok = message.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && message.RootElement.TryGetProperty("chat_id", out _);
                    if (ok) Messages.Enqueue(t.GetString()!);
                    context.Response.StatusCode = ok ? 200 : 400;
                    var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ok ? (object)new { ok = true, result = new { message_id = 9500 + Messages.Count } } : new { ok = false }));
                    context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(reply); context.Response.Close();
                    continue;
                }
                var valid = context.Request.Url!.AbsolutePath.EndsWith("/sendDocument") && text.Contains("image/png") && offset >= 0 && bytes.Length > offset + 24;
                if (valid) Uploads.Enqueue((text, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 16,4))));
                context.Response.StatusCode = valid ? 200 : 400;
                var json = JsonSerializer.Serialize(valid ? (object)new { ok = true, result = new { message_id = 9000 + Uploads.Count } } : new { ok = false });
                var body = Encoding.UTF8.GetBytes(json); context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(body); context.Response.Close();
            }
        } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { _listener.Close(); await _loop; }
}
