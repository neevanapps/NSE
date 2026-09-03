using Microsoft.Extensions.Options;
using NiftySignal.Domain.Configuration;

namespace NiftySignal.Host;

/// <summary>
/// Phase 0 placeholder: proves the service stays alive under Windows Service hosting
/// and that config changes to appsettings.Local.json hot-reload without a restart.
/// Replaced by the real ingestion/scoring pipeline workers in later phases.
/// </summary>
public sealed class Worker(
    ILogger<Worker> logger,
    IOptionsMonitor<KillSwitchOptions> killSwitch) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var _ = killSwitch.OnChange(options =>
            logger.LogInformation(
                "KillSwitch config reloaded: EntriesEnabled={EntriesEnabled}",
                options.EntriesEnabled));

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Heartbeat at {Time}. KillSwitch.EntriesEnabled={EntriesEnabled}",
                DateTimeOffset.Now,
                killSwitch.CurrentValue.EntriesEnabled);

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
