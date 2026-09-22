using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class FutureFlowAccumulatorTests
{
    [Fact]
    public void FirstTick_ContributesZeroVolumeDelta_NoPriorBaselineToDiffAgainst()
    {
        var flow = new FutureFlowAccumulator();
        flow.ApplyTick(23000m, 5000);

        Assert.Equal(0, flow.CadenceVolumeDelta);
        Assert.Equal(5000, flow.LatestCumulativeVolume);
        Assert.Null(flow.Vwap); // zero volume traded so far -> no meaningful VWAP yet
    }

    [Fact]
    public void SecondTick_ContributesTheRealVolumeDelta_AndVwapReflectsThatTradedPrice()
    {
        var flow = new FutureFlowAccumulator();
        flow.ApplyTick(23000m, 5000);
        flow.ApplyTick(23010m, 5100); // +100 volume traded at 23010

        Assert.Equal(100, flow.CadenceVolumeDelta);
        Assert.Equal(5100, flow.LatestCumulativeVolume);
        Assert.Equal(23010.0, flow.Vwap);
    }

    [Fact]
    public void VolumeGoingBackwards_FloorsTheDeltaAtZero_RatherThanGoingNegative()
    {
        var flow = new FutureFlowAccumulator();
        flow.ApplyTick(23000m, 5000);
        flow.ApplyTick(23010m, 4000); // a feed reset -- cumulative volume appears to drop

        Assert.Equal(0, flow.CadenceVolumeDelta);
    }

    [Fact]
    public void ResetCadence_ClearsOnlyTheCadenceDelta_NotTheDayLongVwapAccumulation()
    {
        var flow = new FutureFlowAccumulator();
        flow.ApplyTick(23000m, 5000);
        flow.ApplyTick(23010m, 5100);
        flow.ResetCadence();

        Assert.Equal(0, flow.CadenceVolumeDelta);
        Assert.Equal(23010.0, flow.Vwap); // VWAP is cumulative for the day -- a cadence reset must not touch it

        flow.ApplyTick(23020m, 5200);
        // New VWAP = (100*23010 + 100*23020) / 200 = 23015
        Assert.Equal(23015.0, flow.Vwap);
    }
}
