namespace NiftySignal.MetricTrials;

/// <summary>
/// Deliberately duplicated from <c>NiftySignal.Backtest.SessionRankTracker</c> (same algorithm,
/// verbatim) rather than referenced -- taking a project reference to the entire
/// NiftySignal.Backtest graph (which pulls in Host, Persistence, Rules, Execution, Ingestion,
/// Scoring) just to reuse one 30-line, dependency-free utility would be real over-coupling for a
/// tool meant to stay small and standalone. If this drifts out of sync with the original, that's
/// an acceptable, known cost of the duplication -- not expected, since neither copy has a reason
/// to change independently.
///
/// Tracks every value seen so far in the current session (reset once per trading day) and
/// answers two inverse questions against that growing, self-only distribution: where does a
/// given value rank (<see cref="Rank"/>), and what value sits at a given rank
/// (<see cref="ValueAtPercentile"/>). Used here for the same "no hardcoded threshold, every
/// entry/exit is dynamic" reason it was built for originally (audit finding F55) -- thresholds
/// are expressed as percentiles and resolved against today's own accumulating data, not a fixed
/// magnitude picked by eye.
/// </summary>
public sealed class SessionRankTracker
{
    readonly List<double> _values = [];

    public void Add(double value) => _values.Add(value);

    /// <summary>Percentile rank (0-100, inclusive of <paramref name="value"/> itself) within every value added so far. 0 (not null) when nothing has been added yet.</summary>
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
