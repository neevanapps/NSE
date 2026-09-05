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

    public decimal? BidPrice { get; set; }

    public decimal? AskPrice { get; set; }

    /// <summary>Ask minus bid. Stored rather than derived at query time so spread history is directly queryable.</summary>
    public decimal? SpreadAbs { get; set; }

    /// <summary>Spread as a percentage of mid -- the same normalisation StrikeSelectionConfig.MaxSpreadPctOfMid filters on.</summary>
    public decimal? SpreadPctOfMid { get; set; }

    /// <summary>Null when the solver genuinely fails (deep ITM/OTM near expiry, no depth yet) -- never a fabricated value.</summary>
    public double? ImpliedVolatility { get; set; }

    public double? Delta { get; set; }

    public double? Gamma { get; set; }

    public double? ThetaPerDay { get; set; }

    public double? Vega { get; set; }

    public double? Rho { get; set; }
}
