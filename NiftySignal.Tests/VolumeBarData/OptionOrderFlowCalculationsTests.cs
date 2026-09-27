using NiftySignal.Domain.ValueObjects;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-27 Options Order Flow experiment -- pipeline/calculation coverage built and validated
/// BEFORE the experiment itself runs, per the user's explicit "implement the complete pipeline
/// first, do not run the experiment yet" instruction. Covers <see cref="OptionOrderFlowCalculations"/>
/// (trade detection, aggressor classification, contract flow, depth imbalance) and
/// <see cref="OptionTickReaderV2"/>'s ordering/volume validation.
/// </summary>
public sealed class OptionOrderFlowCalculationsTests
{
    static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 15, 0, TimeSpan.FromHours(5.5));
    static DateTimeOffset T(int seconds) => T0.AddSeconds(seconds);

    static MarketDepth Book(decimal bid, long bidQty, decimal ask, long askQty) =>
        new(bid, bidQty, 0, 0, 0, 0, 0, 0, 0, 0, ask, askQty, 0, 0, 0, 0, 0, 0, 0, 0);

    static OptionTickV2 Tick(long id, int seconds, decimal price, long volume, MarketDepth? depth) =>
        new(id, T(seconds), T(seconds), price, depth, volume, null);

    // ---------------- ClassifyAggressor (Section 5) ----------------

    [Fact]
    public void ClassifyAggressor_TradeAtOrAboveAsk_IsAggressiveBuy()
    {
        Assert.Equal(TradeAggressor.AggressiveBuy, OptionOrderFlowCalculations.ClassifyAggressor(100m, 99m, 100m));
        Assert.Equal(TradeAggressor.AggressiveBuy, OptionOrderFlowCalculations.ClassifyAggressor(101m, 99m, 100m));
    }

    [Fact]
    public void ClassifyAggressor_TradeAtOrBelowBid_IsAggressiveSell()
    {
        Assert.Equal(TradeAggressor.AggressiveSell, OptionOrderFlowCalculations.ClassifyAggressor(99m, 99m, 100m));
        Assert.Equal(TradeAggressor.AggressiveSell, OptionOrderFlowCalculations.ClassifyAggressor(98m, 99m, 100m));
    }

    [Fact]
    public void ClassifyAggressor_StrictlyInsideSpread_IsUnknown_NeverForced()
    {
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(99.5m, 99m, 100m));
    }

    [Fact]
    public void ClassifyAggressor_MissingBookSide_IsUnknown()
    {
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(100m, null, 100m));
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(100m, 99m, null));
    }

    [Fact]
    public void ClassifyAggressor_LockedOrCrossedBook_IsUnknown_NoTickRuleFallback()
    {
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(100m, 100m, 100m)); // locked
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(100m, 101m, 100m)); // crossed
        Assert.Equal(TradeAggressor.Unknown, OptionOrderFlowCalculations.ClassifyAggressor(100m, 0m, 100m));   // invalid (zero bid)
    }

    // ---------------- DetectTradeEvents (Sections 3/4) ----------------

    [Fact]
    public void DetectTradeEvents_QuoteOnlyUpdate_ZeroDelta_IsNotATrade()
    {
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, Book(99m, 5, 100m, 5)),
            Tick(2, 1, 100m, 1000, Book(99m, 8, 100m, 3)), // volume unchanged -- quote-only.
        };
        var events = OptionOrderFlowCalculations.DetectTradeEvents(ticks, out var negativeDeltas);
        Assert.Empty(events);
        Assert.Equal(0, negativeDeltas);
    }

    [Fact]
    public void DetectTradeEvents_PositiveDelta_UsesPreTradeBookNotPostTradeQuote()
    {
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, Book(99m, 5, 100m, 5)),
            // Trade occurs at 100 (the ask), volume steps +50; the tick's OWN depth (post-trade,
            // ask now thinner) must be ignored -- classification must use the PREVIOUS tick's book.
            Tick(2, 1, 100m, 1050, Book(99m, 5, 100.5m, 1)),
        };
        var events = OptionOrderFlowCalculations.DetectTradeEvents(ticks, out var negativeDeltas);
        var e = Assert.Single(events);
        Assert.Equal(50, e.TradeQuantity);
        Assert.Equal(99m, e.PreviousBestBid);
        Assert.Equal(100m, e.PreviousBestAsk);
        Assert.Equal(TradeAggressor.AggressiveBuy, e.Aggressor);
        Assert.Equal(0, negativeDeltas);
    }

    [Fact]
    public void DetectTradeEvents_NegativeDelta_ReportedAsBadDataNeverATrade()
    {
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, Book(99m, 5, 100m, 5)),
            Tick(2, 1, 100m, 400, Book(99m, 5, 100m, 5)), // cumulative volume went backwards.
        };
        var events = OptionOrderFlowCalculations.DetectTradeEvents(ticks, out var negativeDeltas);
        Assert.Empty(events);
        Assert.Equal(1, negativeDeltas);
    }

    [Fact]
    public void DetectTradeEvents_MissingPreviousDepth_ClassifiesUnknown_NeverFabricatesABook()
    {
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, null), // touchline-only update, no depth snapshot.
            Tick(2, 1, 100m, 1050, Book(99m, 5, 100m, 5)),
        };
        var events = OptionOrderFlowCalculations.DetectTradeEvents(ticks, out _);
        var e = Assert.Single(events);
        Assert.Null(e.PreviousBestBid);
        Assert.Null(e.PreviousBestAsk);
        Assert.Equal(TradeAggressor.Unknown, e.Aggressor);
    }

    // ---------------- AggregateContractFlow (Section 6) ----------------

    [Fact]
    public void AggregateContractFlow_ComputesTradeOfiAndCoverage()
    {
        var events = new List<OptionTradeEvent>
        {
            new(1, T(0), 100m, 30, 99m, 100m, TradeAggressor.AggressiveBuy),
            new(2, T(1), 99m, 10, 99m, 100m, TradeAggressor.AggressiveSell),
            new(3, T(2), 99.5m, 5, 99m, 100m, TradeAggressor.Unknown),
        };
        var flow = OptionOrderFlowCalculations.AggregateContractFlow(events);
        Assert.Equal(30, flow.AggressiveBuyVolume);
        Assert.Equal(10, flow.AggressiveSellVolume);
        Assert.Equal(5, flow.UnknownVolume);
        Assert.Equal(40, flow.ClassifiedVolume);
        Assert.Equal(45, flow.TotalTradedVolume);
        Assert.Equal((30.0 - 10.0) / 40.0, flow.TradeOFI!.Value, 10);
        Assert.Equal(40.0 / 45.0, flow.ClassificationCoverage, 10);
    }

    [Fact]
    public void AggregateContractFlow_AllUnknown_TradeOfiIsNullNotZero()
    {
        var events = new List<OptionTradeEvent> { new(1, T(0), 100m, 10, 99m, 101m, TradeAggressor.Unknown) };
        var flow = OptionOrderFlowCalculations.AggregateContractFlow(events);
        Assert.Null(flow.TradeOFI);
        Assert.Equal(0.0, flow.ClassificationCoverage);
    }

    [Fact]
    public void AggregateContractFlow_NoEvents_AllZeroCoverageZero()
    {
        var flow = OptionOrderFlowCalculations.AggregateContractFlow([]);
        Assert.Equal(0, flow.TotalTradedVolume);
        Assert.Null(flow.TradeOFI);
        Assert.Equal(0.0, flow.ClassificationCoverage);
    }

    // ---------------- Depth imbalance (Section 7) ----------------

    [Fact]
    public void ComputeDepthImbalance_SumsAllFiveLevelsEachSide()
    {
        var depth = new MarketDepth(
            10m, 100, 10m, 100, 10m, 100, 10m, 100, 10m, 100,
            11m, 50, 11m, 50, 11m, 50, 11m, 50, 11m, 50);
        var result = OptionOrderFlowCalculations.ComputeDepthImbalance(depth);
        Assert.Equal(500, result.TotalBidDepth);
        Assert.Equal(250, result.TotalAskDepth);
        Assert.Equal((500.0 - 250.0) / 750.0, result.DepthImbalance!.Value, 10);
    }

    [Fact]
    public void ComputeDepthImbalance_ZeroBothSides_IsNullNotDivideByZero()
    {
        var depth = new MarketDepth(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Null(OptionOrderFlowCalculations.ComputeDepthImbalance(depth).DepthImbalance);
    }

    [Fact]
    public void TimeWeightedAverageDepthImbalance_WeightsBySnapshotDuration()
    {
        // Snapshot A holds for 150s of the 180s window (imbalance +1), snapshot B for 30s (imbalance -1).
        var bidHeavy = new MarketDepth(100, 100, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0);
        var askHeavy = new MarketDepth(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 100, 100, 0, 0, 0, 0, 0, 0, 0, 0);
        var snapshots = new List<(DateTimeOffset, MarketDepth?)>
        {
            (T(0), bidHeavy),
            (T(150), askHeavy),
        };
        var twa = OptionOrderFlowCalculations.TimeWeightedAverageDepthImbalance(snapshots, T(0), T(180));
        Assert.NotNull(twa);
        // Expected: (150/180)*bidHeavyImbalance + (30/180)*askHeavyImbalance, both magnitudes ~0.98.
        var bidImb = OptionOrderFlowCalculations.ComputeDepthImbalance(bidHeavy).DepthImbalance!.Value;
        var askImb = OptionOrderFlowCalculations.ComputeDepthImbalance(askHeavy).DepthImbalance!.Value;
        var expected = (150.0 / 180.0) * bidImb + (30.0 / 180.0) * askImb;
        Assert.Equal(expected, twa!.Value, 6);
    }

    [Fact]
    public void MedianOfBand_SkipsNulls_NeverSubstitutesMissingStrike()
    {
        var values = new List<double?> { 0.2, null, 0.4, 0.6, null };
        Assert.Equal(0.4, OptionOrderFlowCalculations.MedianOfBand(values)!.Value, 10);
    }

    [Fact]
    public void MedianOfBand_AllMissing_IsNull()
    {
        Assert.Null(OptionOrderFlowCalculations.MedianOfBand([null, null]));
    }

    // ---------------- Validation utility (Section 28 precursor) ----------------

    [Fact]
    public void CountAggressorMismatches_ZeroForCorrectlyClassifiedEvents()
    {
        var events = OptionOrderFlowCalculations.DetectTradeEvents(new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, Book(99m, 5, 100m, 5)),
            Tick(2, 1, 100m, 1050, Book(99m, 5, 100m, 5)),
            Tick(3, 2, 99m, 1080, Book(99m, 5, 100m, 5)),
        }, out _);
        Assert.Equal(0, OptionOrderFlowCalculations.CountAggressorMismatches(events));
    }

    [Fact]
    public void CountAggressorMismatches_DetectsATamperedLabel()
    {
        var tampered = new List<OptionTradeEvent> { new(1, T(0), 100m, 10, 99m, 100m, TradeAggressor.AggressiveSell) };
        Assert.Equal(1, OptionOrderFlowCalculations.CountAggressorMismatches(tampered));
    }

    // ---------------- OptionTickReaderV2 validation ----------------

    [Fact]
    public void ValidateOrdering_CleanSequence_NoViolations()
    {
        var ticks = new List<OptionTickV2> { Tick(1, 0, 100m, 100, null), Tick(2, 1, 100m, 100, null), Tick(3, 1, 100m, 100, null) };
        Assert.Empty(OptionTickReaderV2.ValidateOrdering(ticks));
    }

    [Fact]
    public void ValidateOrdering_TimestampGoingBackwards_IsFlagged()
    {
        var ticks = new List<OptionTickV2> { Tick(1, 5, 100m, 100, null), Tick(2, 1, 100m, 100, null) };
        Assert.Single(OptionTickReaderV2.ValidateOrdering(ticks));
    }

    [Fact]
    public void ValidateOrdering_IdNotStrictlyIncreasing_IsFlagged()
    {
        var ticks = new List<OptionTickV2> { Tick(5, 0, 100m, 100, null), Tick(5, 1, 100m, 100, null) };
        Assert.Single(OptionTickReaderV2.ValidateOrdering(ticks));
    }

    [Fact]
    public void ValidateCumulativeVolume_CountsNegativeDeltasOnly()
    {
        var ticks = new List<OptionTickV2>
        {
            Tick(1, 0, 100m, 1000, null),
            Tick(2, 1, 100m, 1000, null), // flat -- fine.
            Tick(3, 2, 100m, 900, null),  // negative -- flagged.
            Tick(4, 3, 100m, 950, null),
        };
        var report = OptionTickReaderV2.ValidateCumulativeVolume(ticks);
        Assert.Equal(1, report.NegativeDeltaCount);
        Assert.Equal([2], report.SampleOffendingRowIndices);
    }
}
