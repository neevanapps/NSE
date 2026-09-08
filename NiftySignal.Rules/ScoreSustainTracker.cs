namespace NiftySignal.Rules;

/// <summary>
/// How long, and across how many actual cadences, the current qualifying streak has held --
/// see <see cref="ScoreSustainTracker.Observe"/>. Two separate numbers on purpose (audit
/// finding F12, 2026-09-08): a feed stall that goes unrecorded before a data gap is formally
/// opened would otherwise let elapsed wall-clock time keep advancing while no real readings
/// arrived, letting the 45-second hold requirement pass on silence rather than a genuinely
/// sustained score. <see cref="EntryRuleEvaluator"/> requires both thresholds independently.
/// </summary>
public readonly record struct SustainStatus(TimeSpan Duration, int CadenceCount);

/// <summary>
/// Tracks how long the score has continuously qualified (|score| &gt;= minAbsScore) with
/// the same sign, feeding the "entering on a single noisy spike" guard in plan section 7.3
/// step 3 (MinScoreSustainedSeconds). Stateful by design -- this is genuinely a streak
/// across cadence ticks, not something derivable from a single snapshot, which is exactly
/// why it's kept separate from the stateless EntryRuleEvaluator (that evaluator can then be
/// tested purely against constructed contexts, per plan section 13's testing priority).
/// </summary>
public sealed class ScoreSustainTracker
{
    DateTimeOffset? _streakStartedAt;
    int _streakSign;
    int _streakCadenceCount;

    /// <summary>Call once per cadence tick, in timestamp order. Returns how long, and across how many cadences, the current qualifying streak has held.</summary>
    public SustainStatus Observe(DateTimeOffset timestamp, double score, double minAbsScore)
    {
        var sign = Math.Sign(score);
        var qualifies = Math.Abs(score) >= minAbsScore && sign != 0;

        if (!qualifies)
        {
            _streakStartedAt = null;
            _streakSign = 0;
            _streakCadenceCount = 0;
            return new SustainStatus(TimeSpan.Zero, 0);
        }

        if (_streakStartedAt is null || sign != _streakSign)
        {
            _streakStartedAt = timestamp;
            _streakSign = sign;
            _streakCadenceCount = 1;
            return new SustainStatus(TimeSpan.Zero, 1);
        }

        _streakCadenceCount++;
        return new SustainStatus(timestamp - _streakStartedAt.Value, _streakCadenceCount);
    }
}
