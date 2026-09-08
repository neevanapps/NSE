namespace NiftySignal.Features;

/// <summary>
/// Numerically-stable rolling mean/variance over a time-based (not count-based) window,
/// using Welford's online algorithm extended with the symmetric "remove" update so points
/// can age out as well as be added -- plain Welford is defined for an ever-growing
/// population, but plan section 5.4 calls for a genuine sliding window (5-60 minutes
/// depending on the metric) carried continuously across sessions, never reset daily.
///
/// Not thread-safe -- callers computing one score per cadence tick already serialize
/// access to a given metric's window.
/// </summary>
public sealed class WelfordRollingWindow(TimeSpan windowDuration)
{
    readonly Queue<(DateTimeOffset Timestamp, double Value)> _points = new();

    int _count;
    double _mean;
    double _m2; // sum of squared deviations from the running mean

    DateTimeOffset? _firstSeenAt;
    DateTimeOffset? _latestTimestamp;

    public int Count => _count;

    public double Mean => _mean;

    /// <summary>
    /// Sample standard deviation (n-1 denominator). 0 when Count &lt; 2. Clamps <see cref="_m2"/>
    /// to non-negative here as a second line of defense on top of the clamp in <see
    /// cref="Remove"/> -- if it were ever negative, Math.Sqrt would return NaN, and the
    /// ComputeZScore guard below (`StdDev &lt; 1e-12`) silently fails to catch that: any
    /// comparison with NaN evaluates false in .NET, so a NaN StdDev would pass the guard and
    /// propagate into the composite score (live-caught 2026-09-04, corrupted one score_snapshots
    /// row and crashed the dashboard's JSON serialization).
    /// </summary>
    public double StdDev => _count < 2 ? 0.0 : Math.Sqrt(Math.Max(_m2, 0.0) / (_count - 1));

    /// <summary>
    /// True once real elapsed time since the first observation reaches the full window
    /// duration. Per plan section 1.8/5.4: no score should be emitted from a
    /// partially-filled window, since its mean/stddev estimate is unreliable -- on a fresh
    /// install this correctly means no trades for the first several days while history
    /// accumulates.
    ///
    /// PENDING (audit finding F22, 2026-09-08 -- found while verifying the lead's separate A3
    /// claim, not itself in either audit -- see fix plan Batch 6): _firstSeenAt is set once
    /// (below) and never updated when points age out via Remove. MarketDataIngestionWorker
    /// constructs one LiveFeatureEngine per process lifetime and the Host process idles rather
    /// than exiting outside market hours (confirmed live: "Outside market hours ... waiting for
    /// 08:45"), so if Host isn't restarted between sessions, the first tick of a new day evicts
    /// all of yesterday's points correctly, but this check still spans the ~24h overnight gap
    /// against firstSeenAt and reports true on 1-2 real points -- a false warm-up. Every one of
    /// the 14 weighted/diagnostic components depends on this one class, so the blast radius is
    /// the whole score. Fix: base the span on the oldest point still actually in _points
    /// (_points.Count > 0 ? _points.Peek().Timestamp : null), not a permanent first-ever-seen
    /// marker that eviction never touches.
    /// </summary>
    public bool IsWarmedUp => _firstSeenAt is { } firstSeenAt
        && _latestTimestamp is { } latestTimestamp
        && latestTimestamp - firstSeenAt >= windowDuration
        && _count >= 2;

    public void Add(DateTimeOffset timestamp, double value)
    {
        _firstSeenAt ??= timestamp;
        _latestTimestamp = timestamp;

        EvictOlderThan(timestamp - windowDuration);

        _count++;
        var delta = value - _mean;
        _mean += delta / _count;
        var delta2 = value - _mean;
        _m2 += delta * delta2;

        _points.Enqueue((timestamp, value));
    }

    /// <summary>
    /// Clipped to +/-3 per plan section 5.4. Null when not warmed up yet or the window has
    /// no meaningful spread (StdDev ~ 0), rather than dividing by (near) zero.
    /// </summary>
    public double? ComputeZScore(double value)
    {
        if (!IsWarmedUp || StdDev < 1e-12)
        {
            return null;
        }

        var z = (value - Mean) / StdDev;
        return Math.Clamp(z, -3.0, 3.0);
    }

    void EvictOlderThan(DateTimeOffset cutoff)
    {
        while (_points.Count > 0 && _points.Peek().Timestamp < cutoff)
        {
            var (_, value) = _points.Dequeue();
            Remove(value);
        }
    }

    void Remove(double value)
    {
        if (_count <= 1)
        {
            _count = 0;
            _mean = 0;
            _m2 = 0;
            return;
        }

        var newMean = _mean - ((value - _mean) / (_count - 1));
        // Floating-point cancellation in this subtraction can push _m2 slightly below zero
        // (true variance is never negative) -- clamp rather than let it compound across
        // further Remove calls and eventually hand StdDev a negative radicand.
        _m2 = Math.Max(_m2 - (value - _mean) * (value - newMean), 0.0);
        _mean = newMean;
        _count--;
    }
}
