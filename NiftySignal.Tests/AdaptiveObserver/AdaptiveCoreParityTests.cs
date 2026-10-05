using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveCoreParityTests
{
    [Fact]
    public void TickCleaner_DeduplicatesBeforeCausalOrdering_AndClampsAvailability()
    {
        var t0 = DateTimeOffset.Parse("2026-09-25T03:45:00Z");
        var raw = new[]
        {
            new ObserverRawTick(1, t0.AddSeconds(2), t0.AddSeconds(1), 100, 99, 101, 10, 12, 1000, 500),
            new ObserverRawTick(2, t0.AddSeconds(2), t0.AddSeconds(1.5), 100, 99, 101, 10, 12, 1000, 500),
            new ObserverRawTick(3, t0.AddSeconds(1), t0.AddSeconds(3), 101, 100, 102, 11, 13, 1010, 501),
        };

        var result = AdaptiveTickCleaner.Clean(raw);

        Assert.Equal(3, result.RawRows);
        Assert.Equal(1, result.DuplicatesDropped);
        Assert.Equal(1, result.ClampedTimestamps);
        Assert.Equal([1L, 3L], result.Ticks.Select(x => x.Id).ToArray());
        Assert.Equal(t0.AddSeconds(2), result.Ticks[0].AvailableAt);
    }

    [Fact]
    public void Enricher_UsesCumVolumeMaximum_StrictQuote_ThenTickFallback()
    {
        var t = DateTimeOffset.Parse("2026-09-25T03:45:00Z");
        var e = new AdaptiveTradeFlowEnricher();

        Assert.Equal(0, e.Process(new CleanObserverTick(1,t,t,100,99,101,10,10,1000,100)).TradeVolume);

        var buy = e.Process(new CleanObserverTick(2,t.AddSeconds(1),t.AddSeconds(1),101,100,102,10,10,1010,100));
        Assert.Equal(10, buy.TradeVolume);
        Assert.Equal(ObserverTradeSide.Buy, buy.StrictSide); // previous ask was 101

        var decrease = e.Process(new CleanObserverTick(3,t.AddSeconds(2),t.AddSeconds(2),102,0,0,0,0,1005,101));
        Assert.Equal(0, decrease.TradeVolume);

        var fallback = e.Process(new CleanObserverTick(4,t.AddSeconds(3),t.AddSeconds(3),103,0,0,0,0,1020,101));
        Assert.Equal(10, fallback.TradeVolume); // max was 1010, not the decreased 1005
        Assert.Equal(ObserverTradeSide.Unknown, fallback.StrictSide);
        Assert.Equal(ObserverTradeSide.Buy, fallback.EnrichedSide);
        Assert.Equal(1, e.VolumeDecreases);
    }

    [Fact]
    public void ExactBuilder_SplitsCrossingUpdate_AndKeepsCompleteBarsExact()
    {
        var day = new DateOnly(2026, 9, 25);
        var session = new AdaptiveSessionDefinition(day, "FUT", "NIFTYFUT", day.AddDays(4), 65, 100);
        var b = new ExactAdaptiveFuturesBarBuilder(session);
        var t = DateTimeOffset.Parse("2026-09-25T03:45:00Z");

        b.Add(new EnrichedObserverTick(
            new CleanObserverTick(1,t,t,100,99,101,10,10,1000,1000),
            0, ObserverTradeSide.Unknown, ObserverTradeSide.Unknown));

        var completed = b.Add(new EnrichedObserverTick(
            new CleanObserverTick(2,t.AddSeconds(1),t.AddSeconds(1),101,100,101,10,10,1250,1010),
            250, ObserverTradeSide.Buy, ObserverTradeSide.Buy));

        Assert.Equal(2, completed.Count);
        Assert.All(completed, x => Assert.Equal(100, x.Volume));
        Assert.Equal(50, b.PartialVolume);
        Assert.Equal(1, b.SplitUpdates);
        Assert.Equal(3, b.MaxChunksPerUpdate);

        var partial = b.FlushPartial();
        Assert.NotNull(partial);
        Assert.False(partial!.IsComplete);
        Assert.Equal(50, partial.Volume);
        Assert.Equal(250, b.Bars.Sum(x => x.Volume));
    }

    [Fact]
    public void RollingState_UsesExactlyTenCompletedBars()
    {
        var bars = Enumerable.Range(1, 10)
            .Select(i => MakeBar(i, open:100+i-1, close:100+i, strictBuy:70, strictSell:20, strictUnknown:10))
            .ToList();

        var state = AdaptiveRollingStateTracker.BuildLatest(bars);

        Assert.NotNull(state);
        Assert.Equal(10, state!.WindowBars);
        Assert.Equal(1000, state.WindowVolume);
        Assert.Equal(500, state.StrictDelta);
        Assert.Equal(0.5, state.StrictDeltaRatioTotal, 12);
        Assert.Equal(0.9, state.StrictQuoteCoverage, 12);
        Assert.Equal(10d, state.PriceDisplacement, 12);
        Assert.Equal(100d, state.VolumePerSecond!.Value, 12);
    }

    [Fact]
    public void Weak2_AllowsWeakeningRowsToRemainAboveStrongThreshold()
    {
        var states = new List<AdaptiveFlowState>
        {
            MakeFlowState(10, 0.30, 1, 1, null),
            MakeFlowState(11, 0.26, 1, 1, "Weakening"),
            MakeFlowState(12, 0.22, 1, 1, "Weakening"),
        };

        AdaptiveWeak2Classifier.Apply(states, 0.20);

        Assert.True(states[0].IsStrong);
        Assert.True(states[1].IsStrong);
        Assert.True(states[2].IsStrong);
        Assert.Equal(AdaptiveStateKind.Strong, states[0].State);
        Assert.Equal(AdaptiveStateKind.Weak1, states[1].State);
        Assert.Equal(AdaptiveStateKind.Weak2, states[2].State);
        Assert.Equal(2, states[2].WeakeningSequence);
    }

    [Fact]
    public void Weak2_OverlappingStrongBases_AreBothDetected()
    {
        var states = new List<AdaptiveFlowState>
        {
            MakeFlowState(10, 0.30, 1, 1, null),
            MakeFlowState(11, 0.28, 1, 1, "Weakening"),
            MakeFlowState(12, 0.26, 1, 1, "Weakening"),
            MakeFlowState(13, 0.24, 1, 1, "Weakening"),
        };

        AdaptiveWeak2Classifier.Apply(states, 0.20);

        Assert.Equal(AdaptiveStateKind.Weak2, states[2].State);
        Assert.Equal(10, states[2].StrongBaseBarSeq);
        Assert.Equal(11, states[2].Weak1BarSeq);

        Assert.Equal(AdaptiveStateKind.Weak2, states[3].State);
        Assert.Equal(11, states[3].StrongBaseBarSeq);
        Assert.Equal(12, states[3].Weak1BarSeq);
    }

    [Fact]
    public void OpeningProjection_RoundsToFiftyLotBlock()
    {
        var selected = OpeningVolumeProjectionV1.SelectBaseBarVolume(303_940, 65);
        Assert.Equal(16_250, selected);
        Assert.Equal(0, selected % (65 * 50));
    }

    static ExactAdaptiveBar MakeBar(int seq, double open, double close, long strictBuy, long strictSell, long strictUnknown) =>
        new()
        {
            TradeDate = new DateOnly(2026,9,25),
            BarSeq = seq,
            Symbol = "NIFTYFUT",
            IsComplete = true,
            StartAvailableAtUtc = DateTimeOffset.Parse("2026-09-25T04:00:00Z").AddSeconds(seq-1),
            EndAvailableAtUtc = DateTimeOffset.Parse("2026-09-25T04:00:00Z").AddSeconds(seq),
            Open = open,
            High = Math.Max(open, close),
            Low = Math.Min(open, close),
            Close = close,
            Vwap = (open+close)/2,
            Volume = 100,
            TradeUpdates = 1,
            FirstSourceTickId = seq,
            LastSourceTickId = seq,
            StrictBuyVolume = strictBuy,
            StrictSellVolume = strictSell,
            StrictUnknownVolume = strictUnknown,
            EnrichedBuyVolume = strictBuy,
            EnrichedSellVolume = strictSell,
            EnrichedUnknownVolume = strictUnknown,
            OiOpen = 1000+seq,
            OiClose = 1001+seq,
            Bid = close-1,
            Ask = close+1,
            BidQty = 10,
            AskQty = 10,
            Spread = 2,
            BookImbalance = 0,
            Microprice = close,
        };

    static AdaptiveFlowState MakeFlowState(int seq, double ratio, int priceDirection, int deltaDirection, string? evolution)
    {
        var delta = (long)Math.Round(ratio * 1000);
        return new AdaptiveFlowState
        {
            Bar = MakeBar(seq, 100, 101, 600, 300, 100),
            Rolling = new AdaptiveRollingState
            {
                TradeDate = new DateOnly(2026,9,25),
                WindowBars = 10,
                BaseBarVolume = 100,
                WindowVolume = 1000,
                StartBarSeq = seq-9,
                EndBarSeq = seq,
                StartAvailableAtUtc = DateTimeOffset.Parse("2026-09-25T04:00:00Z"),
                EndAvailableAtUtc = DateTimeOffset.Parse("2026-09-25T04:10:00Z"),
                StartPrice = 100,
                EndPrice = 101,
                High = 102,
                Low = 99,
                PriceDisplacement = priceDirection,
                ReturnBps = priceDirection,
                PathLength = 2,
                Efficiency = .5,
                SignedEfficiency = .5*priceDirection,
                StrictBuyVolume = 500+Math.Max(0,delta),
                StrictSellVolume = 500+Math.Max(0,-delta),
                StrictUnknownVolume = 0,
                StrictDelta = deltaDirection*Math.Abs(delta),
                StrictQuoteCoverage = 1,
                StrictDeltaRatioTotal = deltaDirection*Math.Abs(ratio),
                StrictDeltaRatioClassified = deltaDirection*Math.Abs(ratio),
                EnrichedBuyVolume = 500,
                EnrichedSellVolume = 500,
                EnrichedUnknownVolume = 0,
                EnrichedDelta = 0,
                EnrichedDeltaRatio = 0,
                FallbackShare = 0,
                OiStart = 1000,
                OiEnd = 1010,
                OiChange = 10,
                OiChangePct = .01,
                ElapsedSeconds = 60,
                VolumePerSecond = 1000d/60,
                TradeUpdates = 100,
                WindowRange = 3,
                PriceDirection = priceDirection,
                StrictDeltaDirection = deltaDirection,
                EnrichedDeltaDirection = 0,
                PriceStrictDeltaAgree = priceDirection==deltaDirection,
                PriceEnrichedDeltaAgree = null,
            },
            StrictDominanceEvolution = evolution,
            State = AdaptiveStateKind.Normal,
        };
    }
}
