using NiftySignal.MarketSummary;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Frames;
using FrameMoneyness = NiftySignal.PaperEngineV2.Core.Frames.Moneyness;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6b: building a frame from one verified completed bar's Market Summary - mapping, availability, basket identity, quotes, clock separation and sibling-bar ordinals.</summary>
public class FrameBuilderTests
{
    static double Read(MarketStateFrame f, string id, SummaryWindowId w) => f.TryRead(H(id), w, out var v) ? v : double.NaN;

    static bool Has(MarketStateFrame f, string id, SummaryWindowId w) => f.TryRead(H(id), w, out _);

    [Fact]
    public void FullInputs_MapEveryRegistryMetricInEveryWindow_WithTheSummaryValues()
    {
        var s = Summary(130);
        var f = Build(new MarketStateFrameBuilder(), 1, 130, s);
        Assert.Equal(MetricRegistry.Count * SummaryWindows.Count, f.PresentMetricCount);       // 284 metrics x 5 windows, nothing unmapped
        Assert.All(MetricRegistry.Specs, spec => Assert.All(Enum.GetValues<SummaryWindowId>(), w => Assert.True(f.TryRead(spec.Handle, w, out _), $"{spec.Id} {w}")));

        // spot checks against the summary the frame was built from
        var total10 = s.Grids.Single(g => g.Kind == GridKind.Total).Rows[1];
        Assert.Equal(total10.Direction!.Strict.Ratio!.Value, Read(f, "opt.total.strict.dir:pct", SummaryWindowId.W10), 12);
        Assert.Equal(total10.Ce!.Oi.Raw!.Value, Read(f, "opt.total.oi.ce:raw", SummaryWindowId.W10), 12);
        Assert.Equal(total10.Direction!.Oi.Raw!.Value, Read(f, "opt.total.oi.dir:raw", SummaryWindowId.W10), 12);               // the OI direction (PE - CE) as the summary computed it
        Assert.Equal(total10.Pe!.Activity!.Value, Read(f, "opt.total.activity.pe:raw", SummaryWindowId.W10), 12);
        Assert.Equal(total10.Ce!.Delta.Raw!.Value, Read(f, "opt.total.delta.ce:raw", SummaryWindowId.W10), 12);                // raw Delta: the x100 display scale never enters a frame
        Assert.Equal(total10.Ce.Vega.Raw!.Value, Read(f, "opt.total.vega.ce:raw", SummaryWindowId.W10), 12);
        Assert.Equal(s.Futures[2].Strict.Ratio!.Value, Read(f, "fut.strict:pct", SummaryWindowId.W50), 12);
        Assert.Equal(s.Futures[4].Volume, Read(f, "fut.volume:raw", SummaryWindowId.Day), 12);
        Assert.Equal(s.Futures[0].Price.Raw!.Value, Read(f, "fut.price_change:raw", SummaryWindowId.W5), 12);
        Assert.Equal(100d, Read(f, "opt.atm.coverage_pct", SummaryWindowId.W100), 12);
        Assert.Equal(1d, Read(f, "opt.atm.coverage_full", SummaryWindowId.W100), 12);
        Assert.Equal(1d, Read(f, "fut.coverage_full", SummaryWindowId.Day), 12);
        Assert.Equal(s.Vix![1].End.Value!.Value, Read(f, "vix.level:raw", SummaryWindowId.W10), 12);
        Assert.Equal(s.Vix[4].Change!.Value, Read(f, "vix.change:raw", SummaryWindowId.Day), 12);
        Assert.Equal(1d, Read(f, "vix.fresh", SummaryWindowId.W5), 12);
    }

    [Fact]
    public void InsufficientHistory_LeavesWholeWindowsUnknown_NotZero()
    {
        var f = Build(new MarketStateFrameBuilder(), 1, 7);
        foreach (var w in new[] { SummaryWindowId.W10, SummaryWindowId.W50, SummaryWindowId.W100 })
        {
            Assert.False(Has(f, "fut.strict:pct", w)); Assert.False(Has(f, "opt.atm.strict.dir:pct", w)); Assert.False(Has(f, "opt.atm.coverage_pct", w)); Assert.False(Has(f, "opt.atm.coverage_full", w));
        }

        Assert.True(Has(f, "opt.atm.strict.dir:pct", SummaryWindowId.W5)); Assert.True(Has(f, "opt.atm.strict.dir:pct", SummaryWindowId.Day));
        Assert.False(Has(f, "opt.atm.roll.dir:raw", SummaryWindowId.W5));                       // fewer than the ten bars a roll observation needs
    }

