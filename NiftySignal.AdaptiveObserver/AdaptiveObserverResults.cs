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
    OptionResidualReading Reading,
    double? ResidualDelta,
    int ResidualDirection,
    int FuturesRollingDirection,
    string Relationship);

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
