using NiftySignal.Trading.Core.Scheduling;

namespace NiftySignal.Tests.Trading.Core;

public class DeadlineQueueTests
{
    private static ScheduledDeadline D(DateTimeOffset t, DeadlineClass c, int variant, string key, long seq) => new(t, c, variant, key, seq);

    [Fact]
    public void EqualTime_OrdersByClassThenVariantThenKeyThenCreation()
    {
        var t = Clock.Ist(15, 0);
        var q = new DeadlineQueue();
        q.Add(D(t, DeadlineClass.StrategyTimer, 0, "a", 1));
        q.Add(D(t, DeadlineClass.RiskRetry, 1, "a", 2));
        q.Add(D(t, DeadlineClass.RiskRetry, 0, "b", 3));
        q.Add(D(t, DeadlineClass.RiskRetry, 0, "a", 5));
        q.Add(D(t, DeadlineClass.RiskRetry, 0, "a", 4));
        q.Add(D(t, DeadlineClass.SessionBoundary, ScheduledDeadline.GlobalOrdinal, "session:EntryClose", 6));
        q.Add(D(t, DeadlineClass.ExecutionMaturity, 9, "z", 7));

        var order = new List<long>();
        while (q.Count > 0)
        {
            order.Add(q.Pop().CreationSeq);
        }

        Assert.Equal(new long[] { 7, 6, 4, 5, 3, 2, 1 }, order);
    }

    [Fact]
    public void EarlierTime_AlwaysFirst_EvenWithLowerClassRank()
    {
        var q = new DeadlineQueue();
        q.Add(D(Clock.Ist(15, 0, 0, 1), DeadlineClass.ExecutionMaturity, 0, "late", 1));
        q.Add(D(Clock.Ist(15, 0), DeadlineClass.StrategyTimer, 0, "early", 2));

        Assert.Equal("early", q.Pop().Key);
    }

    [Fact]
    public void Cancel_RemovesOnlyThatDeadline_AndIsIdempotent()
    {
        var q = new DeadlineQueue();
        q.Add(D(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "a", 1));
        q.Add(D(Clock.Ist(10, 0), DeadlineClass.StrategyTimer, 0, "b", 2));

        Assert.True(q.Cancel(new DeadlineHandle(1)));
        Assert.False(q.Cancel(new DeadlineHandle(1)));
        Assert.Equal("b", q.Pop().Key);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void KeyComparison_IsOrdinal_UnderAnyCulture()
    {
        var t = Clock.Ist(10, 0);
        var first = D(t, DeadlineClass.StrategyTimer, 0, "I", 1);
        var second = D(t, DeadlineClass.StrategyTimer, 0, "i", 2);

        var invariant = DeadlineOrdering.Instance.Compare(first, second);
        var turkish = Culture.Under("tr-TR", () => DeadlineOrdering.Instance.Compare(first, second));

        Assert.True(invariant < 0);
        Assert.Equal(invariant, turkish);
    }
}
