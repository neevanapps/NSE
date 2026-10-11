using NiftySignal.AdaptiveObserver;
using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Trading.Adapters;

/// <summary>
/// The single definition of the readiness evidence attached to a completed bar (contract section A.16.3): validity by the adaptive readiness policy, the length of the valid
/// suffix, whether the signal start has been reached. The tick-driven translator and the live bridge (finalized bars from the Adaptive Observer) both call it, so a replayed
/// session and a live session can never disagree about whether a bar was ready.
/// </summary>
public static class BarReadiness
{
    /// <param name="bar">The completed bar.</param>
    /// <param name="futureBars">All completed bars of the session so far, in sequence order, including <paramref name="bar"/>.</param>
    /// <param name="baseBarVolume">The frozen target volume of a base bar.</param>
    /// <param name="signalStartUtc">The frozen signal start of the session.</param>
    /// <param name="readinessSpec">Version label of the readiness definition (see <see cref="TranslatorOptions.ReadinessSpec"/>).</param>
    public static BarInfo Build(ExactAdaptiveBar bar, IReadOnlyList<ExactAdaptiveBar> futureBars, long baseBarVolume, DateTimeOffset signalStartUtc, string readinessSpec)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(futureBars);
        var valid = bar.IsComplete && AdaptiveReadinessPolicy.IsValid(bar.Volume, baseBarVolume, bar.Open, bar.High, bar.Low, bar.Close, bar.TradeUpdates, bar.StartAvailableAtUtc, bar.EndAvailableAtUtc);
        var consecutive = AdaptiveReadinessPolicy.ConsecutiveValidBars(futureBars, baseBarVolume, bar.BarSeq);
        var signalStarted = bar.EndAvailableAtUtc >= signalStartUtc;
        var ready = consecutive >= AdaptiveReadinessPolicy.RequiredBars && signalStarted;
        return new BarInfo(
            bar.StartAvailableAtUtc,
            bar.EndAvailableAtUtc,
            bar.Volume,
            bar.TradeUpdates,
            valid,
            consecutive,
            AdaptiveReadinessPolicy.RequiredBars,
            signalStarted,
            ready,
            readinessSpec);
    }

    /// <summary>The readiness label of a live session (the live counterpart of <see cref="TranslatorOptions.ReadinessSpec"/>, which needs a full session freeze).</summary>
    public static string SpecLabel(DateTimeOffset signalStartUtc, string modelVersion)
        => FormattableString.Invariant($"adaptive-readiness-v1;consecutiveValidBars={AdaptiveReadinessPolicy.RequiredBars};signalStart={signalStartUtc:O};model={modelVersion}");
}
