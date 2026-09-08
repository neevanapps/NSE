using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Entities;

/// <summary>
/// One strike's per-cadence analytics row (2026-09-05) -- volume traded during the cadence,
/// top-of-book spread, and the full Greeks set. Deliberately not wired into scoring: this is
/// accumulation for the plan's phase 8 ("data accumulation, tuning, evaluation"), so the data
/// exists to analyse before anything is built on top of it.
///
/// Persisted for a narrow band around ATM only (see LiveFeatureEngine's PersistedStrikeBand)
/// rather than the full tracked chain. Greeks for ~80 strikes every 15s would be ~600k values
/// a day for strikes that mostly never trade; the near-the-money band is where the
/// decision-relevant signal is, and it keeps this to ~10 rows a cadence.
///
/// The first per-instrument-per-cadence table in the schema -- follows <see cref="Tick"/> for
/// the instrument side (bare Token, no FK) and <see cref="ScoreSnapshot"/> for the cadence side.
/// </summary>
public sealed class StrikeSnapshot
{
    public long Id { get; set; }

    /// <summary>The cadence tick this row belongs to -- shared with the <see cref="ScoreSnapshot"/> computed in the same pass.</summary>
    public required DateTimeOffset ComputedAt { get; set; }

    public required string Token { get; set; }

    public required decimal StrikePrice { get; set; }

    public required OptionType OptionType { get; set; }

    /// <summary>Kept so near-week and next-week rows stay distinguishable without joining back to instruments.</summary>
    public required DateOnly ExpiryDate { get; set; }

    /// <summary>
    /// Volume traded during this cadence, not the running day total. The feed reports cumulative
    /// day volume, so this is the delta against the previous cadence -- same treatment
    /// OiBuildupNet already applies to open interest. Null on the first cadence after startup,
    /// where there's no prior observation to diff against.
    /// </summary>
    public long? VolumeDelta { get; set; }

    public long? OpenInterest { get; set; }

    /// <summary>
    /// OI change during this cadence, not the running level -- same "diff against the previous
    /// cadence" treatment <see cref="VolumeDelta"/> gets. Unlike volume, OI is not a cumulative
    /// day counter (it can legitimately fall as well as rise), so this is never clamped to zero:
    /// a negative value is a real OI decrease, not a feed-reset artifact. Null on the first
    /// cadence after startup, where there's no prior observation to diff against.
    /// </summary>
    public long? OpenInterestDelta { get; set; }

    /// <summary>
    /// <see cref="MarkPrice"/> change during this cadence -- same "diff against the previous
    /// cadence" treatment as <see cref="OpenInterestDelta"/>, needed to classify this strike's
    /// <see cref="OiBuildup"/>. Null on the first cadence after startup.
    /// </summary>
    public decimal? MarkPriceDelta { get; set; }

    /// <summary>
    /// This strike's own price-vs-OI quadrant this cadence (plan section 5.2), computed by
    /// <c>NiftySignal.Features.OiBuildupClassifier.Classify</c> -- the same logic that already
    /// feeds the composite score's largest component (OiBuildupNet), just recorded per strike
    /// here instead of collapsed into one chain-wide net (2026-09-08: that net was a black box
    /// with no visibility into which strikes actually drove it). Null, not Neutral, when either
    /// delta above is null -- there's no prior cadence to classify against yet.
    /// </summary>
    public OiBuildupQuadrant? OiBuildup { get; set; }

    /// <summary>Instantaneous top-of-book bid as of this cadence tick -- not averaged, unlike <see cref="SpreadAbs"/> below.</summary>
    public decimal? BidPrice { get; set; }

    /// <summary>Instantaneous top-of-book ask as of this cadence tick -- not averaged, unlike <see cref="SpreadAbs"/> below.</summary>
    public decimal? AskPrice { get; set; }

    /// <summary>
    /// Ask minus bid, averaged over the ~15s cadence window (sampled every ~3s by
    /// LiveFeatureEngine.SampleSpreads) rather than read once at the cadence boundary -- a
    /// single wide print right at that instant would otherwise look identical to a
    /// persistently illiquid strike. Same smoothing the composite score's five noisy metrics
    /// already get, applied per-strike here (2026-09-05). Stored rather than derived at query
    /// time so spread history is directly queryable.
    /// </summary>
    public decimal? SpreadAbs { get; set; }

    /// <summary>Spread as a percentage of mid, averaged the same way as <see cref="SpreadAbs"/> -- the same normalisation StrikeSelectionConfig.MaxSpreadPctOfMid filters on.</summary>
    public decimal? SpreadPctOfMid { get; set; }

    /// <summary>Null when the solver genuinely fails (deep ITM/OTM near expiry, no depth yet) -- never a fabricated value.</summary>
    public double? ImpliedVolatility { get; set; }

    public double? Delta { get; set; }

    public double? Gamma { get; set; }

    public double? ThetaPerDay { get; set; }

    public double? Vega { get; set; }

    public double? Rho { get; set; }

    /// <summary>
    /// (Bid+Ask)/2, LTP fallback when there's no two-sided quote (see LiveFeatureEngine.MidPrice)
    /// -- the same price this row's <see cref="ImpliedVolatility"/> was solved from, so the two
    /// stay self-consistent. Not the tick's own LastPrice: mixing an LTP-based price against a
    /// mid-based IV/theoretical would make an ordinary quote move look like a false anomaly.
    /// </summary>
    public decimal? MarkPrice { get; set; }

    /// <summary>
    /// Black-Scholes price at the shared ATM-strike reference vol for this expiry+cadence, not
    /// this strike's own IV (which would just reproduce <see cref="MarkPrice"/> and be circular).
    /// Null when no ATM leg had a usable quote to solve a reference vol from that cadence.
    /// </summary>
    public double? TheoreticalPrice { get; set; }

    /// <summary>
    /// <see cref="MarkPrice"/> minus <see cref="TheoreticalPrice"/> -- the skew premium in
    /// rupees. Stored rather than derived at query time (same call as <see cref="SpreadAbs"/>)
    /// so future analysis can filter/sort on it directly. Null unless both inputs are present.
    /// </summary>
    public double? PriceVsTheoretical { get; set; }
}
