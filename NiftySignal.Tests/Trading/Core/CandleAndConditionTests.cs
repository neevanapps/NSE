using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Quotes;

namespace NiftySignal.Tests.Trading.Core;

public class CandleStoreTests
{
    private static QuoteEvent Opt(DateTimeOffset at, long id, string token, decimal? bid, decimal? ask) => Ev.Opt(at, id, token, bid, ask);

    private static CandleStore Store(CandleConfig? config = null) => new(config ?? new CandleConfig());

    [Fact]
    public void Candle_CoversQuotesBetweenConsecutiveBars_AndASameInstantQuoteBelongsToTheClosingBar()
    {
        var store = Store();
        var t1 = Clock.Ist(10, 1);
        var t2 = Clock.Ist(10, 2);

        store.Observe(Opt(Clock.Ist(10, 0, 30), 1, "C", 99, 100));
        store.Observe(Opt(t1, 2, "C", 100, 101));        // same availability group as bar 1's closing tick: ordered before the bar event
        store.PublishBar(1, t1);
        store.Observe(Opt(Clock.Ist(10, 1, 30), 3, "C", 102, 103));
        store.PublishBar(2, t2);

        var view = store.ViewFor("C");
        Assert.Equal(101m, view.Get(1)!.LastAsk);
        Assert.Equal(2, view.Get(1)!.Observations);
        Assert.Equal(103m, view.Get(2)!.LastAsk);
        Assert.Equal(1, view.Get(2)!.Observations);
    }

