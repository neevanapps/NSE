using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.AdaptiveObserver;

public static class AdaptiveOptionBandCalculator
{
    public static OptionBandSelection? Select(
        DateTimeOffset atUtc,
        DateOnly expiry,
        double futureReferencePrice,
        IReadOnlyList<ObserverOptionInstrument> chain,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> latestQuotes,
        double riskFreeRate,
        double maxQuoteAgeSeconds = 5d,
        int halfWidth = 2)
    {
        var sameExpiry = chain.Where(x => x.ExpiryDate == expiry).ToList();
        var strikes = sameExpiry.Select(x => x.Strike).Distinct().OrderBy(x => x).ToArray();
        if (strikes.Length < (halfWidth * 2) + 1)
        {
            return null;
        }

        var byKey = sameExpiry.ToDictionary(x => (x.Strike, x.OptionType));
        var near = strikes.OrderBy(k => Math.Abs(k - futureReferencePrice)).ThenBy(k => k).Take(5).ToArray();
        var pairs = new List<(double Strike, double CallMid, double PutMid)>();
        foreach (var strike in near)
        {
            if (!byKey.TryGetValue((strike, OptionType.Call), out var call)
                || !byKey.TryGetValue((strike, OptionType.Put), out var put)
                || !FreshMid(call.Token, atUtc, latestQuotes, maxQuoteAgeSeconds, out var cm)
                || !FreshMid(put.Token, atUtc, latestQuotes, maxQuoteAgeSeconds, out var pm))
            {
                continue;
            }

            pairs.Add((strike, cm, pm));
        }

        if (pairs.Count < 3)
        {
            return null;
        }

        var t = TimeToExpiry.YearsUntilExpiry(expiry, atUtc);
        var synthetic = SyntheticForward.Compute(pairs, t, riskFreeRate);
        if (synthetic is null)
        {
            return null;
        }

        var center = strikes.OrderBy(k => Math.Abs(k - synthetic.Value)).ThenBy(k => k).First();
        var centerIndex = Array.IndexOf(strikes, center);
        if (centerIndex < halfWidth || centerIndex + halfWidth >= strikes.Length)
        {
            return null;
        }

        var selectedStrikes = strikes[(centerIndex - halfWidth)..(centerIndex + halfWidth + 1)];
        var calls = new List<ObserverOptionInstrument>();
        var puts = new List<ObserverOptionInstrument>();
        foreach (var strike in selectedStrikes)
        {
            if (!byKey.TryGetValue((strike, OptionType.Call), out var call)
                || !byKey.TryGetValue((strike, OptionType.Put), out var put))
            {
                return null;
            }

            calls.Add(call);
            puts.Add(put);
        }

        return new OptionBandSelection(atUtc, expiry, synthetic.Value, center, selectedStrikes, calls, puts);
    }

    public static OptionBandSideMetrics BuildSideMetrics(
        OptionBandSelection selection,
        OptionType side,
        DateTimeOffset endUtc,
        IReadOnlyDictionary<string, InstrumentFlowSnapshot> start,
        IReadOnlyDictionary<string, InstrumentFlowSnapshot> end)
    {
        var instruments = side == OptionType.Call ? selection.Calls : selection.Puts;
        if (side is not (OptionType.Call or OptionType.Put))
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }

        var starts = instruments.Select(x => start.GetValueOrDefault(x.Token) ?? Empty(x.Token)).ToArray();
        var ends = instruments.Select(x => end.GetValueOrDefault(x.Token) ?? Empty(x.Token)).ToArray();

        var strictBuy = Diff(starts, ends, x => x.StrictBuyQuantity);
        var strictSell = Diff(starts, ends, x => x.StrictSellQuantity);
        var strictUnknown = Diff(starts, ends, x => x.StrictUnknownQuantity);
        var enrichedBuy = Diff(starts, ends, x => x.EnrichedBuyQuantity);
        var enrichedSell = Diff(starts, ends, x => x.EnrichedSellQuantity);
        var enrichedUnknown = Diff(starts, ends, x => x.EnrichedUnknownQuantity);

        var nStrictBuy = Diff(starts, ends, x => x.StrictBuyNotional);
        var nStrictSell = Diff(starts, ends, x => x.StrictSellNotional);
        var nStrictUnknown = Diff(starts, ends, x => x.StrictUnknownNotional);
        var nEnrichedBuy = Diff(starts, ends, x => x.EnrichedBuyNotional);
        var nEnrichedSell = Diff(starts, ends, x => x.EnrichedSellNotional);
        var nEnrichedUnknown = Diff(starts, ends, x => x.EnrichedUnknownNotional);

        var total = strictBuy + strictSell + strictUnknown;
        var classified = strictBuy + strictSell;
        var enrichedTotal = enrichedBuy + enrichedSell + enrichedUnknown;
        var notionalTotal = nStrictBuy + nStrictSell + nStrictUnknown;
        var nClassified = nStrictBuy + nStrictSell;

