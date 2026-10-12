using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Tests.Trading.Recovery;

public class StrategyFrameCodecTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 12, 4, 31, 0, TimeSpan.Zero);

    private static AdaptiveStrategyFrame Frame(double? micro, double? usable, long? delta, double? centre, DateOnly? expiry)
        => new("npt1-m1-v1:2026-10-12", new DateOnly(2026, 10, 12), 7, End.AddSeconds(-61), End, true, true, 7, "npt1-m1-v1", "spec", "micro-v1", micro, usable, delta, centre, expiry, expiry is null ? "NoBand" : null);

    [Theory]
    [InlineData(0.1234567890123, 41.5, -3L, 24050d, true)]
    [InlineData(null, null, null, null, false)]
    [InlineData(-7.25, 0d, 0L, 24000.5, true)]
    public void AFrame_RoundTripsExactly_IncludingMissingValues(double? micro, double? usable, long? delta, double? centre, bool hasExpiry)
    {
        var frame = Frame(micro, usable, delta, centre, hasExpiry ? new DateOnly(2026, 10, 13) : null);
        var bar = new BarCompletedEvent(End, 7, "FUT", null, frame);

        var record = InputCodec.ToRecord(1, 1, 0, bar);
        var back = Assert.IsType<BarCompletedEvent>(InputCodec.ToEvent(record));

        Assert.Equal(frame, back.Frame);
        Assert.Equal(record.Payload, InputCodec.ToRecord(1, 1, 0, back).Payload);
    }

    [Fact]
    public void ABarWithoutAFrame_StillRoundTripsWithoutOne()
    {
        var back = Assert.IsType<BarCompletedEvent>(InputCodec.ToEvent(InputCodec.ToRecord(1, 1, 0, new BarCompletedEvent(End, 7, "FUT"))));
        Assert.Null(back.Frame);
    }
}
