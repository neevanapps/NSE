using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 cross-session rolling-baseline bounded-transform research task
/// (docs/VOLUME_BAR_FINDINGS.md's dated "Cross-session rolling-baseline bounded-transform"
/// section): coverage for <see cref="TradeSimulator.CollectThreeWayRawLegReadings"/> -- the pure,
/// DB-free helper that replays a day's own bars into 3 phase-matched raw-reading lists (Open/Mid/
/// Close), used both to seed a later day's rolling baseline and (indirectly, via
/// <see cref="TradeSimulator.SimulateDayAsync"/>) to compute that day's own live per-bar score.
/// Synthetic <see cref="VolumeBarRow"/>/<see cref="OptionAtmBarRow"/>/<see cref="OptionDepthBarRow"/>
/// inputs with hand-computed expected raw values, same "prefer a separable, unit-testable function"
/// discipline <see cref="BoundedTransformTests"/> already establishes for the sibling task.
/// </summary>
public class RollingBoundedTransformTests
{
    static VolumeBarRow Bar(int index, TimeSpan istTimeOfDay, decimal closePrice) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = index,
        BarVolumeThreshold = 2600,
        StartTimestamp = IstToUtc(istTimeOfDay).AddMinutes(-1),
        EndTimestamp = IstToUtc(istTimeOfDay),
        OpenPrice = closePrice,
        HighPrice = closePrice,
        LowPrice = closePrice,
        ClosePrice = closePrice,
        Volume = 2600,
    };

    static DateTimeOffset IstToUtc(TimeSpan istTimeOfDay) =>
        new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero).Add(istTimeOfDay).ToOffset(TimeSpan.Zero) - TimeSpan.FromHours(5.5);

    static OptionAtmBarRow Atm(int barIndex, double atmIv) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = barIndex,
        BarVolumeThreshold = 2600,
        EndTimestamp = DateTimeOffset.UtcNow,
        AtmIv = atmIv,
    };

    static OptionDepthBarRow Depth(int barIndex, double callBid, double callAsk, double putBid, double putAsk) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = barIndex,
        BarVolumeThreshold = 2600,
        BandWidth = 5,
        EndTimestamp = DateTimeOffset.UtcNow,
        CallBidQtyAvg = callBid,
        CallAskQtyAvg = callAsk,
        PutBidQtyAvg = putBid,
        PutAskQtyAvg = putAsk,
    };

    static readonly TimeSpan SwitchTime = new(10, 0, 0);

    [Fact]
    public void OpenWindowBar_ReadsNegatedDepthImbalanceRatio_IntoOpenListOnly()
    {
        // 09:45 IST < 10:00 switch -- Open leg. bid=(6+2)=8, ask=(2+2)=4 -> ratio=(8-4)/(8+4)=0.3333,
        // raw = -ratio = -0.3333 (same sign convention ComputeOptionsThreeWayScoreBoundedTransform uses).
        var bars = new List<VolumeBarRow> { Bar(0, new TimeSpan(9, 45, 0), 100m) };
        var depth = new Dictionary<int, OptionDepthBarRow> { [0] = Depth(0, 6, 2, 2, 2) };

        var result = TradeSimulator.CollectThreeWayRawLegReadings(bars, optionAtmByBarIndex: null, depth, SwitchTime);

        Assert.Single(result.Open);
        Assert.Equal(-1.0 / 3.0, result.Open[0], precision: 8);
        Assert.Empty(result.Mid);
        Assert.Empty(result.Close);
    }

    [Fact]
    public void MidWindowBar_NeedsPriorAtmIvAndPriorClose_ReadsPriceSignedDeltaIv()
    {
        // Bar 0 at 09:45 (Open window) seeds previousAtmIv/previousClose. Bar 1 at 11:00 (Mid
        // window): price went UP (100 -> 105), IV went from 0.20 -> 0.25 (delta=+0.05).
        // raw = -sign(+5) * (+0.05) = -1 * 0.05 = -0.05.
        var bars = new List<VolumeBarRow>
        {
            Bar(0, new TimeSpan(9, 45, 0), 100m),
            Bar(1, new TimeSpan(11, 0, 0), 105m),
        };
        var atm = new Dictionary<int, OptionAtmBarRow> { [0] = Atm(0, 0.20), [1] = Atm(1, 0.25) };

        var result = TradeSimulator.CollectThreeWayRawLegReadings(bars, atm, optionDepthWideByBarIndex: null, SwitchTime);

        Assert.Empty(result.Open); // no depth table supplied -- Open bar contributes nothing.
        Assert.Single(result.Mid);
        Assert.Equal(-0.05, result.Mid[0], precision: 8);
        Assert.Empty(result.Close);
    }

    [Fact]
    public void CloseWindowBar_UsesRawDeltaIv_NotPriceSigned()
    {
        // Bar 0 at 11:00 (Mid, seeds previousAtmIv=0.20). Bar 1 at 14:00 (Close, >= 13:30):
        // raw = curIv - prevIv = 0.18 - 0.20 = -0.02, regardless of price direction.
        var bars = new List<VolumeBarRow>
        {
            Bar(0, new TimeSpan(11, 0, 0), 100m),
            Bar(1, new TimeSpan(14, 0, 0), 50m), // price DROPS sharply -- must not affect the raw Close value.
        };
        var atm = new Dictionary<int, OptionAtmBarRow> { [0] = Atm(0, 0.20), [1] = Atm(1, 0.18) };

        var result = TradeSimulator.CollectThreeWayRawLegReadings(bars, atm, optionDepthWideByBarIndex: null, SwitchTime);

        Assert.Empty(result.Mid);
        Assert.Single(result.Close);
        Assert.Equal(-0.02, result.Close[0], precision: 8);
    }

    [Fact]
    public void FirstBarOfEachWindow_MissingPriorData_IsSkipped_NotFabricated()
    {
        // A lone Mid-window bar with no prior AtmIv/Close has nothing to diff against -- no-look-
        // ahead, no-fabrication convention (same as ComputeOptionsThreeWayScoreBoundedTransform's
        // own null-returning behavior for the same situation).
        var bars = new List<VolumeBarRow> { Bar(0, new TimeSpan(11, 0, 0), 100m) };
        var atm = new Dictionary<int, OptionAtmBarRow> { [0] = Atm(0, 0.20) };

        var result = TradeSimulator.CollectThreeWayRawLegReadings(bars, atm, optionDepthWideByBarIndex: null, SwitchTime);

        Assert.Empty(result.Open);
        Assert.Empty(result.Mid);
        Assert.Empty(result.Close);
    }

    [Fact]
    public void MultiplBars_PhaseMatchIntoThreeSeparateLists_InChronologicalOrder()
    {
        var bars = new List<VolumeBarRow>
        {
            Bar(0, new TimeSpan(9, 40, 0), 100m),  // Open
            Bar(1, new TimeSpan(9, 50, 0), 101m),  // Open
            Bar(2, new TimeSpan(11, 0, 0), 102m),  // Mid
            Bar(3, new TimeSpan(12, 0, 0), 103m),  // Mid
            Bar(4, new TimeSpan(14, 0, 0), 104m),  // Close
        };
        var atm = new Dictionary<int, OptionAtmBarRow>
        {
            [0] = Atm(0, 0.20), [1] = Atm(1, 0.21), [2] = Atm(2, 0.22), [3] = Atm(3, 0.24), [4] = Atm(4, 0.23),
        };
        var depth = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = Depth(0, 5, 5, 5, 5), // balanced -> ratio 0 -> raw 0.
            [1] = Depth(1, 8, 2, 8, 2), // imbalanced -> raw negative.
        };

        var result = TradeSimulator.CollectThreeWayRawLegReadings(bars, atm, depth, SwitchTime);

        Assert.Equal(2, result.Open.Count);
        Assert.Equal(0.0, result.Open[0], precision: 8);
        Assert.Equal(2, result.Mid.Count); // bars 2 and 3 both have a prior close/IV to diff against.
        Assert.Single(result.Close); // bar 4 diffs against bar 3's IV (0.23-0.24=-0.01).
        Assert.Equal(-0.01, result.Close[0], precision: 8);
    }

    [Fact]
    public void SeedingIntoRunningMeanStd_MatchesDirectlyAddingTheSameRawValues()
    {
        // Confirms the intended usage pattern (SimulateDayAsync's own seeding block): the raw
        // readings collected from a prior day feed a fresh RunningMeanStd via plain .Add() calls,
        // landing at the exact same Mean/StdDev as adding the values directly.
        var bars = new List<VolumeBarRow>
        {
            Bar(0, new TimeSpan(9, 40, 0), 100m),
            Bar(1, new TimeSpan(9, 45, 0), 101m),
            Bar(2, new TimeSpan(9, 50, 0), 102m),
        };
        var depth = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = Depth(0, 6, 2, 2, 2),  // raw -0.3333...
            [1] = Depth(1, 5, 5, 5, 5),  // raw 0
            [2] = Depth(2, 2, 6, 2, 2),  // raw +0.3333...
        };

        var legs = TradeSimulator.CollectThreeWayRawLegReadings(bars, optionAtmByBarIndex: null, depth, SwitchTime);
        var seeded = new RunningMeanStd();
        foreach (var v in legs.Open)
        {
            seeded.Add(v);
        }

        var direct = new RunningMeanStd();
        direct.Add(-1.0 / 3.0);
        direct.Add(0.0);
        direct.Add(1.0 / 3.0);

        Assert.Equal(3, seeded.Count);
        Assert.Equal(direct.Mean, seeded.Mean, precision: 10);
        Assert.Equal(direct.StdDev, seeded.StdDev, precision: 10);
    }
}
