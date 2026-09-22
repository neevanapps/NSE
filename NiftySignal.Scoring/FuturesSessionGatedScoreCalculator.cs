using NiftySignal.Features;

namespace NiftySignal.Scoring;

/// <summary>
/// The locked <c>SessionGatedDepthDurationConfirmed</c> futures score, plus its own Open-window TOB
/// confirmation gate -- 2026-09-21, futures-crossover live-wiring task, step 1 ("shared pure
/// function, no duplicate implementation", same discipline <see cref="OptionsThreeWayScoreCalculator"/>
/// already established for the options side's own 2026-09-20 extraction). Extracted verbatim out of
/// <c>NiftySignal.VolumeBarData/TradeSimulator.cs</c>'s private <c>ComputeSessionGatedScore</c>/
/// <c>ComputeBarDurationScore</c>/the TOB branch of <c>PassesConfirmation</c> (formula and constants
/// unchanged -- a pure relocation, not a rewrite) so both the offline backtest
/// (<c>TradeSimulator.SimulateDayAsync</c>'s <c>SessionGatedDepthDurationConfirmed</c> dispatch AND
/// <c>SimulateCrossoverDayAsync</c>'s own futures branch, both of which called the same private
/// method before this extraction) and the new live futures-crossover pipeline
/// (<c>NiftySignal.VolumeBarData.LiveFuturesCrossoverSession</c>) call the exact same code, instead
/// of three hand-synced copies risking drift.
///
/// Never blended -- exactly one leg's own score is the traded score for a given bar, decided purely
/// by session time-of-day: Depth Imbalance (<c>(ΣBidQty-ΣAskQty)/(ΣBidQty+ΣAskQty)</c>, session-rank
/// normalized) before <paramref name="sessionGateSwitchTime"/> (10:00 IST by default), Bar Duration
/// Urgency (signed by this bar's own price direction, magnitude 1/DurationSeconds, session-rank
/// normalized) at/after it.
/// </summary>
public static class FuturesSessionGatedScoreCalculator
{
    /// <summary>The Open/Mid-Close switch boundary -- 10:00 IST, matching <c>TradeSimulator.SessionGateSwitchTime</c>. A real parameter (not a fixed constant) since the offline backtest already sweeps it via <c>--optionsSwitchTime</c>-style overrides for other session-gated metrics; defaults to the locked live value.</summary>
    public static readonly TimeSpan DefaultSessionGateSwitchTime = new(10, 0, 0);

    /// <summary>
    /// Computes one bar's FuturesScore. <paramref name="depthRank"/>/<paramref name="durationRank"/>
    /// are fed every bar regardless of which leg is active, so each tracker's own running
    /// distribution matches what the standalone metric would see over the same day (same convention
    /// the pre-extraction inline code already followed).
    /// </summary>
    public static double? ComputeScore(
        FuturesSessionGatedScoreInputs bar,
        SessionRankTracker depthRank,
        SessionRankTracker durationRank,
        TimeSpan? sessionGateSwitchTime = null)
    {
        var depth = SignedRank.Compute(bar.DepthImbalance, depthRank);
        var duration = ComputeBarDurationScore(bar, durationRank);
        return bar.TimeOfDayIst < (sessionGateSwitchTime ?? DefaultSessionGateSwitchTime) ? depth : duration;
    }

    /// <summary>Signed by this bar's own price direction, magnitude is fill speed (1/DurationSeconds) -- see the pre-extraction <c>VolumeBarMetric.BarDurationUrgency</c> doc comment for why duration alone can't carry a sign. A zero price change reads as a genuine zero (still ranked), not null -- same convention the pre-extraction code used.</summary>
    static double? ComputeBarDurationScore(FuturesSessionGatedScoreInputs bar, SessionRankTracker durationRank)
    {
        if (bar.PreviousClose is not { } prevClose || bar.DurationSeconds <= 0)
        {
            return null;
        }

        var priceChange = bar.ClosePrice - prevClose;
        if (priceChange == 0)
        {
            return SignedRank.Compute(0.0, durationRank);
        }

        return SignedRank.Compute(Math.Sign(priceChange) * (1.0 / bar.DurationSeconds), durationRank);
    }

    /// <summary>This bar's own TOB confirmation reading -- session-rank normalized <c>TopOfBookImbalance</c>, fed every bar (not just during the Open window) so its own tracker matches what the standalone metric would see, same convention as <see cref="ComputeScore"/>'s two legs.</summary>
    public static double? ComputeTobConfirmationScore(FuturesSessionGatedScoreInputs bar, SessionRankTracker tobRank) =>
        SignedRank.Compute(bar.TopOfBookImbalance, tobRank);

    /// <summary>
    /// Entry-time gate for the locked <c>SessionGatedDepthDurationConfirmed</c> score (and, by
    /// direct reuse, the futures-crossover strategy's own entry gate -- see
    /// <c>LiveFuturesCrossoverSession</c>): during the Open window (before
    /// <paramref name="sessionGateSwitchTime"/>) a new position additionally requires
    /// <paramref name="tobConfirmScore"/> to agree in sign with <paramref name="scaledScore"/>;
    /// outside the Open window, no confirmation is required (TOB's own edge there is close to noise
    /// -- see the pre-extraction enum value's own doc comment for why).
    /// </summary>
    public static bool PassesTobConfirmation(TimeSpan timeOfDayIst, double scaledScore, double? tobConfirmScore, TimeSpan? sessionGateSwitchTime = null)
    {
        if (timeOfDayIst >= (sessionGateSwitchTime ?? DefaultSessionGateSwitchTime))
        {
            return true;
        }

        return tobConfirmScore is { } t && Math.Sign(t) == Math.Sign(scaledScore);
    }
}
