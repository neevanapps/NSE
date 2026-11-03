using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Metrics;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// The three TOTAL SUMMARY strategies (TOTAL_ACTIVITY_5_10, TOTAL_VOTE_4_OF_6, TOTAL_VOTE_5_OF_6) loaded from the exact JSON files that ship in paper-engine-v2-strategies, the atLeast/1 count node and the single
/// explicit inverted-MicroDev metric.
/// </summary>
public class TotalStrategiesTests
{
    const string A = "TOTAL_ACTIVITY_5_10", B = "TOTAL_VOTE_4_OF_6", C = "TOTAL_VOTE_5_OF_6";

    static readonly string[] SixIds =
    [
        "opt.total.price.dir:raw", "opt.total.enriched.dir:raw", "opt.total.oi.dir:raw", "opt.total.microdev_inv.dir:raw", "opt.total.ofi.dir:raw", "opt.total.activity.dir:raw",
    ];

    static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "NiftySignal.slnx"))) d = d.Parent;
        return Path.Combine(d?.FullName ?? throw new InvalidOperationException("repository root not found"), "paper-engine-v2-strategies");
    }

    static string Json(string id) => File.ReadAllText(Path.Combine(Dir(), id + ".json"));

    static StrategyConfig Config(string id)
    {
        var r = StrategyConfigParser.Parse(Json(id));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return r.Config!;
    }

    static CompiledStrategy Compiled(string id) => CompiledStrategy.Compile(Config(id));

    // ---- frames carrying exactly the TOTAL metrics the strategies read ----

    sealed class DictFrame(Dictionary<(int, SummaryWindowId), double> values) : IMarketStateFrame
    {
        public bool TryRead(int metricHandle, SummaryWindowId window, out double value) => values.TryGetValue((metricHandle, window), out value);
    }

    static DictFrame Vote(double[] six, bool coverage5 = true, bool coverage10 = true, int? omit = null)
    {
        var d = new Dictionary<(int, SummaryWindowId), double>();
        for (var i = 0; i < 6; i++) if (omit != i) d[(H(SixIds[i]), SummaryWindowId.W5)] = six[i];
        if (coverage5) d[(H("opt.total.coverage_full"), SummaryWindowId.W5)] = 1;
        if (coverage10) d[(H("opt.total.coverage_full"), SummaryWindowId.W10)] = 1;
        return new DictFrame(d);
    }

    static DictFrame Activity(double? a5, double? a10, bool coverage = true)
    {
        var d = new Dictionary<(int, SummaryWindowId), double>();
        if (a5 is { } x) d[(H("opt.total.activity.dir:raw"), SummaryWindowId.W5)] = x;
        if (a10 is { } y) d[(H("opt.total.activity.dir:raw"), SummaryWindowId.W10)] = y;
        if (coverage) { d[(H("opt.total.coverage_full"), SummaryWindowId.W5)] = 1; d[(H("opt.total.coverage_full"), SummaryWindowId.W10)] = 1; }
        return new DictFrame(d);
    }

    static EntrySignals Eval(string id, IMarketStateFrame f) => Compiled(id).EvaluateEntry(new EvaluationContext(f, null));

    static double[] Six(int positive, int negative)
    {
        var v = new double[6];
        for (var i = 0; i < 6; i++) v[i] = i < positive ? 1.5 : i < positive + negative ? -1.5 : 0;
        return v;
    }

    // ---- the files ----

    [Fact]
    public void ThreeFiles_AreValid_Independent_AndPaperOnly()
    {
        var files = Directory.GetFiles(Dir(), "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { A, C, B }.OrderBy(x => x, StringComparer.Ordinal), files);                  // exactly these three, no example strategies
        var set = StrategySet.Load(files.Select(f => (f!, Json(f!))).ToList());
        Assert.True(set.SetAccepted);
        Assert.Empty(set.Rejected);
        Assert.Equal(3, set.Active.Count);
        var configs = files.Select(f => Config(f!)).ToList();
        Assert.Equal(3, configs.Select(c => c.Identity.Hash).Distinct().Count());
        Assert.All(configs, c =>
        {
            Assert.True(c.Enabled);
            Assert.Equal(2, c.Lots); Assert.Equal(50_000d, c.VirtualCapital); Assert.Equal(1, c.MaxConcurrent);              // NPT1 ruleset values; each strategy has its own capital / counters
            Assert.Equal(new PremiumRange(100, 150), c.Premium);
            Assert.Equal(new TimeOnly(9, 30), c.Filters.EntryFrom); Assert.Equal(new TimeOnly(15, 0), c.Filters.EntryBefore);
            Assert.Equal(OnOppositeEntrySignal.Exit, c.OnOppositeEntrySignal);
            Assert.Equal(FillMode.AllOrNone, c.Execution.FillMode);
            Assert.Equal("npt1-frozen-fees-v1", c.FeeSchedule);
            Assert.Equal(5, c.Risk.MaxDailyTrades); Assert.Equal(1_500d, c.Risk.MaxDailyLoss); Assert.Equal(LiquidationPolicy.None, c.Risk.Liquidation);        // owner directive: frozen paper limits
            Assert.Equal(StrategyConfigParser.ExitMechanismOppositeSignalOrSessionEnd, c.ExitMechanism);       // explicit exit contract, no artificial predicate
            Assert.Null(c.ExitConditions); Assert.False(c.Exits.Any);
        });
    }

    [Theory]
    [InlineData(A)] [InlineData(B)] [InlineData(C)]
    public void NoFileUsesTheRawMicroDev_VoteFilesUseTheExplicitInvertedAlias(string id)
    {
        var text = Json(id);
        Assert.DoesNotContain("opt.total.microdev.dir", text, StringComparison.Ordinal);                // never the raw direction inside a strategy
        Assert.Equal(id != A, text.Contains("opt.total.microdev_inv.dir:raw", StringComparison.Ordinal));
        Assert.DoesNotContain(":pct", text, StringComparison.Ordinal);                                   // raw Dir metrics (owner decision 2)
    }

    [Theory]
    [InlineData(A)] [InlineData(B)] [InlineData(C)]
    public void EachFile_PassesTheExecutionActivationChecks(string id)
    {
        Assert.Null(StrategyRuntime.CheckActivation(Config(id), ExecFixtures.Options()));
        var firstAfter = StrategyConfigParser.Parse(Json(id).Replace("AsOfEligibleBarEnd", "FirstAfterDecision")).Config!;
        Assert.Equal(ActivationFailureKind.UnsupportedFillQuoteRule, StrategyRuntime.CheckActivation(firstAfter, ExecFixtures.Options())!.Kind);       // why the files use AsOfEligibleBarEnd
    }

    // ---- A: activity agreement ----

    [Theory]
    [InlineData(1.0, 2.0, true, false)]
    [InlineData(-1.0, -2.0, false, true)]
    [InlineData(1.0, -2.0, false, false)]                 // disagree
    [InlineData(-1.0, 2.0, false, false)]
    [InlineData(0.0, 2.0, false, false)]                  // zero counts for nothing
    [InlineData(2.0, 0.0, false, false)]
    [InlineData(0.0, 0.0, false, false)]
    public void A_NeedsBothWindowsStrictlyAgreeing(double a5, double a10, bool call, bool put)
    {
        var s = Eval(A, Activity(a5, a10));
        Assert.Equal((call ? Tri.True : Tri.False, put ? Tri.True : Tri.False), (s.BuyCall, s.BuyPut));
    }

    [Theory]
    [InlineData(null, 2.0)] [InlineData(2.0, null)] [InlineData(null, null)]
    public void A_UnavailableWindow_IsNoSignal(double? a5, double? a10)
    {
        var s = Eval(A, Activity(a5, a10));
        Assert.NotEqual(Tri.True, s.BuyCall); Assert.NotEqual(Tri.True, s.BuyPut);
    }

    [Fact]
    public void A_PartialCoverage_IsNoSignal_AndMissingCoverageIsUnknown()
    {
        var partial = Activity(1, 1);
        var values = new Dictionary<(int, SummaryWindowId), double>
        {
            [(H("opt.total.activity.dir:raw"), SummaryWindowId.W5)] = 1, [(H("opt.total.activity.dir:raw"), SummaryWindowId.W10)] = 1,
            [(H("opt.total.coverage_full"), SummaryWindowId.W5)] = 1, [(H("opt.total.coverage_full"), SummaryWindowId.W10)] = 0,        // 10-bar window PARTIAL
        };
        Assert.Equal(Tri.True, Eval(A, partial).BuyCall);
        var s = Eval(A, new DictFrame(values));
        Assert.Equal(Tri.False, s.BuyCall); Assert.Equal(Tri.False, s.BuyPut);
        Assert.NotEqual(Tri.True, Eval(A, Activity(1, 1, coverage: false)).BuyCall);
    }

    // ---- B / C: k of 6 ----

    [Theory]
    [InlineData(B, 3, 0, false, false)] [InlineData(B, 4, 0, true, false)] [InlineData(B, 5, 0, true, false)] [InlineData(B, 6, 0, true, false)]
    [InlineData(B, 0, 3, false, false)] [InlineData(B, 0, 4, false, true)] [InlineData(B, 0, 5, false, true)] [InlineData(B, 0, 6, false, true)]
    [InlineData(C, 4, 0, false, false)] [InlineData(C, 5, 0, true, false)] [InlineData(C, 6, 0, true, false)]
    [InlineData(C, 0, 4, false, false)] [InlineData(C, 0, 5, false, true)] [InlineData(C, 0, 6, false, true)]
    [InlineData(B, 3, 3, false, false)] [InlineData(C, 3, 3, false, false)]
    [InlineData(B, 4, 2, true, false)] [InlineData(B, 2, 4, false, true)]                    // the others negative: still 4 of 6 on one side
    [InlineData(C, 5, 1, true, false)] [InlineData(C, 1, 5, false, true)]
    [InlineData(B, 3, 2, false, false)]                                                      // one zero: counts for neither side
    public void Vote_FiresOnlyAtOrAboveTheThreshold_OnItsOwnSide(string id, int positive, int negative, bool call, bool put)
    {
        var s = Eval(id, Vote(Six(positive, negative)));
        Assert.Equal((call ? Tri.True : Tri.False, put ? Tri.True : Tri.False), (s.BuyCall, s.BuyPut));
    }

    [Theory]
    [InlineData(B)] [InlineData(C)]
    public void Vote_ZeroCountsForNeitherSide(string id)
    {
        var s = Eval(id, Vote([1, 1, 1, 1, 0, 0]));                                        // 4 positive + two zeros
        Assert.Equal(id == B ? Tri.True : Tri.False, s.BuyCall);
        Assert.Equal(Tri.False, s.BuyPut);
        var allZero = Eval(id, Vote([0, 0, 0, 0, 0, 0]));
        Assert.Equal((Tri.False, Tri.False), (allZero.BuyCall, allZero.BuyPut));
    }

    [Theory]
    [InlineData(B, 0)] [InlineData(B, 3)] [InlineData(B, 5)] [InlineData(C, 2)] [InlineData(C, 4)]
    public void Vote_AnyUnavailableMetric_IsUnknown_EvenWhenTheOthersAlreadyReachTheThreshold(string id, int missing)
    {
        var s = Eval(id, Vote([2, 2, 2, 2, 2, 2], omit: missing));                          // five positives would pass 4-of-6 / 5-of-6 - but all six must be available
        Assert.Equal(Tri.Unknown, s.BuyCall);
        Assert.NotEqual(Tri.True, s.BuyPut);
    }

    [Theory]
    [InlineData(B)] [InlineData(C)]
    public void Vote_PartialOrMissingCoverage_NeverSignals(string id)
    {
        Assert.NotEqual(Tri.True, Eval(id, Vote([2, 2, 2, 2, 2, 2], coverage5: false)).BuyCall);
        var partial = new Dictionary<(int, SummaryWindowId), double> { [(H("opt.total.coverage_full"), SummaryWindowId.W5)] = 0 };
        for (var i = 0; i < 6; i++) partial[(H(SixIds[i]), SummaryWindowId.W5)] = 2;
        var s = Eval(id, new DictFrame(partial));
        Assert.Equal((Tri.False, Tri.False), (s.BuyCall, s.BuyPut));
    }

    [Fact]
    public void Vote_UsesTheWindowFive_NotTheOthers()
    {
        var v = Vote(Six(6, 0));
        var wrongWindow = new Dictionary<(int, SummaryWindowId), double>();
        foreach (var id in SixIds) wrongWindow[(H(id), SummaryWindowId.W10)] = 2;
        wrongWindow[(H("opt.total.coverage_full"), SummaryWindowId.W5)] = 1;
        Assert.Equal(Tri.True, Eval(B, v).BuyCall);
        Assert.NotEqual(Tri.True, Eval(B, new DictFrame(wrongWindow)).BuyCall);
    }

    // ---- atLeast/1: schema, validation, evaluation ----

    static string Tree(string node) => ConfigTests.Mutate(o =>
    {
        o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {node} ] }}");
    });

    static string AtLeast(int min, int items, string extra = "\"onUnknown\": \"Unknown\"") =>
        $"{{ \"atLeast\": {min}, {extra}, \"of\": [ {string.Join(", ", Enumerable.Range(0, items).Select(i => $"{{ \"metric\": \"opt.total.price.dir:raw\", \"window\": \"{(i % 2 == 0 ? "5" : "10")}\", \"cmp\": \">\", \"value\": {i} }}"))} ] }}";

    [Fact]
    public void AtLeast_Valid_ParsesAndCompiles()
    {
        var r = StrategyConfigParser.Parse(Tree(AtLeast(2, 3)));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        var spec = Assert.IsType<AtLeastSpec>(Assert.Single(r.Config!.BuyCall!.Items));
        Assert.Equal((2, 3, AtLeastUnknownPolicy.Unknown), (spec.Min, spec.Items.Count, spec.OnUnknown));
        CompiledStrategy.Compile(r.Config);
    }

    [Theory]
    [InlineData(0, 3, "atLeast")]
    [InlineData(4, 3, "atLeast")]
    [InlineData(1, 1, "of")]                                                   // fewer than two listed conditions
    [InlineData(2, 13, "of")]                                                  // more than the bound
    public void AtLeast_BoundsAreValidated(int min, int items, string path)
    {
        var r = StrategyConfigParser.Parse(Tree(AtLeast(min, items)));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Code == "Value" && e.Path.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public void AtLeast_RequiresAnExplicitSupportedUnknownPolicy_AndRejectsUnknownProperties()
    {
        Assert.Contains(StrategyConfigParser.Parse(Tree(AtLeast(2, 3, "\"onUnknown\": \"Zero\""))).Errors, e => e.Path.Contains("onUnknown"));
        Assert.Contains(StrategyConfigParser.Parse(Tree(AtLeast(2, 3, "\"x\": 1"))).Errors, e => e.Code == "Required" && e.Path.Contains("onUnknown"));
        Assert.Contains(StrategyConfigParser.Parse(Tree(AtLeast(2, 3, "\"onUnknown\": \"Unknown\", \"script\": \"1\""))).Errors, e => e.Code == "Unknown");
        var nonInteger = Tree(AtLeast(2, 3).Replace("\"atLeast\": 2", "\"atLeast\": 2.5"));
        Assert.False(StrategyConfigParser.Parse(nonInteger).IsValid);
    }

    [Fact]
    public void AtLeast_HoldsLeafConditionsOnly_AndCannotBeMixedWithOtherNodeKinds()
    {
        var nested = Tree("{ \"atLeast\": 1, \"onUnknown\": \"Unknown\", \"of\": [ { \"op\": \"AND\", \"items\": [] }, { \"metric\": \"opt.total.price.dir:raw\", \"window\": \"5\", \"cmp\": \">\", \"value\": 0 } ] }");
        Assert.Contains(StrategyConfigParser.Parse(nested).Errors, e => e.Message.Contains("leaf conditions only"));
        var mixed = Tree("{ \"atLeast\": 1, \"op\": \"AND\", \"onUnknown\": \"Unknown\", \"of\": [] }");
        Assert.Contains(StrategyConfigParser.Parse(mixed).Errors, e => e.Message.Contains("exactly one of"));
        var badLeaf = Tree("{ \"atLeast\": 1, \"onUnknown\": \"Unknown\", \"of\": [ { \"metric\": \"nope\", \"window\": \"5\", \"cmp\": \">\", \"value\": 0 }, { \"metric\": \"opt.total.price.dir:raw\", \"window\": \"5\", \"cmp\": \">\", \"value\": 0 } ] }");
        Assert.Contains(StrategyConfigParser.Parse(badLeaf).Errors, e => e.Code == "Metric");
    }

    [Fact]
    public void AtLeast_LeavesCountTowardTheTreeLimit()
    {
        var many = string.Join(", ", Enumerable.Range(0, 3).Select(_ => AtLeast(1, 12)));                // 36 leaves > 32
        var r = StrategyConfigParser.Parse(Tree(many));
        Assert.Contains(r.Errors, e => e.Code == "Limit");
    }

    [Fact]
    public void AtLeast_EvaluationIsACount_WithStrictUnknown()
    {
        var node = new AtLeastNode(2, [Leaf(0, ">", 0), Leaf(1, ">", 0), Leaf(2, ">", 0)]);
        Assert.Equal(Tri.True, node.Evaluate(Ctx(1, 1, -1)));
        Assert.Equal(Tri.True, node.Evaluate(Ctx(1, 1, 1)));
        Assert.Equal(Tri.False, node.Evaluate(Ctx(1, -1, -1)));
        Assert.Equal(Tri.False, node.Evaluate(Ctx(0, 0, 1)));                                   // zero is not > 0
        Assert.Equal(Tri.Unknown, node.Evaluate(Ctx(1, 1, null)));                              // two already true, one Unknown: still Unknown
        Assert.Equal(Tri.Unknown, node.Evaluate(Ctx(null, null, null)));
    }

    static ConditionNode Leaf(int metricIndex, string cmp, double value) =>
        new ThresholdNode(H(SixIds[metricIndex]), SummaryWindowId.W5, cmp == ">" ? Comparison.Gt : Comparison.Lt, value, value);

    static EvaluationContext Ctx(params double?[] v)
    {
        var d = new Dictionary<(int, SummaryWindowId), double>();
        for (var i = 0; i < v.Length; i++) if (v[i] is { } x) d[(H(SixIds[i]), SummaryWindowId.W5)] = x;
        return new EvaluationContext(new DictFrame(d), null);
    }

    [Fact]
    public void ConfigsWithoutAtLeast_ParseExactlyAsBefore()
    {
        var r = StrategyConfigParser.Parse(ConfigTests.Valid);
        Assert.True(r.IsValid);
        Assert.DoesNotContain("atLeast", r.Config!.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal(r.Config.Identity.Hash, StrategyConfigParser.Parse(ConfigTests.Valid).Config!.Identity.Hash);
    }

    // ---- registry and the single inversion ----

    [Fact]
    public void Registry_OriginalHandlesAreUntouched_TheInvertedAliasIsAppendedAtTheEnd()
    {
        Assert.Equal(286, MetricRegistry.Count);
        Assert.Equal("vix.fresh", MetricRegistry.Specs[283].Id);
        Assert.Equal("opt.total.microdev_inv.dir:raw", MetricRegistry.Specs[284].Id);
        Assert.Equal("opt.total.microdev_inv.dir:pct", MetricRegistry.Specs[285].Id);
        Assert.Equal(MetricDirection.CeMinusPeInverted, MetricRegistry.Specs[284].Direction);
        // the inverted metric is not comparable with the raw one: a strategy cannot mix the two conventions
        Assert.False(MetricRegistry.AreComparable(MetricRegistry.Get("opt.total.microdev.dir:raw"), MetricRegistry.Get("opt.total.microdev_inv.dir:raw"), out _));
        Assert.Equal("opt.total.microdev.dir:raw", MetricRegistry.Specs.Single(s => s.Id == "opt.total.microdev.dir:raw").Id);
    }

    [Fact]
    public void MicroDev_IsInvertedExactlyOnce_RawAndEveryOtherMetricStayUnchanged()
    {
        var s = Summary(130);
        var f = Build(new MarketStateFrameBuilder(), 1, 130, s);
        var total = s.Grids.Single(g => g.Kind == NiftySignal.MarketSummary.GridKind.Total);
        for (var i = 0; i < SummaryWindows.Count; i++)
        {
            var w = (SummaryWindowId)i;
            var d = total.Rows[i].Direction!.MicroDev;
            Assert.True(f.TryRead(H("opt.total.microdev.dir:raw"), w, out var raw));
            Assert.True(f.TryRead(H("opt.total.microdev_inv.dir:raw"), w, out var invRaw));
            Assert.True(f.TryRead(H("opt.total.microdev.dir:pct"), w, out var pct));
            Assert.True(f.TryRead(H("opt.total.microdev_inv.dir:pct"), w, out var invPct));
            Assert.Equal(d.Raw!.Value, raw, 12);                                                    // raw is the producer's value, untouched
            Assert.Equal(d.Ratio!.Value, pct, 12);
            Assert.Equal(-raw, invRaw, 12);                                                         // inverted once, not twice
            Assert.Equal(-pct, invPct, 12);
            Assert.NotEqual(raw, invRaw);                                                           // (nonzero in the fixture)
            foreach (var other in new[] { "opt.total.microdev.ce:raw", "opt.total.microdev.pe:raw", "opt.atm.microdev.dir:raw", "opt.itm.microdev.dir:raw", "opt.otm.microdev.dir:raw", "fut.microdev:raw" })
                Assert.True(f.TryRead(H(other), w, out _));
        }

        var atm = s.Grids.Single(g => g.Kind == NiftySignal.MarketSummary.GridKind.Atm).Rows[1].Direction!.MicroDev.Raw!.Value;
        Assert.True(f.TryRead(H("opt.atm.microdev.dir:raw"), SummaryWindowId.W10, out var atmRaw));
        Assert.Equal(atm, atmRaw, 12);                                                              // no other grid / futures MicroDev is inverted
        Assert.Equal(s.Futures[1].MicroDev.Raw!.Value, Read(f, "fut.microdev:raw", SummaryWindowId.W10), 12);
    }

    static double Read(MarketStateFrame f, string id, SummaryWindowId w) => f.TryRead(H(id), w, out var v) ? v : double.NaN;

    [Fact]
    public void InvertedMicroDev_IsUnknownExactlyWhenTheRawIsUnknown_AndZeroStaysZero()
    {
        var early = Build(new MarketStateFrameBuilder(), 1, 7);                                     // too few bars for the 10-bar window
        Assert.False(early.TryRead(H("opt.total.microdev.dir:raw"), SummaryWindowId.W10, out _));
        Assert.False(early.TryRead(H("opt.total.microdev_inv.dir:raw"), SummaryWindowId.W10, out _));
        Assert.False(early.TryRead(H("opt.total.microdev_inv.dir:pct"), SummaryWindowId.W10, out _));
        Assert.True(early.TryRead(H("opt.total.microdev_inv.dir:raw"), SummaryWindowId.W5, out _) == early.TryRead(H("opt.total.microdev.dir:raw"), SummaryWindowId.W5, out _));
    }

    // ---- execution: independence, reversals, causality, determinism ----

    static string[] Ids => [A, B, C];

    static PaperEngine Engine(params string[] ids) => ExecFixtures.Engine(ids.Select(Compiled).ToList());

    /// <summary>A frame with all six vote metrics, activity at 5 and 10, and full coverage; <paramref name="six"/> is the sign of each vote metric, <paramref name="act10"/> the 10-bar activity direction.</summary>
    static MarketStateFrame Fr(int seq, double[] six, double act10, IEnumerable<ExecFixtures.Q>? quotes = null, DateTimeOffset? end = null, bool coverage = true)
    {
        var e = end ?? End(seq);
        var m = new List<MetricValue>();
        for (var i = 0; i < 6; i++) m.Add(new MetricValue(H(SixIds[i]), SummaryWindowId.W5, six[i]));
        m.Add(new MetricValue(H("opt.total.activity.dir:raw"), SummaryWindowId.W10, act10));
        if (coverage) { m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W5, 1)); m.Add(new MetricValue(H("opt.total.coverage_full"), SummaryWindowId.W10, 1)); }
        m.Add(new MetricValue(H("fut.coverage_pct"), SummaryWindowId.W5, 100));
        m.Add(new MetricValue(H("vix.fresh"), SummaryWindowId.W5, 1));
        var qs = (quotes ?? ExecFixtures.Default).Select(q => new QuoteSnapshot(q.Token, q.Bid, q.Ask, q.Qty, q.Qty, e.AddMilliseconds(-q.AgeMs), 5_000 + seq)).ToList();
        return MarketStateFrame.Create(Identity(1, seq, e), BasketId(), m, qs, [], new VixAvailability(e.AddSeconds(-2), 2, "Fresh"));
    }

    static readonly double[] Up = [2, 2, 2, 2, 2, 2], Down = [-2, -2, -2, -2, -2, -2], Flat = [0, 0, 0, 0, 0, 0];

    static List<T> Of<T>(PaperEngine e, string id) where T : PaperEvent => e.Events.OfType<T>().Where(x => x.StrategyId == id).ToList();

    [Fact]
    public void ThreeStrategies_TradeIndependently_OnTheSameFrames()
    {
        var e = Engine(Ids);
        // seq 5: all three see a CE signal; seq 6 supplies the later quote; seq 7: only A flips to PE (activity 5 down), the votes stay up
        var frames = new List<MarketStateFrame> { Fr(4, Flat, 0), Fr(5, Up, 1), Fr(6, Up, 1), Fr(7, Up, 1) };
        ExecFixtures.Run(e, frames);
        Assert.True(e.Events.OfType<EntryOrderPlaced>().Any(), "events: " + e.Events.Count);
        foreach (var id in Ids)
        {
            Assert.Single(Of<EntryOrderPlaced>(e, id));
            Assert.Single(Of<EntryFilled>(e, id));
        }

        Assert.Equal(3, e.Events.OfType<EntryFilled>().Select(x => x.ConfigHash).Distinct().Count());       // three identities
        Assert.Equal(3, e.Events.OfType<EntryFilled>().Select(x => x.PositionId).Distinct().Count());       // three independent positions (the same contract is held three times, once per strategy)
    }

    [Fact]
    public void OppositeSignal_RequestsExit_NeverReverses_AndThePutOpensOnlyOnALaterEvaluation()
    {
        var e = Engine(B);
        ExecFixtures.Run(e, [Fr(4, Flat, 0), Fr(5, Up, 0), Fr(6, Up, 0)]);
        Assert.Single(Of<EntryFilled>(e, B));
        ExecFixtures.Run(e, [Fr(7, Down, 0)]);                                             // PE signal while holding CE
        var req = Assert.Single(Of<ExitRequested>(e, B));
        Assert.Equal(ExitReason.OppositeSignal, req.Reason);
        Assert.Single(Of<EntryOrderPlaced>(e, B));                                         // no put order on the same evaluation
        ExecFixtures.Run(e, [Fr(8, Down, 0)]);                                             // exit fills on the later quote; the put signal of THIS frame may now enter (a later, separately evaluated frame)
        Assert.Single(Of<ExitFilled>(e, B));
        var placed = Of<EntryOrderPlaced>(e, B);
        Assert.Equal(2, placed.Count);
        Assert.Equal(OptionRight.Put, placed[1].Right);
        Assert.True(placed[1].BarSeq > req.BarSeq);
    }

    [Fact]
    public void NeutralOrUnavailableSignal_PreservesThePosition()
    {
        var e = Engine(B, C);
        ExecFixtures.Run(e, [Fr(4, Flat, 0), Fr(5, Up, 0), Fr(6, Up, 0), Fr(7, Flat, 0), Fr(8, Up, 0, coverage: false), Fr(9, [2, 2, -2, 0, 1, 1], 0)]);
        Assert.Single(Of<EntryFilled>(e, B));
        Assert.Empty(Of<ExitRequested>(e, B));
        Assert.Empty(Of<ExitRequested>(e, C));
    }

    [Fact]
    public void NoEntryBeforeOpenOrFromThreeOClock()
    {
        var ist = TimeSpan.FromHours(5.5);
        var e = Engine(B);
        var at1500 = new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0, 0)), ist).ToUniversalTime();       // frames carry UTC instants, as the database delivers them
        ExecFixtures.Run(e, [Fr(1, Up, 0, end: at1500), Fr(2, Up, 0, end: at1500.AddMinutes(1))]);
        Assert.Empty(e.Events.OfType<EntryOrderPlaced>());
        Assert.Contains(e.Events.OfType<EntryRejected>(), r => r.Reason == RejectionReason.OutsideEntryWindow);
        var before = Engine(B);
        var at0929 = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 29, 0)), ist).ToUniversalTime();
        ExecFixtures.Run(before, [Fr(1, Up, 0, end: at0929)]);
        Assert.Empty(before.Events.OfType<EntryOrderPlaced>());
    }

    [Fact]
    public void QuotesOutsideTheFrozenUniverse_AreNeverSubstituted()
    {
        var e = Engine(B);
        ExecFixtures.Run(e, [Fr(5, Up, 0, quotes: [new("CE26000", 40, 41)])]);                // the only call quote has an ask outside 100-150
        Assert.Empty(e.Events.OfType<EntryOrderPlaced>());
        Assert.Contains(e.Events.OfType<EntryRejected>(), r => r.Reason == RejectionReason.NoQualifyingContract);
    }

    // ---- exit contract: OppositeSignalOrSessionEnd ----

    static string Mutated(string id, Action<JsonObject> edit)
    {
        var o = JsonNode.Parse(Json(id))!.AsObject();
        edit(o);
        return o.ToJsonString();
    }

    [Theory]
    [InlineData(A)] [InlineData(B)] [InlineData(C)]
    public void ExitMechanism_IsBackwardCompatible_AndExclusiveWithAnExitPredicate(string id)
    {
        Assert.DoesNotContain("coverage_pct", Json(id), StringComparison.Ordinal);                          // the artificial never-true exit predicate is gone
        var both = StrategyConfigParser.Parse(Mutated(id, o => o["exit"]!["conditions"] = JsonNode.Parse("""{ "op": "AND", "items": [ { "metric": "fut.coverage_pct", "window": "5", "cmp": ">", "value": 100 } ] }""")));
        Assert.False(both.IsValid);
        Assert.Contains(both.Errors, e => e.ToString()!.Contains("conditions", StringComparison.Ordinal));
        var unknown = StrategyConfigParser.Parse(Mutated(id, o => o["exit"]!["mechanism"] = "Whatever"));
        Assert.False(unknown.IsValid);
        var none = StrategyConfigParser.Parse(Mutated(id, o => ((JsonObject)o["exit"]!).Remove("mechanism")));
        Assert.False(none.IsValid);                                                                         // a strategy still needs SOME exit mechanism
        // the original contract is untouched: an explicit predicate (no mechanism) parses exactly as before, with no mechanism recorded
        var legacy = StrategyConfigParser.Parse(Mutated(id, o => o["exit"] = JsonNode.Parse("""{ "conditions": { "op": "AND", "items": [ { "metric": "opt.total.coverage_full", "window": "5", "cmp": "==", "value": 0 } ] }, "onOppositeEntrySignal": "Exit" }""")));
        Assert.True(legacy.IsValid, string.Join("; ", legacy.Errors));
        Assert.Null(legacy.Config!.ExitMechanism);
        Assert.DoesNotContain("mechanism", legacy.Config.CanonicalJson, StringComparison.Ordinal);          // existing config hashes cannot move
        Assert.Contains("mechanism", Config(id).CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeStrategies_OnlyExit_OnTheOppositeSignal_OrAtTheSessionEnd()
    {
        var ist = TimeSpan.FromHours(5.5);
        var at1500 = new DateTimeOffset(Day.ToDateTime(new TimeOnly(15, 0, 0)), ist).ToUniversalTime();
        var e = Engine(Ids);
        ExecFixtures.Run(e, [Fr(4, Flat, 0), Fr(5, Up, 1), Fr(6, Up, 1), Fr(7, Flat, 0), Fr(8, Up, 1)]);       // neutral / repeated signals never exit
        foreach (var id in Ids) { Assert.Single(Of<EntryFilled>(e, id)); Assert.Empty(Of<ExitRequested>(e, id)); }
        ExecFixtures.Run(e, [Fr(50, Up, 1, end: at1500.AddMinutes(-1)), Fr(51, Up, 1, end: at1500), Fr(52, Up, 1, end: at1500.AddMinutes(1))]);
        foreach (var id in Ids)
        {
            var req = Assert.Single(Of<ExitRequested>(e, id));
            Assert.Equal(ExitReason.Terminal, req.Reason);
            Assert.Single(Of<ExitFilled>(e, id));
            Assert.Empty(Of<PositionUnresolved>(e, id));
        }
    }

    [Fact]
    public void MaxDailyTradesFive_BlocksTheSixthEntry_IndependentPerStrategy()
    {
        var edited = StrategyConfigParser.Parse(Mutated(B, o => o["risk"]!["maxDailyTrades"] = 1)).Config!;
        var e = ExecFixtures.Engine([CompiledStrategy.Compile(edited), Compiled(C)]);
        ExecFixtures.Run(e, [Fr(4, Flat, 0), Fr(5, Up, 0), Fr(6, Up, 0), Fr(7, Down, 0), Fr(8, Down, 0), Fr(9, Down, 0)]);
        Assert.Single(Of<EntryFilled>(e, B));                                                              // one trade allowed, the reversal entry is refused
        Assert.Contains(e.Events.OfType<EntryRejected>(), r => r.StrategyId == B && r.Reason == RejectionReason.RiskBreach && r.Detail.Contains("daily trades", StringComparison.Ordinal));
        Assert.Equal(2, Of<EntryFilled>(e, C).Count);                                                      // C has its own counter (limit 5)
    }

    [Fact]
    public void FrozenLimits_AreFiveTradesAndFifteenHundredLoss()
    {
        foreach (var id in Ids) { Assert.Equal(5, Config(id).Risk.MaxDailyTrades); Assert.Equal(1500d, Config(id).Risk.MaxDailyLoss); }
    }

    [Theory]
    [InlineData(B)] [InlineData(C)]
    public void VoteFiles_ReadExactlyTheSixMetrics_AtTheFiveBarWindow(string id)
    {
        var node = JsonNode.Parse(Json(id))!["entry"]!;
        foreach (var side in new[] { "buyCall", "buyPut" })
        {
            var leaves = node[side]!["items"]!.AsArray().SelectMany(i => i!["of"] is JsonArray of ? of.Select(x => x!) : [i!]).ToList();
            var metrics = leaves.Where(l => l["metric"]!.GetValue<string>() != "opt.total.coverage_full").Select(l => l["metric"]!.GetValue<string>()).ToArray();
            Assert.Equal(SixIds.OrderBy(x => x, StringComparer.Ordinal), metrics.OrderBy(x => x, StringComparer.Ordinal));
            Assert.All(leaves, l => Assert.Equal("5", l["window"]!.GetValue<string>()));
        }
    }

    [Fact]
    public void Replay_IsDeterministic_NoDuplicateFills()
    {
        List<string> Run()
        {
            var e = Engine(Ids);
            ExecFixtures.Run(e, [Fr(4, Flat, 0), Fr(5, Up, 1), Fr(6, Up, 1), Fr(7, Down, -1), Fr(8, Down, -1), Fr(9, Up, 1)]);
            return e.Events.Select(x => x.ToString()!).ToList();
        }

        var first = Run();
        Assert.Equal(first, Run());
        var e2 = Engine(Ids);
        ExecFixtures.Run(e2, [Fr(4, Flat, 0), Fr(5, Up, 1), Fr(6, Up, 1), Fr(7, Down, -1), Fr(8, Down, -1)]);
        Assert.Equal(e2.Events.Count, e2.Events.Select(x => x.EventId).Distinct().Count());
    }
}
