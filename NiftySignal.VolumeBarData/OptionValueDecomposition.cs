using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, option-response decomposition (see docs/VolumeCandle_0DTE_Findings.md's "Option
/// Response Decomposition" section). HARD FREEZE: this file adds exactly TWO new pure
/// calculations -- intrinsic value and moneyness, both plain, deterministic economic
/// definitions using only observable price/strike/futures data (no IV, no Greeks, no
/// Black-Scholes, per the task's own explicit "do not build an IV model yet" instruction). No
/// existing option-return formula is touched -- <see cref="UnderlyingOptionRelationshipSummary.ComputeCeChange"/>/
/// <see cref="ComputePeChange"/> remain the sole source of truth for option % /Rs change; this
/// file only splits the SAME observed price into two economically-meaningful components.
///
/// Underlying-instrument choice (confirmed unambiguous by the task's own instruction: "use the
/// same underlying instrument already used for the relationship's signalling methodology"): the
/// relationship's dynamic-ATM selection (<see cref="EventBarStrikeSelector.PickDynamicAtm"/>) is
/// keyed off the NIFTY FUTURE's own close price, never spot -- so intrinsic value here uses
/// <see cref="RelationshipObservation.FuturesClose"/>, consistently, never spot.
/// </summary>
public static class OptionValueDecomposition
{
    /// <summary>Call: max(futures - strike, 0). Put: max(strike - futures, 0). The signalling strike is FROZEN at the signal event (never re-derived at a later event) -- callers must pass the SAME strike captured at T for every forward horizon, per the task's own "do not select a new strike" instruction.</summary>
    public static decimal ComputeIntrinsic(decimal futuresPrice, decimal strike, OptionType side)
        => side == OptionType.Call ? Math.Max(futuresPrice - strike, 0m) : Math.Max(strike - futuresPrice, 0m);

    /// <summary>Extrinsic = option price - intrinsic value. By construction this makes "option change = intrinsic change + extrinsic change" an EXACT identity, never an approximation -- there is no reconciliation discrepancy possible unless a caller mixes prices from different events/strikes.</summary>
    public static decimal ComputeExtrinsic(decimal optionPrice, decimal intrinsic) => optionPrice - intrinsic;

    /// <summary>Signed moneyness (spec section 6): Call = futures - strike, Put = strike - futures. Positive means in-the-money, negative out-of-the-money, zero exactly at-the-money -- unclipped, unlike <see cref="ComputeIntrinsic"/>.</summary>
    public static decimal ComputeMoneyness(decimal futuresPrice, decimal strike, OptionType side)
        => side == OptionType.Call ? futuresPrice - strike : strike - futuresPrice;
}
