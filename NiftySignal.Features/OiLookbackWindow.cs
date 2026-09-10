namespace NiftySignal.Features;

/// <summary>
/// Per-key (instrument token) "what was open interest approximately <c>window</c> ago"
/// lookback -- audit finding F50 (2026-09-10, user-caught live): NSE/the broker only refresh
/// OI every ~3 minutes (FlatTradeFeedState.ApplyDelta only updates its OpenInterest field when
/// a delta actually carries one, so between real prints the same value is carried forward
/// unchanged on every tick -- see that class's own doc comment). Comparing OI against literally
/// the previous 15s cadence therefore mostly compares an unchanged value against itself: zero
/// on roughly 11 of every 12 cadences, then the whole ~3 minutes' worth of change lands in one
/// lumpy spike on the cadence a real print happens to fall in. That's a poor match for a
/// smoothly-scored, 15s-cadence metric -- worst for <c>LiveFeatureEngine.ComputeOiBuildupNet</c>,
/// whose weight (0.3125) is the largest in the whole composite.
///
/// Same root cause, same fix philosophy, as the analogous (but much coarser, display-only) bug
/// already found and fixed in LiveDataService's own "OI Change %" panel: compare against a
/// window comfortably longer than the real refresh interval, long enough to reliably span at
/// least one real update, instead of the immediately-prior sample.
///
/// Not thread-safe -- same assumption every other rolling-state class in this project already
/// makes (one score cadence at a time already serializes access).
/// </summary>
public sealed class OiLookbackWindow(TimeSpan window)
{
    readonly Dictionary<string, Queue<(DateTimeOffset Timestamp, long OpenInterest)>> _historyByToken = [];
    readonly Dictionary<string, DateTimeOffset> _firstSeenByToken = [];

    /// <summary>
    /// OI observed approximately <c>window</c> ago for <paramref name="token"/>. Null until
    /// real elapsed time since this token's first <see cref="Record"/> call reaches the full
    /// window (same "don't trust a partially-filled window" rule <c>WelfordRollingWindow</c>
    /// already applies) -- a fresh token, or one only recently added to the tracked universe,
    /// has no trustworthy few-minutes-ago answer yet. Read-only: does not itself advance the
    /// window, so a cadence's every lookback reflects state strictly before that cadence's own
    /// <see cref="Record"/> calls, the same before/after ordering <c>_previousCadence</c> uses
    /// elsewhere in <c>LiveFeatureEngine</c>.
    /// </summary>
    public long? Lookback(string token, DateTimeOffset now)
    {
        if (!_firstSeenByToken.TryGetValue(token, out var firstSeen) || now - firstSeen < window)
        {
            return null;
        }

        return _historyByToken.TryGetValue(token, out var history) && history.Count > 0
            ? history.Peek().OpenInterest
            : null;
    }

    /// <summary>
    /// Records this cadence's OI for <paramref name="token"/>, evicting samples older than
    /// <c>window</c>. Call once per token per cadence, after every <see cref="Lookback"/> call
    /// for that same cadence has already run.
    /// </summary>
    public void Record(string token, DateTimeOffset now, long openInterest)
    {
        if (!_historyByToken.TryGetValue(token, out var history))
        {
            history = new Queue<(DateTimeOffset, long)>();
            _historyByToken[token] = history;
        }

        _firstSeenByToken.TryAdd(token, now);

        while (history.Count > 0 && now - history.Peek().Timestamp > window)
        {
            history.Dequeue();
        }

        history.Enqueue((now, openInterest));
    }
}
