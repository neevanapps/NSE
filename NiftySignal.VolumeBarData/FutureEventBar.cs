namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-23, 0-DTE volume-candle research spec, section 4. One futures event bar: closes the
/// instant cumulative futures volume since the previous bar's close reaches
/// <c>FutureEventBarBuilder</c>'s threshold. Deliberately a SEPARATE type from
/// <see cref="NiftySignal.Features.VolumeBar"/> (the existing production event-bar row) -- that
/// type's own doc comment states its bar-construction rule as "a single tick's entire delta is
/// absorbed into whichever bar is open, never split across two bars," which is the OPPOSITE of
/// this spec's own explicit requirement (section 4.1: excess volume past the threshold must be
/// carried into the next bar, never discarded). Building a second type keeps the existing
/// production event-bar builder (and everything downstream of it, the live composite score)
/// completely untouched.
/// </summary>
/// <param name="Volume">
/// This bar's own accumulated volume AT THE MOMENT it reached/crossed the threshold -- always
/// &gt;= the threshold (see <see cref="IsFinalPartialBar"/> for the one exception), and always
/// INCLUDES whatever excess balance was carried in from the previous bar's own crossing (section
/// 4.1). The excess this bar itself produces (Volume - threshold) becomes the next bar's own
/// carried-in starting balance -- see <see cref="FutureEventBarBuilder"/>'s own doc comment for
/// why this is tracked as a threshold-accounting balance rather than by splitting one tick's OHLC
/// contribution across two output rows (which no per-tick data source in this codebase can support
/// below tick granularity).
/// </param>
/// <param name="Vwap">
/// Volume-weighted average price computed ONLY from this bar's own real per-tick deltas -- the
/// carried-in opening balance contributes to the threshold/Volume accounting above but has no
/// bar-local price of its own (it belongs to the previous bar's closing tick), so it is
/// deliberately excluded from this bar's VWAP numerator/denominator rather than fabricating a
/// price for it. Null if this bar observed zero real ticks with nonzero volume delta of its own
/// (possible for a very short bar formed almost entirely from a large carried-in balance).
/// </param>
/// <param name="IsFinalPartialBar">
/// True only for a trading day's last, necessarily-partial bar (spec section 5) -- flushed at
/// session end regardless of whether it reached the threshold. Must never be silently treated as
/// a normal threshold-crossing bar by a consumer that doesn't check this flag.
/// </param>
public sealed record FutureEventBar(
    int EventId, DateOnly TradingDate, DateTimeOffset StartTimestamp, DateTimeOffset EndTimestamp,
    decimal Open, decimal High, decimal Low, decimal Close,
    long Volume, double? Vwap, long? OpenInterest, int TickCount, bool IsFinalPartialBar)
{
    public double DurationMs => (EndTimestamp - StartTimestamp).TotalMilliseconds;
}
