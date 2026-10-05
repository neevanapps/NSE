using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class AdaptiveOpeningVolumeModelTests
{
    [Fact]
    public void Forecast_UsesFrozenLinearModelAndFiftyLotRounding()
    {
        var forecast = AdaptiveOpeningVolumeModel.Forecast(300_000, 65);

        Assert.Equal(2_637_947.691626541, forecast.ExpectedDayVolume, 6);
        Assert.Equal(16_250, forecast.BaseBarVolume);
        Assert.Equal(162_500, forecast.Rolling10BarVolume);
    }

    [Fact]
    public void ExactBuilder_SplitsCrossingVolumeUpdate()
    {
        var builder = new ExactResearchVolumeBarBuilder(100);
        var t0 = new DateTimeOffset(2026, 10, 5, 3, 45, 0, TimeSpan.Zero);

        builder.ApplyTick(Tick(1, t0, 100m, 1_000, 99m, 101m));
        var first = builder.ApplyTick(Tick(2, t0.AddSeconds(1), 101m, 1_150, 100m, 101m));
        var second = builder.ApplyTick(Tick(3, t0.AddSeconds(2), 101m, 1_200, 100m, 101m));

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(100, first[0].Volume);
        Assert.Equal(100, second[0].Volume);
        Assert.Equal(100, first[0].StrictBuyVolume);
        Assert.Equal(100, second[0].StrictBuyVolume);
    }

    static Tick Tick(long id, DateTimeOffset at, decimal last, long volume, decimal bid, decimal ask) =>
        new()
        {
            Id = id,
            Token = "FUT",
            Exchange = Exchange.Nfo,
            ExchangeTimestamp = at,
            ReceivedAt = at,
            LastPrice = last,
            Volume = volume,
            OpenInterest = 10_000,
            Depth = new MarketDepth(
                bid, 100, bid - 1, 100, bid - 2, 100, bid - 3, 100, bid - 4, 100,
                ask, 100, ask + 1, 100, ask + 2, 100, ask + 3, 100, ask + 4, 100),
        };
}
