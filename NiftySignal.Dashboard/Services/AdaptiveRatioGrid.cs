using System.Globalization;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// One displayed metric of one row: the raw value and the parts of its normalized ratio (<c>Num / Den * 100</c>, only when <c>Den &gt; 0</c>).
/// Keeping the numerator and the denominator (not the finished percentage) is what lets the AVG row use SUM(num) / SUM(den) instead of averaging percentages.
/// <para><see cref="RawWeight"/>, when set, makes the AVG of the raw value a weighted mean (used for time-weighted MicroDev); otherwise the raw AVG is the arithmetic mean.</para>
/// A missing input is null and stays null: nothing here ever turns "unavailable" into zero.
/// </summary>
public readonly record struct RatioPart(double? Raw, double? Num, double? Den, double? RawWeight = null)
{
    public static readonly RatioPart None = new(null, null, null);

    /// <summary>Normalized ratio in percentage points, or null when the numerator or a positive denominator is unavailable.</summary>
    public double? Ratio => Num is { } n && Den is { } d && d > 0d && double.IsFinite(n) && double.IsFinite(d) ? n / d * 100d : null;

    /// <summary>A ratio of two integer-valued quantities. Both must be present.</summary>
    public static RatioPart Of(double? raw, double? num, double? den, double? rawWeight = null) => new(raw, num, den, rawWeight);
}

/// <summary>Display formatting shared by every "raw (ratio)" cell.</summary>
public static class RatioFormat
{
    const string Dash = "—";

    static string Num(double v, int decimals) => v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture);

    /// <summary>"6500 (72.50)", "-4200 (-51.30)", "0.42 (64.30)"; "—" when the raw value is unavailable; "raw (—)" when only the ratio is unavailable.</summary>
    public static string Cell(RatioPart p, int rawDecimals = 0)
    {
        if (p.Raw is not { } raw || !double.IsFinite(raw))
        {
            return Dash;
        }

        return p.Ratio is { } r && double.IsFinite(r) ? $"{Num(raw, rawDecimals)} ({Num(r, 2)})" : $"{Num(raw, rawDecimals)} ({Dash})";
    }

    public static string Plain(double? v, int decimals) => v is { } x && double.IsFinite(x) ? Num(x, decimals) : Dash;

    /// <summary>Colour class that preserves the visual sign of a direction / delta cell.</summary>
    public static string SignClass(RatioPart p) => p.Raw is > 0d ? "val-pos" : p.Raw is < 0d ? "val-neg" : string.Empty;
}

/// <summary>Aggregation of the VISIBLE rows into the AVG row. Ratios are SUM(numerator) / SUM(denominator), never an average of percentages.</summary>
public static class RatioAverage
{
    public static RatioPart Aggregate(IEnumerable<RatioPart> parts)
    {
        double rawSum = 0d, rawWeight = 0d, rawCount = 0d, num = 0d, den = 0d;
        var weighted = false;
        var anyRatio = false;
        foreach (var p in parts)
        {
            if (p.Raw is { } r && double.IsFinite(r))
            {
                if (p.RawWeight is { } w && w > 0d)
                {
                    weighted = true;
                    rawSum += r * w;
                    rawWeight += w;
                }
                else
                {
                    rawSum += r;
                    rawCount++;
                }
            }

            if (p.Num is { } n && p.Den is { } d && d > 0d && double.IsFinite(n) && double.IsFinite(d))
            {
                num += n;
                den += d;
                anyRatio = true;
            }
        }

        double? raw = weighted ? (rawWeight > 0d ? rawSum / rawWeight : null) : rawCount > 0d ? rawSum / rawCount : null;
        // The weighted case is a single weighted mean; when rows mix weighted and unweighted raws only the weighted ones count (a row without a weight carries no usable-time evidence).
        return anyRatio ? new RatioPart(raw, num, den) : new RatioPart(raw, null, null);
    }

    /// <summary>Plain arithmetic mean of the available values (null when none).</summary>
    public static double? Mean(IEnumerable<double?> values)
    {
        var v = values.Where(x => x is { } d && double.IsFinite(d)).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }
}

/// <summary>The Futures grid's ratio cells of one completed bar (futures supplemental v3 carries GrossOfi; a v2 fallback row leaves the OFI ratio unavailable).</summary>
public sealed record FuturesRatioRow(RatioPart Strict, RatioPart Enriched, RatioPart MicroDev, RatioPart Ofi);

