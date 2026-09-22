using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22, Part B (docs/VOLUME_BAR_FINDINGS.md): unit coverage for the pure single-side
/// formula helpers introduced for CallScore/PutScore -- <see cref="TradeSimulator.ComputeSideTobDivergence"/>,
/// <see cref="TradeSimulator.ComputeCallScore"/>, <see cref="TradeSimulator.ComputePutScore"/>.
/// These are research-only, backtest-side pure functions (no DB/I-O) made `internal` (not public)
/// specifically so this project can test them directly, per CLAUDE.md's "new behavior gets a test"
/// rule -- see the csproj's own InternalsVisibleTo entry.
/// </summary>
public sealed class PartBSingleSideMetricsTests
{
    static OptionDepthBarRow MakeDepthBar(
        double? callBid = null, double? callAsk = null, double? putBid = null, double? putAsk = null,
        double? callTobBid = null, double? callTobAsk = null, double? putTobBid = null, double? putTobAsk = null) =>
        new()
        {
            AsOfDate = new DateOnly(2026, 9, 8),
            BarIndex = 0,
            BarVolumeThreshold = 2600,
            BandWidth = 3,
            EndTimestamp = DateTimeOffset.UtcNow,
            CallBidQtyAvg = callBid,
            CallAskQtyAvg = callAsk,
            PutBidQtyAvg = putBid,
            PutAskQtyAvg = putAsk,
            CallTobBidQtyAvg = callTobBid,
            CallTobAskQtyAvg = callTobAsk,
            PutTobBidQtyAvg = putTobBid,
            PutTobAskQtyAvg = putTobAsk,
        };

    static VolumeBarRow MakeBar(int barIndex = 0) => new()
    {
        AsOfDate = new DateOnly(2026, 9, 8),
        BarIndex = barIndex,
        BarVolumeThreshold = 2600,
        StartTimestamp = DateTimeOffset.UtcNow,
        EndTimestamp = DateTimeOffset.UtcNow,
        OpenPrice = 100m,
        HighPrice = 101m,
        LowPrice = 99m,
        ClosePrice = 100m,
        Volume = 2600,
    };

    [Fact]
    public void ComputeSideTobDivergence_CallSide_IsFullBookMinusTouchOnly()
    {
        var bar = MakeDepthBar(callBid: 300, callAsk: 100, callTobBid: 50, callTobAsk: 50);

        // Full book (Call): (300-100)/(300+100) = 0.5. Touch (Call): (50-50)/100 = 0.
        var result = TradeSimulator.ComputeSideTobDivergence(bar, side: true);

        Assert.NotNull(result);
        Assert.Equal(0.5, result!.Value, precision: 10);
    }

    [Fact]
    public void ComputeSideTobDivergence_PutSide_NeverReadsCallFields()
    {
        // Call side wildly bullish, Put side exactly flat -- if this leaked Call fields into the
        // Put computation (the exact pooling bug Part B exists to avoid), the result would not be
        // zero.
        var bar = MakeDepthBar(
            callBid: 1000, callAsk: 1, callTobBid: 1000, callTobAsk: 1,
            putBid: 40, putAsk: 40, putTobBid: 40, putTobAsk: 40);

        var result = TradeSimulator.ComputeSideTobDivergence(bar, side: false);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value, precision: 10);
    }

    [Fact]
    public void ComputeSideTobDivergence_NullWhenFullBookMissing()
    {
        var bar = MakeDepthBar(callTobBid: 10, callTobAsk: 10);

        Assert.Null(TradeSimulator.ComputeSideTobDivergence(bar, side: true));
    }

    [Fact]
    public void ComputeCallScore_UsesOnlyCallTouchFields_NeverPutFields()
    {
        var depthByBar = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = MakeDepthBar(callTobBid: 300, callTobAsk: 100, putTobBid: 999, putTobAsk: 1),
        };

        var result = TradeSimulator.ComputeCallScore(MakeBar(), depthByBar, optionOiByBarIndex: null);

        // (300-100)/(300+100) = 0.5, regardless of the wildly different Put touch values.
        Assert.NotNull(result);
        Assert.Equal(0.5, result!.Value, precision: 10);
    }

    [Fact]
    public void ComputeCallScore_NullWhenBarMissingFromDictionary()
    {
        var depthByBar = new Dictionary<int, OptionDepthBarRow> { [1] = MakeDepthBar(callTobBid: 10, callTobAsk: 5) };

        Assert.Null(TradeSimulator.ComputeCallScore(MakeBar(barIndex: 0), depthByBar, optionOiByBarIndex: null));
    }

    [Fact]
    public void ComputePutScore_AveragesFlippedDepthAndTobComponents()
    {
        // Put depth ratio (unflipped) = (200-100)/(300) = 1/3 -> flipped = -1/3.
        // Put TOB ratio (unflipped) = (60-20)/80 = 0.5 -> flipped = -0.5.
        // Average of the two flipped components = (-1/3 + -0.5) / 2 = -0.41666...
        var depthByBar = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = MakeDepthBar(putBid: 200, putAsk: 100, putTobBid: 60, putTobAsk: 20),
        };

        var result = TradeSimulator.ComputePutScore(MakeBar(), depthByBar, optionOiByBarIndex: null);

        Assert.NotNull(result);
        Assert.Equal(-0.41666666666666, result!.Value, precision: 8);
    }

    [Fact]
    public void ComputePutScore_FallsBackToSingleComponentWhenOnlyOneAvailable()
    {
        // Only the depth component resolves (TOB fields absent) -- flipped depth ratio alone,
        // not averaged against a missing/zero TOB reading.
        var depthByBar = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = MakeDepthBar(putBid: 300, putAsk: 100),
        };

        var result = TradeSimulator.ComputePutScore(MakeBar(), depthByBar, optionOiByBarIndex: null);

        Assert.NotNull(result);
        Assert.Equal(-0.5, result!.Value, precision: 10);
    }

    [Fact]
    public void ComputePutScore_NeverReadsCallFields()
    {
        var depthByBar = new Dictionary<int, OptionDepthBarRow>
        {
            [0] = MakeDepthBar(callBid: 5000, callAsk: 1, callTobBid: 5000, callTobAsk: 1, putBid: 40, putAsk: 40, putTobBid: 40, putTobAsk: 40),
        };

        var result = TradeSimulator.ComputePutScore(MakeBar(), depthByBar, optionOiByBarIndex: null);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Value, precision: 10);
    }
}
