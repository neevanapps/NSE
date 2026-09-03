namespace NiftySignal.Rules;

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

    /// <summary>Call once per cadence tick, in timestamp order. Returns how long the current qualifying streak has held.</summary>
    public TimeSpan Observe(DateTimeOffset timestamp, double score, double minAbsScore)
    {
        var sign = Math.Sign(score);
        var qualifies = Math.Abs(score) >= minAbsScore && sign != 0;

        if (!qualifies)
        {
            _streakStartedAt = null;
            _streakSign = 0;
            return TimeSpan.Zero;
        }

        if (_streakStartedAt is null || sign != _streakSign)
        {
            _streakStartedAt = timestamp;
            _streakSign = sign;
            return TimeSpan.Zero;
        }

        return timestamp - _streakStartedAt.Value;
    }
}
