using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Domain;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;

namespace NiftySignal.Tests.Host;

public sealed class AdaptiveFuturesObservationEngineTests
{
    [Fact]
    public void FreezesForecastAt0930AndReplaysOpeningTicks()
    {
        var engine = new AdaptiveFuturesObservationEngine(
            NullLogger<AdaptiveFuturesObservationEngine>.Instance);

        var day = new DateOnly(2026, 10, 5);
        var future = new Instrument
        {
            Token = "FUT",
            Exchange = Exchange.Nfo,
            TradingSymbol = "NIFTY26OCTFUT",
            InstrumentType = InstrumentType.Future,
            Underlying = "NIFTY",
            LotSize = 65,
            TickSize = 0.05m,
            AsOfDate = day,
        };

        engine.StartSession(day, future);

        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), IstTime.Offset).ToUniversalTime();
        var preCutoff = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 29, 59)), IstTime.Offset).ToUniversalTime();
        var cutoff = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 30)), IstTime.Offset).ToUniversalTime();

        engine.OnTick(Tick(1, open, 22_000m, 1_000));
        engine.OnTick(Tick(2, preCutoff, 22_010m, 301_000));

        Assert.False(engine.ForecastFrozen);

        engine.OnTick(Tick(3, cutoff, 22_011m, 301_100));

        Assert.True(engine.ForecastFrozen);
        Assert.NotNull(engine.Current);
        Assert.Equal(300_000, engine.Current!.OpeningVolume);
        Assert.Equal(16_250, engine.Current.BaseBarVolume);
        Assert.Equal(162_500, engine.Current.Rolling10BarVolume);
        Assert.Equal(18, engine.Current.CompletedBars);
    }

    static Tick Tick(long id, DateTimeOffset at, decimal last, long volume) =>
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
        };
}
