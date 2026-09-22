namespace NiftySignal.Domain.Enums;

/// <summary>Moved here from NiftySignal.Rules for the same reason as EntryDirection: a persisted PaperTrade needs it too.</summary>
public enum ExitReason
{
    None,
    SquareOff,
    StopLoss,
    PartialBook,
    ScoreFlip,
    ScoreDecay,
    TimeStop,

    /// <summary>2026-09-13, Core-score Hysteresis strategy only: the score crossed to the OPPOSITE threshold (e.g. a held Bullish position exits once score &lt; -threshold, not merely &lt; +threshold) -- see <c>CoreScoreHysteresisRules.EvaluateExit</c>, matching `CoreScoreOptionSimulator.cs`'s own `scoreInvalidated` check exactly.</summary>
    ScoreInvalidated,

    /// <summary>2026-09-13, Core-score Crossover strategy only: the fast/slow moving averages crossed the opposite way, closing this position so the opposite one can open the same cadence -- see <c>CoreScoreCrossoverRules.Observe</c>.</summary>
    CrossoverFlip,
}
