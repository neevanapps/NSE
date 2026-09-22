using NiftySignal.Domain;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Rules;

/// <summary>One <see cref="CoreScoreCrossoverRules.Observe"/> call's result -- whether a genuine crossover fired this cadence, and which direction it now favors.</summary>
public readonly record struct CrossoverObservation(bool Crossed, EntryDirection Direction);

/// <summary>
/// The Crossover strategy's entry/flip logic (2026-09-13 live-wiring plan A7), ported exactly from
/// `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own state machine. Stateful by design
/// (unlike <see cref="CoreScoreHysteresisRules"/>) -- a crossover is genuinely a comparison
/// against the PREVIOUS cadence's sign, not derivable from a single snapshot, the same reason
/// <see cref="ScoreSustainTracker"/> is kept stateful and separate from the stateless entry/exit
/// evaluators. One instance per trading engine (not shared, not persisted between cadences via any
/// mechanism other than this object's own field).
///
/// **Restart behavior is NOT "reset and wait for the next warm-up," unlike <see
/// cref="ScoreSustainTracker"/>'s own accepted reset** -- see the live-wiring plan's own
/// correction (A5/A7, second review): because the crossover strategy is always-positioned, an
/// already-open position depends entirely on <see cref="Observe"/> detecting the next crossover to
/// ever exit (besides SquareOff). If <see cref="PreviousDiffSign"/> resets to null on a Host
/// restart, the very next call would silently re-baseline to whatever the CURRENT sign happens to
/// be (see <see cref="Observe"/>'s own null-branch) -- even if that current sign already disagrees
/// with the open position's side, because the market moved during the downtime. <see cref="Seed"/>
/// exists specifically so the caller can restore this instance's state by replaying that day's
/// persisted `CoreScoreFast`/`CoreScoreSlow` history through a scratch instance of this same class
/// up to the last known cadence, then seeding the real instance with the result -- restoring exact
/// continuity, as if the process had never gone down.
/// </summary>
public sealed class CoreScoreCrossoverRules
{
    int? _previousDiffSign;

    /// <summary>Null before the first warmed-up cadence (or after a restart, before <see cref="Seed"/> is called) -- diagnostic/restart-replay use only, not read by <see cref="Observe"/>'s own callers.</summary>
    public int? PreviousDiffSign => _previousDiffSign;

    /// <summary>
    /// Restores state directly -- for restart-replay only (see this class's own doc comment), never
    /// for normal per-cadence use. Passing null is a legitimate reset (e.g. day boundary), matching
    /// this instance's own fresh-construction state.
    /// </summary>
    public void Seed(int? previousDiffSign) => _previousDiffSign = previousDiffSign;

    /// <summary>
    /// Call once per cadence, in timestamp order, with the fast/slow moving averages -- ONLY once
    /// both are warmed up (non-empty); the caller is responsible for that warm-up gate, matching
    /// `CoreScoreOptionSimulator.cs`'s own `fastWindow.Count == 0 || slowWindow.Count == 0` check,
    /// which happens before this comparison is ever attempted. An exact tie (`diffSign == 0`) is
    /// treated as "no signal yet," not a crossover either way -- matching the backtest exactly.
    /// </summary>
    public CrossoverObservation Observe(double fastAverage, double slowAverage)
    {
        var diffSign = Math.Sign(fastAverage - slowAverage);

        if (diffSign == 0)
        {
            return new CrossoverObservation(false, EntryDirection.None);
        }

        if (_previousDiffSign is null)
        {
            // First warmed-up reading establishes the baseline, not a cross -- matching
            // CoreScoreOptionSimulator.cs's own `previousDiffSign is null` branch exactly.
            _previousDiffSign = diffSign;
            return new CrossoverObservation(false, EntryDirection.None);
        }

        if (diffSign == _previousDiffSign.Value)
        {
            return new CrossoverObservation(false, EntryDirection.None);
        }

        _previousDiffSign = diffSign;
        return new CrossoverObservation(true, diffSign > 0 ? EntryDirection.Bullish : EntryDirection.Bearish);
    }

    /// <summary>
    /// SquareOff at session close -- the crossover strategy's only non-flip exit. A static helper
    /// (not folded into <see cref="Observe"/>, which is purely about the fast/slow comparison) so
    /// the caller can check it independently of whether a crossover happened this cadence. Same
    /// IST-conversion caveat as <see cref="CoreScoreHysteresisRules.EvaluateExit"/> and
    /// <see cref="ExitRuleEvaluator"/> -- <paramref name="now"/> is UTC-backed.
    /// </summary>
    public static bool IsPastSquareOff(DateTimeOffset now, TimeOnly squareOffTime) =>
        TimeOnly.FromDateTime(now.ToIst().DateTime) >= squareOffTime;
}
