using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.PaperEngineV2.Runtime;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Held-contract quote tracking: a contract held by an open position stays observable (its own genuine bid/ask, with source and availability timestamps) after the dynamic ATM moves it out of the signal-time
/// basket, so the position can still be marked and exited - without substituting another strike, inferring a fill or touching any Market Summary input.
/// </summary>
public class HeldContractTests
{
    static readonly ExecFixtures.Q[] AtmQuotes = [new("CE26000", 125, 126), new("CE25950", 129, 130), new("PE26000", 125, 126), new("PE25950", 129, 130)];

    /// <summary>
    /// A frame whose basket is centred on <paramref name="centre"/>: <paramref name="quotes"/> are the basket contracts' quotes, <paramref name="held"/> the held (out-of-basket) contracts' quotes.
    /// <paramref name="fut"/> &lt; -50 triggers the discretionary exit of ExecFixtures.Strat, <paramref name="dir"/> &gt; 5 a call entry.
    /// </summary>
    static MarketStateFrame Fr(int seq, double centre = 26_000, IEnumerable<ExecFixtures.Q>? quotes = null, IEnumerable<ExecFixtures.Q>? held = null, double dir = 0, double fut = 1, DateTimeOffset? end = null, int ordinal = 0, long? tick = null, double duration = 60)
    {
        var e = end ?? End(seq);
        var metrics = new List<MetricValue>
        {
            new(H("opt.atm.strict.dir:pct"), SummaryWindowId.W10, dir),
            new(H("fut.strict:pct"), SummaryWindowId.W5, fut),
            new(H("vix.fresh"), SummaryWindowId.W5, 1),
        };
        QuoteSnapshot Snap(ExecFixtures.Q q) => new(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, e.AddMilliseconds(-q.AgeMs), 5_000 + seq);
        var heldQuotes = (held ?? []).Select(Snap).ToList();
        var identity = Identity(1, seq, e, tick, ordinal, true, duration);
        if (heldQuotes.Count > 0) identity = identity with { Schema = FrameIdentity.HeldQuotesSchema };
        return MarketStateFrame.Create(identity, BasketId(centre), metrics, (quotes ?? AtmQuotes).Select(Snap).ToList(), [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"), heldQuotes);
    }

    static PaperEngine Engine(params CompiledStrategy[] s) => ExecFixtures.Engine(s);

    static List<T> Of<T>(PaperEngine e) where T : PaperEvent => ExecFixtures.Of<T>(e);

    /// <summary>Entry on CE26000 (frames 4-6), then the ATM runs away (centre 26150: CE26000 leaves the basket) for the frames the caller appends.</summary>
    static List<MarketStateFrame> Entered() => [Fr(4), Fr(5, dir: 10), Fr(6, quotes: [new("CE26000", 126.5, 127)])];

    static readonly ExecFixtures.Q[] Shifted = [new("CE26050", 110, 111), new("CE26100", 95, 96)];

    static double ExitSlippage(CompiledStrategy s) => s.Config.Execution.ExitSlippage;

    // ---- the Strategy A failure mode ----

    [Fact]
    public void HeldContract_LeavingTheBasket_StillExits_OnItsOwnLaterQuote_Causally()
    {
        var s = ExecFixtures.Strat();
        var e = Engine(s);
        ExecFixtures.Run(e, Entered());
        Assert.Single(Of<EntryFilled>(e));
        Assert.Equal(["CE26000"], e.HeldTokens);

        // frame 7: the exit decision (discretionary, fut < -50) and CE26000 is no longer in the basket; its held quote is the one visible at the decision itself
        var decision = Fr(7, 26_150, Shifted, held: [new("CE26000", 120, 121)], fut: -60);
        Assert.DoesNotContain(decision.Basket.Slots, x => x.Token == "CE26000");
        ExecFixtures.Run(e, [decision]);
        var req = Assert.Single(Of<ExitRequested>(e));
        Assert.Empty(Of<ExitFilled>(e));                                                               // the decision-time quote can never fill its own decision

        ExecFixtures.Run(e, [Fr(8, 26_150, Shifted, held: [new("CE26000", 119, 120.5, AgeMs: 100)])]);
        var fill = Assert.Single(Of<ExitFilled>(e));
        Assert.Equal(req.PositionId, fill.PositionId);
        Assert.Equal(8, fill.BarSeq);
        Assert.Equal(119m - (decimal)ExitSlippage(s), fill.FillPrice);                                    // bid of the HELD contract's own quote, less slippage
        Assert.True(fill.QuoteAvailableAt > req.Decision.AvailableAt);                                // strictly newer than the decision
        Assert.Empty(e.HeldTokens);                                                                    // fully closed: no longer observed
        Assert.Empty(Of<PositionUnresolved>(e));
    }

    [Fact]
    public void WithoutAHeldQuote_TheExitStaysPending_NothingIsInferred_AndItIsUnresolvedAfterTheCutoff()
    {
        var e = Engine(ExecFixtures.Strat());
        ExecFixtures.Run(e, Entered());
        ExecFixtures.Run(e, [Fr(7, 26_150, Shifted, fut: -60), Fr(8, 26_150, Shifted), Fr(9, 26_150, Shifted)]);            // a different strike is quoted, the held one is not
        Assert.Single(Of<ExitRequested>(e));
        Assert.Empty(Of<ExitFilled>(e));
        Assert.Equal(["CE26000"], e.HeldTokens);                                                       // still pending and still wanted
        var late = new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 36, 0)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        ExecFixtures.Run(e, [Fr(10, 26_150, Shifted, end: late)]);
        Assert.Single(Of<PositionUnresolved>(e));                                                      // explicitly unresolved, never a fabricated fill
        Assert.Empty(Of<ExitFilled>(e));
        Assert.Empty(e.HeldTokens);
    }

    [Fact]
    public void AStaleHeldQuote_IsIgnored_AndALaterValidOneTriggersThePendingExit()
    {
        var e = Engine(ExecFixtures.Strat());
        ExecFixtures.Run(e, Entered());
        ExecFixtures.Run(e, [Fr(7, 26_150, Shifted, fut: -60), Fr(8, 26_150, Shifted, held: [new("CE26000", 118, 119, AgeMs: 5_000)])]);       // older than the 2 s freshness limit
        Assert.Empty(Of<ExitFilled>(e));
        ExecFixtures.Run(e, [Fr(9, 26_150, Shifted, held: [new("CE26000", 117, 118.5, AgeMs: 200)])]);
        var fill = Assert.Single(Of<ExitFilled>(e));
        Assert.Equal(9, fill.BarSeq);
    }

    [Fact]
    public void ZeroDurationSibling_NeverFillsTheDecisionOfItsSiblingBar()
    {
        var e = Engine(ExecFixtures.Strat());
        ExecFixtures.Run(e, Entered());
        var end7 = End(7);
        ExecFixtures.Run(e, [Fr(7, 26_150, Shifted, held: [new("CE26000", 120, 121)], fut: -60, end: end7, tick: 2_007)]);
        ExecFixtures.Run(e, [Fr(8, 26_150, Shifted, held: [new("CE26000", 120.5, 121.5)], end: end7, ordinal: 1, tick: 2_008, duration: 0)]);      // same availability instant: its quote is not newer than the decision
        Assert.Empty(Of<ExitFilled>(e));
        ExecFixtures.Run(e, [Fr(9, 26_150, Shifted, held: [new("CE26000", 121, 122, AgeMs: 100)])]);
        Assert.Single(Of<ExitFilled>(e));
    }

    [Fact]
    public void HeldQuote_IsUsedForMarking_MfeMae_AndTheFeesAreOnTheRealFill()
    {
        var s = ExecFixtures.Strat();
        var e = Engine(s);
        ExecFixtures.Run(e, Entered());
        var entry = Assert.Single(Of<EntryFilled>(e));
        ExecFixtures.Run(e, [Fr(7, 26_150, Shifted, held: [new("CE26000", 140, 141)]), Fr(8, 26_150, Shifted, held: [new("CE26000", 100, 101)], fut: -60), Fr(9, 26_150, Shifted, held: [new("CE26000", 105, 106, AgeMs: 100)])]);
        var fill = Assert.Single(Of<ExitFilled>(e));
        Assert.True(fill.Mfe > 0m && fill.Mae < 0m);                                                   // marks came from the held contract's own quotes (140 above and 100 below the entry)
        Assert.Equal((fill.FillPrice - entry.FillPrice) * fill.Quantity, fill.GrossPnl);
        Assert.Equal(fill.GrossPnl - entry.Fees - fill.Fees, fill.NetPnl);
        Assert.True(fill.Fees > 0m);
    }

    [Fact]
    public void Duplicate_OrReplayedFrames_NeverDoubleFill()
    {
        var e = Engine(ExecFixtures.Strat());
        var frames = Entered().Concat([Fr(7, 26_150, Shifted, held: [new("CE26000", 120, 121)], fut: -60), Fr(8, 26_150, Shifted, held: [new("CE26000", 119, 120.5, AgeMs: 100)])]).ToList();
        ExecFixtures.Run(e, frames);
        ExecFixtures.Run(e, frames);                                                                   // the same frames again (idempotent per decision key)
        Assert.Single(Of<EntryFilled>(e));
        Assert.Single(Of<ExitFilled>(e));
    }

    // ---- several strategies, different contracts ----

    [Fact]
    public void StrategiesHoldingDifferentContracts_AreObserved_Deduplicated_AndExitIndependently()
    {
        var s1 = ExecFixtures.Strat("S01");
        var s2 = ExecFixtures.Strat("S02", o => { o["instrument"]!["premium"]!["min"] = 128; o["instrument"]!["premium"]!["max"] = 135; });         // only CE25950 (ask 130) qualifies
        var s3 = ExecFixtures.Strat("S03");                                                            // same contract as S01
        var e = Engine(s1, s2, s3);
        ExecFixtures.Run(e, [Fr(4), Fr(5, dir: 10), Fr(6, quotes: [new("CE26000", 126.5, 127), new("CE25950", 129.5, 130.5)])]);
        Assert.Equal(3, Of<EntryFilled>(e).Count);
        Assert.Equal(["CE25950", "CE26000"], e.HeldTokens.OrderBy(x => x, StringComparer.Ordinal));          // deduplicated across strategies
        var held = new ExecFixtures.Q[] { new("CE26000", 120, 121), new("CE25950", 124, 125) };
        ExecFixtures.Run(e, [Fr(7, 26_150, Shifted, held: held, fut: -60), Fr(8, 26_150, Shifted, held: held.Select(q => q with { Bid = q.Bid + 1, Ask = q.Ask + 1, AgeMs = 100 }).ToArray())]);
        var fills = Of<ExitFilled>(e).ToDictionary(x => x.StrategyId);
        Assert.Equal(3, fills.Count);
        Assert.Equal(121m - (decimal)ExitSlippage(s1), fills["S01"].FillPrice);
        Assert.Equal(125m - (decimal)ExitSlippage(s2), fills["S02"].FillPrice);                              // each exit priced on ITS contract's quote, never a neighbour's
        Assert.Equal(fills["S01"].FillPrice, fills["S03"].FillPrice);
        Assert.Empty(e.HeldTokens);
    }

    // ---- restart: the held set is restored from the durable (journaled) frames ----

    [Fact]
    public void Restart_ReplaysTheJournal_ToTheSameHeldSet_AndContinuesWithoutDuplicates()
    {
        var frames = Entered().Concat([Fr(7, 26_150, Shifted, held: [new("CE26000", 120, 121)]), Fr(8, 26_150, Shifted, held: [new("CE26000", 120.5, 121.5, AgeMs: 100)], fut: -60)]).ToList();
        var original = Engine(ExecFixtures.Strat());
        ExecFixtures.Run(original, frames);
        Assert.Equal(["CE26000"], original.HeldTokens);

        var journaled = frames.Select(f => FrameSerializer.Read(f.PayloadJson)).ToList();           // exactly what the durable journal returns
        Assert.Equal(frames.Select(f => f.Hash), journaled.Select(f => f.Hash));
        var restarted = Engine(ExecFixtures.Strat());
        ExecFixtures.Run(restarted, journaled);
        Assert.Equal(original.HeldTokens, restarted.HeldTokens);                                      // restored from the replayed positions
        Assert.Equal(original.Events.Select(x => x.ToString()), restarted.Events.Select(x => x.ToString()));

        var next = Fr(9, 26_150, Shifted, held: [new("CE26000", 119, 120, AgeMs: 100)]);
        ExecFixtures.Run(original, [next]);
        ExecFixtures.Run(restarted, [FrameSerializer.Read(next.PayloadJson)]);
        Assert.Single(Of<ExitFilled>(restarted));
        Assert.Equal(original.Events.Select(x => x.ToString()), restarted.Events.Select(x => x.ToString()));
    }

    // ---- the frame itself ----

    [Fact]
    public void Frame_WithoutHeldQuotes_IsUnchanged_AndOnlyHeldFramesUseSchemaTwo()
    {
        var plain = Fr(5);
        Assert.Equal(FrameIdentity.CurrentSchema, plain.Identity.Schema);
        Assert.DoesNotContain("heldQuotes", plain.PayloadJson, StringComparison.Ordinal);               // byte-identical to before: existing hashes and journals are untouched
        var held = Fr(5, 26_150, Shifted, held: [new("CE26000", 120, 121)]);
        Assert.Equal(FrameIdentity.HeldQuotesSchema, held.Identity.Schema);
        Assert.Contains("heldQuotes", held.PayloadJson, StringComparison.Ordinal);
        var back = FrameSerializer.Read(held.PayloadJson);
        Assert.Equal(held.Hash, back.Hash);
        var q = Assert.Single(back.HeldQuotes);
        Assert.Equal(("CE26000", 120d, 121d), (q.Token, q.Bid, q.Ask));
        Assert.Equal(held.HeldQuotes[0].AvailableAt, q.AvailableAt);                                  // source and availability timestamps survive
        Assert.Equal(held.HeldQuotes[0].SourceTickId, q.SourceTickId);
        Assert.NotEqual(held.Hash, Fr(5, 26_150, Shifted).Hash);
    }

    [Fact]
    public void Frame_RefusesInconsistentOrFutureHeldQuotes()
    {
        var e = End(5);
        var basket = BasketId(26_150);
        MarketStateFrame Make(FrameIdentity id, params QuoteSnapshot[] held) => MarketStateFrame.Create(id, basket, [], [], [], new VixAvailability(null, null, "Unavailable"), held);
        var two = Identity(1, 5, e) with { Schema = FrameIdentity.HeldQuotesSchema };
        var ok = new QuoteSnapshot("CE26000", 120, 121, 650, 650, e.AddMilliseconds(-100), 1);
        Assert.Throws<FrameValidationException>(() => Make(Identity(1, 5, e), ok));                                        // held quotes need schema /2
        Assert.Throws<FrameValidationException>(() => Make(two));                                                           // and /2 means there are some
        Assert.Throws<FrameValidationException>(() => Make(two, ok with { AvailableAt = e.AddMilliseconds(1) }));          // look-ahead
        Assert.Throws<FrameValidationException>(() => Make(two, ok with { Bid = 0 }));                                      // not two-sided
        Assert.Throws<FrameValidationException>(() => Make(two, ok with { Ask = 119 }));
        Assert.Throws<FrameValidationException>(() => Make(two, ok, ok));                                                   // duplicate token
        Assert.Throws<FrameValidationException>(() => Make(two, ok with { Token = "CE26100" }));                            // a BASKET token is quoted once, in Quotes
        Assert.Equal(1, Make(two, ok).HeldQuotes.Count);
    }

    [Fact]
    public void Builder_SeparatesHeldFromBasketQuotes_AndLeavesEveryMetricUntouched()
    {
        var inputs = Summary(8, 26_000);
        var builder = new MarketStateFrameBuilder();
        var e = End(8);
        var bar = Bar(1, 8, e);
        var without = new MarketStateFrameBuilder().Build(bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, inputs.Quotes);
        var heldQuote = new QuoteInput("CE25500", 120, 121, 650, 650, e.AddMilliseconds(-200), 77);
        var with = builder.Build(bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, inputs.Quotes, [heldQuote]);
        Assert.Equal(without.Metrics().ToList(), with.Metrics().ToList());                              // Market Summary metrics are byte-for-byte the same
        Assert.Equal(without.Quotes, with.Quotes);
        Assert.Equal(["CE25500"], with.HeldQuotes.Select(x => x.Token));
        Assert.Equal(FrameIdentity.CurrentSchema, without.Identity.Schema);
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, inputs.Quotes, [heldQuote with { Token = inputs.Quotes[0].Token }]));
        Assert.Throws<FrameValidationException>(() => new MarketStateFrameBuilder().Build(bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, inputs.Quotes, [heldQuote with { AvailableAt = e.AddSeconds(1) }]));
        var junk = new MarketStateFrameBuilder().Build(bar, inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix, inputs.Quotes, [heldQuote with { Bid = 0 }]);
        Assert.Empty(junk.HeldQuotes);                                                                  // an unusable held quote is simply absent (never repaired)
        Assert.Equal(FrameIdentity.CurrentSchema, junk.Identity.Schema);
    }

    sealed class FakeSource(SummaryInputs inputs) : IV2FrameInputSource
    {
        public Task<(V2FrameInputs? Inputs, string? Problem)> LoadAsync(V2BarNotice notice, CancellationToken ct) => Task.FromResult<(V2FrameInputs?, string?)>((new V2FrameInputs(inputs.Basket, inputs.Futures, inputs.Grids, inputs.Vix), null));
    }

    [Fact]
    public async Task Factory_CarriesOnlyTheHeldTokens_ThatLeftTheBasket_AndDedupes()
    {
        var inputs = Summary(8, 26_000);
        var e = End(8);
        var session = new V2SessionInfo(1, Day, "adaptive-fixed13650-v1", "spot-atm-v1", Expiry, Listed(), new Dictionary<string, int>());
        var inBasket = inputs.Quotes[0].Token;
        var outside = new[] { new QuoteInput("CE25500", 120, 121, 650, 650, e.AddMilliseconds(-200), 70), new QuoteInput("PE25500", 90, 91, 650, 650, e.AddMilliseconds(-200), 71) };
        var notice = new V2BarNotice(session, Bar(1, 8, e), 26_000, inputs.Quotes.Concat(outside).ToList()) { HeldTokens = new HashSet<string> { "CE25500", inBasket, "CE99999" } };
        var (frame, problem) = await new InputSourceFrameFactory(new FakeSource(inputs)).BuildAsync(notice, new MarketStateFrameBuilder(), default);
        Assert.Null(problem);
        Assert.Equal(["CE25500"], frame!.HeldQuotes.Select(x => x.Token));                              // PE25500 is not held; the in-basket token stays in Quotes; CE99999 has no observed quote (absent, not invented)
        Assert.Contains(frame.Quotes, x => x.Token == inBasket);
        Assert.DoesNotContain(frame.HeldQuotes, x => x.Token == inBasket);
        var (plain, _) = await new InputSourceFrameFactory(new FakeSource(inputs)).BuildAsync(notice with { HeldTokens = null }, new MarketStateFrameBuilder(), default);
        Assert.Empty(plain!.HeldQuotes);
    }

    // ---- the three TOTAL strategies, trending away from three different strikes ----

    static readonly string[] SixIds = ["opt.total.price.dir:raw", "opt.total.enriched.dir:raw", "opt.total.oi.dir:raw", "opt.total.microdev_inv.dir:raw", "opt.total.ofi.dir:raw", "opt.total.activity.dir:raw"];

    static string RepoStrategyDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NiftySignal.slnx"))) d = d.Parent;
        return Path.Combine(d!.FullName, "paper-engine-v2-strategies");
    }

    static CompiledStrategy Total(string id, int min, int max)
    {
        var o = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoStrategyDir(), id + ".json")))!.AsObject();
        o["instrument"]!["premium"]!["min"] = min; o["instrument"]!["premium"]!["max"] = max;       // test-only: separate premium bands so the three strategies pick three different strikes
        var r = StrategyConfigParser.Parse(o.ToJsonString());
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return CompiledStrategy.Compile(r.Config!);
    }

    static MarketStateFrame Total(int seq, double sign, double centre, IEnumerable<ExecFixtures.Q> quotes, IEnumerable<ExecFixtures.Q>? held = null)
    {
        var e = End(seq);
        var m = new List<MetricValue>();
        foreach (var id in SixIds) m.Add(new MetricValue(H(id), SummaryWindowId.W5, 2 * sign));
        m.Add(new MetricValue(H("opt.total.activity.dir:raw"), SummaryWindowId.W10, 2 * sign));
        m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W5, 1)); m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W10, 1));
        m.Add(new MetricValue(H("vix.fresh"), SummaryWindowId.W5, 1));
        QuoteSnapshot Snap(ExecFixtures.Q q) => new(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, e.AddMilliseconds(-q.AgeMs), 5_000 + seq);
        var hq = (held ?? []).Select(Snap).ToList();
        var id0 = Identity(1, seq, e);
        if (hq.Count > 0) id0 = id0 with { Schema = FrameIdentity.HeldQuotesSchema };
        return MarketStateFrame.Create(id0, BasketId(centre), m, quotes.Select(Snap).ToList(), [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"), hq);
    }

    [Fact]
    public void TrendingMarket_AllThreeTotalStrategies_HoldDifferentStrikes_OutsideTheBasket_AndStillExit()
    {
        var strategies = new[] { Total("TOTAL_ACTIVITY_5_10", 100, 110), Total("TOTAL_VOTE_4_OF_6", 120, 130), Total("TOTAL_VOTE_5_OF_6", 140, 150) };
        var e = ExecFixtures.Engine(strategies);
        // centre 26000: CE25900 (ask 105), CE26000 (ask 125) and CE26100 (ask 145) are in the basket; every strategy sees an up signal and its own premium band
        ExecFixtures.Q[] atm = [new("CE25900", 104, 105), new("CE26000", 124, 125), new("CE26100", 144, 145), new("CE25950", 160, 161), new("CE26050", 90, 91)];
        ExecFixtures.Run(e, [Total(4, 0, 26_000, atm), Total(5, 1, 26_000, atm), Total(6, 1, 26_000, atm.Select(q => q with { Bid = q.Bid + 0.5, Ask = q.Ask + 0.5 }))]);
        var fills = Of<EntryFilled>(e).ToDictionary(x => x.StrategyId, x => x.Token);
        Assert.Equal(new Dictionary<string, string> { ["TOTAL_ACTIVITY_5_10"] = "CE25900", ["TOTAL_VOTE_4_OF_6"] = "CE26000", ["TOTAL_VOTE_5_OF_6"] = "CE26100" }, fills);

        // the market runs up: centre 26250 puts all three held strikes outside the basket (CE26150 and above only); signals stay long, then flip
        ExecFixtures.Q[] run = [new("CE26150", 100, 101), new("CE26200", 80, 81)];
        ExecFixtures.Q[] held = [new("CE25900", 210, 211), new("CE26000", 150, 151), new("CE26100", 120, 121)];
        ExecFixtures.Run(e, [Total(7, 1, 26_250, run, held), Total(8, -1, 26_250, run, held.Select(q => q with { Bid = q.Bid + 1, Ask = q.Ask + 1, AgeMs = 150 }).ToArray())]);
        Assert.Equal(3, Of<ExitRequested>(e).Count);
        Assert.All(Of<ExitRequested>(e), r => Assert.Equal(ExitReason.OppositeSignal, r.Reason));
        Assert.Empty(Of<ExitFilled>(e));                                                               // the decision frame cannot fill itself

        ExecFixtures.Run(e, [Total(9, -1, 26_250, run, held.Select(q => q with { Bid = q.Bid + 2, Ask = q.Ask + 2, AgeMs = 100 }).ToArray())]);
        var xs = Of<ExitFilled>(e).ToDictionary(x => x.StrategyId);
        Assert.Equal(3, xs.Count);
        Assert.Equal(212m - (decimal)strategies[0].Config.Execution.ExitSlippage, xs["TOTAL_ACTIVITY_5_10"].FillPrice);
        Assert.Equal(152m - (decimal)strategies[1].Config.Execution.ExitSlippage, xs["TOTAL_VOTE_4_OF_6"].FillPrice);
        Assert.Equal(122m - (decimal)strategies[2].Config.Execution.ExitSlippage, xs["TOTAL_VOTE_5_OF_6"].FillPrice);
        Assert.Empty(Of<PositionUnresolved>(e));
        Assert.Empty(e.HeldTokens);
        Assert.Equal(3, Of<EntryFilled>(e).Count);                                                     // exit only: no reversal fills on the opposite signal
    }
}
