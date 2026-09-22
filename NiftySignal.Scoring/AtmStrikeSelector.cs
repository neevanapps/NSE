using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Scoring;

/// <summary>
/// Phase D of docs/LIVE_PARITY_PLAN.md: the ATM strike-selection rule extracted out of
/// <c>NiftySignal.VolumeBarData.TradeSimulator</c>'s own two local <c>PickAtm</c> closures (nearest
/// strike to the current future price, same option side as the signal) -- pure, dependency-free, no
/// DB/I-O, same "extract once, call from both sides, never duplicate" discipline the plan's own
/// Phase-1 scoring extraction already established for <see cref="OptionsThreeWayScoreCalculator"/>.
/// Both <c>TradeSimulator.cs</c> (backtest) and the live paper-trade path now call this SAME function
/// -- verified byte-identical (nearest-strike selection is deterministic given the same chain/price),
/// so there is exactly one strike-selection rule in the codebase, not two copies that could drift.
/// </summary>
public static class AtmStrikeSelector
{
    /// <summary>
    /// Nearest-to-<paramref name="futurePrice"/> instrument of the given <paramref name="side"/>
    /// within <paramref name="chain"/> -- simplified ATM selection (not price-range matching like the
    /// time-cadence simulator's own convention), same deliberate simplification
    /// <c>TradeSimulator.cs</c>'s own doc comment already flags. Null if <paramref name="chain"/> has
    /// no instrument of that side at all.
    /// </summary>
    public static Instrument? PickAtm(IEnumerable<Instrument> chain, OptionType side, decimal futurePrice) => chain
        .Where(o => o.OptionType == side)
        .OrderBy(o => Math.Abs(o.StrikePrice!.Value - futurePrice))
        .FirstOrDefault();
}