        var openIndex = SumMids(starts);
        var closeIndex = SumMids(ends);
        var oiOpen = SumOi(starts);
        var oiClose = SumOi(ends);
        var premiumNotionalOiOpen = SumPremiumNotionalOi(instruments, starts);
        var premiumNotionalOiClose = SumPremiumNotionalOi(instruments, ends);

        return new OptionBandSideMetrics(
            side,
            selection.CenterStrike,
            selection.Strikes,
            openIndex,
            closeIndex,
            openIndex.HasValue && closeIndex.HasValue ? closeIndex.Value - openIndex.Value : null,
            total,
            strictBuy,
            strictSell,
            strictUnknown,
            strictBuy - strictSell,
            total > 0 ? (double)(strictBuy - strictSell) / total : 0d,
            total > 0 ? (double)classified / total : 0d,
            enrichedBuy,
            enrichedSell,
            enrichedUnknown,
            enrichedBuy - enrichedSell,
            enrichedTotal > 0 ? (double)(enrichedBuy - enrichedSell) / enrichedTotal : 0d,
            notionalTotal,
            nStrictBuy,
            nStrictSell,
            nStrictUnknown,
            nStrictBuy - nStrictSell,
            notionalTotal > 0d ? (nStrictBuy - nStrictSell) / notionalTotal : 0d,
            notionalTotal > 0d ? nClassified / notionalTotal : 0d,
            nEnrichedBuy,
            nEnrichedSell,
            nEnrichedUnknown,
            nEnrichedBuy - nEnrichedSell,
            nEnrichedBuy+nEnrichedSell+nEnrichedUnknown > 0d
                ? (nEnrichedBuy-nEnrichedSell)/(nEnrichedBuy+nEnrichedSell+nEnrichedUnknown)
                : 0d,
            Diff(starts, ends, x => x.TradeUpdates),
            MaxQuoteAge(ends, endUtc),
            oiOpen,
            oiClose,
            oiOpen.HasValue && oiClose.HasValue ? oiClose.Value - oiOpen.Value : null,
            oiOpen is > 0 && oiClose.HasValue ? (double)(oiClose.Value - oiOpen.Value) / oiOpen.Value : null,
            premiumNotionalOiOpen,
            premiumNotionalOiClose,
            premiumNotionalOiOpen.HasValue && premiumNotionalOiClose.HasValue
                ? premiumNotionalOiClose.Value - premiumNotionalOiOpen.Value
                : null);
    }

    static bool FreshMid(
        string token,
        DateTimeOffset at,
        IReadOnlyDictionary<string, OptionQuoteSnapshot> quotes,
        double maxAgeSeconds,
        out double mid)
    {
        mid = 0d;
        if (!quotes.TryGetValue(token, out var q) || q.AvailableAt > at)
        {
            return false;
        }

        if ((at - q.AvailableAt).TotalSeconds > maxAgeSeconds || q.Mid is not { } value)
        {
            return false;
        }

        mid = value;
        return true;
    }

    static InstrumentFlowSnapshot Empty(string token) =>
        new(token,null,null,null,null,null,0,0,0,0,0,0,0,0,0,0,0,0,0);

    static long Diff(InstrumentFlowSnapshot[] a, InstrumentFlowSnapshot[] b, Func<InstrumentFlowSnapshot,long> f) =>
        b.Sum(f) - a.Sum(f);

    static double Diff(InstrumentFlowSnapshot[] a, InstrumentFlowSnapshot[] b, Func<InstrumentFlowSnapshot,double> f) =>
        b.Sum(f) - a.Sum(f);

    static double? SumMids(IEnumerable<InstrumentFlowSnapshot> xs)
    {
        var vals = xs.Select(x => x.Mid).ToArray();
        return vals.All(x => x.HasValue) ? vals.Sum(x => x!.Value) : null;
    }

    static long? SumOi(IEnumerable<InstrumentFlowSnapshot> xs)
    {
        var vals = xs.Select(x => x.OpenInterest).ToArray();
        return vals.All(x => x.HasValue) ? vals.Sum(x => x!.Value) : null;
    }

    static double? SumPremiumNotionalOi(
        IReadOnlyList<ObserverOptionInstrument> instruments,
        IReadOnlyList<InstrumentFlowSnapshot> snapshots)
    {
        if (instruments.Count != snapshots.Count)
        {
            throw new ArgumentException("Instrument and snapshot counts must match.");
        }

        double total = 0d;
        for (var i = 0; i < instruments.Count; i++)
        {
            var snapshot = snapshots[i];
            if (!snapshot.OpenInterest.HasValue || snapshot.Mid is not { } mid)
            {
                return null;
            }

            total += snapshot.OpenInterest.Value * instruments[i].LotSize * mid;
        }

        return total;
    }

    static double? MaxQuoteAge(IEnumerable<InstrumentFlowSnapshot> xs, DateTimeOffset endUtc)
    {
        var times = xs.Select(x => x.LastAvailableAt).ToArray();
        if (!times.All(x => x.HasValue))
        {
            return null;
        }

        return times.Max(x => Math.Max(0d, (endUtc - x!.Value).TotalSeconds));
    }
}
