namespace NiftySignal.VolumeBarData;

/// <summary>
/// The 25-delta option chain skew as of one future volume-bar's close -- 2026-09-18, Phase 1
/// metric 4. A point-in-time snapshot (IV/delta is a level, same shape as
/// <see cref="OptionAtmBarRow"/>, not accumulated across the bar's window).
///
/// Reuses the time-cadence pipeline's own already-CONFIRMED approximation exactly
/// (`docs/SCORE_CANDIDATES.md`'s "Ratio-composite IvSkew25Delta"): the single nearest-to-25-delta
/// strike per side, using EACH STRIKE'S OWN individually-solved Delta (not a cheap shared-vol
/// first-pass estimate), no bracket interpolation. `SkewRatio = PutIv / CallIv`, matching that
/// candidate's exact confirmed orientation.
///
/// Band is deliberately WIDER than metrics 2/3's ATM±1 aggregation band -- a 25-delta strike is
/// meaningfully OTM by construction and will rarely fall inside ATM±1. Candidates are the 21
/// strikes nearest the FUTURE's own close (ATM±10 strikes), same "ATM = nearest to future close"
/// convention as the narrower band metrics, just wider -- matches the time-cadence candidate's own
/// "full chain (ATM±10)" scope.
/// </summary>
public sealed class OptionSkew25DeltaBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    public decimal? SyntheticForward { get; set; }

    public decimal? Call25DeltaStrike { get; set; }

    public double? Call25DeltaIv { get; set; }

    public decimal? Put25DeltaStrike { get; set; }

    public double? Put25DeltaIv { get; set; }

    /// <summary>Null whenever either leg's 25-delta strike/IV didn't resolve (illiquid quotes, or no candidate strike came close enough to either target delta that day) -- never fabricated from a partial pair.</summary>
    public double? SkewRatio { get; set; }
}
