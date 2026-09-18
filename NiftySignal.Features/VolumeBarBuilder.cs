using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Features;

/// <summary>
/// One volume bar's own OHLC/flow/CVD/depth-imbalance summary -- the foundation row the
/// volume-cadence scoring plan (2026-09-17) builds every futures metric on top of. Deliberately
/// carries only RAW per-bar values, not pre-rolled multi-bar windows (e.g. no "TrendReversion
/// over the last 15 bars" field here) -- same separation of concerns
/// `NiftySignal.BacktestData.CadenceContext` already uses: a consumer applies its own
/// bar-count-windowed tracker on top of a series of these, this record just answers "what
/// happened in this one bar."
/// </summary>
/// <param name="Volume">
/// This bar's own accumulated future volume -- always &gt;= the threshold that triggered it,
/// never exactly equal by construction (a single tick's entire delta is absorbed into whichever
/// bar is open when it arrives, never split across two bars -- the standard "event bar"
/// convention, and the only way to avoid inventing a fill price for a fractional tick that was
/// never actually quoted). A day's final bar is the one exception -- see
/// <see cref="VolumeBarBuilder.FlushPartial"/>.
/// </param>
/// <param name="OpenInterestAtClose">The latest known future OI as of this bar's last tick -- forward-filled from whatever OI value the caller last saw, since OI updates less frequently than trades.</param>
/// <param name="VwapAtClose">Whole-SESSION (not per-bar) volume-weighted average price as of this bar's close -- see <see cref="FutureFlowAccumulator.Vwap"/>'s own doc comment for why it's cumulative, not windowed. This is the reference a "VWAP deviation" metric reads ClosePrice against.</param>
/// <param name="OrderFlowImbalance">
/// This bar's own net Order Flow Imbalance (<see cref="OrderFlowImbalanceAccumulator"/>) -- a
/// genuinely different question from <paramref name="DepthImbalance"/>: that one is a resting-book
/// SNAPSHOT average, this one is the CHANGE in the book between ticks (size added/pulled, or a
/// price level itself moving). Null if no depth-bearing tick arrived this bar.
/// </param>
/// <param name="TopOfBookImbalance">
/// This bar's own touch-only imbalance (<see cref="TopOfBookImbalanceAccumulator"/>) -- same
/// resting-book SNAPSHOT-average shape as <paramref name="DepthImbalance"/>, but reading only
/// Bid1Qty/Ask1Qty instead of the summed 5-level totals. Can diverge sharply from
/// <paramref name="DepthImbalance"/> at the same instant (a thin best offer behind a heavy total
/// book reads bullish at the touch, bearish in aggregate) -- new 2026-09-17, 9th future-side
/// candidate. Null if no depth-bearing tick arrived this bar.
/// </param>
public sealed record VolumeBar(
    DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp,
    decimal OpenPrice, decimal HighPrice, decimal LowPrice, decimal ClosePrice,
    long Volume, long? OpenInterestAtClose, double? VwapAtClose,
    long? FutureCvdNet, double? DepthImbalance, double? OrderFlowImbalance, double? TopOfBookImbalance)
{
    /// <summary>Wall-clock time this bar took to fill -- the volume-clock's own analogue of "how urgently did participants trade," a candidate metric in its own right per the 2026-09-17 plan, not just plumbing.</summary>
    public TimeSpan Duration => EndTimestamp - StartTimestamp;
}

/// <summary>
/// Turns a raw tick stream into a series of <see cref="VolumeBar"/>s, cutting a new bar every time
/// cumulative future volume since the last bar crosses <paramref name="barVolumeThreshold"/> --
/// the volume-cadence foundation from the 2026-09-17 plan ("participant behaviour, not clock
/// time, should set the pace"). Deliberately source-agnostic: nothing here knows whether
/// <see cref="ApplyTick"/> is being fed by a historical replay
/// (<c>NiftySignal.VolumeBarData</c>) or a live socket -- same reasoning
/// <see cref="FutureCvdProxyAccumulator"/>/<see cref="FutureFlowAccumulator"/>/
/// <see cref="DepthImbalanceAccumulator"/> (all reused here, not reimplemented) were already
/// moved to this project for. Live wiring is a later, separate step; this class doesn't need to
/// change for it to happen.
///
/// One instance covers exactly one trading day for one instrument (the future) -- construct a
/// fresh one per day, same convention every other per-day tracker in this codebase already uses.
/// </summary>
public sealed class VolumeBarBuilder(long barVolumeThreshold)
{
    readonly FutureFlowAccumulator _flow = new();
    readonly FutureCvdProxyAccumulator _cvd = new();
    readonly DepthImbalanceAccumulator _depth = new();
    readonly OrderFlowImbalanceAccumulator _ofi = new();
    readonly TopOfBookImbalanceAccumulator _tob = new();