public static class FuturesRatios
{
    public static FuturesRatioRow Build(AdaptiveFutureBarRow bar, AdaptiveFuturesSupplementalRow? s)
    {
        var strict = RatioPart.Of(bar.StrictDelta, bar.StrictDelta, bar.Volume);
        var enriched = RatioPart.Of(bar.EnrichedDelta, bar.EnrichedDelta, bar.EnrichedBuyVolume + bar.EnrichedSellVolume + bar.EnrichedUnknownVolume);
        if (s is null)
        {
            return new FuturesRatioRow(strict, enriched, RatioPart.None, RatioPart.None);
        }

        // MicroDev = (Spread / 2) * TOB, so TOB is the dimensionless pressure measure: ratio = TOB * 100. Both are time-weighted over the bar's usable seconds.
        var w = s.TobUsableSeconds;
        var micro = new RatioPart(s.MicroDevTimeWeighted, s.TobTimeWeighted is { } tob && w > 0d ? tob * w : null, s.TobTimeWeighted is not null && w > 0d ? w : null,
            s.MicroDevTimeWeighted is not null && s.MicroDevUsableSeconds > 0d ? s.MicroDevUsableSeconds : null);
        var ofi = RatioPart.Of(s.Ofi, s.Ofi, s.GrossOfi);
        return new FuturesRatioRow(strict, enriched, micro, ofi);
    }
}

/// <summary>One fixed-strike row's direction metrics (CE, PE and the combined direction), each as a <see cref="RatioPart"/>.</summary>
public sealed record SideDirection(RatioPart Ce, RatioPart Pe, RatioPart Direction);

public sealed record CoreDirectionRow(SideDirection Price, SideDirection Strict, SideDirection Enriched, SideDirection Oi, SideDirection MicroDev, SideDirection Ofi);

public sealed record OptionContextRow(SideDirection Iv, SideDirection Activity);

/// <summary>
/// The fixed-strike CORE DIRECTION and OPTION CONTEXT metrics. Direction is CE minus PE everywhere EXCEPT open interest, where it is PE minus CE.
/// Every direction ratio uses the COMBINED denominator of the two sides (never CE ratio minus PE ratio). A side that is missing makes its own cells and the direction unavailable.
/// </summary>
public static class OptionDirectionRatios
{
    static bool Pos(double? v) => v is { } x && x > 0d && double.IsFinite(x);

    /// <summary>Generic "delta over a base" metric (price over start price, OI over start OI, strict over total quantity ...). Direction = sign * (CE delta - PE delta) / (CE base + PE base).</summary>
    static SideDirection DeltaOverBase(double? ceDelta, double? ceBase, double? peDelta, double? peBase, double sign)
    {
        var ce = RatioPart.Of(ceDelta, ceDelta, ceBase);
        var pe = RatioPart.Of(peDelta, peDelta, peBase);
        RatioPart dir = RatioPart.None;
        if (ceDelta is { } c && peDelta is { } p)
        {
            var raw = sign * (c - p);
            dir = ceBase is { } cb && peBase is { } pb && cb >= 0d && pb >= 0d && ce.Den is not null && pe.Den is not null
                ? RatioPart.Of(raw, raw, cb + pb)
                : RatioPart.Of(raw, null, null);
        }

        return new SideDirection(ce, pe, dir);
    }

    public static CoreDirectionRow Core(AdaptiveOptionStrikeBarRow? ce, AdaptiveOptionStrikeBarRow? pe)
    {
        // Price: only strictly positive start prices are a valid denominator.
        var price = DeltaOverBase(ce?.PriceChange, Pos(ce?.PriceStart) ? ce!.PriceStart : null, pe?.PriceChange, Pos(pe?.PriceStart) ? pe!.PriceStart : null, 1d);
        var strict = DeltaOverBase(ce?.StrictDelta, ce?.TotalQuantity, pe?.StrictDelta, pe?.TotalQuantity, 1d);
        long? EnrichedTotal(AdaptiveOptionStrikeBarRow? r) => r is null ? null : r.EnrichedBuy + r.EnrichedSell + r.EnrichedUnknown;
        var enriched = DeltaOverBase(ce?.EnrichedDelta, EnrichedTotal(ce), pe?.EnrichedDelta, EnrichedTotal(pe), 1d);
        // Open interest is the one metric whose direction is PE - CE (put build-up is the bullish reading).
        var oi = DeltaOverBase(ce?.OiDelta, Pos(ce?.OiStart) ? ce!.OiStart : null, pe?.OiDelta, Pos(pe?.OiStart) ? pe!.OiStart : null, -1d);
        return new CoreDirectionRow(price, strict, enriched, oi, MicroDev(ce, pe), Ofi(ce, pe));
    }

