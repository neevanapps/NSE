using NiftySignal.Domain;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Rules;

/// <summary>
/// The Hysteresis strategy's entry/exit logic (2026-09-13 live-wiring plan A7), ported exactly
/// from `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s own state machine -- NOT a
/// reuse/config-adaptation of <see cref="EntryRuleEvaluator"/>/<see cref="ExitRuleEvaluator"/>.
/// Confirmed by reading both in full: neither's rule shape matches what was actually backtested --
/// `EntryRuleEvaluator` requires a `MinAbsScore` + sustain-duration model this strategy never used;
/// `ExitRuleEvaluator`'s priority chain includes StopLoss/PartialBook/TimeStop (this strategy has
/// none of those) and a plain zero-crossing `ScoreFlip` (this strategy's exit is a wider
/// hysteresis band, not a zero-cross). Forcing this strategy through that machinery via config
/// tricks (absurdly wide StopLossPct, huge MaxHoldMinutes) would risk silent drift from the tested
/// behavior -- these are new, minimal, purpose-built pure functions instead.
///
/// Stateless by design (unlike <see cref="CoreScoreCrossoverRules"/>) -- the hysteresis band needs
/// no memory beyond the currently-held direction and the current score, both already available to
/// the caller each cadence.
/// </summary>
public static class CoreScoreHysteresisRules
{
    /// <summary>Entry fires at `|score| >= entryScoreThreshold` -- matching `CoreScoreOptionSimulator.cs`'s `Math.Abs(sc) >= options.EntryScoreThreshold` exactly (inclusive, not a strict `&gt;`).</summary>
    public static EntryDecision EvaluateEntry(double score, double entryScoreThreshold)
    {
        if (Math.Abs(score) < entryScoreThreshold)
        {
            return new EntryDecision(false, EntryDirection.None, ["|score| below EntryScoreThreshold"]);
        }

        return new EntryDecision(true, score > 0 ? EntryDirection.Bullish : EntryDirection.Bearish, []);
    }

    /// <summary>
    /// Hysteresis band, exactly as validated in `CoreScoreOptionSimulator.cs`'s own
    /// `scoreInvalidated` check: a held Bullish position exits only once the score crosses BELOW
    /// -threshold (not merely below +threshold); a held Bearish position only once it crosses
    /// ABOVE +threshold. Deliberately wider than a symmetric zero-cross -- the score has to swing
    /// a full 2*threshold before the position closes. Plus SquareOff at session close -- the one
    /// shared, non-negotiable safety exit, reusing `config.Session.SquareOffTime` the same way
    /// <see cref="ExitRuleEvaluator"/> already does (same IST-conversion caveat: <paramref
    /// name="now"/> is UTC-backed per this codebase's Npgsql/timestamptz convention, so it must be
    /// converted before comparing against a wall-clock <see cref="TimeOnly"/>, the exact bug class
    /// both existing evaluators' own doc comments warn already bit them once).
    /// </summary>
    public static ExitDecision EvaluateExit(EntryDirection heldDirection, double score, double entryScoreThreshold, DateTimeOffset now, TimeOnly squareOffTime)
    {
        var nowIstTime = TimeOnly.FromDateTime(now.ToIst().DateTime);
        if (nowIstTime >= squareOffTime)
        {
            return new ExitDecision(true, false, ExitReason.SquareOff);
        }

        var invalidated = heldDirection == EntryDirection.Bullish
            ? score < -entryScoreThreshold
            : score > entryScoreThreshold;

        return invalidated
            ? new ExitDecision(true, false, ExitReason.ScoreInvalidated)
            : new ExitDecision(false, false, ExitReason.None);
    }
}
