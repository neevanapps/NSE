using System.Globalization;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;

namespace NiftySignal.Dashboard.Services;

/// <summary>Critical Observer alerts (section "ObserverHealth"). The thresholds are the mandate's (30 s, 5 min) or starting values.</summary>
public sealed class ObserverHealthOptions
{
    public const string SectionName = "ObserverHealth";

    /// <summary>Send Telegram messages. The dashboard banner is shown either way.</summary>
    public bool Telegram { get; set; } = true;
    public int StaleHeartbeatSeconds { get; set; } = 30;
    public int RecoveryLimitSeconds { get; set; } = 300;
    /// <summary>A missing session is reported from this IST time on a weekday (exchange holidays are not known here).</summary>
    public string SessionExpectedFromIst { get; set; } = "09:20";
    public string MonitorFromIst { get; set; } = "09:16";
    public string MonitorUntilIst { get; set; } = "15:30";
    /// <summary>An unresolved condition is repeated at this interval; 0 sends it once.</summary>
    public int ReminderMinutes { get; set; } = 15;
    public int PollSeconds { get; set; } = 5;
}

public enum ObserverCondition { Healthy, OutsideHours, Pending, NoSession, Unreadable, StaleHeartbeat, Degraded, RecoveryFailed, RecoveryTooLong, ClosedEarly }

public sealed record ObserverHealthState(ObserverCondition Condition, string Message, DateTimeOffset EvaluatedAtUtc)
{
    public bool IsCritical => Condition is not (ObserverCondition.Healthy or ObserverCondition.OutsideHours or ObserverCondition.Pending);
}

/// <summary>Pure classification of the Observer from its runtime row and the clock.</summary>
public static class ObserverHealth
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public static ObserverHealthState Evaluate(AdaptiveObserverRuntimeRow? runtime, string? readError, DateTimeOffset nowUtc, ObserverHealthOptions options)
    {
        ObserverHealthState State(ObserverCondition c, string message) => new(c, message, nowUtc);
        var ist = nowUtc.ToOffset(IstOffset);
        var time = TimeOnly.FromDateTime(ist.DateTime);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || time < At(options.MonitorFromIst) || time >= At(options.MonitorUntilIst))
            return State(ObserverCondition.OutsideHours, "outside monitored market hours");
        if (readError is not null) return State(ObserverCondition.Unreadable, $"Observer state cannot be read: {readError}");
        if (runtime is null)
            return time >= At(options.SessionExpectedFromIst)
                ? State(ObserverCondition.NoSession, "no Observer session exists for today")
                : State(ObserverCondition.Pending, "waiting for today's session");

        var age = nowUtc - runtime.LastHeartbeatUtc;
        var ageText = $"last heartbeat {runtime.LastHeartbeatUtc.ToIst().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} IST ({age.TotalSeconds:0} s ago), status {runtime.RuntimeStatus}";
        if (runtime.RuntimeStatus == AdaptiveRuntimeStatus.Closed) return State(ObserverCondition.ClosedEarly, $"the session is already closed during market hours; {ageText}");
        if (age > TimeSpan.FromSeconds(options.StaleHeartbeatSeconds)) return State(ObserverCondition.StaleHeartbeat, $"Observer is stale: {ageText}");
        return runtime.RuntimeStatus switch
        {
            AdaptiveRuntimeStatus.RecoveryFailed => State(ObserverCondition.RecoveryFailed, $"recovery failed (consecutive failures {runtime.RecoveryFailureCount}): {runtime.LastError}"),
            AdaptiveRuntimeStatus.Degraded => State(ObserverCondition.Degraded, $"Observer degraded: {runtime.LastError}"),
            AdaptiveRuntimeStatus.Rebuilding when runtime.LastRecoveryStartedUtc is { } started && nowUtc - started > TimeSpan.FromSeconds(options.RecoveryLimitSeconds)
                => State(ObserverCondition.RecoveryTooLong, $"recovery has run {(nowUtc - started).TotalSeconds:0} s (limit {options.RecoveryLimitSeconds} s): {ObserverRecoveryText.Describe(runtime, nowUtc)}"),
            AdaptiveRuntimeStatus.Live => State(ObserverCondition.Healthy, ageText),
            // A recovery after failed attempts is not yet healthy and not a new problem.
            _ => State(ObserverCondition.Pending, ageText),
        };
    }

    static TimeOnly At(string text) => TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// One alert per condition per episode, an optional reminder while it lasts, and one recovery notice once the Observer is healthy again. An episode that flips between critical and
/// "recovering" stays open, so repeated failed attempts do not repeat messages. Not thread safe: owned by the monitor loop.
/// </summary>
public sealed class ObserverAlertTracker(ObserverHealthOptions options)
{
    readonly Dictionary<ObserverCondition, DateTimeOffset> _sent = [];
    DateTimeOffset? _episodeStart;

    /// <summary>The message due for this state, or null. Call <see cref="Delivered"/> once it was sent; until then it stays due.</summary>
    public string? Due(ObserverHealthState state)
    {
        if (state.Condition == ObserverCondition.OutsideHours)
        {
            // The day's episode ends with the market; no recovery is claimed.
            _sent.Clear(); _episodeStart = null;
            return null;
        }

        if (state.IsCritical)
        {
            _episodeStart ??= state.EvaluatedAtUtc;
            var due = !_sent.TryGetValue(state.Condition, out var last)
                || (options.ReminderMinutes > 0 && state.EvaluatedAtUtc - last >= TimeSpan.FromMinutes(options.ReminderMinutes));
            return due ? $"NiftySignal CRITICAL: {state.Message}" : null;
        }

        return state.Condition == ObserverCondition.Healthy && _sent.Count > 0
            ? $"NiftySignal RECOVERED: Observer healthy again after {(state.EvaluatedAtUtc - _episodeStart!.Value).TotalSeconds:0} s ({state.Message})"
            : null;
    }

    public void Delivered(ObserverHealthState state)
    {
        if (state.IsCritical) _sent[state.Condition] = state.EvaluatedAtUtc;
        else { _sent.Clear(); _episodeStart = null; }
    }
}
