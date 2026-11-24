using NiftySignal.AdaptiveObserverData;
using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Presentation of the Top-5 depth sidecars (futures-depth5-v1 / options-depth5-v1). Pure functions of persisted rows: a missing, withheld or unattested row is an UNAVAILABLE cell (RatioPart.None), never zero, never another bar or strike.
/// All new signs are hypotheses, not established directional rules. MicroDev5Proxy follows the same display convention as MicroDev (inverted once, at render time, by <see cref="MicroDevDisplay"/>); OFI5 is shown raw.
/// </summary>
public static class Depth5Ratios
{
    /// <summary>Net OFI5 over gross OFI5 (ratio = net / gross x 100). Null net (no valid transition inside the bar) is unavailable.</summary>
    public static RatioPart Ofi5(long? net, long? gross) => RatioPart.Of(net, net, gross);

    /// <summary>MicroDev5Proxy = HS1 x Tob5, time-weighted over the bar's usable seconds; ratio = Tob5 x 100 (same shape as the Level-1 MicroDev). RAW (the caller inverts for display).</summary>
    public static RatioPart MicroDev5(double? microDev5, double? tob5, double usableSeconds)
    {
        if (microDev5 is not { } md || !(usableSeconds > 0d)) return RatioPart.None;
        return new RatioPart(md, tob5 is { } t ? t * usableSeconds : null, tob5 is not null ? usableSeconds : null, usableSeconds);
    }

    public static RatioPart Ofi5(AdaptiveFuturesDepth5Row? r) => r is null ? RatioPart.None : Ofi5(r.Ofi5, r.GrossOfi5);

    public static RatioPart MicroDev5(AdaptiveFuturesDepth5Row? r) => r is null ? RatioPart.None : MicroDev5(r.MicroDev5TimeWeighted, r.Tob5TimeWeighted, r.Depth5UsableSeconds);

    /// <summary>CE, PE and the CE - PE direction of OFI5, built from the COMBINED denominator exactly like the Level-1 OFI direction.</summary>
    public static SideDirection OptionOfi5(AdaptiveOptionsDepth5Row? ce, AdaptiveOptionsDepth5Row? pe)
    {
        var c = Ofi5(ce?.Ofi5, ce?.GrossOfi5);
        var p = Ofi5(pe?.Ofi5, pe?.GrossOfi5);
        RatioPart dir = RatioPart.None;
        if (ce?.Ofi5 is { } cn && pe?.Ofi5 is { } pn)
            dir = ce.GrossOfi5 is { } cg && pe.GrossOfi5 is { } pg ? RatioPart.Of(cn - pn, cn - pn, cg + pg) : RatioPart.Of(cn - pn, null, null);
        return new SideDirection(c, p, dir);
    }

    /// <summary>CE, PE and direction of MicroDev5Proxy (RAW; invert for display). Direction ratio = (CE Tob5 - PE Tob5) / 2 x 100 weighted by the usable overlap, like the Level-1 MicroDev direction.</summary>
    public static SideDirection OptionMicroDev5(AdaptiveOptionsDepth5Row? ce, AdaptiveOptionsDepth5Row? pe)
    {
        var c = ce is null ? RatioPart.None : MicroDev5(ce.MicroDev5TimeWeighted, ce.Tob5TimeWeighted, ce.Depth5UsableSeconds);
        var p = pe is null ? RatioPart.None : MicroDev5(pe.MicroDev5TimeWeighted, pe.Tob5TimeWeighted, pe.Depth5UsableSeconds);
        RatioPart dir = RatioPart.None;
        if (ce?.MicroDev5TimeWeighted is { } cm && pe?.MicroDev5TimeWeighted is { } pm)
        {
            var overlap = Math.Min(ce.Depth5UsableSeconds, pe.Depth5UsableSeconds);
            dir = ce.Tob5TimeWeighted is { } ct && pe.Tob5TimeWeighted is { } pt && overlap > 0d
                ? new RatioPart(cm - pm, (ct - pt) / 2d * overlap, overlap, overlap)
                : RatioPart.Of(cm - pm, null, null);
        }

        return new SideDirection(c, p, dir);
    }

    public static DepthBar? ToDepthBar(AdaptiveFuturesDepth5Row? r) => r is null ? null : new DepthBar(r.BarSeq, r.Depth5UsableSeconds, r.BidNotionalTimeWeighted, r.AskNotionalTimeWeighted);

    public static DepthBar ToDepthBar(AdaptiveOptionsDepth5Row r) => new(r.BarSeq, r.Depth5UsableSeconds, r.BidNotionalTimeWeighted, r.AskNotionalTimeWeighted);

    /// <summary>Whether a fixed-strike bar's OFI5 evidence was direct only, or included bridged transitions across L1Superseded markers (diagnostics: "d/b" counts).</summary>
    public static string EvidenceText(int direct, int bridged) => bridged == 0 ? $"{direct} direct" : $"{direct} direct + {bridged} bridged";
}

/// <summary>The new compact-grid futures columns of an AVG / DAY AVG row.</summary>
public sealed record Depth5AvgRow(RatioPart Ofi5, RatioPart MicroDev5, double? AgreeStrictPct, double? AgreeEnrichedPct, int AgreeStrictBars, int AgreeEnrichedBars, double? FallbackSharePct, int DepthBars);

public static class Depth5Averages
{
    /// <summary>
    /// OFI5 = SUM(net)/SUM(gross) over the bars that have a row; MicroDev5Proxy is usable-seconds weighted; Price-Flow Agreement is the SHARE of the bars that have a defined value (true / defined); Fallback Share is the plain mean.
    /// Bars without a row are excluded and counted nowhere as zero.
    /// </summary>
    public static Depth5AvgRow Compute(IReadOnlyList<AdaptiveFuturesGridRow> rows, Func<int, AdaptiveFuturesDepth5Row?> depth)
    {
        var d = rows.Select(x => depth(x.Bar.BarSeq)).ToList();
        var strict = rows.Select(x => x.Rolling?.PriceStrictDeltaAgree).Where(v => v is not null).Select(v => v!.Value).ToList();
        var enriched = rows.Select(x => x.Rolling?.PriceEnrichedDeltaAgree).Where(v => v is not null).Select(v => v!.Value).ToList();
        return new Depth5AvgRow(
            RatioAverage.Aggregate(d.Select(Depth5Ratios.Ofi5)),
            RatioAverage.Aggregate(d.Select(Depth5Ratios.MicroDev5)),
            strict.Count == 0 ? null : 100d * strict.Count(v => v) / strict.Count,
            enriched.Count == 0 ? null : 100d * enriched.Count(v => v) / enriched.Count,
            strict.Count, enriched.Count,
            RatioAverage.Mean(rows.Select(x => x.Rolling is { } r ? (double?)(r.FallbackShare * 100d) : null)),
            d.Count(x => x is not null));
    }
}
