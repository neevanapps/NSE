using NiftySignal.Domain.Configuration;

namespace NiftySignal.Tests.Domain.Configuration;

public class KillSwitchOptionsTests
{
    [Fact]
    public void EntriesEnabled_DefaultsToTrue_SoAFreshInstallDoesNotSilentlyRefuseAllTrades()
    {
        var options = new KillSwitchOptions();

        Assert.True(options.EntriesEnabled);
    }
}
