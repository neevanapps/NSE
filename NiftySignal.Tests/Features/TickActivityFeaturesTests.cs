using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class TickActivityFeaturesTests
{
    [Fact]
    public void ComputesDensityAndVelocity_FromRawBarFields()
    {
        // 20 ticks, 650 volume, 10 seconds -- deliberately round numbers so expected values are exact.
        var r = TickActivityFeatures.Compute(open: 100m, high: 105m, low: 98m, close: 102m, volume: 650, tickCount: 20, durationSeconds: 10.0);

        Assert.Equal(20.0 / 650.0, r.TickDensity);
        Assert.Equal(650.0 / 20.0, r.VolumePerTick);
        Assert.Equal(2.0, r.TickVelocity);   // 20 ticks / 10s
        Assert.Equal(65.0, r.VolumeVelocity); // 650 / 10s
    }

    [Fact]
    public void NetMoveAndEfficiency_UseAbsoluteDisplacementOverTickCount()
    {
        var r = TickActivityFeatures.Compute(open: 100m, high: 106m, low: 99m, close: 104m, volume: 500, tickCount: 8, durationSeconds: 4.0);

        Assert.Equal(4m, r.NetMove);
        Assert.Equal(4m, r.AbsNetMove);
        Assert.Equal(4.0 / 8, r.PriceEfficiency);
        Assert.Equal((106.0 - 99.0) / 8, r.RangeEfficiency);
        Assert.Equal(4.0 / 500, r.PricePerVolume);
        Assert.Equal(1, r.Direction);
        Assert.Equal(4.0 / 8, r.SignedEfficiency);
    }

    [Fact]
    public void Churn_IsRangeOverAbsNetMove_HighWhenRangeFarExceedsDisplacement()
    {
        // Close==Open (net move zero) but the bar still traveled a wide range -- maximal churn,
        // guarded against divide-by-zero via the epsilon floor rather than returning infinity.
        var r = TickActivityFeatures.Compute(open: 100m, high: 110m, low: 90m, close: 100m, volume: 1000, tickCount: 30, durationSeconds: 15.0);

        Assert.Equal(0, r.Direction);
        Assert.True(r.Churn > 1_000_000); // range(20) / epsilon(~1e-9) -- effectively "infinite churn", finite and safe
        Assert.False(double.IsInfinity(r.Churn!.Value));
        Assert.False(double.IsNaN(r.Churn!.Value));
    }

    [Fact]
    public void ZeroTickCountOrZeroDuration_GuardsAgainstDivideByZero_ReturnsNull()
    {
        var r = TickActivityFeatures.Compute(open: 100m, high: 101m, low: 99m, close: 100m, volume: 300, tickCount: 0, durationSeconds: 0.0);

        Assert.Null(r.VolumePerTick);
        Assert.Null(r.TickVelocity);
        Assert.Null(r.VolumeVelocity);
        Assert.Null(r.PriceEfficiency);
        Assert.Null(r.RangeEfficiency);
        Assert.Null(r.SignedEfficiency);
        // TickDensity only needs Volume (nonzero here), so it's still computable even with zero ticks.
        Assert.Equal(0.0, r.TickDensity);
    }

    [Fact]
    public void ZeroVolume_GuardsTickDensityAndPricePerVolume()
    {
        var r = TickActivityFeatures.Compute(open: 100m, high: 101m, low: 99m, close: 100.5m, volume: 0, tickCount: 5, durationSeconds: 2.0);

        Assert.Null(r.TickDensity);
        Assert.Null(r.PricePerVolume);
        // VolumePerTick only needs TickCount (nonzero here), so it's still computable.
        Assert.Equal(0.0, r.VolumePerTick);
    }

    [Fact]
    public void NegativeNetMove_SetsDirectionDownAndSignedEfficiencyNegative()
    {
        var r = TickActivityFeatures.Compute(open: 100m, high: 100.5m, low: 96m, close: 97m, volume: 400, tickCount: 10, durationSeconds: 5.0);

        Assert.Equal(-3m, r.NetMove);
        Assert.Equal(3m, r.AbsNetMove);
        Assert.Equal(-1, r.Direction);
        Assert.Equal(-3.0 / 10, r.SignedEfficiency);
    }
}