    [Fact]
    public void Candle_IsInvisibleUntilPublished()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0), 1, "C", 99, 100));

        Assert.Null(store.ViewFor("C").Get(1));
        Assert.Equal(WindowStatus.NoBarsPublished, store.ViewFor("C").ConsecutiveWindow(1).Status);

        store.PublishBar(1, Clock.Ist(10, 1));
        Assert.NotNull(store.ViewFor("C").Get(1));
    }

    [Fact]
    public void PriceSources_AreComputedFromTheWindowOnly()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0, 1), 1, "C", 99, 101));    // mid 100
        store.Observe(Opt(Clock.Ist(10, 0, 2), 2, "C", 100, 102));   // mid 101
        store.Observe(Opt(Clock.Ist(10, 0, 3), 3, "C", 98, 100));    // mid 99 (last)
        store.PublishBar(1, Clock.Ist(10, 1));
        var candle = store.ViewFor("C").Get(1)!;

        Assert.True(candle.IsValid);
        Assert.Equal(100m, candle.Price(CandlePriceSource.AskClose));
        Assert.Equal(98m, candle.Price(CandlePriceSource.BidClose));
        Assert.Equal(99m, candle.Price(CandlePriceSource.MidClose));
        Assert.Equal((101m + 99m + 99m) / 3m, candle.Price(CandlePriceSource.TypicalPrice));
    }

    [Fact]
    public void MissingData_YieldsAnInvalidCandle_NeverAForwardFill()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0), 1, "C", 99, 100));
        store.PublishBar(1, Clock.Ist(10, 1));
        store.PublishBar(2, Clock.Ist(10, 2));                       // no quotes at all in bar 2

        var second = store.ViewFor("C").Get(2)!;
        Assert.False(second.IsValid);
        Assert.Equal(CandleInvalidReason.NoObservations, second.InvalidReason);
        Assert.Null(second.Price(CandlePriceSource.AskClose));
        Assert.Equal(WindowStatus.InvalidCandleInWindow, store.ViewFor("C").ConsecutiveWindow(2).Status);
    }

    [Fact]
    public void NonExecutableQuotes_AreNotObservations_AndTheirAbsenceIsNotCarried()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0, 1), 1, "C", 99, 100));
        store.Observe(Opt(Clock.Ist(10, 0, 2), 2, "C", null, 105));  // one-sided
        store.Observe(Opt(Clock.Ist(10, 0, 3), 3, "C", 0, 106));
        store.Observe(Opt(Clock.Ist(10, 0, 4), 4, "C", 110, 109));   // crossed
        store.PublishBar(1, Clock.Ist(10, 1));
        store.Observe(Opt(Clock.Ist(10, 1, 1), 5, "C", null, 105));  // only a one-sided quote in bar 2
        store.PublishBar(2, Clock.Ist(10, 2));

        Assert.Equal(1, store.ViewFor("C").Get(1)!.Observations);
        Assert.Equal(100m, store.ViewFor("C").Get(1)!.LastAsk);
        Assert.False(store.ViewFor("C").Get(2)!.IsValid);
    }

    [Fact]
    public void MinimumObservationsAndCloseAge_AreEnforced()
    {
        var strict = Store(new CandleConfig { MinObservationsPerCandle = 2, MaxCloseAge = TimeSpan.FromSeconds(30) });
        strict.Observe(Opt(Clock.Ist(10, 0, 50), 1, "A", 99, 100));                    // one observation only
        strict.Observe(Opt(Clock.Ist(10, 0, 1), 2, "B", 99, 100));
        strict.Observe(Opt(Clock.Ist(10, 0, 2), 3, "B", 99, 100));                     // two observations but 58 s before the bar end
        strict.Observe(Opt(Clock.Ist(10, 0, 40), 4, "C", 99, 100));
        strict.Observe(Opt(Clock.Ist(10, 0, 45), 5, "C", 99, 100));                    // two observations, 15 s before the end
        strict.PublishBar(1, Clock.Ist(10, 1));

        Assert.Equal(CandleInvalidReason.TooFewObservations, strict.ViewFor("A").Get(1)!.InvalidReason);
        Assert.Equal(CandleInvalidReason.StaleClose, strict.ViewFor("B").Get(1)!.InvalidReason);
        Assert.True(strict.ViewFor("C").Get(1)!.IsValid);
    }

    [Fact]
    public void Tokens_AreIsolated_AndOtherKindsAreIgnored()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0), 1, "C1", 99, 100));
        store.Observe(Ev.Fut(Clock.Ist(10, 0), 2));
        store.Observe(Ev.Spot(Clock.Ist(10, 0), 3));
        store.PublishBar(1, Clock.Ist(10, 1));

        Assert.True(store.ViewFor("C1").Get(1)!.IsValid);
        Assert.Null(store.ViewFor("C2").Get(1));
        Assert.Null(store.ViewFor("FUT").Get(1));
    }

    [Fact]
    public void ConsecutiveWindow_ReportsStatusExactly()
    {
        var store = Store();
        for (var bar = 1; bar <= 5; bar++)
        {
            if (bar != 3)
            {
                store.Observe(Opt(Clock.Ist(10, bar - 1, 30), bar, "C", 99, 100 + bar));
            }

            store.PublishBar(bar, Clock.Ist(10, bar));
        }

        var view = store.ViewFor("C");
        Assert.Equal(WindowStatus.Ready, view.ConsecutiveWindow(2).Status);                    // bars 4..5
        Assert.Equal(new long[] { 4, 5 }, view.ConsecutiveWindow(2).Candles.Select(c => c.BarSeq).ToArray());
        var gap = view.ConsecutiveWindow(3);                                                    // bars 3..5, bar 3 invalid
        Assert.Equal(WindowStatus.InvalidCandleInWindow, gap.Status);
        Assert.Equal(3, gap.OffendingBarSeq);
        Assert.Equal(WindowStatus.InsufficientHistory, view.ConsecutiveWindow(6).Status);
        Assert.Equal(WindowStatus.InsufficientHistory, store.ViewFor("LATE").ConsecutiveWindow(1).Status);
    }

    [Fact]
    public void ATokenFirstSeenLate_HasInsufficientHistoryForEarlierBars_NotAGap()
    {
        var store = Store();
        store.PublishBar(1, Clock.Ist(10, 1));
        store.PublishBar(2, Clock.Ist(10, 2));
        store.Observe(Opt(Clock.Ist(10, 2, 30), 1, "NEW", 99, 100));
        store.PublishBar(3, Clock.Ist(10, 3));

        Assert.Equal(WindowStatus.Ready, store.ViewFor("NEW").ConsecutiveWindow(1).Status);
        Assert.Equal(WindowStatus.InsufficientHistory, store.ViewFor("NEW").ConsecutiveWindow(2).Status);
    }

    [Fact]
    public void BarSequence_MustIncrease_AndSkippedBarsStayAbsent()
    {
        var store = Store();
        store.Observe(Opt(Clock.Ist(10, 0), 1, "C", 99, 100));
        store.PublishBar(1, Clock.Ist(10, 1));
        store.Observe(Opt(Clock.Ist(10, 2), 2, "C", 99, 100));
        store.PublishBar(3, Clock.Ist(10, 3));                       // bar 2 never arrived

        Assert.Throws<InvalidOperationException>(() => store.PublishBar(3, Clock.Ist(10, 4)));
        Assert.Throws<InvalidOperationException>(() => store.PublishBar(2, Clock.Ist(10, 4)));
        Assert.Null(store.ViewFor("C").Get(2));
        Assert.Equal(WindowStatus.InvalidCandleInWindow, store.ViewFor("C").ConsecutiveWindow(3).Status);
    }

    [Fact]
    public void History_IsBoundedByDepth()
    {
        var store = Store(new CandleConfig { HistoryDepth = 3 });
        for (var bar = 1; bar <= 6; bar++)
        {
            store.Observe(Opt(Clock.Ist(10, bar - 1, 30), bar, "C", 99, 100));
            store.PublishBar(bar, Clock.Ist(10, bar));
        }

        Assert.Null(store.ViewFor("C").Get(3));
        Assert.NotNull(store.ViewFor("C").Get(4));
        Assert.Equal(WindowStatus.Ready, store.ViewFor("C").ConsecutiveWindow(3).Status);
        Assert.Equal(WindowStatus.InsufficientHistory, store.ViewFor("C").ConsecutiveWindow(4).Status);
    }

    [Fact]
    public void Config_Validation()
    {
        Assert.Empty(new CandleConfig().Validate());
        Assert.NotEmpty((new CandleConfig { MinObservationsPerCandle = 0 }).Validate());
        Assert.NotEmpty((new CandleConfig { HistoryDepth = 0 }).Validate());
        Assert.NotEmpty((new CandleConfig { MaxCloseAge = TimeSpan.Zero }).Validate());
        Assert.Throws<ArgumentException>(() => new CandleStore(new CandleConfig { HistoryDepth = 0 }));
    }
}