    static RatioPart MicroSide(AdaptiveOptionStrikeBarRow? r)
    {
        if (r?.MicroDevTimeWeighted is not { } md)
        {
            return RatioPart.None;
        }

        var w = r.TobUsableSeconds;
        var tob = r.TobTimeWeighted;
        return new RatioPart(md, tob is { } t && w is > 0d ? t * w : null, tob is not null && w is > 0d ? w : null, w is > 0d ? w : null);
    }

    static SideDirection MicroDev(AdaptiveOptionStrikeBarRow? ce, AdaptiveOptionStrikeBarRow? pe)
    {
        var c = MicroSide(ce);
        var p = MicroSide(pe);
        RatioPart dir = RatioPart.None;
        if (ce?.MicroDevTimeWeighted is { } cm && pe?.MicroDevTimeWeighted is { } pm)
        {
            // Direction ratio = (CE TOB - PE TOB) / 2 * 100, weighted by the usable overlap (the shorter of the two sides' usable seconds) so the AVG row stays deterministic.
            var overlap = Math.Min(ce.TobUsableSeconds ?? 0d, pe.TobUsableSeconds ?? 0d);
            dir = ce.TobTimeWeighted is { } ct && pe.TobTimeWeighted is { } pt && overlap > 0d
                ? new RatioPart(cm - pm, (ct - pt) / 2d * overlap, overlap, overlap)
                : RatioPart.Of(cm - pm, null, null);
        }

        return new SideDirection(c, p, dir);
    }

    static SideDirection Ofi(AdaptiveOptionStrikeBarRow? ce, AdaptiveOptionStrikeBarRow? pe)
    {
        var c = RatioPart.Of(ce?.NetOfi, ce?.NetOfi, ce?.GrossOfi);
        var p = RatioPart.Of(pe?.NetOfi, pe?.NetOfi, pe?.GrossOfi);
        RatioPart dir = RatioPart.None;
        if (ce?.NetOfi is { } cn && pe?.NetOfi is { } pn)
        {
            dir = ce.GrossOfi is { } cg && pe.GrossOfi is { } pg ? RatioPart.Of(cn - pn, cn - pn, cg + pg) : RatioPart.Of(cn - pn, null, null);
        }

        return new SideDirection(c, p, dir);
    }

    public static OptionContextRow Context(AdaptiveOptionStrikeBarRow? ce, AdaptiveOptionStrikeBarRow? pe)
    {
        // IV: CE and PE ratios use their own start IV; the direction uses the AVERAGE of the two start IVs: (CE dIV - PE dIV) / ((CE IV0 + PE IV0) / 2).
        var ceIv = RatioPart.Of(ce?.IvChange, ce?.IvChange, Pos(ce?.IvStart) ? ce!.IvStart : null);
        var peIv = RatioPart.Of(pe?.IvChange, pe?.IvChange, Pos(pe?.IvStart) ? pe!.IvStart : null);
        RatioPart ivDir = RatioPart.None;
        if (ce?.IvChange is { } cd && pe?.IvChange is { } pd)
        {
            ivDir = Pos(ce.IvStart) && Pos(pe.IvStart) ? RatioPart.Of(cd - pd, cd - pd, (ce.IvStart!.Value + pe.IvStart!.Value) / 2d) : RatioPart.Of(cd - pd, null, null);
        }

        // Activity: the ratio is each side's share of the total activity; the direction is (CE - PE) / (CE + PE).
        var ceA = ce?.ActivityPerSecond;
        var peA = pe?.ActivityPerSecond;
        double? total = ceA is { } a && peA is { } b ? a + b : null;
        var ceAct = new RatioPart(ceA, ceA, total);
        var peAct = new RatioPart(peA, peA, total);
        var actDir = ceA is { } x && peA is { } y ? new RatioPart(x - y, x - y, total) : RatioPart.None;
        return new OptionContextRow(new SideDirection(ceIv, peIv, ivDir), new SideDirection(ceAct, peAct, actDir));
    }

    public static SideDirection Average(IEnumerable<SideDirection> rows, Func<SideDirection, SideDirection> select) => Average(rows.Select(select));

    public static SideDirection Average(IEnumerable<SideDirection> rows)
    {
        var list = rows.ToList();
        return new SideDirection(
            RatioAverage.Aggregate(list.Select(r => r.Ce)),
            RatioAverage.Aggregate(list.Select(r => r.Pe)),
            RatioAverage.Aggregate(list.Select(r => r.Direction)));
    }
}
