using Microsoft.Extensions.Options;
using NiftySignal.Notifications;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Watches the Observer from the Dashboard process, so a stalled or stopped Host is still reported. Read-only on the observer database; sends Telegram text and feeds the dashboard banner.
/// It cannot report its own process or the whole machine being down.
/// </summary>
public sealed class ObserverHealthMonitor(
    AdaptiveObserverDataService data,
    ITelegramTextSender telegram,
    IOptions<ObserverHealthOptions> options,
    TimeProvider clock,
    ILogger<ObserverHealthMonitor> logger) : BackgroundService
{
    readonly ObserverAlertTracker _tracker = new(options.Value);
    DateTimeOffset? _unreadableSince;

    public ObserverHealthState Current { get; private set; } = new(ObserverCondition.OutsideHours, "not yet evaluated", DateTimeOffset.MinValue);

    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 60)));
        try
        {
            do
            {
                try { await CheckAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Observer health check failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        AdaptiveObserverData.AdaptiveObserverRuntimeRow? runtime = null;
        string? readError = null;
        try
        {
            // A hung database must not hang the watcher.
            using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limited.CancelAfter(TimeSpan.FromSeconds(10));
            runtime = (await data.LoadHeaderAsync(limited.Token))?.Runtime;
            _unreadableSince = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One failed read is not an incident; reads failing for longer than the stale limit are.
            _unreadableSince ??= now;
            logger.LogWarning(ex, "Observer health read failed.");
            if (now - _unreadableSince < TimeSpan.FromSeconds(options.Value.StaleHeartbeatSeconds)) return;
            readError = ex.GetType().Name;
        }

        var state = ObserverHealth.Evaluate(runtime, readError, now, options.Value);
        if (state.Condition != Current.Condition)
            logger.Log(state.IsCritical ? LogLevel.Critical : LogLevel.Information, "Observer health {Previous} -> {Condition}: {Message}", Current.Condition, state.Condition, state.Message);
        var changed = state.Condition != Current.Condition || (state.IsCritical && state.Message != Current.Message);
        Current = state;
        if (changed) Changed?.Invoke();

        if (_tracker.Due(state) is not { } message) return;
        if (!options.Value.Telegram) { _tracker.Delivered(state); return; }
        var result = await telegram.SendAsync(message, ct);
        // Rejected: certainly not delivered, try again on the next check. Uncertain: do not resend now (the reminder interval repeats it).
        if (result.Outcome != TelegramDocumentOutcome.Rejected) _tracker.Delivered(state);
        if (result.Outcome != TelegramDocumentOutcome.Sent) logger.LogError("Observer alert not confirmed by Telegram ({Outcome}): {Error}", result.Outcome, result.Error);
    }
}
