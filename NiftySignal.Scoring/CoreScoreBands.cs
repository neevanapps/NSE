using NiftySignal.Domain.Enums;

namespace NiftySignal.Scoring;

/// <summary>
/// Strike-band definitions shared by the Core score's per-metric raw formulas (2026-09-17, Phase 2
/// unification). Ported byte-for-byte from `NiftySignal.MetricTrials/CoreScoreOptionSimulator.cs`'s
/// own `InItm2Atm1` and `NiftySignal.Host/LiveFeatureEngine.cs`'s own `InCoreItm2Atm1` -- both were
/// already identical (diffed directly before extracting), so this removes a duplication that hadn't
/// yet drifted, rather than fixing one that had.
/// </summary>
public static class CoreScoreBands
{
    /// <summary>
    /// The 2 in-the-money strikes plus the shared ATM strike, on each side of the chain: calls at
    /// offset -2..0, puts at offset 0..2 (0 = ATM, counted on both sides). <paramref name="offset"/>
    /// is a signed INDEX offset from ATM among distinct tracked strikes (0=ATM, negative=below spot,
    /// positive=above) -- not a raw price distance; see `ComputeStrikeOffsets`
    /// (`NiftySignal.Host/LiveFeatureEngine.cs`) / `StrikeCadenceSnapshot.StrikeOffsetFromAtm`
    /// (backtest) for how each side resolves it.
    /// </summary>
    public static bool InItm2Atm1(OptionType type, int offset) =>
        type == OptionType.Call ? offset is >= -2 and <= 0 : offset is >= 0 and <= 2;
}
