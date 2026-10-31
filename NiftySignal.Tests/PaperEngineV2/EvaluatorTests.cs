using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Phase 6a: the typed registry, condition validation (unit / direction / window), three-valued evaluation and determinism.</summary>
public class EvaluatorTests
{
    sealed class DictFrame(Dictionary<(int, SummaryWindowId), double> values) : IMarketStateFrame
    {
        public int Reads;

        public bool TryRead(int metricHandle, SummaryWindowId window, out double value)
        {
            Reads++;
            return values.TryGetValue((metricHandle, window), out value);
        }
    }

    static int H(string id) => MetricRegistry.Get(id).Handle;

    static DictFrame Frame(params (string Id, SummaryWindowId W, double V)[] v) => new(v.ToDictionary(x => (H(x.Id), x.W), x => x.V));

    static StrategyConfig Config(Action<JsonObject>? edit = null)
    {
        var r = StrategyConfigParser.Parse(ConfigTests.Mutate(edit ?? (_ => { })));
        Assert.True(r.IsValid, string.Join("; ", r.Errors));
        return r.Config!;
    }

    static string LeafJson(string metric, string window, string cmp, string value) => $"{{ \"metric\": \"{metric}\", \"window\": \"{window}\", \"cmp\": \"{cmp}\", \"value\": {value} }}";

