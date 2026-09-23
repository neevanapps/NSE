namespace NiftySignal.Features;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 "tick activity + option premium SMA/EMA research" task --
/// see docs/VOLUME_BAR_FINDINGS.md's dated section). Pure per-bar transform from a bar's own raw
/// OHLC/TickCount/Volume/DurationSeconds into the candidate tick-activity feature set the task's
/// Part 2 specifies. Deliberately a pure function of ONE bar's already-closed values -- no rolling
/// state, no look-ahead, mirrors the "raw per-bar value, consumer applies its own rolling window"
/// separation <see cref="VolumeBar"/>'s own doc comment describes for TrendReversion-style metrics.
///
/// Not wired into any trading path -- <see cref="NiftySignal.Rules.EntryRuleEvaluator"/> and
/// <see cref="Host.LiveTradingEngine"/> (off-limits per this task) never call this. Consumed only by
/// NiftySignal.VolumeBarData's research CLI commands.
///
/// IMPORTANT CAVEAT (see <see cref="VolumeBar.TickCount"/>'s own doc comment, already established
/// before this class was written): TickCount is feed-message density (every raw
/// <see cref="NiftySignal.Domain.Entities.Tick"/> row observed, trade/touchline/depth-only
/// undiscriminated), NOT a count of discrete executed trades. Every feature below built from
/// TickCount inherits that caveat -- "tick density"/"tick velocity", never "trade density"/"trade
/// velocity", in any report of these numbers.
/// </summary>
public static class TickActivityFeatures
{
    public readonly record struct Result(
        // B
        double? TickDensity, double? VolumePerTick,
        // C
        double? TickVelocity, double? VolumeVelocity,
        // D
        decimal NetMove, decimal AbsNetMove, double? PriceEfficiency, double? RangeEfficiency,
        // E
        double? PricePerVolume,
        // F
        double? Churn,
        // G
        int Direction, double? SignedEfficiency);

    /// <summary>
    /// Computes the full Part 2 feature set from one bar's raw fields. All ratios guard their
    /// denominator explicitly and return null rather than fabricate a value (NaN/Infinity) when the
    /// denominator is zero or the numerator input is unavailable -- same "no fabricated reading"
    /// discipline <see cref="PriceCrossoverEngine"/>'s warm-up window already follows.
    /// </summary>
    public static Result Compute(decimal open, decimal high, decimal low, decimal close, long volume, int tickCount, double durationSeconds)
    {
        const double epsilon = 1e-9;

        double? tickDensity = volume > 0 ? tickCount / (double)volume : null;
        double? volumePerTick = tickCount > 0 ? volume / (double)tickCount : null;

        double? tickVelocity = durationSeconds > 0 ? tickCount / durationSeconds : null;
        double? volumeVelocity = durationSeconds > 0 ? volume / durationSeconds : null;

        var netMove = close - open;
        var absNetMove = Math.Abs(netMove);
        double? priceEfficiency = tickCount > 0 ? (double)absNetMove / tickCount : null;
        double? rangeEfficiency = tickCount > 0 ? (double)(high - low) / tickCount : null;

        double? pricePerVolume = volume > 0 ? (double)absNetMove / volume : null;

        var range = high - low;
        // F: Churn -- how much the bar wandered (High-Low) relative to how far it actually ended up
        // moving (|Close-Open|). High churn = lots of range covered for little net displacement.
        // Deliberately called a "churn/absorption PROXY", not "absorption" -- Part 6 is the only
        // place that investigates whether it actually behaves like absorption.
        double? churn = Math.Max((double)range, epsilon) / Math.Max((double)absNetMove, epsilon);

        var direction = netMove > 0 ? 1 : netMove < 0 ? -1 : 0;
        double? signedEfficiency = tickCount > 0 ? (double)netMove / tickCount : null;

        return new Result(
            tickDensity, volumePerTick,
            tickVelocity, volumeVelocity,
            netMove, absNetMove, priceEfficiency, rangeEfficiency,
            pricePerVolume,
            churn,
            direction, signedEfficiency);
    }
}
