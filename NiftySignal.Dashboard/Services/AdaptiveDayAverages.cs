using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

/// <summary>The Futures grid's AVG / DAY AVG row. Continuous raws are arithmetic means, ratios are SUM(numerator)/SUM(denominator), MicroDev is usable-seconds weighted; missing values are excluded, never zero.</summary>
public sealed record FuturesAvgRow(double? Duration, double? Urgency, double? BarPx, double? RollPx, RatioPart Strict, RatioPart Enriched, RatioPart MicroDev, RatioPart Ofi, double? RollOi, double? Basis, double? Efficiency);

public static class FuturesAverages
{
    /// <summary>One implementation for the visible-rows AVG and the DAY AVG so the two can never use different semantics.</summary>
    public static FuturesAvgRow Compute(
        IReadOnlyList<AdaptiveFuturesGridRow> rows,
        Func<int, AdaptiveFuturesSupplementalRow?> supplemental,
        Func<int, AdaptiveBasisSupplementalRow?> basis)
    {
        var ratios = rows.Select(x => FuturesRatios.Build(x.Bar, supplemental(x.Bar.BarSeq))).ToList();
        return new FuturesAvgRow(
            RatioAverage.Mean(rows.Select(x => (double?)x.Bar.DurationSeconds)),
            RatioAverage.Mean(rows.Select(x => AdaptiveGridProjection.Urgency(x.Bar.Volume, x.Bar.DurationSeconds))),
            RatioAverage.Mean(rows.Select(x => (double?)x.Bar.BarPriceDisplacement)),
            RatioAverage.Mean(rows.Select(x => (double?)x.Rolling?.PriceDisplacement)),
            RatioAverage.Aggregate(ratios.Select(r => r.Strict)),
            RatioAverage.Aggregate(ratios.Select(r => r.Enriched)),
            RatioAverage.Aggregate(ratios.Select(r => r.MicroDev)),
            RatioAverage.Aggregate(ratios.Select(r => r.Ofi)),
            RatioAverage.Mean(rows.Select(x => (double?)x.Rolling?.OiChange)),
            RatioAverage.Mean(rows.Select(x => basis(x.Bar.BarSeq)?.DeltaBasis)),
            RatioAverage.Mean(rows.Select(x => (double?)x.Rolling?.Efficiency)));
    }
}

/// <summary>Aggregates of the fixed-strike rows (CORE DIRECTION metrics and OPTION CONTEXT metrics). Pure: the rows decide the identity (one session / expiry / strike / CE token / PE token).</summary>
public sealed record OptionsCoreAverage(SideDirection Strict, SideDirection Enriched, SideDirection Oi, SideDirection MicroDev, SideDirection Ofi);

public sealed record OptionsContextAverage(SideDirection Price, SideDirection RollPrice, SideDirection Iv, SideDirection Activity);

public static class OptionAverages
{
    public static OptionsCoreAverage Core(IEnumerable<CoreDirectionRow> rows)
    {
        var l = rows.ToList();
        return new OptionsCoreAverage(
            OptionDirectionRatios.Average(l.Select(x => x.Strict)),
            OptionDirectionRatios.Average(l.Select(x => x.Enriched)),
            OptionDirectionRatios.Average(l.Select(x => x.Oi)),
            OptionDirectionRatios.Average(l.Select(x => x.MicroDev)),
            OptionDirectionRatios.Average(l.Select(x => x.Ofi)));
    }

    public static OptionsContextAverage Context(IEnumerable<CoreDirectionRow> core, IEnumerable<OptionContextRow> context)
    {
        var c = core.ToList();
        var x = context.ToList();
        return new OptionsContextAverage(
            OptionDirectionRatios.Average(c.Select(r => r.Price)),
            OptionDirectionRatios.Average(c.Select(r => r.RollPrice)),
            OptionDirectionRatios.Average(x.Select(r => r.Iv)),
            OptionDirectionRatios.Average(x.Select(r => r.Activity)));
    }

    /// <summary>Aggregate of ALL rows of a fixed-strike history (the caller has already bounded it to the target boundary).</summary>
    public static (OptionsCoreAverage Core, OptionsContextAverage Context) Of(FixedStrikeHistory history)
    {
        var core = history.Rows.Select(OptionDirectionRatios.Core).ToList();
        var ctx = history.Rows.Select(r => OptionDirectionRatios.Context(r.Ce, r.Pe)).ToList();
        return (Core(core), Context(core, ctx));
    }
}

