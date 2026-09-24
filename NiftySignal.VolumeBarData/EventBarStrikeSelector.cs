using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Scoring;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec (section 14): strike selection kept structurally
/// separate from bar construction, so the mode can change later without rebuilding any bar. Only
/// <see cref="PickDynamicAtm"/> is implemented in this pass (Experiment 1, spec section 47) --
/// Fixed Daily ATM, Fixed Offset, and Dynamic Band are the deferred Experiments 2-4 and are left
/// as documented not-yet-implemented extension points rather than guessed at now.
/// </summary>
public static class EventBarStrikeSelector
{
    /// <summary>Nearest strike to <paramref name="futurePrice"/> for the given side -- reuses <see cref="AtmStrikeSelector.PickAtm"/> directly (this project's one, already-extracted ATM rule) rather than duplicating it.</summary>
    public static Instrument? PickDynamicAtm(IEnumerable<Instrument> chain, OptionType side, decimal futurePrice)
        => AtmStrikeSelector.PickAtm(chain, side, futurePrice);

    // Deferred to Experiment 2 (spec section 47): freeze the ATM strike picked at the first
    // completed futures event bar of the day and hold it for every subsequent bar.
    // public static Instrument? PickFixedDailyAtm(...)

    // Deferred to Experiment 3 (spec section 47): a fixed strike offset from the day's frozen ATM.
    // public static Instrument? PickFixedOffset(...)

    // Deferred to Experiment 4 (spec section 47): every strike within +/-N of the current dynamic ATM.
    // public static IReadOnlyList<Instrument> PickDynamicBand(...)
}
