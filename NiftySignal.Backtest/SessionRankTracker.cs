namespace NiftySignal.Backtest;

/// <summary>
/// Audit finding F55's dynamic-hybrid mode (2026-09-11, user requirement: "no hardcoded metric or
/// value for entry or exit -- every entry and exit must be dynamic"). Tracks every value seen so
/// far in the current session (reset once per trading day -- construct a fresh instance per
/// BacktestRunner.RunDayAsync call, same lifetime as that day's LiveFeatureEngine) and answers two
/// inverse questions against that growing, self-only distribution: where does a given value rank
/// (<see cref="Rank"/>), and what value sits at a given rank (<see cref="ValueAtPercentile"/>).
///
/// This replaces a fixed score-space threshold (MinAbsScore=17, tuned to one 4-day snapshot of
/// one score's distribution -- exactly the miscalibration this session's earlier diagnosis found)
/// with a threshold *expressed as a percentile* and *resolved against today's own accumulating
/// data*. The percentile itself (e.g. "top 15%") is the one number left in this design, and it's
/// deliberately a different kind of constant: portable across regimes/scores by construction
/// (unlike a raw score magnitude, which is only ever valid for the exact distribution it was
/// derived from), and directly justified by the project's own 5-10 trades/day goal rather than a
/// curve-fit. See BacktestRunner's own comment at its use site.
///
/// Deliberately simple (a growing list, re-sorted on read) rather than an incremental data
/// structure -- at most ~1600 cadences/day, this is trivial work for a backtest tool that isn't on
/// any live-critical path. Known, accepted limitation: the first several minutes of a session rank
/// against very few samples, so early-session ranks are noisy -- no different in kind from every
/// other "cold start" this project already accepts (e.g. IvRankSessionCount's own same-day-only
/// fallback), not fixed here since it isn't what F55 set out to solve.
/// </summary>
public sealed class SessionRankTracker
{
    readonly List<double> _values = [];

    public void Add(double value) => _values.Add(value);

    /// <summary>Percentile rank (0-100, inclusive of <paramref name="value"/> itself) within every value added so far. 0 (not null) when nothing has been added yet -- a value can't outrank an empty history.</summary>
    public double Rank(double value)
    {
        if (_values.Count == 0)
        {
            return 0;
        }

        var countAtOrBelow = _values.Count(v => v <= value);
        return 100.0 * countAtOrBelow / _values.Count;
    }

    /// <summary>The value that would sit at <paramref name="percentile"/> (0-100) within every value added so far -- the inverse of <see cref="Rank"/>. Null until at least one value has been added.</summary>
    public double? ValueAtPercentile(double percentile)
    {
        if (_values.Count == 0)
        {
            return null;
        }

        var sorted = _values.OrderBy(v => v).ToList();
        var rank = percentile / 100.0 * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper ? sorted[lower] : sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }
}
