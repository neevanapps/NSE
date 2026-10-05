using NiftySignal.Host;
using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

public sealed class OpeningVolumeProjectionV1Tests
{
    [Fact]
    public void Projection_UsesFrozenResearchCoefficients()
    {
        var predicted = OpeningVolumeProjectionV1.PredictFullDayVolume(303_940);

        Assert.Equal(
            OpeningVolumeProjectionV1.Intercept + OpeningVolumeProjectionV1.Slope * 303_940,
            predicted,
            6);
    }

    [Fact]
    public void SelectBaseBarVolume_RoundsToFiftyLotBlock()
    {
        var selected = OpeningVolumeProjectionV1.SelectBaseBarVolume(303_940, 65);

        Assert.Equal(16_250, selected);
        Assert.Equal(0, selected % (65 * 50));
    }

    [Fact]
    public void PositiveCumulativeVolume_MirrorsResearchMaxSemantics()
    {
        long[] cumulative = [100, 120, 115, 125, 125, 140];

        var traded = LiveAdaptiveVolumeObserver.ComputePositiveCumulativeVolume(cumulative);

        Assert.Equal(40, traded);
    }

    [Fact]
    public void PositiveCumulativeVolume_UsesFirstValueAsBaseline()
    {
        long[] cumulative = [10_000, 10_100, 10_250];

        var traded = LiveAdaptiveVolumeObserver.ComputePositiveCumulativeVolume(cumulative);

        Assert.Equal(250, traded);
    }
}
