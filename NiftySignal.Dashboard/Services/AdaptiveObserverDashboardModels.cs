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
    int ConsecutiveValidBars = 0,
    // Sidecar rows (current MetricsVersion) keyed by BarSeq. Absent bars simply have no supplemental metrics.
    IReadOnlyDictionary<int, AdaptiveFuturesSupplementalRow>? FuturesSupplemental = null,
    IReadOnlyDictionary<int, AdaptiveOptionsSupplementalRow>? OptionsSupplemental = null,
    // The residual rows of the bar immediately before the oldest displayed bar, so the adjacent delta of the oldest row is computable.
    IReadOnlyList<AdaptiveOptionResidualBarRow>? PreviousResiduals = null,
    // Bars whose sidecar failed replay verification (durable marker). Their stored values are withheld, which is different from "missing".
    IReadOnlySet<int>? InvalidFuturesSupplementalBars = null,
    IReadOnlySet<int>? InvalidOptionsSupplementalBars = null,
    // Basis sidecar (current MetricsVersion) keyed by BarSeq, with known-invalid bars withheld.
    IReadOnlyDictionary<int, AdaptiveBasisSupplementalRow>? BasisSupplemental = null,
    IReadOnlySet<int>? InvalidBasisSupplementalBars = null)
{
    public int InvalidSupplementalBarCount => (InvalidFuturesSupplementalBars?.Count ?? 0) + (InvalidOptionsSupplementalBars?.Count ?? 0) + (InvalidBasisSupplementalBars?.Count ?? 0);

    public bool CanUseForDecisions => ConsecutiveValidBars >= NiftySignal.AdaptiveObserver.AdaptiveReadinessPolicy.RequiredBars
        && Runtime.RuntimeStatus == AdaptiveRuntimeStatus.Live;
}

public sealed record AdaptiveObserverHeaderSnapshot(
    AdaptiveSessionStateRow Session,
    AdaptiveObserverRuntimeRow Runtime,
    int ConsecutiveValidBars = 0);
