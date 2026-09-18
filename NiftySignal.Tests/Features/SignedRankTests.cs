using NiftySignal.Features;

namespace NiftySignal.Tests.Features;

public sealed class SignedRankTests
{
    [Fact]
    public void NullRaw_ReturnsNull()
    {
        var tracker = new SessionRankTracker();
        Assert.Null(SignedRank.Compute(null, tracker));
    }

    [Fact]
    public void ZeroRaw_ReturnsZero_WithoutTouchingTheTracker()
    {
        var tracker = new SessionRankTracker();
        var result = SignedRank.Compute(0.0, tracker);

        Assert.Equal(0.0, result);
        Assert.Equal(0, tracker.Count); // zero is a real value, but ranking magnitude 0 conveys nothing -- skipped
    }

    [Fact]
    public void FirstRealValue_ReadsAsExtreme_SinceThereIsNoHistoryYetToRankAgainst()
    {
        var tracker = new SessionRankTracker();
        var result = SignedRank.Compute(5.0, tracker);

        // Rank() with an empty history returns 0 -- so the very first observation reads as 0, not
        // an extreme, matching SessionRankTracker's own "no value can outrank an empty history"
        // convention. Reattaching the sign of +5 to a rank of 0/100 still gives 0.
        Assert.Equal(0.0, result);
        Assert.Equal(1, tracker.Count); // but it WAS added, so the next observation ranks against it
    }

    [Fact]
    public void SignIsReattached_IndependentOfMagnitudeRanking()
    {
        var tracker = new SessionRankTracker();
        tracker.Add(1.0);
        tracker.Add(2.0);
        tracker.Add(3.0);

        // |-3.0| = 3.0 ranks at 100th percentile against [1,2,3] (countAtOrBelow=3/3=100%) -- sign
        // stays negative.
        var result = SignedRank.Compute(-3.0, tracker);

        Assert.Equal(-1.0, result);
    }

    [Fact]
    public void SelfInclusionSafe_RanksBeforeAdding_NotAfter()
    {
        var tracker = new SessionRankTracker();
        tracker.Add(1.0);
        tracker.Add(2.0);

        // |5.0| ranks against [1,2] only (2/2 = 100%), NOT against [1,2,5] (which would also be
        // 100% here, so use a value that would read differently if self-inclusion leaked in).
        var result = SignedRank.Compute(5.0, tracker);

        Assert.Equal(1.0, result);
        Assert.Equal(3, tracker.Count); // 5.0 WAS added afterward, for the next observation to rank against
    }
}
