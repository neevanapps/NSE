using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Entities;

namespace NiftySignal.Host;

/// <summary>
/// Host's side of the Host-to-Dashboard push channel (2026-09-04) -- a SignalR client
/// connecting to Dashboard's own MarketDataHub. Best-effort by design: if Dashboard is
/// down, mid-redeploy, or unreachable, ticks simply don't get pushed -- ingestion, scoring,
/// and the trading engine all keep running exactly as before, since none of them depend on
/// this. WithAutomaticReconnect covers a connection that drops *after* connecting
/// successfully (e.g. redeploying Dashboard while Host keeps running); the initial connect
/// is handled separately below, since neither Windows Service has a startup-order
/// dependency on the other -- on a machine reboot, Host can easily start before Dashboard
/// is listening, and that must not turn into "push silently stays dead all day."
/// </summary>
public sealed class DashboardPushClient : IAsyncDisposable
{
    static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    readonly HubConnection _connection;
    readonly ILogger<DashboardPushClient> _logger;
    bool _lastPushFailed;

    public DashboardPushClient(IOptions<DashboardPushOptions> options, ILogger<DashboardPushClient> logger)
    {
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl(options.Value.HubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.Reconnected += _ =>
        {
            _logger.LogInformation("Reconnected to Dashboard push hub");
            return Task.CompletedTask;
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning(ex, "Dashboard push hub connection closed");
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Returns immediately -- connecting happens on a background retry loop, so a
    /// Dashboard that's slow or not up yet never delays Host's own critical startup path
    /// (ingestion, scoring, the trading engine). Retries every 30s, indefinitely, until
    /// either it connects or the app shuts down.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        _ = ConnectWithRetryAsync(ct);
        return Task.CompletedTask;
    }

    async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(ConnectAttemptTimeout);
                await _connection.StartAsync(attemptCts.Token);
                _logger.LogInformation("Connected to Dashboard push hub");
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not connect to Dashboard push hub -- retrying in {Delay}. Ingestion continues normally.", RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async Task PushTickAsync(Tick tick, CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.SendAsync("PushTick", tick, ct);
            _lastPushFailed = false;
        }
        catch (Exception ex)
        {
            // Logged once per failure streak, not once per tick -- at ~150+ ticks/sec a
            // Dashboard outage would otherwise flood the log.
            if (!_lastPushFailed)
            {
                _logger.LogWarning(ex, "Dashboard push failed -- will keep retrying silently until it recovers");
                _lastPushFailed = true;
            }
        }
    }

    /// <summary>
    /// Signal-only (2026-09-07) -- tells Dashboard a paper trade changed (entry/partial-book/
    /// exit) so it re-reads positions/closed trades immediately instead of waiting up to 5s
    /// for its next poll. Best-effort, same as PushTickAsync: if Dashboard is unreachable this
    /// silently no-ops and the next poll picks the change up anyway.
    /// </summary>
    public async Task PushTradesChangedAsync(CancellationToken ct)
    {
        if (_connection.State != HubConnectionState.Connected)
        {
            return;
        }

        try
        {
            await _connection.SendAsync("PushTradesChanged", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dashboard trade-changed push failed -- the next poll will still pick it up");
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
