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
    bool IsActionableWeak2);

public sealed record AdaptivePartialBarStatus(
    long AccumulatedVolume,
    long TargetVolume,
    DateTimeOffset? StartedAtUtc)
{
    public double ProgressPct => TargetVolume > 0 ? Math.Min(100d, 100d * AccumulatedVolume / TargetVolume) : 0d;
}
