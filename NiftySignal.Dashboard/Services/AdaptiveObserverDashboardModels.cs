using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

public sealed record AdaptiveFuturesGridRow(
    AdaptiveFutureBarRow Bar,
    AdaptiveRollingStateRow? Rolling);

public sealed record AdaptiveOptionPairGridRow(
    int BarSeq,
    DateTimeOffset EndAvailableAtUtc,
    AdaptiveOptionBandBarRow? Call,
    AdaptiveOptionBandBarRow? Put);

public sealed record AdaptiveObserverSnapshot(
    AdaptiveSessionStateRow Session,
    AdaptiveObserverRuntimeRow Runtime,
    IReadOnlyList<AdaptiveFuturesGridRow> Futures,
    IReadOnlyList<AdaptiveOptionPairGridRow> Options,
    IReadOnlyList<AdaptiveOptionResidualBarRow> Residuals,
    IReadOnlyList<AdaptiveResidualAnchorComponentRow> ResidualAnchors,
    int ConsecutiveValidBars = 0)
{
    public bool CanUseForDecisions => ConsecutiveValidBars >= NiftySignal.AdaptiveObserver.AdaptiveReadinessPolicy.RequiredBars
        && Runtime.RuntimeStatus == AdaptiveRuntimeStatus.Live;
}

public sealed record AdaptiveObserverHeaderSnapshot(
    AdaptiveSessionStateRow Session,
    AdaptiveObserverRuntimeRow Runtime,
    int ConsecutiveValidBars = 0);
