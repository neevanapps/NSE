using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Maps persisted rows into the plain inputs of <c>NiftySignal.MarketSummary</c>. Per (bar, contract) exactly one row is used - option-strike-v3, else v2, else v1 (the same precedence the fixed-strike grids
/// use) - and only completed bars up to the boundary are accepted, so nothing after the target bar can reach the calculator.
/// </summary>
public static class MarketSummaryMapper
{
    static int VersionRank(string version) =>
        version == OptionStrikeBarMetrics.MetricsVersion ? 3 : version == OptionStrikeBarMetrics.PreviousMetricsVersion ? 2 : version == OptionStrikeBarMetrics.LegacyMetricsVersion ? 1 : 0;

    public static IReadOnlyList<ContractBar> ContractBars(IEnumerable<AdaptiveOptionStrikeBarRow> rows, IReadOnlySet<string> tokens, DateOnly expiry, int throughBarSeq) =>
        rows.Where(r => r.BarSeq >= 1 && r.BarSeq <= throughBarSeq && r.Expiry == expiry && tokens.Contains(r.Token) && VersionRank(r.MetricsVersion) > 0)
            .GroupBy(r => (r.BarSeq, r.Token))
            .Select(g => g.OrderByDescending(r => VersionRank(r.MetricsVersion)).First())
            .Select(r => new ContractBar(
                r.BarSeq, r.Token, r.Side == OptionType.Call,
                r.PriceStart, r.PriceEnd,
                r.TotalQuantity, r.StrictDelta, r.EnrichedBuy, r.EnrichedSell, r.EnrichedUnknown, r.EnrichedDelta,
                r.OiStart, r.OiEnd, r.IvStart, r.IvEnd,
                r.DeltaStart, r.DeltaEnd, r.VegaStart, r.VegaEnd,
                r.TobTimeWeighted, r.MicroDevTimeWeighted, r.TobUsableSeconds,
                r.NetOfi, r.GrossOfi))
            .ToList();

    public static IReadOnlyList<ListedStrike> Listed(FixedStrikeCatalog catalog) =>
        catalog.Strikes.Select(s => new ListedStrike(s.Strike, s.CeToken, s.PeToken)).ToList();

    public static FuturesBar FuturesBarOf(AdaptiveFutureBarRow bar, AdaptiveFuturesSupplementalRow? s) => new(
        bar.BarSeq, bar.DurationSeconds, bar.Open, bar.Close, bar.Volume, bar.StrictDelta,
        bar.EnrichedBuyVolume, bar.EnrichedSellVolume, bar.EnrichedUnknownVolume, bar.EnrichedDelta, bar.OiOpen, bar.OiClose,
        s?.TobTimeWeighted, s?.MicroDevTimeWeighted, s?.TobUsableSeconds, s?.MicroDevUsableSeconds, s?.Ofi, s?.GrossOfi);
}
