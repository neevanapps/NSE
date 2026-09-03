namespace NiftySignal.Domain.Configuration;

/// <summary>
/// Bound from the "KillSwitch" config section. Disables new trade entries only;
/// ingestion and data collection keep running regardless of this flag.
/// </summary>
public sealed class KillSwitchOptions
{
    public const string SectionName = "KillSwitch";

    public bool EntriesEnabled { get; set; } = true;
}
