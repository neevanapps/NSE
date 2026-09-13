namespace NiftySignal.Features;

/// <summary>
/// Ported from `NiftySignal.Backtest/SessionRankTracker.cs` (2026-09-13, Core-score live-wiring
/// plan A1) -- unchanged logic, moved here so `NiftySignal.Host`/`NiftySignal.Scoring` can
/// reference it, which neither `NiftySignal.Backtest` nor `NiftySignal.MetricTrials` are visible
/// to. Deliberately duplicated rather than having Host depend on the backtest project (matching
/// this codebase's own established precedent for exactly this kind of small, self-contained
/// duplication -- see `NiftySignal.MetricTrials`'s own earlier copy of this same class).
///
/// Tracks every value seen so far in the current session (reset once per trading day -- construct
/// a fresh instance per day/per metric) and answers two inverse questions against that growing,
/// self-only distribution: where does a given value rank (<see cref="Rank"/>), and what value sits
/// at a given rank (<see cref="ValueAtPercentile"/>). This is the "no hardcoded magnitude, no
/// look-ahead" normalization the Core score's 7 ranked terms use (see
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own `RankSigned` helper) -- a fixed
/// clip scale derived from a fixed dataset would itself be a form of look-ahead; ranking only
/// against strictly-prior same-day observations avoids that by construction.
///
/// Deliberately simple (a growing list, re-sorted on read) rather than an incremental data
/// structure -- at most ~1600 cadences/day, trivial work even on the live path. Known, accepted
/// limitation: the first several minutes of a session rank against very few samples, so
/// early-session ranks are noisy -- no different in kind from every other "cold start" this
/// project already accepts (e.g. `IvRankSessionCount`'s own same-day-only fallback).
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
