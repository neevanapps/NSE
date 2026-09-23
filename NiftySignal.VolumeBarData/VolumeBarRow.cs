namespace NiftySignal.VolumeBarData;

/// <summary>
/// One persisted volume bar for the tracked future, built purely from raw historical ticks
/// (2026-09-17 volume-cadence plan). Field-for-field mirror of
/// <see cref="NiftySignal.Features.VolumeBar"/> plus the identity/sequencing columns a database
/// row needs that the in-memory record doesn't -- <see cref="VolumeBarPopulator"/> is the only
/// place that maps one to the other.
///
/// Deliberately futures-only and RAW-per-bar-only for this first phase (2026-09-17 plan: "we will
/// concentrate on future first" and "carries only raw per-bar values, not pre-rolled multi-bar
/// windows" -- see <see cref="NiftySignal.Features.VolumeBar"/>'s own doc comment for why). A
/// consumer reading a series of these rows applies its own bar-count-windowed tracker for
/// TrendReversion/etc., same separation <c>NiftySignal.BacktestData.CadenceContext</c> already
/// uses for the time cadence. Option-chain volume bars and the new candidate metrics (OFI,
/// OI-quadrant, VWAP-deviation-as-a-score, ...) are later, separate phases, not this table.
///
/// Lives in its own dedicated database (see <see cref="VolumeBarPopulator.VolumeBarDatabaseName"/>),
/// never <see cref="NiftySignal.Persistence.NiftySignalDbContext"/>'s live schema and never
/// <c>NiftySignal.BacktestData.BacktestAnalysisDbContext</c>'s time-cadence schema -- three
/// separate, purpose-built databases, none of which the others read.
/// </summary>
public sealed class VolumeBarRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>0-based position within the trading day -- lets a consumer reconstruct "the last N bars" by a plain index range without re-deriving it from timestamps.</summary>
    public required int BarIndex { get; set; }

    /// <summary>The bar volume threshold this row was built at (qty, not lots) -- carried on every row rather than assumed from the run, so two calibration runs (e.g. 1300 vs 650) can coexist in the same table without ambiguity.</summary>
    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset StartTimestamp { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Wall-clock time this bar took to fill -- stored explicitly (not left for a consumer to recompute from the two timestamps above) since it's a first-class candidate metric in its own right per the 2026-09-17 plan, not just plumbing.</summary>
    public double DurationSeconds { get; set; }

    public required decimal OpenPrice { get; set; }

    public required decimal HighPrice { get; set; }

    public required decimal LowPrice { get; set; }

    public required decimal ClosePrice { get; set; }

    /// <summary>This bar's own accumulated future volume -- always &gt;= <see cref="BarVolumeThreshold"/>, except the trading day's final (partial) bar.</summary>
    public required long Volume { get; set; }

    /// <summary>Latest known future OI as of this bar's close, forward-filled. Null only if no OI update was ever observed before this bar (should not happen past the first few bars of a normal day).</summary>
    public long? OpenInterestAtClose { get; set; }

    /// <summary>Whole-SESSION volume-weighted average future price as of this bar's close -- see <see cref="NiftySignal.Features.FutureFlowAccumulator.Vwap"/>'s own doc comment for why this is cumulative, not per-bar.</summary>
    public double? VwapAtClose { get; set; }

    /// <summary>This bar's own net buy-minus-sell classified volume (the mid-point-rule proxy, not true CVD -- see <see cref="NiftySignal.Features.FutureCvdProxyAccumulator"/>'s own doc comment). Null if no tick in this bar had both a two-sided depth quote and nonzero volume to classify.</summary>
    public long? FutureCvdNet { get; set; }

    /// <summary>Average per-tick (BidQty-AskQty)/(BidQty+AskQty) across this bar's ticks. Null if no depth-bearing tick arrived this bar.</summary>
    public double? FutureDepthImbalance { get; set; }

    /// <summary>This bar's own net Order Flow Imbalance (<see cref="NiftySignal.Features.OrderFlowImbalanceAccumulator"/>) -- the CHANGE in top-of-book size between ticks, not a resting-book snapshot like <see cref="FutureDepthImbalance"/>. Null if no depth-bearing tick arrived this bar.</summary>
    public double? OrderFlowImbalance { get; set; }

    /// <summary>This bar's own touch-only depth imbalance (<see cref="NiftySignal.Features.TopOfBookImbalanceAccumulator"/>) -- same resting-book SNAPSHOT-average shape as <see cref="FutureDepthImbalance"/>, but Bid1Qty/Ask1Qty only, not the summed 5-level totals. New 2026-09-17, 9th future-side candidate. Null if no depth-bearing tick arrived this bar.</summary>
    public double? TopOfBookImbalance { get; set; }

    /// <summary>Count of raw <see cref="NiftySignal.Domain.Entities.Tick"/> rows (feed messages) observed while this bar was open -- NOT a count of discrete trades, since a Tick row is broker-agnostic and undiscriminated (trade/touchline/depth-only updates all produce one). See <see cref="NiftySignal.Features.VolumeBar.TickCount"/>'s own doc comment for the full semantics caveat. New 2026-09-22, tick-activity research task.</summary>
    public int TickCount { get; set; }
}
