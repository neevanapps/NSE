namespace NiftySignal.Features;

/// <summary>
/// Per-metric rolling-window lengths -- originally plan section 5.4's starting values,
/// tuned from there as live behavior warrants (2026-09-04: IvSkew shortened from 60 to 15
/// minutes so it warms up in the same session it's tested in, not the next day).
/// </summary>
public static class FeatureWindowLengths
{
    /// <summary>Fast-moving, noisy.</summary>
    public static readonly TimeSpan DepthImbalance = TimeSpan.FromMinutes(5);

    /// <summary>Intraday responsiveness.</summary>
    public static readonly TimeSpan PriceMomentum = TimeSpan.FromMinutes(15);

    /// <summary>Slower-moving structural signal.</summary>
    public static readonly TimeSpan Pcr = TimeSpan.FromMinutes(30);

    /// <summary>OI updates are not tick-frequency.</summary>
    public static readonly TimeSpan OiBuildupNet = TimeSpan.FromMinutes(30);

    /// <summary>Slow structural signal.</summary>
    public static readonly TimeSpan IvSkew = TimeSpan.FromMinutes(15);

    /// <summary>Moderate.</summary>
    public static readonly TimeSpan FuturesBasis = TimeSpan.FromMinutes(30);
}
