using System.Globalization;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain;

namespace NiftySignal.Dashboard.Services;

/// <summary>What the Observer's recovery is doing, from the runtime row only. No percentage or finish time is shown because no total is known.</summary>
public static class ObserverRecoveryText
{
    public static string Describe(AdaptiveObserverRuntimeRow r, DateTimeOffset nowUtc)
    {
        if (r.RuntimeStatus == AdaptiveRuntimeStatus.RecoveryFailed)
            return $"RECOVERY FAILED (consecutive failures: {r.RecoveryFailureCount}) at {Time(r.LastHeartbeatUtc)}: {r.LastError ?? "no error text"}";
        if (r.RuntimeStatus != AdaptiveRuntimeStatus.Rebuilding)
            return r.LastRecoveryCompletedUtc is { } done ? $"last completed {Time(done)} · reconciled {r.LastRecoveryReconciledBars}" : "not required yet";

        var parts = new List<string> { $"rebuilding since {Time(r.LastRecoveryStartedUtc)}" };
        if (r.LastRecoveryStartedUtc is { } started) parts.Add($"elapsed {Span(nowUtc - started)}");
        if (r.RecoveryPhase is { } phase) parts.Add(phase);
        parts.Add(r.RecoveryPersistedBarsAtStart is { } persisted
            ? $"{r.RecoveryBarsReplayed:N0} bars replayed ({persisted:N0} persisted before restart)"
            : $"{r.RecoveryBarsReplayed:N0} bars replayed");
        parts.Add($"{r.RecoveryTicksProcessed:N0} ticks");
        if (r.RecoveryReplayedThroughUtc is { } through) parts.Add($"replayed through {Time(through)} ({Span(nowUtc - through)} behind now)");
        if (r.LastRecoveryProgressUtc is { } progress) parts.Add($"last progress {Span(nowUtc - progress)} ago");
        if (r.RecoveryFailureCount > 0) parts.Add($"after {r.RecoveryFailureCount} failed attempt(s): {r.LastError}");
        return string.Join(" · ", parts);
    }

    static string Time(DateTimeOffset? utc) => utc is { } t ? t.ToIst().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—";

    static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{(int)span.TotalSeconds}s";
    }
}
