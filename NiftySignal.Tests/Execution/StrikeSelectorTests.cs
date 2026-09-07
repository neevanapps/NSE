using NiftySignal.Domain.Enums;
using NiftySignal.Execution;
using NiftySignal.Rules;
using NiftySignal.Tests.Rules;

namespace NiftySignal.Tests.Execution;

public class StrikeSelectorTests
{
    static StrikeCandidate Call(
        string symbol, decimal mid, decimal? bid = null, decimal? ask = null,
        long oi = 200_000, long volume = 5000, double? delta = 0.45, double? iv = 0.18) =>
        new(Token: symbol, TradingSymbol: symbol, OptionType.Call, mid,
            bid ?? mid - 1, ask ?? mid + 1, oi, volume, delta, iv);

    static StrikeCandidate Put(
        string symbol, decimal mid, decimal? bid = null, decimal? ask = null,
        long oi = 200_000, long volume = 5000, double? delta = -0.45, double? iv = 0.18) =>
        new(Token: symbol, TradingSymbol: symbol, OptionType.Put, mid,
            bid ?? mid - 1, ask ?? mid + 1, oi, volume, delta, iv);

    static readonly StrikeSelector Selector = new();
    static readonly RulesetConfig Config = TestRulesetConfigs.Default();

    [Fact]
    public void SelectBestCandidate_PicksACall_ForABullishDirection()
    {
        var candidates = new[] { Call("NIFTY_C_25000", 125), Put("NIFTY_P_25000", 125) };

        var result = Selector.SelectBestCandidate(candidates, EntryDirection.Bullish, Config);

        Assert.NotNull(result.Selected);
        Assert.Equal(OptionType.Call, result.Selected.OptionType);
    }

    [Fact]
    public void SelectBestCandidate_PicksAPut_ForABearishDirection()
    {
        var candidates = new[] { Call("NIFTY_C_25000", 125), Put("NIFTY_P_25000", 125) };

        var result = Selector.SelectBestCandidate(candidates, EntryDirection.Bearish, Config);

        Assert.NotNull(result.Selected);
        Assert.Equal(OptionType.Put, result.Selected.OptionType);
    }

    [Fact]
    public void SelectBestCandidate_ExcludesPremiumOutsideTheConfiguredBand()
    {
        var tooCheap = Call("cheap", 70);
        var tooExpensive = Call("expensive", 200);
        var justRight = Call("right", 125);

        var result = Selector.SelectBestCandidate([tooCheap, tooExpensive, justRight], EntryDirection.Bullish, Config);

        Assert.Equal("right", result.Selected!.TradingSymbol);
        Assert.Contains(result.Diagnostics, d => d.Contains("cheap"));
        Assert.Contains(result.Diagnostics, d => d.Contains("expensive"));
    }

    [Fact]
    public void SelectBestCandidate_ExcludesACandidateWithNoValidQuote()
    {
        var noQuote = new StrikeCandidate(
            Token: "no_quote", TradingSymbol: "no_quote", OptionType.Call, Mid: 125,
            BidPrice: null, AskPrice: null, OpenInterest: 200_000, Volume: 5000, Delta: 0.45, ImpliedVolatility: 0.18);
        var valid = Call("valid", 125);

        var result = Selector.SelectBestCandidate([noQuote, valid], EntryDirection.Bullish, Config);

        Assert.Equal("valid", result.Selected!.TradingSymbol);
        Assert.Contains(result.Diagnostics, d => d.Contains("no_quote") && d.Contains("bid/ask"));
    }

    [Fact]
    public void SelectBestCandidate_ExcludesASpreadWiderThanTheConfiguredMax()
    {
        // Config max spread is 2% of mid. Mid=125 -> 2% = 2.5.
        var wideSpread = Call("wide", 125, bid: 120, ask: 130); // spread=10, 8% of mid
        var tightSpread = Call("tight", 125, bid: 124, ask: 126); // spread=2, 1.6% of mid

        var result = Selector.SelectBestCandidate([wideSpread, tightSpread], EntryDirection.Bullish, Config);

        Assert.Equal("tight", result.Selected!.TradingSymbol);
        Assert.Contains(result.Diagnostics, d => d.Contains("wide") && d.Contains("spread"));
    }

    [Fact]
    public void SelectBestCandidate_ExcludesOpenInterestBelowTheFloor()
    {
        var thin = Call("thin", 125, oi: 50_000); // floor is 100_000
        var liquid = Call("liquid", 125, oi: 500_000);

        var result = Selector.SelectBestCandidate([thin, liquid], EntryDirection.Bullish, Config);

        Assert.Equal("liquid", result.Selected!.TradingSymbol);
        Assert.Contains(result.Diagnostics, d => d.Contains("thin") && d.Contains("OI"));
    }

    [Fact]
    public void SelectBestCandidate_ExcludesACandidateWithNoImpliedVolatility()
    {
        var noIv = Call("no_iv", 125, iv: null);
        var valid = Call("valid", 125);

        var result = Selector.SelectBestCandidate([noIv, valid], EntryDirection.Bullish, Config);

        Assert.Equal("valid", result.Selected!.TradingSymbol);
        Assert.Contains(result.Diagnostics, d => d.Contains("no_iv") && d.Contains("IV"));
    }

    [Fact]
    public void SelectBestCandidate_ReturnsNull_WhenNoCandidateSurvives()
    {
        var allExcluded = new[] { Call("too_cheap", 50), Call("too_expensive", 500) };

        var result = Selector.SelectBestCandidate(allExcluded, EntryDirection.Bullish, Config);

        Assert.Null(result.Selected);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void SelectBestCandidate_PrefersTheDeltaBand_OverHigherLiquidityOutsideIt()
    {
        // Higher OI but delta outside [0.30, 0.60]; lower OI but delta inside the band.
        var highLiquidityLowDelta = Call("far_otm", 125, oi: 900_000, delta: 0.15);
        var inBandDelta = Call("preferred", 125, oi: 150_000, delta: 0.50);

        var result = Selector.SelectBestCandidate([highLiquidityLowDelta, inBandDelta], EntryDirection.Bullish, Config);

        Assert.Equal("preferred", result.Selected!.TradingSymbol);
    }

    [Fact]
    public void SelectBestCandidate_RanksByOpenInterest_AsTheTiebreakWithinTheDeltaBand()
    {
        var lowerOi = Call("lower_oi", 125, oi: 150_000, delta: 0.45);
        var higherOi = Call("higher_oi", 125, oi: 400_000, delta: 0.50);

        var result = Selector.SelectBestCandidate([lowerOi, higherOi], EntryDirection.Bullish, Config);

        Assert.Equal("higher_oi", result.Selected!.TradingSymbol);
    }

    [Fact]
    public void SelectBestCandidate_IgnoresCandidatesOnTheWrongSide()
    {
        var wrongSide = Put("wrong_side", 125, oi: 900_000);
        var rightSide = Call("right_side", 125, oi: 50); // low OI, would fail on its own

        // rightSide alone would fail the OI floor, but the point here is that wrongSide
        // (a Put, when Bullish wants a Call) is never even considered as an alternative.
        var result = Selector.SelectBestCandidate([wrongSide, rightSide], EntryDirection.Bullish, Config);

        Assert.Null(result.Selected);
    }

    [Fact]
    public void SelectBestCandidate_ThrowsForANonDirectionalEntryDirection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Selector.SelectBestCandidate([Call("c", 125)], EntryDirection.None, Config));
    }
}
