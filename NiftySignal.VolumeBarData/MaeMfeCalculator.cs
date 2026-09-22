namespace NiftySignal.VolumeBarData;

/// <summary>
/// MAE (Maximum Adverse Excursion) / MFE (Maximum Favorable Excursion) for one already-closed,
/// always-LONG trade (see <see cref="VolumeBarTrade.NetPnlPoints"/>'s own doc comment on the LONG
/// convention). Pure function over the trade's own intra-holding tick price path -- no DB access,
/// no simulation logic -- so it's separately unit-testable against a synthetic price path with a
/// known worst/best excursion, per docs/VOLUME_BAR_FINDINGS.md's existing testing conventions.
///
/// Standard definitions, both reported as a POSITIVE magnitude (bigger = more extreme), clamped at
/// zero when the position never actually moved in that direction during the window (e.g. a trade
/// whose price only ever rose has an MAE of 0, not a negative number):
///   MAE = -min(PriceAtTick_t - EntryPrice), t over every tick from the start to the end of the
///         analysis window (worst unrealized loss the position touched).
///   MFE =  max(PriceAtTick_t - EntryPrice), same window (best unrealized gain the position touched).
/// </summary>
public static class MaeMfeCalculator
{
    public readonly record struct MaeMfeResult(decimal MaePoints, decimal MfePoints, decimal MaePercent, decimal MfePercent);

    /// <param name="entryPrice">The trade's own realized entry fill price -- the reference point both excursions are measured from.</param>
    /// <param name="pricesDuringHold">Every tick price observed from the start to the end of the analysis window, in any order -- only the min/max matter.</param>
    public static MaeMfeResult Compute(decimal entryPrice, IReadOnlyList<decimal> pricesDuringHold)
    {
        if (pricesDuringHold.Count == 0 || entryPrice <= 0)
        {
            return new MaeMfeResult(0m, 0m, 0m, 0m);
        }

        var min = pricesDuringHold.Min();
        var max = pricesDuringHold.Max();

        var maePoints = Math.Max(0m, entryPrice - min);
        var mfePoints = Math.Max(0m, max - entryPrice);

        return new MaeMfeResult(maePoints, mfePoints, maePoints / entryPrice * 100m, mfePoints / entryPrice * 100m);
    }
}
