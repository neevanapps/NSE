namespace NiftySignal.AdaptiveObserver;

public static class AdaptiveRollingStateTracker
{
    public static AdaptiveRollingState? BuildLatest(IReadOnlyList<ExactAdaptiveBar> bars, int windowBars = 10)
    {
        var complete = bars.Where(x => x.IsComplete).OrderBy(x => x.BarSeq).ToList();
        if (complete.Count < windowBars)
        {
            return null;
        }

        return BuildOne(complete, complete.Count - windowBars, complete.Count - 1);
    }

    public static IReadOnlyList<AdaptiveRollingState> BuildAll(IReadOnlyList<ExactAdaptiveBar> bars, int windowBars = 10)
    {
        if (windowBars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowBars));
        }

        var complete = bars.Where(x => x.IsComplete).OrderBy(x => x.BarSeq).ToList();
        var result = new List<AdaptiveRollingState>();
        for (var end = windowBars - 1; end < complete.Count; end++)
        {
            result.Add(BuildOne(complete, end - windowBars + 1, end));
        }

        return result;
    }

    static AdaptiveRollingState BuildOne(IReadOnlyList<ExactAdaptiveBar> bars, int start, int end)
    {
        var first = bars[start];
        var last = bars[end];

        if (first.TradeDate != last.TradeDate)
        {
            throw new InvalidOperationException("Rolling state cannot cross sessions.");
        }

        long windowVolume = 0;
        long strictBuy = 0;
        long strictSell = 0;
        long strictUnknown = 0;
        long enrichedBuy = 0;
        long enrichedSell = 0;
        long enrichedUnknown = 0;
        var tradeUpdates = 0;
        var high = double.NegativeInfinity;
        var low = double.PositiveInfinity;
        var pathLength = 0d;
        var previous = first.Open;

        for (var i = start; i <= end; i++)
        {
            var b = bars[i];
            windowVolume += b.Volume;
            strictBuy += b.StrictBuyVolume;
            strictSell += b.StrictSellVolume;
            strictUnknown += b.StrictUnknownVolume;
            enrichedBuy += b.EnrichedBuyVolume;
            enrichedSell += b.EnrichedSellVolume;
            enrichedUnknown += b.EnrichedUnknownVolume;
            tradeUpdates += b.TradeUpdates;
            high = Math.Max(high, b.High);
            low = Math.Min(low, b.Low);
            pathLength += Math.Abs(b.Close - previous);
            previous = b.Close;
        }

        var displacement = last.Close - first.Open;
        var classified = strictBuy + strictSell;
        var strictDelta = strictBuy - strictSell;
        var enrichedDelta = enrichedBuy - enrichedSell;
        var efficiency = pathLength > 0 ? Math.Min(1d, Math.Abs(displacement) / pathLength) : 0d;
        var elapsed = (last.EndAvailableAtUtc - first.StartAvailableAtUtc).TotalSeconds;
        var priceDirection = Sign(displacement);
        var strictDirection = Sign(strictDelta);
        var enrichedDirection = Sign(enrichedDelta);

        long? oiChange = null;
        double? oiChangePct = null;
        if (first.OiOpen.HasValue && last.OiClose.HasValue)
        {
            oiChange = last.OiClose.Value - first.OiOpen.Value;
            if (first.OiOpen.Value != 0)
            {
                oiChangePct = (double)oiChange.Value / first.OiOpen.Value;
            }
        }

        return new AdaptiveRollingState
        {
            TradeDate = first.TradeDate,
            WindowBars = end - start + 1,
            BaseBarVolume = first.Volume,
            WindowVolume = windowVolume,
            StartBarSeq = first.BarSeq,
            EndBarSeq = last.BarSeq,
            StartAvailableAtUtc = first.StartAvailableAtUtc,
            EndAvailableAtUtc = last.EndAvailableAtUtc,
            StartPrice = first.Open,
            EndPrice = last.Close,
            High = high,
            Low = low,
            PriceDisplacement = displacement,
            ReturnBps = first.Open != 0d ? displacement / first.Open * 10_000d : 0d,
            PathLength = pathLength,
            Efficiency = efficiency,
            SignedEfficiency = priceDirection * efficiency,
            StrictBuyVolume = strictBuy,
            StrictSellVolume = strictSell,
            StrictUnknownVolume = strictUnknown,
            StrictDelta = strictDelta,
            StrictQuoteCoverage = windowVolume > 0 ? (double)classified / windowVolume : 0d,
            StrictDeltaRatioTotal = windowVolume > 0 ? (double)strictDelta / windowVolume : 0d,
            StrictDeltaRatioClassified = classified > 0 ? (double)strictDelta / classified : null,
            EnrichedBuyVolume = enrichedBuy,
            EnrichedSellVolume = enrichedSell,
            EnrichedUnknownVolume = enrichedUnknown,
            EnrichedDelta = enrichedDelta,
            EnrichedDeltaRatio = windowVolume > 0 ? (double)enrichedDelta / windowVolume : 0d,
            FallbackShare = windowVolume > 0 ? (double)(strictUnknown - enrichedUnknown) / windowVolume : 0d,
            OiStart = first.OiOpen,
            OiEnd = last.OiClose,
            OiChange = oiChange,
            OiChangePct = oiChangePct,
            ElapsedSeconds = elapsed,
            VolumePerSecond = elapsed > 0 ? windowVolume / elapsed : null,
            TradeUpdates = tradeUpdates,
            WindowRange = high - low,
            PriceDirection = priceDirection,
            StrictDeltaDirection = strictDirection,
            EnrichedDeltaDirection = enrichedDirection,
            PriceStrictDeltaAgree = Agree(priceDirection, strictDirection),
            PriceEnrichedDeltaAgree = Agree(priceDirection, enrichedDirection),
        };
    }

    static int Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : 0;
    static int Sign(long x) => x > 0 ? 1 : x < 0 ? -1 : 0;
    static bool? Agree(int a, int b) => a == 0 || b == 0 ? null : a == b;
}
