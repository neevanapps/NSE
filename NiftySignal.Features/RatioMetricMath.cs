namespace NiftySignal.Features;

/// <summary>
/// Pure, stateless math for the ratio-based composite's five clipped inputs (weekend build,
/// 2026-09-09) -- see the fix plan's "New Initiative" section for the full design. Every
/// metric ends up on the same [-1,1] scale via one of these two transforms before being
/// weighted and summed by RatioScoreCalculator.
/// </summary>
public static class RatioMetricMath
{
    /// <summary>
    /// Log-ratio clip: null if <paramref name="ratio"/> is null or &lt;=0 (a ratio can't be
    /// non-positive and still mean anything here), else
    /// <c>clamp(log(ratio)/log(rMax), -1, 1)</c>. Boundary contract: ratio==rMax -&gt; 1.0,
    /// ratio==1/rMax -&gt; -1.0, ratio==1 -&gt; 0.0. Log, not raw ratio, because a raw ratio is
    /// asymmetric around 1.0 (a call/put ratio of 2.0 and 0.5 are equally "extreme" in opposite
    /// directions, but 0.5 sits four times closer to 1.0 than 2.0 does on a linear scale) --
    /// log-ratio is symmetric around 0, so every metric lands on the same clipped scale after
    /// this transform regardless of its own natural range.
    ///
    /// Deliberately does NOT apply any epsilon padding itself -- a caller whose metric needs
    /// one (e.g. the sized-OI-flow ratio, which can see a genuinely zero denominator on a quiet
    /// 15s bar) is responsible for computing an already-epsilon-adjusted ratio and passing that
    /// in. Keeping the two concerns apart means this function's own null/&lt;=0 contract stays
    /// simple, and its own tests don't have to reason about any particular caller's epsilon
    /// choice.
    /// </summary>
    public static double? ClipLogRatio(double? ratio, double rMax)
    {
        if (ratio is not { } r || r <= 0)
        {
            return null;
        }

        return Math.Clamp(Math.Log(r) / Math.Log(rMax), -1.0, 1.0);
    }

    /// <summary>
    /// Scaled-difference clip: null if <paramref name="diff"/> is null, else
    /// <c>clamp(diff/scale, -1, 1)</c>. Used for the residual metric, which is a difference
    /// (call residual minus put residual), not a ratio -- a ratio blows up near zero, which a
    /// residual genuinely can be.
    /// </summary>
    public static double? ClipScaledDifference(double? diff, double scale)
    {
        if (diff is not { } d)
        {
            return null;
        }

        return Math.Clamp(d / scale, -1.0, 1.0);
    }
}
