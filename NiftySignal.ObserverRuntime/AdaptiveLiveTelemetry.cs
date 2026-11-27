using System.Diagnostics;

namespace NiftySignal.Host;

/// <summary>
/// Stage timing hooks for the adaptive live path and recovery. <see cref="Source"/> is an <see cref="ActivitySource"/>: with no listener attached
/// (production) <c>StartActivity</c> returns null and costs a single check, so nothing is recorded, logged or allocated per tick. The performance
/// validation tool attaches an <see cref="ActivityListener"/> to read the per-stage durations.
/// </summary>
public static class AdaptiveLiveTelemetry
{
    public const string SourceName = "NiftySignal.AdaptiveLive";

    public static readonly ActivitySource Source = new(SourceName);
}
