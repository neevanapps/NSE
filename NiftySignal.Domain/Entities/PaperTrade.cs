using NiftySignal.Domain.Enums;

namespace NiftySignal.Domain.Entities;

/// <summary>
/// One paper trade's full lifecycle as a single row (plan section 9: "entry time/price/
/// reason/score, partial exits, final exit, gross and net P&amp;L, ruleset version,
/// score-weights version"). Open while <see cref="ExitTime"/> is null; updated in place on
/// partial book and again on final exit rather than modeled as separate event rows, since
/// a trade is one thing with a lifecycle, not a stream of independent events.
/// </summary>
public sealed class PaperTrade
{
    public long Id { get; set; }

    public required string InstrumentToken { get; set; }

    public required string TradingSymbol { get; set; }

    public required EntryDirection Direction { get; set; }

    public required DateTimeOffset EntryTime { get; set; }

    public required decimal EntryPrice { get; set; }

    /// <summary>The composite score (plan section 6) that triggered this entry.</summary>
    public required double EntryScore { get; set; }

    public bool HasPartiallyBooked { get; set; }

    public DateTimeOffset? PartialExitTime { get; set; }

    public decimal? PartialExitPrice { get; set; }

    /// <summary>Null while the trade is still open.</summary>
    public DateTimeOffset? ExitTime { get; set; }

    public decimal? ExitPrice { get; set; }

    public ExitReason ExitReason { get; set; } = ExitReason.None;

    /// <summary>Before brokerage/slippage costs.</summary>
    public decimal? GrossPnl { get; set; }

    /// <summary>After costs (plan section 9) -- the number performance reporting should use.</summary>
    public decimal? NetPnl { get; set; }

    /// <summary>India VIX at entry, for regime-bucketed reporting (plan section 9). Null until a VIX data source is wired up.</summary>
    public double? VixAtEntry { get; set; }

    /// <summary>
    /// Best unrealised profit the trade ever reached, as a percentage of entry premium
    /// (2026-09-05). Same percent-of-entry basis as StopLossPct/PartialBookAtProfitPct so the
    /// three are directly comparable -- "it ran to +40% before we exited at +30%" is the
    /// question this exists to answer. Null until the first cadence with a live quote.
    /// </summary>
    public double? MaxFavourableExcursionPct { get; set; }

    /// <summary>
    /// Worst unrealised drawdown the trade ever reached, same basis as
    /// <see cref="MaxFavourableExcursionPct"/>. Negative by convention (a trade that never went
    /// underwater records a value at or above zero).
    /// </summary>
    public double? MaxAdverseExcursionPct { get; set; }

    public required string RulesetVersion { get; set; }

    public required string ScoreWeightsVersion { get; set; }
}
