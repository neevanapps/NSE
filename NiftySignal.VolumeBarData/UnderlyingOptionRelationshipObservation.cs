namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, descriptive research layer (separate from, and does not modify, Experiment 1 --
/// see docs/VolumeCandle_0DTE_Findings.md's "Underlying-Spot-CE-PE Relationship Analysis"
/// section). One futures event bar's synchronized snapshot of futures, spot (if available), and
/// the dynamic-ATM Call/Put contracts -- built entirely from the SAME
/// <see cref="FutureEventBar"/>/<see cref="SynchronizedOptionEventBar"/> series Experiment 1
/// already produces (no new event clock, no new bar construction rule).
/// </summary>
public enum RelationshipDirection { Up, Down, Flat, Unavailable }

/// <summary>Deterministic direction classification: <see cref="RelationshipDirection.Up"/>/<see cref="RelationshipDirection.Down"/> for any nonzero change, <see cref="RelationshipDirection.Flat"/> only for an EXACT zero change -- no invented minimum-move threshold (spec section 9's own first-preference option). <see cref="RelationshipDirection.Unavailable"/> when the prior same-contract reading doesn't exist (first bar of the day/series, missing data, or a contract transition).</summary>
public static class RelationshipDirectionExtensions
{
    public static RelationshipDirection Classify(decimal? change) => change switch
    {
        null => RelationshipDirection.Unavailable,
        0 => RelationshipDirection.Flat,
        > 0 => RelationshipDirection.Up,
        _ => RelationshipDirection.Down,
    };
}

/// <param name="SpotAvailable">False for every field prefixed <c>Spot</c> when this day's dataset has no NIFTY Index instrument at all -- never fabricated, never substituted with the future.</param>
/// <param name="SpotMissingData">True when a spot instrument exists for the day but no real spot tick fell inside THIS event's own interval (same "never fabricate" convention <see cref="SynchronizedOptionEventBar.MissingData"/> already uses).</param>
/// <param name="CeContractTransition">True when this event's dynamic-ATM Call token differs from the PREVIOUS event's Call token -- false for the day's first event (nothing to transition from, never treated as a transition). Any change/direction calculation spanning this event must not attribute it to one contract.</param>
/// <param name="FuturesChange1">This event's Close minus the PREVIOUS event's Close -- null only for the day's first event. Futures always has one continuous identity all day (unlike CE/PE), so this is never contract-transition-gated.</param>
/// <param name="CeChange1">This event's CE AverageLtp minus the previous event's CE AverageLtp -- null if either bar is missing data, OR if <see cref="CeContractTransition"/> is true this event (never attributes a level jump across two different contracts to "movement").</param>
/// <param name="FuturesCrossoverState">The fast/slow relative POSITION at this event ("AboveSlow"/"BelowSlow"/"Equal"/"Unavailable" while the 10-bar window is still warming) -- a STATE, not a crossing edge; distinct from (and does not replace) Experiment 1's own <c>CrossedUp</c>/<c>CrossedDown</c> EVENTS in <see cref="Vc0DteBehaviorRecorder"/>.</param>
/// <param name="RelationshipCategory">Deterministic label from <see cref="FuturesChange1"/>/<see cref="CeChange1"/>/<see cref="PeChange1"/>'s directions alone -- e.g. "UnderlyingUp_CEUp_PEDown". "ContractTransition" takes precedence if either option leg transitioned this event; "OptionDataIncomplete" if either leg is missing/stale (and no transition); otherwise built from the three directions. A provisional RESEARCH label, never a trading signal, never labeled bullish/bearish/reversal.</param>
public sealed record RelationshipObservation(
    DateOnly TradingDate, int EventId, DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp, int Dte,
    decimal FuturesOpen, decimal FuturesHigh, decimal FuturesLow, decimal FuturesClose, long FuturesVolume, double FuturesEventDurationMs,
    bool SpotAvailable, bool SpotMissingData, decimal? SpotAveragePrice, decimal? SpotOpen, decimal? SpotHigh, decimal? SpotLow, decimal? SpotClose, int SpotTickCount,
    decimal AtmStrike,
    string CeToken, decimal? CeAverageLtp, bool CeMissingData, bool CeIsStale, int CeTickCount, bool CeContractTransition,
    string PeToken, decimal? PeAverageLtp, bool PeMissingData, bool PeIsStale, int PeTickCount, bool PeContractTransition,
    decimal? FuturesChange1, decimal? SpotChange1, decimal? CeChange1, decimal? PeChange1,
    double? FuturesFastMa, double? FuturesSlowMa, string FuturesCrossoverState,
    double? SpotFastMa, double? SpotSlowMa, string SpotCrossoverState,
    double? CeFastMa, double? CeSlowMa, string CeCrossoverState,
    double? PeFastMa, double? PeSlowMa, string PeCrossoverState,
    string RelationshipCategory)
{
    public RelationshipDirection FuturesDirection1 => RelationshipDirectionExtensions.Classify(FuturesChange1);
    public RelationshipDirection SpotDirection1 => RelationshipDirectionExtensions.Classify(SpotChange1);
    public RelationshipDirection CeDirection1 => RelationshipDirectionExtensions.Classify(CeChange1);
    public RelationshipDirection PeDirection1 => RelationshipDirectionExtensions.Classify(PeChange1);
}
