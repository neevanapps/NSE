namespace NiftySignal.Features;

/// <summary>
/// Ranks |raw| against a metric's own session-so-far distribution of magnitudes and reattaches
/// raw's sign -- the "no hardcoded magnitude, no look-ahead" normalization this project prefers
/// by default for any metric whose real distribution isn't yet well-understood (see
/// docs/SCORE_CANDIDATES.md's own "Normalization and gating" section: session-rank based is "the
/// safer default while a metric's real distribution is still unknown"; a fixed tanh scale is
/// preferred only once it is). Reads the rank BEFORE adding (self-inclusion-safe, no
/// same-bar/same-cadence leakage) -- same convention every other caller of
/// <see cref="SessionRankTracker"/> in this codebase already follows.
///
/// Deliberately a small, standalone, Features-level utility rather than a copy of
/// `NiftySignal.MetricTrials.CoreScoreOptionSimulator`'s own private `RankSigned` -- that one
/// stays exactly as it is (already validated, don't risk it), this is for new code
/// (`NiftySignal.VolumeBarData`) that needs the identical technique on a different clock.
/// </summary>
public static class SignedRank
{
    public static double? Compute(double? raw, SessionRankTracker tracker)
    {
        if (raw is not { } value)
        {
            return null;
        }

        if (value == 0)
        {
            return 0.0;
        }

        var signed = Math.Sign(value) * (tracker.Rank(Math.Abs(value)) / 100.0);
        tracker.Add(Math.Abs(value));
        return signed;
    }
}