    DateTimeOffset? _barStart;
    decimal? _open;
    decimal? _high;
    decimal? _low;
    decimal? _lastPrice;
    long? _lastOpenInterest;

    /// <summary>
    /// Feeds one tick into the bar under construction. Returns the completed bar once this tick's
    /// own volume delta pushes the bar's cumulative volume to or past
    /// <paramref name="barVolumeThreshold"/> -- null while still accumulating.
    /// </summary>
    /// <param name="cumulativeVolume">The exchange's own cumulative-for-the-day volume field (<see cref="NiftySignal.Domain.Entities.Tick.Volume"/>) -- NOT a per-tick trade size; <see cref="FutureFlowAccumulator"/> diffs it against the previous tick internally, same as every other caller of that class.</param>
    /// <param name="depth">Null for a touchline-only update with no depth snapshot -- CVD/depth-imbalance simply don't observe that tick, matching every other accumulator's own null handling in this codebase.</param>
    /// <param name="openInterest">Null when this tick carried no OI update -- the bar remembers the latest non-null value it has seen, forward-filled.</param>
    public VolumeBar? ApplyTick(DateTimeOffset timestamp, decimal lastPrice, long cumulativeVolume, MarketDepth? depth, long? openInterest)
    {
        _barStart ??= timestamp;
        _open ??= lastPrice;
        _high = _high is { } h ? Math.Max(h, lastPrice) : lastPrice;
        _low = _low is { } l ? Math.Min(l, lastPrice) : lastPrice;
        _lastPrice = lastPrice;
        _lastOpenInterest = openInterest ?? _lastOpenInterest;

        _flow.ApplyTick(lastPrice, cumulativeVolume);
        var delta = _flow.LastVolumeDelta;

        if (depth is { } d)
        {
            if (delta > 0)
            {
                _cvd.ApplyTick(lastPrice, d, delta);
            }

            _depth.ApplyTick(d);
            _ofi.ApplyTick(d);
            _tob.ApplyTick(d);
        }

        return _flow.CadenceVolumeDelta >= barVolumeThreshold ? CompleteBar(timestamp) : null;
    }

    /// <summary>
    /// The trading day's final, necessarily-partial bar -- volume since the last real bar closed
    /// almost never lands exactly on the threshold, so the day's last stretch of ticks would
    /// otherwise simply vanish rather than becoming a (shorter, lower-volume) bar of its own. Call
    /// once after the day's final tick. Null if no tick has arrived since the previous bar closed
    /// (the day's volume divided the threshold evenly, or no ticks arrived at all).
    /// </summary>
    public VolumeBar? FlushPartial(DateTimeOffset asOfTimestamp) => _barStart is not null ? CompleteBar(asOfTimestamp) : null;

    VolumeBar CompleteBar(DateTimeOffset endTimestamp)
    {
        var bar = new VolumeBar(
            _barStart!.Value, endTimestamp,
            _open!.Value, _high!.Value, _low!.Value, _lastPrice!.Value,
            _flow.CadenceVolumeDelta, _lastOpenInterest, _flow.Vwap,
            _cvd.CadenceNet, _depth.CadenceImbalance, _ofi.CadenceNet, _tob.CadenceImbalance);

        _barStart = null;
        _open = null;
        _high = null;
        _low = null;
        _flow.ResetCadence();
        _cvd.ResetCadence();
        _depth.Reset();
        _ofi.ResetCadence();
        _tob.Reset();

        return bar;
    }
}