    static StrategyConfig WithBuyCall(string leaves, string op = "AND") =>
        Config(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"{op}\", \"items\": [ {leaves} ] }}"));

    static void AssertInvalid(string leaf, string code, string pathPart)
    {
        var r = StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {leaf} ] }}")));
        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => e.Code == code && (e.Path.Contains(pathPart, StringComparison.Ordinal) || e.Message.Contains(pathPart, StringComparison.Ordinal)));
    }

    // ---- registry ----

    [Fact]
    public void Registry_IsDenseUniqueAndTyped()
    {
        Assert.Equal(284, MetricRegistry.Count);                                                           // 16 futures + 4 grids x 66 + 4 VIX
        Assert.Equal(Enumerable.Range(0, MetricRegistry.Count), MetricRegistry.Specs.Select(s => s.Handle));
        Assert.Equal(MetricRegistry.Count, MetricRegistry.Specs.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(MetricRegistry.Specs, s => { Assert.Equal(MetricRegistry.SourceModel, s.SourceModel); Assert.False(string.IsNullOrWhiteSpace(s.Description)); Assert.False(string.IsNullOrWhiteSpace(s.Availability)); });
        Assert.Equal(MetricUnit.DeltaRaw, MetricRegistry.Get("opt.atm.delta.ce:raw").Unit);                // raw units: the x100 display scale never reaches a threshold
        Assert.Equal(MetricUnit.VegaRaw, MetricRegistry.Get("opt.total.vega.dir:raw").Unit);
        Assert.Equal(MetricDirection.PeMinusCe, MetricRegistry.Get("opt.itm.oi.dir:raw").Direction);       // OI direction is PE - CE
        Assert.Equal(MetricDirection.CeMinusPe, MetricRegistry.Get("opt.itm.strict.dir:pct").Direction);
        Assert.Equal(MetricDirection.Level, MetricRegistry.Get("opt.itm.strict.ce:pct").Direction);
        Assert.Equal(MetricUnit.Percent, MetricRegistry.Get("opt.otm.ofi.pe:pct").Unit);
        Assert.True(MetricRegistry.Specs.Where(s => s.Discrete).All(s => s.Unit == MetricUnit.Flag || s.Id.EndsWith("coverage_full", StringComparison.Ordinal)));
        Assert.False(MetricRegistry.TryGet("opt.atm.price.ce", out _));                                       // the kind suffix is mandatory
        Assert.False(MetricRegistry.TryGet("opt.chain.price.ce:raw", out _));
        Assert.False(MetricRegistry.TryGet(null, out _));
    }

    // ---- validation: metric, unit, direction, window, equality ----

    [Fact]
    public void Validation_UnknownMetricWindowAndOperator()
    {
        AssertInvalid(LeafJson("opt.atm.nonsense.ce:raw", "10", ">", "1"), "Metric", "metric");
        AssertInvalid(LeafJson("opt.atm.strict.ce:pct", "7", ">", "1"), "Window", "window");
        AssertInvalid(LeafJson("opt.atm.strict.ce:pct", "10", "=>", "1"), "Value", "cmp");
        AssertInvalid(LeafJson("opt.atm.strict.ce:pct", "10", ">", "\"x\""), "Type", "value");
        AssertInvalid("{ \"metric\": \"opt.atm.strict.ce:pct\", \"window\": \"10\", \"cmp\": \">\" }", "Value", "$/entry/buyCall/items/0");   // neither value nor metricB
        AssertInvalid("{ \"metric\": \"opt.atm.strict.ce:pct\", \"window\": \"10\", \"cmp\": \">\", \"value\": 1, \"metricB\": \"opt.atm.strict.pe:pct\" }", "Value", "$/entry/buyCall/items/0");
        AssertInvalid("{ \"metric\": \"opt.atm.strict.ce:pct\", \"window\": \"10\", \"cmp\": \">\", \"value\": 1, \"script\": \"x\" }", "Unknown", "script");     // no expressions
    }

    [Fact]
    public void Validation_AllFiveWindowsAreAccepted()
    {
        foreach (var w in new[] { "5", "10", "50", "100", "Day" })
            Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {LeafJson("fut.strict:pct", w, ">", "1")} ] }}"))).IsValid, w);
    }

    [Fact]
    public void Validation_MetricVsMetric_RequiresSameUnitAndSameDirectionConvention()
    {
        string Pair(string a, string b) => $"{{ \"metric\": \"{a}\", \"window\": \"10\", \"cmp\": \">\", \"metricB\": \"{b}\" }}";
        AssertInvalid(Pair("opt.atm.strict.ce:pct", "opt.atm.price.ce:raw"), "Unit", "metricB");                  // Percent vs Price
        AssertInvalid(Pair("opt.atm.delta.ce:raw", "opt.atm.vega.ce:raw"), "Unit", "metricB");                    // DeltaRaw vs VegaRaw
        AssertInvalid(Pair("opt.atm.strict.ce:pct", "opt.atm.strict.dir:pct"), "Unit", "direction conventions");  // a side value vs a CE-PE difference
        AssertInvalid(Pair("opt.atm.strict.dir:pct", "opt.atm.oi.dir:pct"), "Unit", "direction conventions");     // CE-PE vs the OI PE-CE convention
        AssertInvalid(Pair("opt.atm.strict.ce:pct", "opt.atm.strict.ce:pct"), "Value", "itself");                 // pointless self comparison
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {Pair("opt.atm.strict.ce:pct", "opt.atm.strict.pe:pct")} ] }}"))).IsValid);
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {{ \"metric\": \"opt.atm.strict.ce:pct\", \"window\": \"5\", \"cmp\": \">\", \"metricB\": \"opt.atm.strict.ce:pct\", \"windowB\": \"50\" }} ] }}"))).IsValid);   // the same metric in two windows is fine
    }

    [Fact]
    public void Validation_ExactEquality_IsOnlyForDiscreteMetrics_AndFlagsTakeZeroOrOne()
    {
        AssertInvalid(LeafJson("opt.atm.strict.dir:pct", "10", "==", "5"), "Value", "cmp");
        AssertInvalid(LeafJson("fut.price_change:raw", "10", "!=", "5"), "Value", "cmp");
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {LeafJson("vix.fresh", "5", "==", "1")}, {LeafJson("opt.atm.coverage_full", "10", "==", "1")} ] }}"))).IsValid);
        AssertInvalid(LeafJson("vix.fresh", "5", "==", "2"), "Value", "value");
        AssertInvalid(LeafJson("vix.fresh", "5", ">", "1"), "Value", "value");
        AssertInvalid(LeafJson("vix.fresh", "5", "between", "[0, 1]"), "Value", "value");
    }

    [Fact]
    public void Validation_BetweenAndCross()
    {
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {LeafJson("fut.strict:pct", "10", "between", "[-5, 5]")} ] }}"))).IsValid);
        AssertInvalid(LeafJson("fut.strict:pct", "10", "between", "[5, -5]"), "Value", "value");
        AssertInvalid(LeafJson("fut.strict:pct", "10", "between", "5"), "Type", "value");
        AssertInvalid(LeafJson("fut.strict:pct", "10", "between", "[1, 2, 3]"), "Type", "value");
        AssertInvalid("{ \"metric\": \"fut.strict:pct\", \"window\": \"10\", \"cmp\": \"between\", \"metricB\": \"opt.atm.strict.ce:pct\" }", "Value", "cmp");
        AssertInvalid("{ \"cross\": \"Sideways\", \"metric\": \"fut.strict:pct\", \"window\": \"10\", \"level\": 0 }", "Value", "cross");
        AssertInvalid("{ \"cross\": \"Up\", \"metric\": \"vix.fresh\", \"window\": \"10\", \"level\": 0 }", "Value", "metric");              // discrete metric
        AssertInvalid("{ \"cross\": \"Up\", \"metric\": \"fut.strict:pct\", \"window\": \"10\", \"level\": 0, \"levelMetric\": \"fut.enriched:pct\" }", "Value", "$/entry/buyCall/items/0");
        AssertInvalid("{ \"cross\": \"Up\", \"metric\": \"fut.strict:pct\", \"window\": \"10\", \"levelMetric\": \"fut.price_change:raw\" }", "Unit", "levelMetric");
        AssertInvalid("{ \"op\": \"AND\", \"cross\": \"Up\", \"items\": [] }", "Value", "either a group");
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse("{ \"op\": \"AND\", \"items\": [ { \"cross\": \"Up\", \"metric\": \"fut.strict:pct\", \"window\": \"5\", \"levelMetric\": \"fut.enriched:pct\", \"levelWindow\": \"10\" } ] }"))).IsValid);
    }

    [Fact]
    public void Validation_TreeShape_DepthLeafAndEmptyLimits()
    {
        static string Nest(int levels, string leaf) => levels == 1 ? $"{{ \"op\": \"AND\", \"items\": [ {leaf} ] }}" : $"{{ \"op\": \"OR\", \"items\": [ {Nest(levels - 1, leaf)} ] }}";
        var leaf = LeafJson("fut.strict:pct", "10", ">", "1");
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse(Nest(4, leaf)))).IsValid);                   // depth 4 is allowed
        var tooDeep = StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse(Nest(5, leaf))));
        Assert.Contains(tooDeep.Errors, e => e.Code == "Limit");
        string Many(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => LeafJson("fut.strict:pct", "10", ">", i.ToString())));
        Assert.True(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"OR\", \"items\": [ {Many(32)} ] }}"))).IsValid);   // 32 leaves
        Assert.Contains(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"OR\", \"items\": [ {Many(33)} ] }}"))).Errors, e => e.Code == "Limit");
        Assert.False(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse("{ \"op\": \"AND\", \"items\": [] }"))).IsValid);
        Assert.False(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse("{ \"op\": \"XOR\", \"items\": [ " + leaf + " ] }"))).IsValid);
        Assert.False(StrategyConfigParser.Parse(ConfigTests.Mutate(o => o["entry"]!["buyCall"] = JsonNode.Parse(leaf))).IsValid);        // the root must be a group
    }

    // ---- three-valued logic ----

    [Fact]
    public void TriLogic_FollowsKleeneTruthTables()
    {
        var all = new[] { Tri.False, Tri.True, Tri.Unknown };
        foreach (var a in all)
            foreach (var b in all)
            {
                var and = a == Tri.False || b == Tri.False ? Tri.False : a == Tri.True && b == Tri.True ? Tri.True : Tri.Unknown;
                var or = a == Tri.True || b == Tri.True ? Tri.True : a == Tri.False && b == Tri.False ? Tri.False : Tri.Unknown;
                Assert.Equal(and, TriLogic.And(a, b)); Assert.Equal(or, TriLogic.Or(a, b));
                Assert.Equal(TriLogic.And(a, b), TriLogic.And(b, a)); Assert.Equal(TriLogic.Or(a, b), TriLogic.Or(b, a));
            }

        Assert.Equal(Tri.False, TriLogic.And(Tri.Unknown, Tri.False));        // a decided False wins over Unknown
        Assert.Equal(Tri.True, TriLogic.Or(Tri.Unknown, Tri.True));
        Assert.Equal(Tri.Unknown, TriLogic.And(Tri.True, Tri.Unknown));       // Unknown is never silently True or False
        Assert.Equal(Tri.Unknown, TriLogic.Or(Tri.False, Tri.Unknown));
    }

    // ---- evaluation ----

    static readonly string Strict = "opt.atm.strict.dir:pct";

    [Fact]
    public void Threshold_TrueFalseAndUnknown_NeverZeroForMissing()
    {
        var s = CompiledStrategy.Compile(WithBuyCall(LeafJson(Strict, "10", ">", "5")));
        Assert.Equal(Tri.True, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, 6)), null)).BuyCall);
        Assert.Equal(Tri.False, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, 5)), null)).BuyCall);          // strict inequality
        Assert.Equal(Tri.Unknown, s.EvaluateEntry(new(Frame(), null)).BuyCall);                                          // missing value: Unknown, not "0 > 5 = False"
        Assert.Equal(Tri.Unknown, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W5, 99)), null)).BuyCall);          // another window is not a substitute
        Assert.Equal(Tri.Unknown, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, double.NaN)), null)).BuyCall);
        Assert.Equal(Tri.Unknown, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, double.PositiveInfinity)), null)).BuyCall);
        Assert.Equal(Tri.False, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, 99)), null)).BuyPut);          // the strategy has a buyPut group (< -5) and 99 is not below it
    }

    [Theory]
    [InlineData(">", 5, 4, false)] [InlineData(">", 5, 6, true)] [InlineData("<", 5, 4, true)] [InlineData("<", 5, 5, false)]
    [InlineData(">=", 5, 5, true)] [InlineData("<=", 5, 5, true)] [InlineData("<=", 5, 6, false)]
    public void Comparisons_BehaveAsWritten(string cmp, double threshold, double value, bool expected)
    {
        var s = CompiledStrategy.Compile(WithBuyCall(LeafJson("fut.strict:pct", "5", cmp, threshold.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(expected ? Tri.True : Tri.False, s.EvaluateEntry(new(Frame(("fut.strict:pct", SummaryWindowId.W5, value)), null)).BuyCall);
    }

    [Fact]
    public void Between_IsInclusive_AndDiscreteEquality()
    {
        var s = CompiledStrategy.Compile(WithBuyCall(LeafJson("fut.strict:pct", "5", "between", "[-5, 5]")));
        foreach (var (v, e) in new[] { (-5d, true), (0d, true), (5d, true), (5.0001, false), (-5.0001, false) })
            Assert.Equal(e ? Tri.True : Tri.False, s.EvaluateEntry(new(Frame(("fut.strict:pct", SummaryWindowId.W5, v)), null)).BuyCall);
        var eq = CompiledStrategy.Compile(WithBuyCall(LeafJson("opt.atm.coverage_full", "10", "==", "1")));
        Assert.Equal(Tri.True, eq.EvaluateEntry(new(Frame(("opt.atm.coverage_full", SummaryWindowId.W10, 1)), null)).BuyCall);
        Assert.Equal(Tri.False, eq.EvaluateEntry(new(Frame(("opt.atm.coverage_full", SummaryWindowId.W10, 0)), null)).BuyCall);
    }

    [Fact]
    public void MetricVsMetric_NeedsBothReadings()
    {
        var s = CompiledStrategy.Compile(WithBuyCall("{ \"metric\": \"opt.atm.strict.ce:pct\", \"window\": \"10\", \"cmp\": \">\", \"metricB\": \"opt.atm.strict.pe:pct\" }"));
        Assert.Equal(Tri.True, s.EvaluateEntry(new(Frame(("opt.atm.strict.ce:pct", SummaryWindowId.W10, 3), ("opt.atm.strict.pe:pct", SummaryWindowId.W10, 1)), null)).BuyCall);
        Assert.Equal(Tri.False, s.EvaluateEntry(new(Frame(("opt.atm.strict.ce:pct", SummaryWindowId.W10, 1), ("opt.atm.strict.pe:pct", SummaryWindowId.W10, 3)), null)).BuyCall);
        Assert.Equal(Tri.Unknown, s.EvaluateEntry(new(Frame(("opt.atm.strict.ce:pct", SummaryWindowId.W10, 3)), null)).BuyCall);
    }

    [Fact]
    public void AndOrGroups_PropagateUnknownByKleeneLogic()
    {
        var a = LeafJson("fut.strict:pct", "5", ">", "0"); var b = LeafJson("fut.enriched:pct", "5", ">", "0");
        var and = CompiledStrategy.Compile(WithBuyCall(a + "," + b, "AND"));
        var or = CompiledStrategy.Compile(WithBuyCall(a + "," + b, "OR"));
        DictFrame F(double? x, double? y)
        {
            var d = new Dictionary<(int, SummaryWindowId), double>();
            if (x is { } xv) d[(H("fut.strict:pct"), SummaryWindowId.W5)] = xv;
            if (y is { } yv) d[(H("fut.enriched:pct"), SummaryWindowId.W5)] = yv;
            return new DictFrame(d);
        }
        Assert.Equal(Tri.False, and.EvaluateEntry(new(F(-1, null), null)).BuyCall);       // False AND Unknown = False
        Assert.Equal(Tri.Unknown, and.EvaluateEntry(new(F(1, null), null)).BuyCall);      // True AND Unknown = Unknown
        Assert.Equal(Tri.True, and.EvaluateEntry(new(F(1, 1), null)).BuyCall);
        Assert.Equal(Tri.True, or.EvaluateEntry(new(F(1, null), null)).BuyCall);          // True OR Unknown = True
        Assert.Equal(Tri.Unknown, or.EvaluateEntry(new(F(-1, null), null)).BuyCall);      // False OR Unknown = Unknown
        Assert.Equal(Tri.False, or.EvaluateEntry(new(F(-1, -1), null)).BuyCall);
    }

    [Fact]
    public void ShortCircuit_StopsReadingOnceTheGroupIsDecided()
    {
        var a = LeafJson("fut.strict:pct", "5", ">", "100"); var b = LeafJson("fut.enriched:pct", "5", ">", "0");
        var s = CompiledStrategy.Compile(WithBuyCall(a + "," + b, "AND"));
        var f = Frame(("fut.strict:pct", SummaryWindowId.W5, 1), ("fut.enriched:pct", SummaryWindowId.W5, 1));
        Assert.Equal(Tri.False, s.EvaluateEntry(new(f, null)).BuyCall);
        Assert.Equal(2, f.Reads);                                                          // buyCall stopped at its first False (1 read, the second leaf was never read) + the buyPut group's single read
    }

    [Fact]
    public void Crossover_UsesPreviousAndCurrentFrame_AndIsNeverInferredAcrossAGap()
    {
        var up = CompiledStrategy.Compile(WithBuyCall("{ \"cross\": \"Up\", \"metric\": \"fut.strict:pct\", \"window\": \"5\", \"level\": 0 }"));
        DictFrame F(double v) => Frame(("fut.strict:pct", SummaryWindowId.W5, v));
        Assert.Equal(Tri.True, up.EvaluateEntry(new(F(1), F(-1))).BuyCall);
        Assert.Equal(Tri.True, up.EvaluateEntry(new(F(1), F(0))).BuyCall);               // previous <= level counts (as NPT1's d[t-1] <= 0)
        Assert.Equal(Tri.False, up.EvaluateEntry(new(F(1), F(1))).BuyCall);              // already above
        Assert.Equal(Tri.False, up.EvaluateEntry(new(F(0), F(-1))).BuyCall);             // current must be strictly above
        Assert.Equal(Tri.False, up.EvaluateEntry(new(F(-2), F(-1))).BuyCall);
        Assert.Equal(Tri.Unknown, up.EvaluateEntry(new(F(1), null)).BuyCall);            // no previous frame (first bar / gap / unverified predecessor)
        Assert.Equal(Tri.Unknown, up.EvaluateEntry(new(F(1), Frame())).BuyCall);         // previous frame lacks the value
        var down = CompiledStrategy.Compile(WithBuyCall("{ \"cross\": \"Down\", \"metric\": \"fut.strict:pct\", \"window\": \"5\", \"level\": 0 }"));
        Assert.Equal(Tri.True, down.EvaluateEntry(new(F(-1), F(1))).BuyCall);
        Assert.Equal(Tri.True, down.EvaluateEntry(new(F(-1), F(0))).BuyCall);
        Assert.Equal(Tri.False, down.EvaluateEntry(new(F(0), F(1))).BuyCall);
        var vs = CompiledStrategy.Compile(WithBuyCall("{ \"cross\": \"Up\", \"metric\": \"fut.strict:pct\", \"window\": \"5\", \"levelMetric\": \"fut.enriched:pct\", \"levelWindow\": \"5\" }"));
        DictFrame G(double a, double b) => Frame(("fut.strict:pct", SummaryWindowId.W5, a), ("fut.enriched:pct", SummaryWindowId.W5, b));
        Assert.Equal(Tri.True, vs.EvaluateEntry(new(G(5, 3), G(2, 3))).BuyCall);          // crossed a moving level
        Assert.Equal(Tri.Unknown, vs.EvaluateEntry(new(G(5, 3), F(2))).BuyCall);
    }

    [Fact]
    public void RequireFreshVix_IsAnEntryOnlyFilter_UnknownWhenTheFlagIsMissing()
    {
        var plain = CompiledStrategy.Compile(WithBuyCall(LeafJson("fut.strict:pct", "5", ">", "0")));
        var filtered = CompiledStrategy.Compile(Config(o =>
        {
            o["entry"]!["buyCall"] = JsonNode.Parse("{ \"op\": \"AND\", \"items\": [ " + LeafJson("fut.strict:pct", "5", ">", "0") + " ] }");
            o["filters"]!["requireFreshVix"] = true;
        }));
        var stale = Frame(("fut.strict:pct", SummaryWindowId.W5, 1), ("vix.fresh", SummaryWindowId.W5, 0));
        var fresh = Frame(("fut.strict:pct", SummaryWindowId.W5, 1), ("vix.fresh", SummaryWindowId.W5, 1));
        Assert.Equal(Tri.True, plain.EvaluateEntry(new(stale, null)).BuyCall);              // OFF by default: the flag is not even consulted
        Assert.Equal(Tri.False, filtered.EvaluateEntry(new(stale, null)).BuyCall);
        Assert.Equal(Tri.True, filtered.EvaluateEntry(new(fresh, null)).BuyCall);
        Assert.Equal(Tri.Unknown, filtered.EvaluateEntry(new(Frame(("fut.strict:pct", SummaryWindowId.W5, 1)), null)).BuyCall);   // flag unavailable
        Assert.Equal(Tri.False, filtered.EvaluateEntry(new(Frame(("fut.strict:pct", SummaryWindowId.W5, -1)), null)).BuyCall);
        var withPrev = new EvaluationContext(stale, Frame(("fut.strict:pct", SummaryWindowId.W5, 1), ("vix.fresh", SummaryWindowId.W5, 0)));
        Assert.Equal(plain.EvaluateExit(withPrev), filtered.EvaluateExit(withPrev));         // exits are never filtered by requireFreshVix
    }

    [Fact]
    public void Exit_AndSidesWithoutAGroup()
    {
        var s = CompiledStrategy.Compile(Config(o => { ((JsonObject)o["entry"]!).Remove("buyPut"); }));
        var f = Frame((Strict, SummaryWindowId.W10, 9));
        Assert.Equal(Tri.False, s.EvaluateEntry(new(f, null)).BuyPut);                      // a side without a group never trades
        Assert.Equal(Tri.True, s.EvaluateEntry(new(Frame((Strict, SummaryWindowId.W10, 9), ("fut.strict:pct", SummaryWindowId.W5, 1)), null)).BuyCall);
        Assert.Equal(Tri.True, s.EvaluateExit(new(Frame((Strict, SummaryWindowId.W10, -1)), Frame((Strict, SummaryWindowId.W10, 1)))));   // the configured Down-cross exit
        Assert.Equal(Tri.Unknown, s.EvaluateExit(new(Frame((Strict, SummaryWindowId.W10, -1)), null)));
        var noCond = CompiledStrategy.Compile(Config(o => { ((JsonObject)o["exit"]!).Remove("conditions"); o["exits"] = JsonNode.Parse("{ \"maxHoldBars\": 3 }"); }));
        Assert.Equal(Tri.False, noCond.EvaluateExit(new(f, null)));
    }

    [Fact]
    public void EntryTimeWindow_IsHalfOpen_FromInclusiveToBeforeExclusive()
    {
        var s = CompiledStrategy.Compile(Config());
        Assert.False(s.IsEntryTimeAllowed(new TimeOnly(9, 29, 59)));
        Assert.True(s.IsEntryTimeAllowed(new TimeOnly(9, 30, 0)));
        Assert.True(s.IsEntryTimeAllowed(new TimeOnly(14, 59, 59)));
        Assert.False(s.IsEntryTimeAllowed(new TimeOnly(15, 0, 0)));                         // no entry at / after 15:00:00
    }

    // ---- determinism, immutability, isolation ----

    static IReadOnlyList<(string Source, string Json)> Thirty() =>
        Enumerable.Range(1, 30).Select(i => ($"f{i}", ConfigTests.Mutate(o =>
        {
            o["strategyId"] = $"S{i:00}";
            o["entry"]!["buyCall"] = JsonNode.Parse("{ \"op\": \"AND\", \"items\": [ " + LeafJson(Strict, "10", ">", (i % 7 - 3).ToString()) + ", " + LeafJson("fut.strict:pct", "5", ">", "0") + " ] }");
            o["entry"]!["buyPut"] = JsonNode.Parse("{ \"op\": \"OR\", \"items\": [ " + LeafJson(Strict, "10", "<", (-(i % 5)).ToString()) + " ] }");
        }))).ToList();

    [Fact]
    public void ThirtyStrategies_AreIndependent_DeterministicUnderPermutation_AndNeverMutateTheSharedFrame()
    {
        var set = StrategySet.Load(Thirty());
        Assert.Equal(30, set.Active.Count);
        var frame = Frame((Strict, SummaryWindowId.W10, 1.5), ("fut.strict:pct", SummaryWindowId.W5, 2), ("opt.atm.coverage_full", SummaryWindowId.W10, 1));
        var previous = Frame((Strict, SummaryWindowId.W10, -0.5), ("fut.strict:pct", SummaryWindowId.W5, 1));
        var ctx = new EvaluationContext(frame, previous);

        var forward = set.Active.Select(s => (s.Identity.StrategyId, s.EvaluateEntry(ctx), s.EvaluateExit(ctx))).ToList();
        var reversed = set.Active.Reverse().Select(s => (s.Identity.StrategyId, s.EvaluateEntry(ctx), s.EvaluateExit(ctx))).OrderBy(x => x.StrategyId, StringComparer.Ordinal).ToList();
        Assert.Equal(forward, reversed);                                                                   // evaluation order cannot change any strategy's outcome
        // a fresh compile of the same JSON gives the same results (no hidden state, no clock)
        var again = StrategySet.Load(Thirty()).Active.Select(s => (s.Identity.StrategyId, s.EvaluateEntry(ctx), s.EvaluateExit(ctx))).ToList();
        Assert.Equal(forward, again);
        // repeated evaluation of one strategy on one frame never drifts (stateless)
        var first = set.Active[3].EvaluateEntry(ctx);
        for (var i = 0; i < 100; i++) Assert.Equal(first, set.Active[3].EvaluateEntry(ctx));
        // different strategies really differ (the test is not vacuous) and the frame was only read
        Assert.True(forward.Select(x => x.Item2).Distinct().Count() > 1);
        Assert.True(frame.Reads > 0);
        Assert.Equal(["TryRead"], typeof(IMarketStateFrame).GetMethods().Select(m => m.Name));                       // the shared frame has no write surface at all
    }

    [Fact]
    public void CompiledStrategy_HoldsNoLiveOrBrokerSurface_AndTheCoreAssemblyIsPure()
    {
        var asm = typeof(CompiledStrategy).Assembly;
        Assert.All(asm.GetReferencedAssemblies(), r => Assert.True(
            r.Name!.StartsWith("System", StringComparison.Ordinal) || r.Name is "mscorlib" or "netstandard" or "Microsoft.CSharp", $"unexpected reference {r.Name}"));
        Assert.DoesNotContain(asm.GetTypes().Select(t => t.FullName!), n =>
            new[] { "Broker", "Router", "Submit", "Live", "Authoriz", "Npgsql", "Http" }.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(asm.GetTypes().SelectMany(t => t.GetMembers()).Select(m => m.Name), n =>
            new[] { "Broker", "Router", "Submit", "PlaceOrder", "LiveEnabled" }.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }
}
