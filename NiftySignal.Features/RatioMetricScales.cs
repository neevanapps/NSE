namespace NiftySignal.Features;

/// <summary>
/// Provisional placeholder constants for the ratio-based composite's five metrics (weekend
/// build, 2026-09-09) -- same spirit as CompositeScoreCalculator.DefaultK's own doc comment
/// ("starting point, not the answer"). PENDING (audit finding F40): revisit every value here
/// once real Ratio*Raw distributions exist from a live session -- none of these were derived
/// from live data.
/// </summary>
public static class RatioMetricScales
{
    public const double NotionalVolumeRatioRMax = 2.0;
    public const double SizedOiFlowRatioRMax = 3.0;

    /// <summary>
    /// Rupees. Amended 2026-09-09 review: was 2.0, too tight -- an ATM weekly can move ~Rs 2
    /// on a routine quiet bar, which would slam clip(diff/2) to +/-1 constantly and make this
    /// metric nearly binary. 6.0 gives more realistic headroom. Consider scaling by
    /// max(spread, 0.5) instead of a flat rupee constant once live data exists, so the unit
    /// becomes "spreads of richness" rather than a DTE/strike-dependent rupee amount.
    /// </summary>
    public const double ResidualDifferenceScale = 6.0;

    public const double IvSkewRatioRMax = 1.5;
    public const double SpreadRatioAtmRMax = 2.0;

    /// <summary>
    /// Minimum combined call+put notional (rupees) for metric 1 to be non-null this bar --
    /// below it, there's no real signal, not just a numerically-awkward one. Crude starting
    /// value (2026-09-09 review), not derived from data -- picked to sit comfortably above
    /// normal quiet-bar noise.
    /// </summary>
    public const double MinNotionalForVolumeRatio = 5_000;

    /// <summary>Minimum combined call+put constructive |dOI| (contracts) for metric 2 to be non-null this bar -- same reasoning as <see cref="MinNotionalForVolumeRatio"/>.</summary>
    public const double MinContractsForOiFlow = 50;
}
