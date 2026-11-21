using Microsoft.Extensions.Logging;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Logs a Dashboard read failure at its catch site without flooding the log: the panel re-polls every few seconds, so an identical failure (same site, exception type and message) is logged once per
/// <see cref="Cooldown"/>, with a count of the repeats suppressed in between. A different failure (another site, type or message) is a different key and logs at once. Cancellation is normal shutdown/navigation
/// and is never logged. Only the failure is recorded; what the UI shows in response is unchanged.
/// </summary>
public sealed class PollingFailureLogger(ILogger<PollingFailureLogger> logger, TimeProvider? time = null, TimeSpan? cooldown = null)
{
    const int MaxKeys = 256;
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly object _gate = new();
    readonly Dictionary<string, (DateTimeOffset LastLogged, int Suppressed)> _seen = new(StringComparer.Ordinal);

    public TimeSpan Cooldown { get; } = cooldown ?? TimeSpan.FromSeconds(60);

    /// <summary>True when the failure was written to the log (false: cancellation, or an identical failure inside the cooldown).</summary>
    public bool Report(string site, Exception ex, string context)
    {
        if (ex is OperationCanceledException) return false;
        var key = $"{site}|{ex.GetType().FullName}|{ex.Message}";
        var now = _time.GetUtcNow();
        int suppressed;
        lock (_gate)
        {
            if (_seen.TryGetValue(key, out var s) && now - s.LastLogged < Cooldown)
            {
                _seen[key] = (s.LastLogged, s.Suppressed + 1);
                return false;
            }

            suppressed = _seen.TryGetValue(key, out var prev) ? prev.Suppressed : 0;
            if (_seen.Count >= MaxKeys) foreach (var k in _seen.Where(kv => now - kv.Value.LastLogged >= Cooldown).Select(kv => kv.Key).ToList()) _seen.Remove(k);
            if (_seen.Count >= MaxKeys) _seen.Clear();                  // pathological variety of messages: bounded memory wins over perfect dedup
            _seen[key] = (now, 0);
        }

        logger.LogWarning(ex, "Dashboard read failed at {Site} ({Context}); {Suppressed} identical failures suppressed in the last cooldown window", site, context, suppressed);
        return true;
    }
}
