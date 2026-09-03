using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Ingestion.FlatTrade;

/// <summary>
/// Live <see cref="ITickSource"/> over FlatTrade's WebSocket feed. An access token and the
/// day's subscription list are supplied at construction -- obtaining/refreshing the token
/// (plan section 4.3, daily) and building the subscription list (section 3.1, the 08:45
/// job) are separate concerns, not yet wired up; this class only owns "given those, stream
/// ticks reliably and reconnect when the connection drops."
///
/// Message shapes confirmed 2026-09-03 against the live docs at pi.flattrade.in/docs.
/// That site's own changelog documents a breaking change from an older API generation
/// (connect task "c" -> "a", auth field "susertoken" -> "accesstoken", connect
/// acknowledgement "ck" -> "ak") -- this targets the current shape, not the older one
/// still found in some reference clients circulating online. Still not exercised against
/// a real account, since that needs an approved API key.
/// </summary>
public sealed class FlatTradeTickSource(
    FlatTradeOptions options,
    string userId,
    string accessToken,
    IReadOnlyList<(Exchange Exchange, string Token)> subscriptions,
    IDataGapRecorder dataGapRecorder,
    ILogger<FlatTradeTickSource> logger) : ITickSource
{
    public async IAsyncEnumerable<Tick> ReadTicksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var pump = PumpAsync(channel.Writer, cancellationToken);

        await foreach (var tick in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return tick;
        }

        await pump;
    }

    async Task PumpAsync(ChannelWriter<Tick> writer, CancellationToken ct)
    {
        var policy = new ReconnectionPolicy();
        long? openGapId = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(writer, policy, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    policy.RecordFailure();
                    logger.LogError(ex, "FlatTrade feed session failed (consecutive failures: {Count})", policy.ConsecutiveFailures);

                    openGapId ??= await dataGapRecorder.RecordGapStartedAsync(DateTimeOffset.UtcNow, ex.Message, ct);

                    if (policy.ShouldAlert)
                    {
                        logger.LogCritical(
                            "FlatTrade feed has failed {Count} times consecutively -- exceeds the {Threshold} alert threshold",
                            policy.ConsecutiveFailures, ReconnectionPolicy.AlertThreshold);
                    }

                    var delay = policy.NextDelay();
                    logger.LogWarning("Reconnecting to FlatTrade feed in {Delay}", delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (openGapId is { } gapId)
                {
                    await dataGapRecorder.RecordGapEndedAsync(gapId, DateTimeOffset.UtcNow, ct);
                    openGapId = null;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    async Task RunSessionAsync(ChannelWriter<Tick> writer, ReconnectionPolicy policy, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(options.WebSocketUrl), ct);
        logger.LogInformation("FlatTrade WebSocket connected");

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatLoopAsync(ws, sessionCts.Token);

        try
        {
            await SendAsync(ws, $$"""{"t":"a","uid":"{{userId}}","actid":"{{userId}}","source":"API","accesstoken":"{{accessToken}}"}""", ct);

            var state = new Dictionary<string, FlatTradeFeedState>();
            var buffer = new byte[64 * 1024];
            var subscribed = false;

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveMessageAsync(ws, buffer, ct);
                if (json is null)
                {
                    logger.LogWarning("FlatTrade WebSocket closed by server");
                    return;
                }

                var msg = JsonSerializer.Deserialize<RawFeedMessage>(json, JsonOpts);
                if (msg?.Type is null)
                {
                    continue;
                }

                if (msg.Type == "ak")
                {
                    policy.RecordSuccess();
                    if (!subscribed)
                    {
                        await SubscribeAllAsync(ws, ct);
                        subscribed = true;
                    }
                }
                else if (msg.Type is "tk" or "tf" or "dk" or "df" && msg.Exchange is not null && msg.Token is not null)
                {
                    var key = $"{msg.Exchange}|{msg.Token}";
                    if (!state.TryGetValue(key, out var feedState))
                    {
                        state[key] = feedState = new FlatTradeFeedState(FlatTradeAuthClient.ParseExchange(msg.Exchange), msg.Token);
                    }

                    feedState.ApplyDelta(msg);
                    // UtcNow, not Now: Npgsql only accepts Offset=0 for timestamptz columns,
                    // and this flows straight into a persisted Tick.ReceivedAt.
                    if (feedState.ToTick(DateTimeOffset.UtcNow) is { } tick)
                    {
                        await writer.WriteAsync(tick, ct);
                    }
                }
                // "om"/"ok" order-update messages are ignored -- this is a market-data-only tick source.
            }
        }
        finally
        {
            sessionCts.Cancel();
            await Task.WhenAny(heartbeat, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    async Task SubscribeAllAsync(ClientWebSocket ws, CancellationToken ct)
    {
        foreach (var (exchange, token) in subscriptions)
        {
            var code = FlatTradeAuthClient.ExchangeCode(exchange);
            await SendAsync(ws, $$"""{"t":"t","k":"{{code}}|{{token}}"}""", ct);
            await SendAsync(ws, $$"""{"t":"d","k":"{{code}}|{{token}}"}""", ct);
        }
        logger.LogInformation("Subscribed to {Count} instrument(s) (touchline + depth)", subscriptions.Count);
    }

    async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Docs require a heartbeat every 30s to keep the connection alive; 25s
                // leaves a small margin.
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (ws.State == WebSocketState.Open)
                {
                    await SendAsync(ws, """{"t":"h"}""", ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var messageStream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            messageStream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(messageStream.ToArray());
    }

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}
