using NiftySignal.Features;

namespace NiftySignal.Scoring;

/// <summary>
/// The Max Pain confirmation filter behind <c>OptionsScoreThreeWaySwitchMaxPainConfirmed</c> --
/// 2026-09-20, step 1 of the live/backtest parity plan: extracted out of
/// `NiftySignal.VolumeBarData/TradeSimulator.cs`'s own <c>PassesConfirmation</c> Max Pain branch
/// (formula unchanged -- a pure relocation). Never drives the traded score itself; a new position
/// additionally requires this gate's own sign to agree with whichever <see cref="OptionsThreeWayScoreCalculator"/>
/// leg is active, applied all day (no session-window carve-out, unlike the futures side's own
/// open-only TOB gate -- Max Pain distance is computable, and was found useful, every bar).
/// </summary>
public static class MaxPainConfirmationGate
{
    /// <summary>
    /// This bar's own confirmation score: distance from the future's close to the Max Pain strike,
    /// sign-flipped (price trading ABOVE max pain reads bearish -- pulled back down toward it) and
    /// session-rank normalized, same "pinning" hypothesis <c>DistanceToMaxPain</c> itself tests as a
    /// standalone metric. Null if this bar has no Max Pain reading yet (e.g. no usable OI anywhere
    /// in the chain so far today).
    /// </summary>
    public static double? ComputeScore(decimal closePrice, decimal? maxPainStrike, SessionRankTracker rank) =>
        maxPainStrike is { } strike
            ? SignedRank.Compute(-(double)(closePrice - strike), rank)
            : null;

    /// <summary>
    /// True only when a Max Pain reading exists this bar AND its sign agrees with the traded score's
    /// own sign -- matches `PassesConfirmation`'s exact condition
    /// (<c>maxPainConfirmScore is {} mp &amp;&amp; Math.Sign(mp) == Math.Sign(scaledScore)</c>).
    /// </summary>
    public static bool Passes(double? maxPainConfirmScore, double scaledScore) =>
        maxPainConfirmScore is { } mp && Math.Sign(mp) == Math.Sign(scaledScore);
}