public class EntryConditionTests
{
    private static CandleStore History(string token, params decimal[] asks)
    {
        var store = new CandleStore(new CandleConfig());
        for (var i = 0; i < asks.Length; i++)
        {
            store.Observe(Ev.Opt(Clock.Ist(10, i, 30), i + 1, token, asks[i] - 1m, asks[i]));
            store.PublishBar(i + 1, Clock.Ist(10, i + 1));
        }

        return store;
    }

    private static QuoteSnapshot Quote(string token, decimal? bid, decimal? ask) => new(token, bid, ask, Clock.Ist(11, 0), 999);

    private static ConditionResult Eval(CandleStore store, string token, decimal ask, int n = 3, CandlePriceSource source = CandlePriceSource.AskClose)
        => EntryConditions.Evaluate(new AskBelowCandleMaSpec { N = n, PriceSource = source }, new ConditionContext(token, Quote(token, ask - 1m, ask), store.ViewFor(token)));

    [Fact]
    public void Immediate_IsAlwaysSatisfied()
    {
        var result = EntryConditions.Evaluate(new ImmediateEntrySpec(), new ConditionContext("C", Quote("C", 99, 100), new CandleStore(new CandleConfig()).ViewFor("C")));

        Assert.Equal(ConditionOutcome.Satisfied, result.Outcome);
    }

    [Theory]
    [InlineData("101.95", ConditionOutcome.Satisfied)]
    [InlineData("102.00", ConditionOutcome.NotSatisfied)]   // strictly below
    [InlineData("102.05", ConditionOutcome.NotSatisfied)]
    public void AskBelowAverage_IsStrict(string askText, ConditionOutcome expected)
    {
        var store = History("C", 100m, 102m, 104m);                       // mean of the three ask closes = 102
        var ask = decimal.Parse(askText, System.Globalization.CultureInfo.InvariantCulture);

        var result = Eval(store, "C", ask);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(102m, result.Average);
        Assert.Equal(3, result.N);
    }

