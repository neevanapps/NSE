namespace NiftySignal.VolumeBarData;

/// <summary>
/// The ATM options complex's own state as of one future volume-bar's close -- 2026-09-18, first
/// options-phase table. Keyed by (AsOfDate, BarIndex, BarVolumeThreshold), the exact same identity
/// <see cref="VolumeBarRow"/> uses, by design: the futures volume bars stay the clock for the
/// options phase (locked decision, see docs/VOLUME_BAR_FINDINGS.md) -- no new bar engine, one row
/// here per future bar, joined by that shared identity.
///
/// Underlying for every IV/strike-selection calculation here is a SYNTHETIC FORWARD
/// (<see cref="NiftySignal.Pricing.SyntheticForward"/>, put-call parity across the 5 strikes
/// nearest the future's own close price), never the raw future price directly -- a known,
/// already-fixed bug in this codebase's own history (2026-09-07: using the tracked future as the
/// underlying for weekly options systematically biased put IV ~7-8 vol points below call IV at
/// every strike, the signature of a too-low underlying, not genuine skew). Checked against that
/// history before building this, per the evaluation playbook's own step 0.
/// </summary>
public sealed class OptionAtmBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>Matches the corresponding <see cref="VolumeBarRow.BarIndex"/> at the same threshold -- the join key back to the future bar this row describes the options complex "as of."</summary>
    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Put-call-parity synthetic forward for the near-week expiry as of this bar's close -- null only if fewer than 1 of the 5 nearest strikes had both legs quoted (should not happen past the first few bars of a normal day).</summary>
    public decimal? SyntheticForward { get; set; }

    /// <summary>Nearest strike to <see cref="SyntheticForward"/> (not the raw future price) -- consistent with this codebase's own established `AtmStrikeBySyntheticForward` convention.</summary>
    public decimal? AtmStrike { get; set; }

    public double? AtmCallIv { get; set; }

    public double? AtmPutIv { get; set; }

    /// <summary>Average of the call and put leg -- null if either leg's solve failed (never fabricated; see <see cref="NiftySignal.Pricing.ImpliedVolatilitySolver"/>'s own doc comment on why a failed solve is null, not a guess).</summary>
    public double? AtmIv { get; set; }
}
