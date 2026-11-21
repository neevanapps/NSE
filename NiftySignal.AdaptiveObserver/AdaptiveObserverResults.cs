namespace NiftySignal.AdaptiveObserver;

public sealed record OptionBandBarResult(
    OptionBandSelection? Selection,
    bool BandRolled,
    OptionBandSideMetrics? Call,
    OptionBandSideMetrics? Put,
    OptionBandRollingMetrics? CallRolling,
    OptionBandRollingMetrics? PutRolling,
    string? UnavailableReason);

public sealed record ResidualBarResult(
    ResidualVariant Variant,
    OptionResidualReading? Reading,
    double? ResidualDelta,
    int ResidualDirection,
    int FuturesRollingDirection,
    string Relationship,
    string? UnavailableReason)
{
    public bool IsAvailable => Reading is not null;
}

public sealed record AdaptiveCompletedBarPackage(
    ExactAdaptiveBar FutureBar,
    AdaptiveFlowState FlowState,
    OptionBandBarResult OptionBand,
    IReadOnlyList<ResidualBarResult> Residuals,
    bool IsActionableWeak2,
    // Supplemental observation only (sidecar-persisted, section 71 of the 08-Oct plan). Never part of the
    // parity-protected core rows; null when the producer did not compute it.
    FuturesMicrostructureBar? Microstructure = null,
    // Supplemental center-contract options observations (sidecar-persisted). Never part of the parity-protected core rows.
    OptionsSupplementalBar? OptionsSupplemental = null,
    // Supplemental futures-minus-spot basis observation with the approved 5-second spot freshness rule (null when the session has no frozen spot instrument).
    FuturesBasisBar? Basis = null,
    // Per-strike observational projection of the frozen weekly option universe (sidecar-persisted; null unless the engine was created with projectStrikes). Never part of the parity-protected core rows.
    IReadOnlyList<OptionStrikeBarMetrics>? OptionStrikes = null,
    // The causal ATM at THIS bar's CLOSE (not the band's start-of-bar centre). Sidecar-persisted; never part of the parity-protected core rows. Null only when the producer did not compute it.
    BarCloseAtm? CloseAtm = null);

/// <summary>
/// The ATM strike in force at a completed bar's CLOSE instant, by the session's own ATM rule (spot-atm-v1: nearest strike of the frozen weekly ladder to the spot at most 5 s old, ties choose the lower strike; the legacy
/// synthetic-forward rule: the band selection made at that instant). It uses only data available at the close and is independent of whether the ATM±2 option BAND could be built there (a known ATM near the frozen ladder
/// edge has a null band but a non-null <see cref="Strike"/>). <see cref="Strike"/> null = unavailable, with <see cref="UnavailableReason"/>; no later tick, prior ATM or manual selection is ever substituted.
/// </summary>
public sealed record BarCloseAtm(double? Strike, double? Spot, string? UnavailableReason)
{
    public const string Version = "close-atm-v1";
}

public sealed record AdaptivePartialBarStatus(
    long AccumulatedVolume,
    long TargetVolume,
    DateTimeOffset? StartedAtUtc)
{
    public double ProgressPct => TargetVolume > 0 ? Math.Min(100d, 100d * AccumulatedVolume / TargetVolume) : 0d;
}
