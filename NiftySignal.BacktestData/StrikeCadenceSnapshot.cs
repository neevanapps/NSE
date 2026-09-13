using NiftySignal.Domain.Enums;
using NiftySignal.Features;

namespace NiftySignal.BacktestData;

/// <summary>
/// One strike's own per-15-second-cadence row (2026-09-12 design, see docs/CHILD_TABLE_SCHEMA.md)
/// -- 1:1 with <see cref="CadenceContext"/> by construction (same cadence boundary, same trading
/// day) via <see cref="CadenceContextId"/>. Full fidelity: nothing here is a rollup or an average
/// over a longer window -- that's <see cref="StrikeBandCadenceSnapshot"/>'s job. Persisted for an
/// ATM+/-3 band around each cadence's own ATM strike (see <see cref="StrikeOffsetFromAtm"/>), for
/// both the near-week and next-week expiry chains.
///
/// Field set mirrors production's own <c>NiftySignal.Domain.Entities.StrikeSnapshot</c> closely
/// (same live-cadence sampling, same MarkPrice/Greeks shape), minus <c>Rho</c> (never read
/// anywhere in live scoring) and the theoretical-price/richness pair (that research thread closed
/// as not replicating out-of-sample -- see docs/REVIEW_FINDINGS.md's research thread section),
/// plus the CVD proxy and traded-price OHLC fields new to this backtest exercise.
/// </summary>
public sealed class StrikeCadenceSnapshot
{
    public long Id { get; set; }

