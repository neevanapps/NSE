namespace NiftySignal.VolumeBarData;

/// <summary>
/// One bar's live-computed reading for the locked futures SMA-crossover strategy (8-fast/40-slow/
/// 5-point-threshold on <c>SessionGatedDepthDurationConfirmed</c>'s own FuturesScore, see
/// docs/LIVE_PARITY_PLAN.md's "Futures crossover: wired live" section) -- 2026-09-21. Written by
/// <see cref="LiveFuturesCrossoverSession"/> (via <c>NiftySignal.Host.LiveFuturesCrossoverEngine</c>)
/// for every future <see cref="VolumeBarRow"/>, same (AsOfDate, BarVolumeThreshold, BarIndex)
/// identity every other Phase-A/C table uses.
///
/// A SEPARATE table from <see cref="LiveOptionsScoreRow"/> rather than a shared/widened one --
/// the two strategies' own per-bar diagnostics genuinely don't overlap in shape (this one has no
/// SessionLeg/MaxPain fields, and carries the fast/slow MA + gap the options table has no concept
/// of), so widening the shared table would leave most columns null for one strategy or the other on
/// every row. The task's own required strategy discriminator (see <see cref="LivePaperTradeRow.Strategy"/>)
/// is what unifies the SIGNAL/TRADE tables, which genuinely do share one shape across strategies;
/// this per-bar diagnostic table does not need it since it is never queried across strategies.
/// </summary>
public sealed class LiveFuturesCrossoverScoreRow
{
    public long Id { get; set; }

    public required DateOnly AsOfDate { get; set; }

    /// <summary>Matches the corresponding <see cref="VolumeBarRow.BarIndex"/> at the same threshold.</summary>
    public required int BarIndex { get; set; }

    public required long BarVolumeThreshold { get; set; }

    public required DateTimeOffset EndTimestamp { get; set; }

    /// <summary>Raw signed FuturesScore in [-1,1] from <see cref="NiftySignal.Scoring.FuturesSessionGatedScoreCalculator.ComputeScore"/> this bar. Null if this bar's active leg had no usable reading.</summary>
    public double? RawScore { get; set; }

    /// <summary>100 x <see cref="RawScore"/> -- the scale the rolling fast/slow windows are built from, same as <c>TradeSimulator.SimulateCrossoverDayAsync</c>'s own <c>scoreWindow</c>.</summary>
    public double? ScaledScore { get; set; }

    /// <summary>The last <c>FastBars</c> (8) scaled scores' simple moving average -- null until the rolling window has at least <c>SlowBars</c> (40) real readings (same "the crossover isn't evaluable yet" gate <c>SimulateCrossoverDayAsync</c>'s own <c>scoreWindow.Count &gt;= slowBars</c> check enforces).</summary>
    public double? FastMa { get; set; }

    /// <summary>The last <c>SlowBars</c> (40) scaled scores' simple moving average.</summary>
    public double? SlowMa { get; set; }

    /// <summary><see cref="FastMa"/> minus <see cref="SlowMa"/> -- the crossover's own signed gap, compared against the 5-point threshold at a crossing bar.</summary>
    public double? Diff { get; set; }

    /// <summary>True only on the bar the fast/slow gap crosses from &lt;=0 to &gt;0 AND the gap's own magnitude at the crossing bar is at least the 5-point threshold -- a qualifying bullish crossing.</summary>
    public bool CrossedUp { get; set; }

    /// <summary>True only on the bar the fast/slow gap crosses from &gt;=0 to &lt;0 AND the gap's own magnitude at the crossing bar is at least the 5-point threshold -- a qualifying bearish crossing.</summary>
    public bool CrossedDown { get; set; }

    /// <summary>TOB confirmation gate's own signed, session-rank-normalized score this bar (<see cref="NiftySignal.Scoring.FuturesSessionGatedScoreCalculator.ComputeTobConfirmationScore"/>) -- only meaningful (and only required to agree in sign) during the Open window, same as the locked <c>SessionGatedDepthDurationConfirmed</c> score's own gate.</summary>
    public double? TobConfirmScore { get; set; }
}