    [Fact]
    public void OnlyTheLastNConsecutiveCandles_AreAveraged()
    {
        var store = History("C", 200m, 100m, 102m, 104m);              // bar 1 (200) is outside the window of 3

        Assert.Equal(102m, Eval(store, "C", 101m).Average);
        Assert.Equal((200m + 100m + 102m + 104m) / 4m, Eval(store, "C", 101m, n: 4).Average);
    }

    [Fact]
    public void PriceSource_IsExplicit_AndChangesTheAverage()
    {
        var store = History("C", 100m, 102m, 104m);                     // bid close = ask - 1, mid close = ask - 0.5

        Assert.Equal(102m, Eval(store, "C", 99m, source: CandlePriceSource.AskClose).Average);
        Assert.Equal(101m, Eval(store, "C", 99m, source: CandlePriceSource.BidClose).Average);
        Assert.Equal(101.5m, Eval(store, "C", 99m, source: CandlePriceSource.MidClose).Average);
        Assert.Equal(101.5m, Eval(store, "C", 99m, source: CandlePriceSource.TypicalPrice).Average);   // one observation per candle: high = low = close = mid
    }

    [Fact]
    public void InsufficientOrBrokenHistory_IsNotEvaluable()
    {
        Assert.Equal("InsufficientHistory", Eval(History("C", 100m, 102m), "C", 99m).Reason);
        Assert.Equal("NoBarsPublished", Eval(new CandleStore(new CandleConfig()), "C", 99m).Reason);

        var gap = new CandleStore(new CandleConfig());
        gap.Observe(Ev.Opt(Clock.Ist(10, 0, 30), 1, "C", 99, 100));
        gap.PublishBar(1, Clock.Ist(10, 1));
        gap.PublishBar(2, Clock.Ist(10, 2));                             // empty bar
        gap.Observe(Ev.Opt(Clock.Ist(10, 2, 30), 2, "C", 99, 100));
        gap.PublishBar(3, Clock.Ist(10, 3));
        var result = Eval(gap, "C", 99m);
        Assert.Equal(ConditionOutcome.NotEvaluable, result.Outcome);
        Assert.Equal("InvalidCandleInWindow", result.Reason);
    }

    [Fact]
    public void NonExecutableQuote_IsNotEvaluable()
    {
        var store = History("C", 100m, 102m, 104m);

        var result = EntryConditions.Evaluate(new AskBelowCandleMaSpec { N = 3, PriceSource = CandlePriceSource.AskClose }, new ConditionContext("C", Quote("C", null, 99m), store.ViewFor("C")));

        Assert.Equal(ConditionOutcome.NotEvaluable, result.Outcome);
    }

    [Fact]
    public void ProhibitedInputs_AreRefused()
    {
        var store = History("C", 100m, 102m, 104m);
        store.Observe(Ev.Opt(Clock.Ist(10, 3, 30), 50, "OTHER", 99, 100));
        var spec = new AskBelowCandleMaSpec { N = 3, PriceSource = CandlePriceSource.AskClose };

        // another strike's history
        Assert.Throws<ProhibitedConditionInputException>(() => EntryConditions.Evaluate(spec, new ConditionContext("C", Quote("C", 99, 100), store.ViewFor("OTHER"))));
        // a rolling ATM basket (or any synthetic series) cannot be a view of the opportunity's token
        Assert.Throws<ProhibitedConditionInputException>(() => EntryConditions.Evaluate(spec, new ConditionContext("C", Quote("C", 99, 100), store.ViewFor("ATM_BASKET"))));
        // a quote of another contract
        Assert.Throws<ProhibitedConditionInputException>(() => EntryConditions.Evaluate(spec, new ConditionContext("C", Quote("OTHER", 99, 100), store.ViewFor("C"))));
    }

    [Fact]
    public void Evaluation_IsCultureIndependent()
    {
        var store = History("C", 100.5m, 102.25m, 104.125m);

        var invariant = Eval(store, "C", 101m);
        var other = Culture.Under("de-DE", () => Eval(store, "C", 101m));

        Assert.Equal(invariant, other);
    }
}
