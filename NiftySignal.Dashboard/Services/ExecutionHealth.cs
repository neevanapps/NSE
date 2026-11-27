using System.Globalization;
using Microsoft.Extensions.Options;
using NiftySignal.Domain;
using NiftySignal.Notifications;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.Dashboard.Services;

/// <summary>Watching the paper execution service from outside it (section "ExecutionHealth"). Its own conditions are alerted by the service; this process reports what a dead or hung service cannot.</summary>
public sealed class ExecutionHealthOptions
{
    public const string SectionName = "ExecutionHealth";

    public bool Enabled { get; set; } = true;
    /// <summary>Alert when the service has never reported. Off: watching starts with its first report.</summary>
    public bool Required { get; set; }
    public bool Telegram { get; set; } = true;
    public int StaleHeartbeatSeconds { get; set; } = 30;
    public string MonitorFromIst { get; set; } = "09:16";
    public string MonitorUntilIst { get; set; } = "15:30";
    /// <summary>An unresolved condition is repeated at this interval; 0 sends it once.</summary>
    public int ReminderMinutes { get; set; } = 15;
    public int PollSeconds { get; set; } = 5;
}

public enum ExecutionCondition { NotWatched, Healthy, OutsideHours, NeverReported, Unreadable, Silent, RecoveryFailed, ProtectionOutstanding }

/// <summary><see cref="Reported"/> are the service's own outstanding conditions and open positions, shown on the banner (the service sends its own messages for them).</summary>
public sealed record ExecutionHealthState(ExecutionCondition Condition, string Message, DateTimeOffset EvaluatedAtUtc, IReadOnlyList<string> Reported)
{
    /// <summary>The service cannot report for itself: this process sends the message.</summary>
    public bool IsOutage => Condition is ExecutionCondition.NeverReported or ExecutionCondition.Unreadable or ExecutionCondition.Silent;

    /// <summary>Shown as critical. A running service that reports a recovery or protection problem sends its own message, so this process does not repeat it.</summary>
    public bool IsCritical => IsOutage || Condition is ExecutionCondition.RecoveryFailed or ExecutionCondition.ProtectionOutstanding;

    public bool ShowBanner => IsCritical || Reported.Count > 0;
}

