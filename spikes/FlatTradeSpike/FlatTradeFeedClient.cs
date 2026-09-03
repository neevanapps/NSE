using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace FlatTradeSpike;

/// <summary>
/// Raw WebSocket handling for FlatTrade's Pi Connect feed: connect handshake, heartbeat,
/// subscribe, and a receive loop that logs every raw message with a local receive
/// timestamp. Deliberately doesn't reuse the reference iBuzz WebSocket wrapper's
/// sync-over-async style (blocking .Wait() calls) -- this is async/await throughout,
/// which is what the real NiftySignal.Ingestion worker will need anyway.
/// </summary>
public sealed class FlatTradeFeedClient(string wsUrl, string uid, string susertoken) : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly TaskCompletionSource _sessionEstablished = new();
    readonly Dictionary<string, int> _messageCountsByType = [];
    StreamWriter? _log;
    Task? _receiveLoop;
    Task? _heartbeatLoop;

    public IReadOnlyDictionary<string, int> MessageCountsByType => _messageCountsByType;

    public async Task ConnectAsync(string logFilePath, CancellationToken ct)
    {
        _log = new StreamWriter(logFilePath, append: false) { AutoFlush = true };

        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        Console.WriteLine("[ws] connected");

        _receiveLoop = ReceiveLoopAsync(ct);
        _heartbeatLoop = HeartbeatLoopAsync(ct);

        await SendAsync($$"""{"t":"c","uid":"{{uid}}","actid":"{{uid}}","susertoken":"{{susertoken}}"}""", ct);

        // "ck" (session established) must arrive before subscribing to anything.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        await _sessionEstablished.Task.WaitAsync(linked.Token);
        Console.WriteLine("[ws] session established (ck)");
    }

    public Task SubscribeTouchlineAsync(string exch, string token, CancellationToken ct) =>
        SendAsync($$"""{"t":"t","k":"{{exch}}|{{token}}"}""", ct);

    public Task SubscribeDepthAsync(string exch, string token, CancellationToken ct) =>
        SendAsync($$"""{"t":"d","k":"{{exch}}|{{token}}"}""", ct);

    async Task SendAsync(string json, CancellationToken ct)
    {
        Console.WriteLine($"[ws send] {json}");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // FlatTrade's own reference client pings roughly every 60s to hold the
        // connection open; 45s leaves margin without spamming the socket.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(45), ct);
                await SendAsync("""{"t":"h"}""", ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("[ws] server closed the connection");
                        return;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    void HandleMessage(string json)
    {
        var receivedAt = DateTimeOffset.Now;
        _log!.WriteLine($"{receivedAt:O}\t{json}");

        string type = "?";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("t", out var tProp))
            {
                type = tProp.GetString() ?? "?";
            }
        }
        catch (JsonException)
        {
            type = "unparsable";
        }

        _messageCountsByType[type] = _messageCountsByType.GetValueOrDefault(type) + 1;

        if (type == "ck")
        {
            _sessionEstablished.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "spike complete", CancellationToken.None);
        }

        if (_receiveLoop is not null)
        {
            await Task.WhenAny(_receiveLoop, Task.Delay(TimeSpan.FromSeconds(2)));
        }

        _log?.Dispose();
        _ws.Dispose();
    }
}
