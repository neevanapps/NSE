namespace NiftySignal.BacktestData;

/// <summary>
/// One (bucket, expiry, band) row -- a two-dimensional rollup of <see cref="StrikeCadenceSnapshot"/>:
/// summed/averaged across every strike in the named <see cref="BandDefinition"/>, AND across every
/// 15-second cadence inside this bucket's own <see cref="CadenceMinutes"/> window. Real aggregation
/// over stored data, not a guess at what happened between snapshots -- see
/// docs/CHILD_TABLE_SCHEMA.md for the full reasoning.
///
/// Call-side and put-side columns sit side by side in the same row rather than a separate row per
/// option type, since almost every interesting metric here (PCR-style ratios, CVD net comparison)
/// is a call-vs-put comparison and this avoids a self-join at analysis time.
///
/// Metric columns below are a deliberate starter set, not exhaustive -- more land metric-by-metric
/// once this structure is validated, per the standing evaluation process (CLAUDE.md).
/// </summary>
public sealed class StrikeBandCadenceSnapshot
{
    public long Id { get; set; }

    /// <summary>FK to the CadenceContext row at this bucket's own boundary timestamp -- always resolvable, since CadenceMinutes is chosen as an exact multiple of the parent's 15s cadence.</summary>
    public required long CadenceContextId { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>Bucket boundary (e.g. every 5th or 15th minute), not a 15s cadence timestamp.</summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>5 or 15 today, not hardcoded -- the only one of the three tables carrying this column. Rows for different granularities coexist here rather than requiring separate tables or a rebuild to compare.</summary>
    public required int CadenceMinutes { get; set; }

    public required DateOnly ExpiryDate { get; set; }

    public required int DaysToExpiry { get; set; }

    /// <summary>"Strike3" / "Strike5" / "Strike7" / "Itm2Atm1" -- see docs/CHILD_TABLE_SCHEMA.md's band-definition table for the exact StrikeOffsetFromAtm ranges each name selects, per option type.</summary>
    public required string BandDefinition { get; set; }

    /// <summary>Distinct strikes that actually contributed at least one non-null observation within this bucket -- chain gaps happen near open/close, this makes coverage directly queryable rather than silently assumed.</summary>
    public required int CallStrikeCount { get; set; }

    public required int PutStrikeCount { get; set; }

    // --- Blind totals (how much activity, either direction) -----------------------------------
    public long? CallVolumeSum { get; set; }

    public long? PutVolumeSum { get; set; }

    /// <summary>Sum(MarkPrice x VolumeDelta) across the band's strikes and this bucket's cadences -- valuation basis is MarkPrice (matches the ratio-composite's own RatioNotionalVolumeRaw convention), a snapshot-style activity-level figure, distinct from the CVD notional fields below which value each classified trade at its own transacted price.</summary>
    public decimal? CallNotionalSum { get; set; }

    public decimal? PutNotionalSum { get; set; }

    public long? CallOiChangeSum { get; set; }

    public long? PutOiChangeSum { get; set; }

    /// <summary>Total open interest (the level, not the change) across the band's strikes, at this bucket's own boundary cadence -- a level quantity, summed rather than averaged since "total resting OI in this band" is the natural comparable figure for a PCR-OI-style call/put ratio, not a per-strike average. Same boundary-only convention as CallAvgIv below, not summed across the bucket's cadences.</summary>
    public long? CallOiSum { get; set; }

    public long? PutOiSum { get; set; }

    /// <summary>Rupee value of this bucket's OI change (sum of StrikeCadenceSnapshot.OiChangeNotional across strikes in band and cadences in bucket) -- the notional counterpart to CallOiChangeSum/PutOiChangeSum above, mirroring how CallNotionalSum mirrors CallVolumeSum.</summary>
    public decimal? CallOiChangeNotionalSum { get; set; }

    public decimal? PutOiChangeNotionalSum { get; set; }

    /// <summary>OI-weighted average across the band's strikes, at this bucket's own boundary cadence (a level quantity, not summed across cadences the way flow quantities above are) -- a thin, far strike shouldn't count the same as one carrying most of the band's open interest. A strike with null OI that cadence is excluded from the weighted average, not treated as zero-weight.</summary>
    public double? CallAvgIv { get; set; }

    public double? PutAvgIv { get; set; }

    // --- CVD proxy net direction (distinct from the blind totals above) -----------------------
    // Two-dimensional rollup of StrikeCadenceSnapshot's own CVD proxy fields: summed across every
    // strike in the band AND across every 15s cadence inside this bucket. Answers "which way it
    // leaned," not "how much activity" -- both are kept as separate columns rather than one
    // replacing the other.
    public long? CallCvdProxyVolumeNet { get; set; }

    public long? PutCvdProxyVolumeNet { get; set; }

    public decimal? CallCvdProxyNotionalNet { get; set; }

    public decimal? PutCvdProxyNotionalNet { get; set; }

    /// <summary>Plain average of StrikeCadenceSnapshot.DepthImbalanceFromLastCadence across every strike in the band and every 15s cadence in this bucket -- resting liquidity, not executed flow (that's the CVD fields above). Unweighted, unlike CallAvgIv's OI-weighting -- no established reason yet to weight depth imbalance by OI specifically; revisit if correlation work suggests it should be.</summary>
    public double? CallDepthImbalanceAvg { get; set; }

    public double? PutDepthImbalanceAvg { get; set; }
}
