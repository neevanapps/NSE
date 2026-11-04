using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.Dashboard;

/// <summary>Avg5 / Avg10 of ONE exact contract: strict windows (no bridging, no zero-fill), pinned history, quote freshness and the ask-minus-average gap.</summary>
public sealed class OptionPriceAveragesTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 4, 4, 0, 0, TimeSpan.Zero);

    static AdaptiveOptionStrikeBarRow Leg(int seq, OptionType side, double? price) => new()
    {
        BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, Side = side, Token = side == OptionType.Call ? "CE1" : "PE1",
        TradingSymbol = side == OptionType.Call ? "NIFTY24500CE" : "NIFTY24500PE", Strike = 24500, Expiry = new DateOnly(2026, 11, 10), PriceEnd = price,
    };

    static FixedStrikeHistory History(int bars, Func<int, double?> cePrice, Func<int, double?>? pePrice = null, params int[] missing)
    {
        pePrice ??= cePrice;
        var rows = Enumerable.Range(1, bars).Where(i => !missing.Contains(i)).OrderByDescending(i => i)
            .Select(i => new FixedStrikeRow(i, T0.AddMinutes(i), Leg(i, OptionType.Call, cePrice(i)), Leg(i, OptionType.Put, pePrice(i)))).ToList();
        return new FixedStrikeHistory(new DateOnly(2026, 11, 10), 24500, new FixedStrikeContract(24500, "CE1", "NIFTY24500CE", "PE1", "NIFTY24500PE"), rows);
    }

    static PinnedQuote Q(decimal ltp, decimal? bid, decimal? ask, DateTimeOffset at) => new(new QuickQuote(ltp, bid, ask, null), at);

    [Fact]
    public void Avg5AndAvg10_AreTheMeansOfTheLatestCompletedBarPrices()
    {
        var h = History(12, i => 100 + i);                                       // bars 1..12 -> 101..112
        var v = OptionPriceAverages.Build(h, 12, T0.AddMinutes(12), null, null);
        Assert.Equal((108d + 109 + 110 + 111 + 112) / 5, v.Ce.Avg5.Value);
        Assert.Equal((103d + 104 + 105 + 106 + 107 + 108 + 109 + 110 + 111 + 112) / 10, v.Ce.Avg10.Value);
        Assert.Equal(5, v.Ce.Avg5.Observations); Assert.Equal(10, v.Pe.Avg10.Observations);
    }

    [Fact]
    public void AMissingOrInvalidBar_MakesTheWindowUnavailable_NeverBridgedOrZeroFilled()
    {
        var gap = History(12, i => 100d + i, null, missing: 10);                  // bar 10 has no stored row
        Assert.Null(OptionPriceAverages.Build(gap, 12, null, null, null).Ce.Avg5.Value);
        Assert.Null(OptionPriceAverages.Build(gap, 12, null, null, null).Ce.Avg10.Value);
        Assert.NotNull(OptionPriceAverages.Build(gap, 9, null, null, null).Ce.Avg5.Value);   // an earlier target whose window does not include bar 10 is still valid

        foreach (var bad in new double?[] { null, 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            var h = History(12, i => i == 11 ? bad : 100d + i);
            var v = OptionPriceAverages.Build(h, 12, null, null, null);
            Assert.Null(v.Ce.Avg5.Value);
            Assert.Contains("bar 11", v.Ce.Avg5.WhyUnavailable);
        }

        var shortHistory = History(7, i => 100d + i);
        Assert.NotNull(OptionPriceAverages.Build(shortHistory, 7, null, null, null).Ce.Avg5.Value);
        Assert.Null(OptionPriceAverages.Build(shortHistory, 7, null, null, null).Ce.Avg10.Value);   // fewer than ten bars exist
    }

    [Fact]
    public void TheCeAndPeSidesAreIndependent()
    {
        var h = History(10, i => 100d + i, i => i == 10 ? null : 50d);
        var v = OptionPriceAverages.Build(h, 10, null, null, null);
        Assert.NotNull(v.Ce.Avg5.Value); Assert.Null(v.Pe.Avg5.Value); Assert.Equal(50d, OptionPriceAverages.Build(h, 9, null, null, null).Pe.Avg5.Value);
    }

    [Fact]
    public void PinnedHistory_UsesOnlyBarsAtOrBeforeTheTarget()
    {
        var h = History(14, i => i <= 12 ? 100d : 9_999d);                       // bars 13 and 14 happen AFTER the pinned target
        var v = OptionPriceAverages.Build(h, 12, null, null, null);
        Assert.Equal(100d, v.Ce.Avg5.Value); Assert.Equal(100d, v.Ce.Avg10.Value);
    }

    [Fact]
    public void AskMinusAverage_RupeesAndPercent_OnlyWithAFreshQuote()
    {
        var h = History(10, i => 100d);
        var now = T0.AddMinutes(10);
        var fresh = OptionPriceAverages.Build(h, 10, now, Q(103m, 102m, 104m, now.AddSeconds(-3)), Q(90m, 89m, 91m, now.AddSeconds(-20)));
        Assert.Equal(4d, fresh.Ce.AskMinus5); Assert.Equal(4d, fresh.Ce.AskMinus10); Assert.Equal(4d, fresh.Ce.AskMinus5Pct!.Value, 9);
        Assert.True(fresh.Ce.Quote!.Fresh); Assert.Equal(3d, fresh.Ce.Quote.AgeSeconds!.Value, 9);
        Assert.False(fresh.Pe.Quote!.Fresh);                                     // 20 s old: shown, flagged stale
        Assert.Null(fresh.Pe.AskMinus5); Assert.Null(fresh.Pe.AskMinus10Pct);    // and never used for a gap
        Assert.Equal(91d, fresh.Pe.Quote.Ask);
    }

    [Fact]
    public void InvalidQuotes_AreUnavailable_NotZero()
    {
        var h = History(10, i => 100d);
        var now = T0.AddMinutes(10);
        var v = OptionPriceAverages.Build(h, 10, now, Q(103m, 0m, 0m, now), Q(103m, 105m, 104m, now));     // CE: zero book; PE: crossed book
        Assert.Null(v.Ce.Quote!.Bid); Assert.Null(v.Ce.Quote.Ask); Assert.Null(v.Ce.AskMinus5);
        Assert.Null(v.Pe.Quote!.Bid); Assert.Null(v.Pe.Quote.Ask); Assert.False(v.Pe.Quote.Fresh); Assert.Null(v.Pe.AskMinus5);
        Assert.Null(OptionPriceAverages.Build(h, 10, now, null, null).Ce.Quote);                             // no quote at all
    }

    [Fact]
    public void TheView_NamesTheExactContract()
    {
        var v = OptionPriceAverages.Build(History(10, i => 100d), 10, null, null, null);
        Assert.Equal("NIFTY24500CE", v.Ce.Symbol); Assert.Equal("CE1", v.Ce.Token); Assert.Equal("PE1", v.Pe.Token);
    }
}