    [Fact]
    public void UnavailableOrNonFiniteInputs_AreOmitted_NeverWrittenAsZero()
    {
        var s = Summary(130);
        // an unavailable activity (no valid elapsed time) and a NaN raw value
        var futures = s.Futures.Select(r => r with { Activity = null, Price = new Metric(double.NaN, double.NaN, 100) }).ToList();
        var f = new MarketStateFrameBuilder().Build(Bar(1, 130), s.Basket, futures, s.Grids, s.Vix, s.Quotes);
        Assert.False(Has(f, "fut.activity:raw", SummaryWindowId.W10));
        Assert.False(Has(f, "fut.price_change:raw", SummaryWindowId.W10)); Assert.False(Has(f, "fut.price_change:pct", SummaryWindowId.W10));
        Assert.True(Has(f, "fut.strict:pct", SummaryWindowId.W10));                              // everything else is untouched
        // a zero-denominator ratio has no percent form
        var grids = s.Grids.Select(g => g with { Rows = g.Rows.Select(r => r.Ce is null ? r : r with { Ce = r.Ce with { Strict = new Metric(0, 0, 0) } }).ToList() }).ToList();
        var f2 = new MarketStateFrameBuilder().Build(Bar(1, 130), s.Basket, s.Futures, grids, s.Vix, s.Quotes);
        Assert.True(Has(f2, "opt.atm.strict.ce:raw", SummaryWindowId.W10)); Assert.False(Has(f2, "opt.atm.strict.ce:pct", SummaryWindowId.W10));
    }