public static class ExecutionHealth
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static ExecutionHealthState Evaluate(ExecutionStateView? state, string? readError, DateTimeOffset nowUtc, ExecutionHealthOptions options)
    {
        ExecutionHealthState State(ExecutionCondition c, string message, IReadOnlyList<string>? reported = null) => new(c, message, nowUtc, reported ?? []);
        if (!options.Enabled) return State(ExecutionCondition.NotWatched, "execution watching is switched off");
        var ist = nowUtc.ToOffset(IstOffset);
        var time = TimeOnly.FromDateTime(ist.DateTime);
        var monitored = ist.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time >= At(options.MonitorFromIst) && time < At(options.MonitorUntilIst);

        if (readError is not null) return monitored ? State(ExecutionCondition.Unreadable, $"the paper execution state cannot be read: {readError}") : State(ExecutionCondition.OutsideHours, "outside monitored market hours");
        if (state is null)
        {
            if (!options.Required) return State(ExecutionCondition.NotWatched, "the paper execution service has never reported");
            return monitored ? State(ExecutionCondition.NeverReported, "the paper execution service has never reported") : State(ExecutionCondition.OutsideHours, "outside monitored market hours");
        }

        var snapshot = ExecutionStore.ReadSnapshot(state.SnapshotJson);
        var open = snapshot?.Positions.Where(p => p.State != PositionState.Closed).Select(p => $"{p.StrategyId} {p.Token} x{p.RemainingQuantity} {p.State}").ToList() ?? [];
        var age = nowUtc - state.Heartbeat;
        if (age > TimeSpan.FromSeconds(options.StaleHeartbeatSeconds))
        {
            var message = $"paper execution service is silent: last report {state.Heartbeat.ToIst().ToString("dd-MMM HH:mm:ss", CultureInfo.InvariantCulture)} IST ({age.TotalSeconds:0} s ago). "
                + (open.Count == 0 ? "No open position was last reported." : $"{open.Count} position(s) have NO protection: {string.Join(", ", open)}");
            // positions left behind stay visible at any hour; messages are limited to monitored hours
            return monitored ? State(ExecutionCondition.Silent, message) : State(ExecutionCondition.OutsideHours, message, open.Count == 0 ? [] : [message]);
        }

        if (!monitored) return State(ExecutionCondition.OutsideHours, $"execution status {snapshot?.Status ?? state.Status}", snapshot?.Conditions);
        if (snapshot is { Reconciled: false } || (snapshot?.Status ?? state.Status) is "Recovering" or "Unreconciled")
            return State(ExecutionCondition.RecoveryFailed, $"paper execution has not recovered its state ({snapshot?.Status ?? state.Status}): entries are blocked", snapshot?.Conditions);
        // an unresolved position, or one that a protective flatten has not closed yet
        var outstanding = snapshot?.Positions.Count(p => p.State == PositionState.Unresolved || (snapshot.Flatten is not null && p.State != PositionState.Closed)) ?? 0;
        return outstanding > 0
            ? State(ExecutionCondition.ProtectionOutstanding, $"{outstanding} paper position(s) with a protective action outstanding or unresolved", snapshot?.Conditions)
            : State(ExecutionCondition.Healthy, $"execution status {snapshot?.Status ?? state.Status}", snapshot?.Conditions);
    }

    static TimeOnly At(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>One alert per condition per episode, an optional reminder while it lasts, one recovery notice. Not thread safe: owned by the monitor loop.</summary>
public sealed class ExecutionAlertTracker(ExecutionHealthOptions options)
{
    readonly Dictionary<ExecutionCondition, DateTimeOffset> _sent = [];
    DateTimeOffset? _episodeStart;

    /// <summary>The message due for this state, or null. Call <see cref="Delivered"/> once it was sent; until then it stays due.</summary>
    public string? Due(ExecutionHealthState state)
    {
        if (state.Condition == ExecutionCondition.OutsideHours)
        {
            _sent.Clear(); _episodeStart = null;
            return null;
        }

        if (state.IsOutage)
        {
            _episodeStart ??= state.EvaluatedAtUtc;
            var due = !_sent.TryGetValue(state.Condition, out var last)
                || (options.ReminderMinutes > 0 && state.EvaluatedAtUtc - last >= TimeSpan.FromMinutes(options.ReminderMinutes));
            return due ? $"NiftySignal CRITICAL: {state.Message}" : null;
        }

        return state.Condition == ExecutionCondition.Healthy && _sent.Count > 0
            ? $"NiftySignal RECOVERED: paper execution service reporting again after {(state.EvaluatedAtUtc - _episodeStart!.Value).TotalSeconds:0} s"
            : null;
    }

    public void Delivered(ExecutionHealthState state)
    {
        if (state.IsOutage) _sent[state.Condition] = state.EvaluatedAtUtc;
        else { _sent.Clear(); _episodeStart = null; }
    }
}

/// <summary>Watches the execution service from the Dashboard process. Read-only. It cannot report its own process or the whole machine being down.</summary>
public sealed class ExecutionHealthMonitor(
    Func<CancellationToken, Task<ExecutionStateView?>>? read,
    ITelegramTextSender telegram,
    IOptions<ExecutionHealthOptions> options,
    TimeProvider clock,
    ILogger<ExecutionHealthMonitor> logger) : BackgroundService
{
    readonly ExecutionAlertTracker _tracker = new(options.Value);
    DateTimeOffset? _unreadableSince;

    public ExecutionHealthState Current { get; private set; } = new(ExecutionCondition.NotWatched, "not yet evaluated", DateTimeOffset.MinValue, []);

    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 60)));
        try
        {
            do
            {
                try { await CheckAsync(stoppingToken); }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Execution health check failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        ExecutionStateView? state = null;
        string? readError = null;
        if (read is null)
        {
            if (options.Value.Required) readError = "no TradingPaperLive connection is configured for the Dashboard";
        }
        else
        {
            try
            {
                using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limited.CancelAfter(TimeSpan.FromSeconds(10));
                state = await read(limited.Token);
                _unreadableSince = null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One failed read is not an incident; reads failing for longer than the stale limit are.
                _unreadableSince ??= now;
                logger.LogWarning(ex, "Execution health read failed.");
                if (now - _unreadableSince < TimeSpan.FromSeconds(options.Value.StaleHeartbeatSeconds)) return;
                readError = ex.GetType().Name;
            }
        }

        var next = ExecutionHealth.Evaluate(state, readError, now, options.Value);
        if (next.Condition != Current.Condition)
            logger.Log(next.IsCritical ? LogLevel.Critical : LogLevel.Information, "Execution health {Previous} -> {Condition}: {Message}", Current.Condition, next.Condition, next.Message);
        var changed = next.Condition != Current.Condition || (next.IsCritical && next.Message != Current.Message) || !next.Reported.SequenceEqual(Current.Reported);
        Current = next;
        if (changed) Changed?.Invoke();

        if (_tracker.Due(next) is not { } message) return;
        if (!options.Value.Telegram) { _tracker.Delivered(next); return; }
        var result = await telegram.SendAsync(message, ct);
        // Rejected: certainly not delivered, try again on the next check. Uncertain: do not resend now (the reminder interval repeats it).
        if (result.Outcome != TelegramDocumentOutcome.Rejected) _tracker.Delivered(next);
        if (result.Outcome != TelegramDocumentOutcome.Sent) logger.LogError("Execution alert not confirmed by Telegram ({Outcome}): {Error}", result.Outcome, result.Error);
    }
}
