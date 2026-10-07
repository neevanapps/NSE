using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.Upstox.Protocol;

namespace NiftySignal.Ingestion.Upstox;

public sealed class UpstoxTickSource(
    UpstoxRestClient rest, string token, IReadOnlyList<Instrument> instruments,
    IDataGapRecorder gaps, ILogger<UpstoxTickSource> logger) : ILiveTickSource
{
    readonly HashSet<string> _keys = instruments.Select(x => x.NativeInstrumentKey
        ?? throw new InvalidOperationException("Upstox instrument has no native key.")).ToHashSet(StringComparer.Ordinal);
    readonly Lock _keyLock = new();
    readonly Channel<string> _requests = Channel.CreateUnbounded<string>();
    readonly SemaphoreSlim _sendLock = new(1, 1);
    public event Func<int, Task>? ConnectionUnstable;

    public void RequestSubscribe(Exchange exchange, string internalToken)
    {
        if (!internalToken.StartsWith("UP:", StringComparison.Ordinal)) throw new ArgumentException("Upstox requires a namespaced instrument token.", nameof(internalToken));
        var key = internalToken[3..];
        lock (_keyLock)
        {
            if (_keys.Contains(key)) return;
            if (_keys.Count >= 2000) throw new InvalidOperationException("Upstox full-feed subscription limit reached.");
            _keys.Add(key);
        }
        _requests.Writer.TryWrite(key);
    }

    public async IAsyncEnumerable<Tick> ReadTicksAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = Channel.CreateBounded<Tick>(new BoundedChannelOptions(8192) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var pump = PumpAsync(output.Writer, lifetime.Token);
        try
        {
            await foreach (var tick in output.Reader.ReadAllAsync(ct)) yield return tick;
        }
        finally
        {
            lifetime.Cancel();
            try { await pump; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    async Task PumpAsync(ChannelWriter<Tick> output, CancellationToken ct)
    {
        var policy = new ReconnectionPolicy();
        long? gap = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(output, async () =>
                    {
                        policy.RecordSuccess();
                        await gaps.CloseAllOpenGapsAsync(DateTimeOffset.UtcNow, ct);
                        gap = null;
                    }, ct);
                    throw new WebSocketException("Upstox socket closed.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    policy.RecordFailure();
                    // Authorized WebSocket URLs carry secrets. Do not log exception messages/stack traces.
                    var reason = "Upstox feed failure: " + ex.GetType().Name;
                    logger.LogWarning("{Reason}; consecutive failures={Count}", reason, policy.ConsecutiveFailures);
                    if (gap.HasValue) await gaps.RecordGapAttemptAsync(gap.Value, reason, ct);
                    else gap = await gaps.RecordGapStartedAsync(DateTimeOffset.UtcNow, reason, ct);
                    if (policy.ShouldAlert && ConnectionUnstable is { } handler) await handler(policy.ConsecutiveFailures);
                    await Task.Delay(policy.NextDelay(), ct);
                }
            }
        }
        finally { output.TryComplete(); }
    }

    async Task RunSessionAsync(ChannelWriter<Tick> output, Func<Task> ready, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var uri = await rest.AuthorizeFeedAsync(token, timeout.Token);
        await ws.ConnectAsync(uri, timeout.Token);
        string[] keys;
        lock (_keyLock) keys = _keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (keys.Length > 2000) throw new InvalidOperationException("Too many Upstox full-feed subscriptions.");
        await SendSubscriptionAsync(ws, keys, ct);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drain = DrainAsync(ws, session.Token);
        var mapper = new UpstoxFeedMapper();
        var resumed = false;
        try
        {
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult read;
                using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiveTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                do
                {
                    read = await ws.ReceiveAsync(buffer, receiveTimeout.Token);
                    if (read.MessageType == WebSocketMessageType.Close) return;
                    if (read.MessageType != WebSocketMessageType.Binary) throw new InvalidOperationException("Upstox returned a non-binary frame.");
                    frame.Write(buffer, 0, read.Count);
                    if (frame.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Upstox frame exceeds size limit.");
                } while (!read.EndOfMessage);
                var decoded = FeedResponse.Parser.ParseFrom(frame.ToArray());
                var ticks = mapper.Map(decoded, DateTimeOffset.UtcNow);
                foreach (var tick in ticks)
                {
                    lock (_keyLock)
                    {
                        if (!_keys.Contains(tick.Token[3..])) throw new InvalidOperationException("Unexpected Upstox instrument in feed.");
                    }
                    await output.WriteAsync(tick, ct);
                }
                if (!resumed && ticks.Count > 0) { await ready(); resumed = true; }
                if (drain.IsFaulted) await drain;
            }
        }
        finally
        {
            session.Cancel();
            ws.Abort();
            try { await drain; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        }
    }

    async Task DrainAsync(ClientWebSocket ws, CancellationToken ct)
    {
        await foreach (var key in _requests.Reader.ReadAllAsync(ct)) await SendSubscriptionAsync(ws, [key], ct);
    }

    async Task SendSubscriptionAsync(ClientWebSocket ws, string[] keys, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { guid = Guid.NewGuid().ToString("N"), method = "sub", data = new { mode = "full", instrumentKeys = keys } });
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(payload, WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }
}
