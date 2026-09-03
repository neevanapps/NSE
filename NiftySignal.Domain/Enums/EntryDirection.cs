namespace NiftySignal.Domain.Enums;

/// <summary>
/// A trade's directional bias -- moved here from NiftySignal.Rules (where it was first
/// needed) once a persisted PaperTrade entity also needed it: this is a domain concept,
/// not something specific to rule evaluation, and Domain is where every layer (Rules,
/// Execution, Backtest, Persistence) can see it without a circular reference.
/// </summary>
public enum EntryDirection
{
    None,
    Bullish,
    Bearish,
}
