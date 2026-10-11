using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Host.PaperLive;
using NiftySignal.Trading.Live;

namespace NiftySignal.Tests.Trading.Live;

public class PaperLiveBridgeTests
{
    [Fact]
    public void WithoutConfiguration_TheBridgeIsDisabled_AndEveryCallIsANoOp()
    {
        using var bridge = new PaperLiveBridge(new ConfigurationBuilder().Build(), NullLogger<PaperLiveBridge>.Instance);

        bridge.OnPollCompleted(DateTimeOffset.UtcNow);
        bridge.OnSessionClosing();

        Assert.False(bridge.Enabled);
        Assert.Equal(PaperLiveState.Disabled, bridge.Status.State);
    }

    [Fact]
    public void EnabledInConfigurationButWithoutAStrategy_ReportsIdleUntilASessionStarts_AndNeverTrades()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:PaperLive:Enabled"] = "true",
            ["Trading:PaperLive:Capital"] = "1000000",
            ["Trading:PaperLive:BrokeragePerOrder"] = "20",
            ["Trading:PaperLive:OptionTickSize"] = "0.05",
        }).Build();
        using var bridge = new PaperLiveBridge(configuration, NullLogger<PaperLiveBridge>.Instance);

        Assert.True(bridge.Enabled);
        Assert.Equal(PaperLiveState.Idle, bridge.Status.State);
        Assert.Empty(PaperLiveSetup.Strategies(configuration.GetSection(PaperLiveOptions.SectionName).Get<PaperLiveOptions>()!));
    }

    [Theory]
    [InlineData(124.15, "124.15")]
    [InlineData(0.05, "0.05")]
    [InlineData(100.30000000000001, "100.3")]
    [InlineData(0.0, "0")]
    [InlineData(-1.0, "0")]
    [InlineData(double.NaN, "0")]
    public void ThePriceConversion_KeepsEveryTickPriceExactly_AndRemovesDoubleNoise(double value, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PaperLiveBridge.Price(value));
}
