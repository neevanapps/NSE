using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.AdaptiveObserver;

/// <summary>
/// One option contract (expiry + strike + side + token) over one completed adaptive bar: the per-strike observational projection behind the fixed-strike Options history.
/// Nothing here is an ATM±2 aggregate. Every value comes from the SAME session-long per-instrument flow state the band metrics use (<see cref="AdaptiveInstrumentFlowAccumulator"/>:
/// strict / enriched classification, two-sided quote mid, open interest) between the SAME bar boundaries; a value that cannot be established is null and never zero.
/// </summary>
public sealed record OptionStrikeBarMetrics(
    string Token,
    string TradingSymbol,
    DateOnly Expiry,
    double Strike,
    OptionType Side,
    double? PriceStart,
    double? PriceEnd,
    double? PriceChange,
    long TotalQuantity,
    long StrictBuy,
    long StrictSell,
    long StrictUnknown,
    long StrictDelta,
    long EnrichedBuy,
    long EnrichedSell,
    long EnrichedUnknown,
    long EnrichedDelta,
    long TradeUpdates,
    long? OiStart,
    long? OiEnd,
    long? OiDelta,
    double? IvStart,
    double? IvEnd,
    double? IvChange,
    double? ActivityPerSecond,
    double? QuoteAgeEndSeconds,
    double? TobTimeWeighted = null,
    double? MicroDevTimeWeighted = null,
    double? TobUsableSeconds = null,
    long? NetOfi = null,
    long? GrossOfi = null,
    int? OfiTransitions = null)
{
    /// <summary>
    /// Identifies the calculation contract. Change it whenever any formula, boundary or freshness rule changes.
    /// v2 = every v1 value (unchanged) + the contract's own Level-1 book metrics from the SAME per-token <see cref="FuturesMicrostructureTracker"/> the observer already feeds:
    /// time-weighted TOB / MicroDev, TOB-usable seconds, net OFI and gross OFI (SUM |e_i|). v1 rows (no book metrics) stay in place and are never rewritten.
    /// </summary>
    public const string MetricsVersion = "option-strike-v2";

    /// <summary>The previous contract (no book metrics). Still readable by the Dashboard for sessions that only have v1 rows; never written any more.</summary>
    public const string LegacyMetricsVersion = "option-strike-v1";
}

/// <summary>Pure builder of the per-strike rows of one completed bar. No state, no clock, no I/O.</summary>
public static class AdaptiveStrikeProjection
{
    /// <param name="universe">The frozen weekly option universe (every contract the observer already tracks).</param>
    /// <param name="start">Flow snapshot of every token at the bar's start boundary (the previous bar's end).</param>
    /// <param name="end">Flow snapshot of every token at the bar's end.</param>
    /// <param name="startUnderlying">Synthetic weekly underlying of the band selection at the bar's start (null = unavailable, IV then unavailable).</param>
    /// <param name="endUnderlying">Synthetic weekly underlying at the bar's end (from the next selection at the same instant).</param>
    public static IReadOnlyList<OptionStrikeBarMetrics> Build(
        IReadOnlyList<ObserverOptionInstrument> universe,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        double durationSeconds,
        double riskFreeRate,
        double? startUnderlying,
        double? endUnderlying,
        Func<string, InstrumentFlowSnapshot?> start,
        Func<string, InstrumentFlowSnapshot> end,
        Func<string, FuturesMicrostructureBar?>? book = null)
    {
        var rows = new List<OptionStrikeBarMetrics>(universe.Count);
        foreach (var i in universe.OrderBy(x => x.Strike).ThenBy(x => x.OptionType).ThenBy(x => x.Token, StringComparer.Ordinal))
        {
            var s = start(i.Token);
            var e = end(i.Token);
            long Diff(Func<InstrumentFlowSnapshot, long> f) => f(e) - (s is null ? 0L : f(s));
            var strictBuy = Diff(x => x.StrictBuyQuantity);
            var strictSell = Diff(x => x.StrictSellQuantity);
            var strictUnknown = Diff(x => x.StrictUnknownQuantity);
            var enrichedBuy = Diff(x => x.EnrichedBuyQuantity);
            var enrichedSell = Diff(x => x.EnrichedSellQuantity);
            var enrichedUnknown = Diff(x => x.EnrichedUnknownQuantity);
            var total = strictBuy + strictSell + strictUnknown;
            var priceStart = s?.Mid;
            var priceEnd = e.Mid;
            var oiStart = s?.OpenInterest;
            var oiEnd = e.OpenInterest;

            double? Iv(InstrumentFlowSnapshot? snapshot, DateTimeOffset at, double? underlying)
            {
                if (snapshot is null || underlying is not { } u || OptionsSupplementalCalculator.FreshMid(snapshot, at) is not { } mid) return null;
                var t = TimeToExpiry.YearsUntilExpiry(i.ExpiryDate, at);
                return AdaptiveResearchPricing.SolveIv(i.OptionType, mid, u, i.Strike, t, riskFreeRate) is { } iv ? iv * 100d : null;
            }

            var ivStart = Iv(s, startUtc, startUnderlying);
            var ivEnd = Iv(e, endUtc, endUnderlying);
            var micro = book?.Invoke(i.Token);       // this token's own book over the bar; null when the book was never observed
            rows.Add(new OptionStrikeBarMetrics(
                i.Token, i.TradingSymbol, i.ExpiryDate, i.Strike, i.OptionType,
                priceStart, priceEnd, priceStart is { } p0 && priceEnd is { } p1 ? p1 - p0 : null,
                total, strictBuy, strictSell, strictUnknown, strictBuy - strictSell,
                enrichedBuy, enrichedSell, enrichedUnknown, enrichedBuy - enrichedSell,
                Diff(x => x.TradeUpdates),
                oiStart, oiEnd, oiStart is { } o0 && oiEnd is { } o1 ? o1 - o0 : null,
                ivStart, ivEnd, ivStart is { } v0 && ivEnd is { } v1 ? v1 - v0 : null,
                durationSeconds > 0d ? total / durationSeconds : null,
                e.LastAvailableAt is { } at2 && at2 <= endUtc ? (endUtc - at2).TotalSeconds : null,
                micro?.TobTimeWeighted, micro?.MicroDevTimeWeighted, micro is null ? null : micro.TobUsableSeconds, micro?.Ofi, micro?.GrossOfi, micro?.OfiTransitions));
        }

        return rows;
    }
}