    /// <summary>
    /// FK to the CadenceContext row at this exact 15s timestamp -- always resolvable, since this
    /// table's cadence is a fixed 1:1 pairing with the parent, never coarser. Not `required`: the
    /// populator builds this row before the parent's real Id exists (EF assigns it on SaveChanges),
    /// and stamps this field in immediately afterward -- see CadencePopulator.PopulateDayAsync.
    /// </summary>
    public long CadenceContextId { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>== the paired CadenceContext row's own Timestamp -- kept here too so this table can be queried without a join, matching StrikeSnapshot's own precedent of carrying its own cadence identity.</summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>Bare instrument token -- direct traceability back to Instruments/Ticks, matches production's StrikeSnapshot convention.</summary>
    public required string Token { get; set; }

    /// <summary>This week's or next week's -- both populated side by side, distinguished by this field.</summary>
    public required DateOnly ExpiryDate { get; set; }

    /// <summary>Persisted directly rather than re-derived later -- feeds straight into the DTE-confound investigation already under way (see docs/REVIEW_FINDINGS.md's 2026-09-12 DTE section).</summary>
    public required int DaysToExpiry { get; set; }

    public required decimal StrikePrice { get; set; }

    public required OptionType OptionType { get; set; }

    /// <summary>Signed: 0=ATM, negative=below spot, positive=above spot -- persisted at snapshot time since the ATM strike itself drifts intraday as spot moves. Band membership (Strike3/5/7/Itm2Atm1) is a filter on this field, computed at aggregation time, not stored redundantly here.</summary>
    public required int StrikeOffsetFromAtm { get; set; }

    // --- Quote-based price (for IV/Greeks) ----------------------------------------------------
    /// <summary>(bid1+ask1)/2, LTP fallback only when there's no two-sided quote yet -- the same price ImpliedVolatility below is solved from, so the two stay self-consistent. Null (not held over) when this specific 15s cadence had no tick for this token.</summary>
    public decimal? MarkPrice { get; set; }

    public decimal? BidPrice { get; set; }

    public decimal? AskPrice { get; set; }

    // --- Traded-price OHLC (for paper-trade fill accuracy) ------------------------------------
    // Distinct from MarkPrice above: a resting or stop order fills against traded price, not the
    // quote mid. Matches CadenceContext's own Open/High/Low/Close-per-cadence naming exactly.
    public decimal? OpenFromLastCadence { get; set; }

    public decimal? HighFromLastCadence { get; set; }

    public decimal? LowFromLastCadence { get; set; }

    public decimal? CloseFromLastCadence { get; set; }

    // --- Volume / OI ---------------------------------------------------------------------------
    /// <summary>Traded this 15s cadence only, not a running total -- 0 (not null) once this token has been observed at least once (a real, confirmed zero); null only before any tick has ever established a baseline for this token.</summary>
    public long? VolumeDelta { get; set; }

    /// <summary>Blind (unclassified) rupee notional traded this cadence -- sum of each tick's own LastPrice x that tick's own volume delta, distinct from CvdProxyNotionalThisCadence below (which is the same idea but signed buy-minus-sell). "How much traded," not "which way it leaned." Same null-vs-zero semantics as VolumeDelta.</summary>
    public decimal? NotionalDelta { get; set; }

    public long? OpenInterest { get; set; }

    /// <summary>Same null-vs-zero distinction as VolumeDelta above.</summary>
    public long? OpenInterestDelta { get; set; }

    /// <summary>
    /// Value of open positions -- OpenInterest x MarkPrice averaged over a trailing 1 minute, not
    /// the instantaneous MarkPrice. Originally 3 minutes on an assumed OI refresh rate; recalibrated
    /// 2026-09-12 after directly measuring real OI-change gaps on populated data (~60s median,
    /// consistent across liquid and thin strikes). Valuing OI at the raw instantaneous price would
    /// let ordinary 15s-level price noise dominate a figure that's supposed to track real
    /// OI-value change. Null until the 1-minute price window has warmed up, or if OI is unknown.
    /// </summary>
    public decimal? OiNotional { get; set; }

    /// <summary>
    /// Rupee value of THIS cadence's OI change specifically (OpenInterestDelta x the same
    /// 1-minute-averaged price OiNotional uses), not the value of total open interest -- mirrors
    /// exactly how NotionalDelta = VolumeDelta x price mirrors the blind volume total. Same
    /// null-vs-zero semantics as OpenInterestDelta (null with no baseline yet or no OI tick this
    /// cadence, a real number once one exists).
    /// </summary>
    public decimal? OiChangeNotional { get; set; }

    /// <summary>MarkPrice change during this cadence -- a diagnostic column only (2026-09-13: no longer feeds OiBuildup's classification below, which now correctly uses the underlying's own spotPriceChange instead, matching LiveFeatureEngine). Same "diff against the previous cadence" treatment as OpenInterestDelta.</summary>
    public decimal? MarkPriceDelta { get; set; }

    /// <summary>This strike's own price-vs-OI quadrant this cadence, via NiftySignal.Features.OiBuildupClassifier.Classify -- the same logic already driving the live composite's largest weighted component, recorded per strike here. Classified against the UNDERLYING's own price change (spot, not persisted as its own column here -- shared across every strike this cadence) and this strike's own OpenInterestDelta, matching LiveFeatureEngine's convention (2026-09-13 fix; previously used this strike's own MarkPriceDelta, an external-review-flagged inconsistency). Null (not Neutral) when either input is null -- no prior cadence to classify against yet.</summary>
    public OiBuildupClassification? OiBuildup { get; set; }

    public decimal? SpreadAbs { get; set; }

    public decimal? SpreadPctOfMid { get; set; }

    // --- Depth imbalance (2026-09-12) -----------------------------------------------------------
    // Resting order-book quantity, not executed flow -- this strike's own analog of
    // CadenceContext.FutureDepthImbalanceFromLastCadence, using the exact same accumulator
    // (renamed from FutureDepthAccumulator to DepthImbalanceAccumulator since the implementation
    // was already fully generic). Averaged over every real tick observed this cadence, not a
    // single boundary snapshot -- same "averaging a ratio differs from a ratio of averages"
    // reasoning already applied to the future's own version.
    public double? TotalBidQty { get; set; }

    public double? TotalAskQty { get; set; }

    public double? DepthImbalanceFromLastCadence { get; set; }

    // --- CVD proxy (2026-09-12) ----------------------------------------------------------------
    // Same quote-rule technique as FutureCvdProxy (CvdProxyAccumulator, shared with the future's
    // own accumulator logic): classify each raw tick's volume delta as buy-/sell-leaning via
    // LastPrice vs. this option's own bid-ask midpoint, net the classified deltas within this
    // exact 15s cadence -- a direct, honest analog of FutureCvdProxyThisCadence, not a rollup.
    // Expect more nulls than the future's version on thin, far-from-ATM strikes without a live
    // two-sided quote every cadence.

    /// <summary>Signed net classified contract volume, this 15s cadence.</summary>
    public long? CvdProxyVolumeThisCadence { get; set; }

    /// <summary>Signed net classified notional (+/- volume x that tick's own LastPrice, not MarkPrice -- this values each classified trade at its own transacted price, an honest turnover figure, distinct from StrikeBandCadenceSnapshot's blind NotionalSum which values current activity at the row's own mark), this 15s cadence.</summary>
    public decimal? CvdProxyNotionalThisCadence { get; set; }

    // --- Greeks ---------------------------------------------------------------------------------
    /// <summary>Null when the solver genuinely fails (deep ITM/OTM near expiry, no depth yet) -- never a fabricated value.</summary>
    public double? ImpliedVolatility { get; set; }

    public double? Delta { get; set; }

    public double? Gamma { get; set; }

    public double? ThetaPerDay { get; set; }

    public double? Vega { get; set; }

    /// <summary>d Delta/d sigma (2026-09-13, added to unblock live VannaExposure testing) -- already returned by BlackScholes.Calculate's OptionGreeks alongside Delta/Gamma/Theta/Vega, just not previously read here. Same null-when-parity-can't-solve convention as every other Greek above.</summary>
    public double? Vanna { get; set; }

    /// <summary>d Delta/d time, per day (2026-09-13, added to unblock live CharmExposure testing) -- same source and null convention as Vanna above.</summary>
    public double? CharmPerDay { get; set; }
}