/// <summary>The residual grid's numeric AVG / DAY AVG (categorical Direction / Relation are not aggregated). Unavailable rows are excluded, never counted as zero.</summary>
public sealed record ResidualAverage(int AvailableRows, double? FutureChange, double? CePct, double? CeDeltaPct, double? PePct, double? PeDeltaPct, double? DirectionalPct, double? AdjacentDirectionalDelta, double? StraddlePct, double? QuoteAge);

public static class ResidualAverages
{
    public static ResidualAverage Compute(
        IReadOnlyCollection<AdaptiveOptionResidualBarRow> allRows, ResidualVariant variant, IReadOnlyCollection<AdaptiveResidualAnchorComponentRow> anchors, int? throughBarSeq = null)
    {
        var rows = allRows.Where(x => x.Variant == variant && (throughBarSeq is null || x.BarSeq <= throughBarSeq)).ToList();
        var chosen = variant == ResidualVariant.Atm ? anchors.Where(x => x.IsCenterStrike).ToList() : anchors.ToList();
        var bases = new ResidualBases(
            chosen.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Call).Sum(x => x.Price0930),
            chosen.Where(x => x.Side == NiftySignal.Domain.Enums.OptionType.Put).Sum(x => x.Price0930));
        var derived = AdaptiveResidualProjection.Derive(
            rows.Select(x => new ResidualReadingPoint(x.BarSeq, x.IsAvailable, x.CEResidual, x.PEResidual, x.CEResidualPct, x.PEResidualPct, x.DirectionalResidualPct)), bases)
            .ToDictionary(x => x.BarSeq);
        var av = rows.Where(r => r.IsAvailable).ToList();
        var dv = av.Select(r => derived.GetValueOrDefault(r.BarSeq)).ToList();
        return new ResidualAverage(
            av.Count,
            RatioAverage.Mean(av.Select(r => (double?)r.FutureChangeFrom0930)),
            RatioAverage.Mean(av.Select(r => (double?)r.CEResidualPct)),
            RatioAverage.Mean(dv.Select(d => d?.CeResidualDeltaPct)),
            RatioAverage.Mean(av.Select(r => (double?)r.PEResidualPct)),
            RatioAverage.Mean(dv.Select(d => d?.PeResidualDeltaPct)),
            RatioAverage.Mean(av.Select(r => (double?)r.DirectionalResidualPct)),
            RatioAverage.Mean(dv.Select(d => d?.AdjacentDirectionalResidualDelta)),
            RatioAverage.Mean(dv.Select(d => d?.StraddleResidualPct)),
            RatioAverage.Mean(av.Select(r => (double?)r.MaxQuoteAgeSeconds)));
    }
}

/// <summary>
/// Everything the DAY AVG rows need: all completed bars of the SAME session from the start through <see cref="ThroughBarSeq"/> (never beyond), the sidecars of those bars, the exact selected fixed-strike
/// history (null when no strike is selected) and the residual rows of those bars. Read-only, derived from persisted rows, never persisted.
/// </summary>
public sealed record DayAverageData(
    long SessionId,
    int ThroughBarSeq,
    int BarCount,
    double? Strike,
    IReadOnlyList<AdaptiveFuturesGridRow> Futures,
    IReadOnlyDictionary<int, AdaptiveFuturesSupplementalRow> FuturesSupplemental,
    IReadOnlyDictionary<int, AdaptiveBasisSupplementalRow> Basis,
    FixedStrikeHistory? Fixed,
    IReadOnlyList<AdaptiveOptionResidualBarRow> Residuals,
    IReadOnlyList<AdaptiveResidualAnchorComponentRow> Anchors)
{
    public string Key => $"{SessionId}|{ThroughBarSeq}|{Strike}";

    public FuturesAvgRow FuturesAverage() => FuturesAverages.Compute(Futures, s => FuturesSupplemental.GetValueOrDefault(s), s => Basis.GetValueOrDefault(s));

    public ResidualAverage ResidualAverage(ResidualVariant variant) => ResidualAverages.Compute(Residuals, variant, Anchors, ThroughBarSeq);
}
