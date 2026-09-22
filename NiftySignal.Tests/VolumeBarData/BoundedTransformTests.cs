using NiftySignal.VolumeBarData;
using Xunit;

namespace NiftySignal.Tests.VolumeBarData;

/// <summary>
/// 2026-09-22 bounded-transform research task (docs/VOLUME_BAR_FINDINGS.md's dated "Bounded-transform
/// score research" section): coverage for <see cref="RunningMeanStd"/> (Welford's online algorithm)
/// and <see cref="BoundedTransform"/> (session z-score, tanh-squashed) against deterministic synthetic
/// sequences with hand-computed expected output, same "prefer a separable, unit-testable function"
/// discipline <see cref="ExponentialMeanTests"/> already establishes for the sibling Part A smoothers.
/// </summary>
public class BoundedTransformTests
{
    [Fact]
    public void RunningMeanStd_NoValues_CountZeroMeanZeroStdZero()
    {
        var tracker = new RunningMeanStd();

        Assert.Equal(0, tracker.Count);
        Assert.Equal(0.0, tracker.Mean);
        Assert.Equal(0.0, tracker.StdDev);
    }

    [Fact]
    public void RunningMeanStd_OneValue_MeanIsThatValueStdIsZero()
    {
        var tracker = new RunningMeanStd();
        tracker.Add(10.0);

        Assert.Equal(1, tracker.Count);
        Assert.Equal(10.0, tracker.Mean);
        Assert.Equal(0.0, tracker.StdDev); // std undefined with n=1 -- convention: 0, not NaN.
    }

    [Fact]
    public void RunningMeanStd_MatchesHandComputedSampleMeanAndStdDev()
    {
        // Values: 2, 4, 4, 4, 5, 5, 7, 9 -- textbook sample-std example, mean=5, sample std=2
        // (Bessel-corrected, n-1 denominator: sum((x-mean)^2)=32, 32/7=4.5714..., sqrt=2.1381...).
        var tracker = new RunningMeanStd();
        foreach (var v in new[] { 2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0 })
        {
            tracker.Add(v);
        }

        Assert.Equal(8, tracker.Count);
        Assert.Equal(5.0, tracker.Mean, precision: 10);
        Assert.Equal(2.13808993529939, tracker.StdDev, precision: 10);
    }

    [Fact]
    public void BoundedTransform_Compute_FirstTwoObservations_ReturnNullAndWarmUpTracker()
    {
        // Needs >=2 PRIOR observations before a z-score is meaningful -- the first two calls each
        // just add to the tracker and return null (no fabricated reading against undefined spread).
        var tracker = new RunningMeanStd();

        var first = BoundedTransform.Compute(10.0, tracker);
        var second = BoundedTransform.Compute(20.0, tracker);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void BoundedTransform_Compute_ReadsStatsBeforeAdding_SelfInclusionSafe()
    {
        // Same "rank before adding" convention SignedRank.Compute follows. Seed with [10, 20]
        // (mean=15, std=7.0710678...). Third observation (30) is z-scored against THAT prior
        // mean/std, not one that already includes 30 itself.
        var tracker = new RunningMeanStd();
        tracker.Add(10.0);
        tracker.Add(20.0);

        var result = BoundedTransform.Compute(30.0, tracker);

        var expectedZ = (30.0 - 15.0) / (10.0 / Math.Sqrt(2)); // std of [10,20] = sqrt(50) = 7.0710678...
        var expectedTanh = Math.Tanh(expectedZ);
        Assert.Equal(expectedTanh, result!.Value, precision: 8);
        Assert.Equal(3, tracker.Count); // the observed value was added AFTER the z-score was read.
    }

    [Fact]
    public void BoundedTransform_Compute_OutputAlwaysBoundedToAtMostOneInMagnitude()
    {
        // Even an extreme outlier (many std devs away) never exceeds tanh's own [-1,1] range by
        // construction -- the whole point of the tanh squash versus an unbounded z-score. A truly
        // enormous z-score saturates to exactly +/-1.0 in double precision (tanh's own asymptote),
        // which is the expected, bounded behavior, not an overflow.
        var tracker = new RunningMeanStd();
        tracker.Add(1.0);
        tracker.Add(1.0);
        tracker.Add(1.01); // tiny variance -> huge z-score for a far outlier next.

        var result = BoundedTransform.Compute(1000.0, tracker);

        Assert.NotNull(result);
        Assert.True(result!.Value <= 1.0 && result.Value >= -1.0);
        Assert.True(result.Value > 0.999); // saturates close to (or at) +1 for a huge positive outlier.
    }

    [Fact]
    public void BoundedTransform_Compute_ModerateOutlier_StaysStrictlyWithinOpenInterval()
    {
        // A more realistic z-score (a few std devs, not thousands) stays strictly inside (-1,1),
        // not just at-or-inside [-1,1] -- confirms tanh only SATURATES at extreme z, it doesn't
        // clip to exactly +/-1 for every out-of-range reading.
        var tracker = new RunningMeanStd();
        tracker.Add(10.0);
        tracker.Add(20.0);
        tracker.Add(15.0);

        var result = BoundedTransform.Compute(25.0, tracker); // a few std devs above the running mean.

        Assert.NotNull(result);
        Assert.True(result!.Value < 1.0 && result.Value > 0.0);
    }

    [Fact]
    public void BoundedTransform_Compute_ZeroVariance_ZScoreDefinedAsZero()
    {
        // Every prior reading identical (std=0) -- division by zero avoided; z-score treated as
        // neutral (0), so tanh(0)=0, rather than NaN/infinity.
        var tracker = new RunningMeanStd();
        tracker.Add(5.0);
        tracker.Add(5.0);

        var result = BoundedTransform.Compute(5.0, tracker);

        Assert.Equal(0.0, result!.Value, precision: 10);
    }

    [Fact]
    public void BoundedTransform_Compute_NullRaw_ReturnsNullAndLeavesTrackerUntouched()
    {
        // Same null-passthrough convention every other smoother/normalizer in this file follows: a
        // bar with no reading leaves the tracker state untouched and yields a null score that bar.
        var tracker = new RunningMeanStd();
        tracker.Add(10.0);
        tracker.Add(20.0);

        var result = BoundedTransform.Compute(null, tracker);

        Assert.Null(result);
        Assert.Equal(2, tracker.Count); // unchanged -- the null observation was never added.
    }

    [Fact]
    public void BoundedTransform_Compute_ValueBelowMean_ReturnsNegativeSign()
    {
        var tracker = new RunningMeanStd();
        tracker.Add(10.0);
        tracker.Add(20.0);
        tracker.Add(15.0);

        var result = BoundedTransform.Compute(1.0, tracker); // well below the running mean so far.

        Assert.NotNull(result);
        Assert.True(result!.Value < 0);
    }
}
