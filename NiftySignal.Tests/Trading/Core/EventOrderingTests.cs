using NiftySignal.Trading.Core.Events;

namespace NiftySignal.Tests.Trading.Core;

public class EventOrderingTests
{
    [Fact]
    public void EqualTimestamp_OrdersSpotThenOptionsThenFutureThenBar()
    {
        var t = Clock.Ist(10, 0);
        var shuffled = new EngineEvent[]
        {
            Ev.Bar(t, 7), Ev.Fut(t, 5), Ev.Opt(t, 9), Ev.Spot(t, 99), Ev.Opt(t, 3), Ev.Fut(t, 4),
        };

        var ordered = shuffled.OrderBy(e => e, EventOrdering.Instance).Select(e => (e.Kind, e.SourceId)).ToArray();

        Assert.Equal(
            new[]
            {
                (EventKind.SpotQuote, 99L), (EventKind.OptionQuote, 3L), (EventKind.OptionQuote, 9L),
                (EventKind.FutureQuote, 4L), (EventKind.FutureQuote, 5L), (EventKind.BarCompleted, 7L),
            },
            ordered);
    }

    [Fact]
    public void EarlierAvailableAt_AlwaysPrecedesLaterRegardlessOfKind()
    {
        var early = Ev.Bar(Clock.Ist(10, 0), 100);
        var late = Ev.Spot(Clock.Ist(10, 0).AddTicks(1), 1);

        Assert.True(EventOrdering.Instance.Compare(early, late) < 0);
        Assert.True(EventOrdering.Instance.Compare(late, early) > 0);
    }

    [Fact]
    public void Ordering_IsIndependentOfInputPermutation()
    {
        var t = Clock.Ist(11, 30);
        var events = new List<EngineEvent>();
        for (var i = 0; i < 40; i++)
        {
            events.Add(Ev.Opt(t, 1000 - i, token: "OPT" + (i % 5)));
            events.Add(Ev.Fut(t, 500 + i));
        }

        events.Add(Ev.Bar(t, 3));
        events.Add(Ev.Spot(t, 77));
        var baseline = events.OrderBy(e => e, EventOrdering.Instance).Select(e => (e.Kind, e.SourceId)).ToArray();

        var rng = new Random(12345);
        for (var run = 0; run < 100; run++)
        {
            var permutation = events.OrderBy(_ => rng.Next()).ToList();
            var ordered = permutation.OrderBy(e => e, EventOrdering.Instance).Select(e => (e.Kind, e.SourceId)).ToArray();
            Assert.Equal(baseline, ordered);
        }
    }

    [Fact]
    public void Ordering_UsesOrdinalTokenComparison_UnderAnyCulture()
    {
        var t = Clock.Ist(10, 0);
        var a = new QuoteEvent(t, EventKind.OptionQuote, 1, "I", 1m, 2m);
        var b = new QuoteEvent(t, EventKind.OptionQuote, 1, "i", 1m, 2m);

        var invariant = EventOrdering.Instance.Compare(a, b);
        var turkish = Culture.Under("tr-TR", () => EventOrdering.Instance.Compare(a, b));

        Assert.True(invariant < 0);
        Assert.Equal(invariant, turkish);
    }

    [Fact]
    public void NonUtcAvailableAt_IsRejected()
    {
        var local = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.FromHours(5.5));

        Assert.Throws<ArgumentException>(() => Ev.Opt(local, 1));
    }

    [Fact]
    public void QuoteEvent_CannotHaveBarKind()
    {
        Assert.Throws<ArgumentException>(() => new QuoteEvent(Clock.Ist(10, 0), EventKind.BarCompleted, 1, "X", 1m, 2m));
    }
}
