namespace NiftySignal.Host;

/// <summary>Bound from the "DashboardPush" config section.</summary>
public sealed class DashboardPushOptions
{
    public const string SectionName = "DashboardPush";

    public string HubUrl { get; set; } = "http://localhost:5210/hubs/market-data";
}