    [Fact]
    public void VixMissingOrStale_IsUnknownExceptTheFreshFlag_WhichIsZero()
    {
        var none = Build(new MarketStateFrameBuilder(), 1, 20, Summary(20, withVix: false));
        Assert.Equal("Unavailable", none.Vix.Status);
        Assert.All(Enum.GetValues<SummaryWindowId>(), w => { Assert.Equal(0d, Read(none, "vix.fresh", w)); Assert.False(Has(none, "vix.level:raw", w)); Assert.False(Has(none, "vix.change:pct", w)); });

        var s = Summary(20);
        var stale = s.Vix!.Select(v => v with { Start = new VixPoint(null, T0, 120, VixStatus.Stale), End = new VixPoint(null, T0, 120, VixStatus.Stale), Change = null, ChangePct = null }).ToList();
        var f = new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids, stale, s.Quotes);
        Assert.Equal("Stale", f.Vix.Status); Assert.Equal(120d, f.Vix.AgeSeconds);
        Assert.Equal(0d, Read(f, "vix.fresh", SummaryWindowId.W5)); Assert.False(Has(f, "vix.level:raw", SummaryWindowId.W5));       // a stale value is withheld, not carried
        var fresh = Build(new MarketStateFrameBuilder(), 1, 20, s);
        Assert.Equal("Fresh", fresh.Vix.Status); Assert.True(fresh.Vix.ObservedAt <= End(20));
        Assert.Equal(1d, Read(fresh, "vix.fresh", SummaryWindowId.W5));
    }

    [Fact]
    public void BasketIdentity_IsTheExactTenSlotBasket_UnresolvedSlotsStayUnresolved()
    {
        var f = Build(new MarketStateFrameBuilder(), 1, 20);
        Assert.Equal((Expiry, 26_000d, 50d), (f.Basket.Expiry, f.Basket.Centre, f.Basket.Step));
        Assert.Equal(10, f.Basket.Slots.Count);
        Assert.Equal(["CE25900", "CE25950", "CE26000", "CE26050", "CE26100", "PE25900", "PE25950", "PE26000", "PE26050", "PE26100"], f.Basket.Slots.Select(x => x.Token!).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(FrameMoneyness.Atm, f.Basket.Slots.Single(x => x.Token == "CE26000").Moneyness);
        Assert.Equal(FrameMoneyness.Itm, f.Basket.Slots.Single(x => x.Token == "CE25900").Moneyness);
        Assert.Equal(FrameMoneyness.Otm, f.Basket.Slots.Single(x => x.Token == "CE26100").Moneyness);
        Assert.Equal(FrameMoneyness.Itm, f.Basket.Slots.Single(x => x.Token == "PE26100").Moneyness);                    // a PE is ITM above the centre
        Assert.Equal("adaptive-fixed13650-v1", f.Identity.AdaptiveModelVersion); Assert.Equal("spot-atm-v1", f.Identity.AtmRuleVersion);

        var holed = BasketResolver.Resolve(Expiry, 26_000, Listed().Where(l => l.Strike != 26_100).ToList());
        var s = Summary(20);
        var quotes = s.Quotes.Where(q => holed.Contracts.Any(c => c.Token == q.Token)).ToList();
        var g = new MarketStateFrameBuilder().Build(Bar(1, 20), holed, s.Futures, s.Grids, s.Vix, quotes);
        Assert.Equal(2, g.Basket.Slots.Count(x => x.Token is null));                                           // reported, never substituted
        Assert.NotEmpty(g.Basket.Problems);
        Assert.DoesNotContain(g.QuoteIssues, i => i.Token.EndsWith("26100", StringComparison.Ordinal));
    }

    [Fact]
    public void Quotes_AreOnlyBasketContracts_AsOfTheBoundary_AndProblemsAreRecorded()
    {
        var s = Summary(20);
        var f = Build(new MarketStateFrameBuilder(), 1, 20, s);
        Assert.Equal(10, f.Quotes.Count); Assert.Empty(f.QuoteIssues);
        Assert.Equal(f.Quotes.Select(q => q.Token).OrderBy(x => x, StringComparer.Ordinal), f.Quotes.Select(q => q.Token));
        Assert.All(f.Quotes, q => Assert.True(q.AvailableAt <= End(20)));

        var quotes = s.Quotes.ToList();
        quotes.RemoveAll(q => q.Token == "CE25900");                                                              // missing
        var i = quotes.FindIndex(q => q.Token == "CE25950"); quotes[i] = quotes[i] with { Bid = 0 };               // not two-sided
        var j = quotes.FindIndex(q => q.Token == "CE26000"); quotes[j] = quotes[j] with { Bid = 130, Ask = 120 };    // crossed
        quotes.Add(quotes[quotes.FindIndex(q => q.Token == "PE26000")]);                                           // ambiguous (two for one token)
        var g = new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids, s.Vix, quotes);
        Assert.Equal(6, g.Quotes.Count);
        Assert.Equal(["CE25900:Missing", "CE25950:NotTwoSided", "CE26000:NotTwoSided", "PE26000:Ambiguous"], g.QuoteIssues.Select(x => $"{x.Token}:{x.Reason}"));

        var ahead = s.Quotes.ToList(); ahead[3] = ahead[3] with { AvailableAt = End(20).AddMilliseconds(1) };
        Assert.Contains("look-ahead", Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids, s.Vix, ahead)).Message);
        var foreign = s.Quotes.Append(new QuoteInput("PE99999", 100, 101, 1, 1, End(20), 1)).ToList();
        Assert.Contains("outside the basket", Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids, s.Vix, foreign)).Message);
    }

    [Theory]
    [InlineData("adaptive-v1")] [InlineData("npt1-m1-v2")] [InlineData("npt1-m1-v1")] [InlineData("adaptive-fixed13650-v2")] [InlineData("")]
    public void OtherClocks_AreRefused(string model)
    {
        var s = Summary(20);
        var ex = Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20, model: model), s.Basket, s.Futures, s.Grids, s.Vix, s.Quotes));
        Assert.Contains("fixed-13,650 clock", ex.Message);
    }

    [Fact]
    public void SummaryRows_MustBeForThisBar_NoLagNoLookAhead()
    {
        var s = Summary(20); var other = Summary(21);
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, other.Futures, s.Grids, s.Vix, s.Quotes));
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, other.Grids, s.Vix, s.Quotes));
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures.Take(4).ToList(), s.Grids, s.Vix, s.Quotes));
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids.Take(3).ToList(), s.Vix, s.Quotes));
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(Bar(1, 20), s.Basket, s.Futures, s.Grids, s.Vix!.Take(2).ToList(), s.Quotes));
    }

    [Fact]
    public void SiblingBarsOfOneMarketUpdate_GetConsecutiveOrdinals_AndTheBuilderOnlyAdvancesOnSuccess()
    {
        var b = new MarketStateFrameBuilder();
        var t = End(5);
        var f5 = Build(b, 1, 5, end: t, tick: 77);
        var f6 = Build(b, 1, 6, end: t, tick: 77, duration: 0);                    // same market update: same instant, same source tick, zero duration
        var f7 = Build(b, 1, 7, end: t, tick: 77, duration: 0);
        var f8 = Build(b, 1, 8, end: t.AddSeconds(20), tick: 90, duration: 20);
        Assert.Equal([0, 1, 2, 0], new[] { f5, f6, f7, f8 }.Select(f => f.Identity.SameInstantOrdinal));
        Assert.Equal([false, true, true, false], new[] { f5, f6, f7, f8 }.Select(f => f.Identity.IsZeroDuration));
        Assert.Equal(f5.Identity.DecisionKey, f6.Identity.DecisionKey);             // same event: the decision key does not advance
        Assert.False(f6.IsEligibleFillBarFor(f5)); Assert.False(f7.IsEligibleFillBarFor(f5)); Assert.True(f8.IsEligibleFillBarFor(f7));
        // activity per second is unavailable for a zero-duration bar's own summary only if the summary says so; the frame never invents a rate
        var s9 = Summary(9, end: t.AddSeconds(40)); var s10 = Summary(10, end: t.AddSeconds(40));
        var f9 = Build(b, 1, 9, s9, end: t.AddSeconds(40), tick: 95);
        // a failed build (look-ahead) must not advance the ordinal state
        var ahead = s10.Quotes.ToList(); ahead[0] = ahead[0] with { AvailableAt = t.AddSeconds(41) };
        Assert.Throws<FrameValidationException>(() => b.Build(Bar(1, 10, end: t.AddSeconds(40), tick: 95), s10.Basket, s10.Futures, s10.Grids, s10.Vix, ahead));
        var f10 = Build(b, 1, 10, s10, end: t.AddSeconds(40), tick: 95, duration: 0);
        Assert.Equal(1, f10.Identity.SameInstantOrdinal);                          // still the sibling of bar 9
        // a gap in the sequence or a new session restarts the numbering
        Assert.Equal(0, Build(b, 1, 12, end: t.AddSeconds(40), tick: 95).Identity.SameInstantOrdinal);
        Assert.Equal(0, Build(b, 2, 13, end: t.AddSeconds(40), tick: 95).Identity.SameInstantOrdinal);
    }

    [Fact]
    public void Building_IsDeterministic_AndUnverifiedBarsAreMarkedNotHidden()
    {
        var a = Build(new MarketStateFrameBuilder(), 1, 40);
        var b = Build(new MarketStateFrameBuilder(), 1, 40);
        Assert.Equal(a.Hash, b.Hash); Assert.Equal(a.PayloadJson, b.PayloadJson);
        var u = Build(new MarketStateFrameBuilder(), 1, 40, verified: false);
        Assert.False(u.Identity.Verified); Assert.Equal("sidecar integrity unknown", u.Identity.UnverifiedReason);
        Assert.NotEqual(a.Hash, u.Hash);
        Assert.Equal(a.PresentMetricCount, u.PresentMetricCount);                  // the metrics are still there; the VERIFIED flag is what blocks entries downstream
    }

    [Fact]
    public void FramesBuiltFromSummaries_RoundTripThroughTheJournalPayload()
    {
        var f = Build(new MarketStateFrameBuilder(), 1, 130);
        var back = FrameSerializer.Read(f.PayloadJson);
        Assert.Equal(f.Hash, back.Hash);
        Assert.Equal(f.Metrics().ToList(), back.Metrics().ToList());
        Assert.True(f.PayloadJson.Length < 200_000);
    }
}
