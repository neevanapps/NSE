namespace NiftySignal.Features;

/// <summary>
/// Per-metric rolling-window lengths, exactly as specified in plan section 5.4 -- named
/// here so the scoring engine (Phase 4) references these constants instead of scattering
/// magic durations across the codebase.
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
    public static readonly TimeSpan IvSkew = TimeSpan.FromMinutes(60);

    /// <summary>Moderate.</summary>
    public static readonly TimeSpan FuturesBasis = TimeSpan.FromMinutes(30);
}
