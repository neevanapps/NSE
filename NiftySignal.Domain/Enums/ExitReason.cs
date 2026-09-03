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
}
