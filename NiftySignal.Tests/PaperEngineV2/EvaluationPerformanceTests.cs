using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Metrics;
using Xunit.Abstractions;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>
/// Measurements of the pure evaluation path (in-memory only: no database, no clock, no frame building): the per-frame time of 1 / 10 / 30 strategies of eight leaf conditions each, and the one-off
/// parse + compile cost of a 30-strategy set. Numbers go to the test output and to <c>paper-v2-perf.txt</c> in the temp directory; the assertions are loose guards around the 5 ms / 10 ms targets.
/// </summary>
public class EvaluationPerformanceTests(ITestOutputHelper output)
{
    sealed class ArrayFrame : IMarketStateFrame
    {
        public readonly double[] Values = new double[MetricRegistry.Count * SummaryWindows.Count];
        public readonly bool[] Present = new bool[MetricRegistry.Count * SummaryWindows.Count];

        public bool TryRead(int metricHandle, SummaryWindowId window, out double value)
        {
            var i = (metricHandle * SummaryWindows.Count) + (int)window;
            value = Values[i];
            return Present[i];
        }
    }

    static string Doc(int i)
    {
        string Leaf(string metric, string window, string cmp, double v) => $"{{ \"metric\": \"{metric}\", \"window\": \"{window}\", \"cmp\": \"{cmp}\", \"value\": {v.ToString(CultureInfo.InvariantCulture)} }}";
        var call = string.Join(",", [
            Leaf("opt.atm.strict.dir:pct", "10", ">", (i % 7) - 3), Leaf("opt.total.enriched.dir:pct", "50", ">", -(i % 4)), Leaf("fut.strict:pct", "5", ">", 0),
            Leaf("opt.itm.ofi.dir:pct", "10", ">", -(i % 5)), Leaf("opt.otm.microdev.dir:raw", "10", ">", -1), Leaf("opt.atm.oi.dir:pct", "100", ">", -5),
            Leaf("vix.change:pct", "Day", "<", 10), Leaf("opt.atm.coverage_pct", "10", ">=", 50),
        ]);
        var put = string.Join(",", [
            Leaf("opt.atm.strict.dir:pct", "10", "<", -(i % 7) + 3), Leaf("opt.total.enriched.dir:pct", "50", "<", i % 4), Leaf("fut.strict:pct", "5", "<", 0),
            Leaf("opt.itm.ofi.dir:pct", "10", "<", i % 5), Leaf("opt.otm.microdev.dir:raw", "10", "<", 1), Leaf("opt.atm.oi.dir:pct", "100", "<", 5),
            Leaf("vix.change:pct", "Day", "<", 10), Leaf("opt.atm.coverage_pct", "10", ">=", 50),
        ]);
        return ConfigTests.Mutate(o =>
        {
            o["strategyId"] = $"S{i:00}";
            o["entry"]!["buyCall"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {call} ] }}");
            o["entry"]!["buyPut"] = JsonNode.Parse($"{{ \"op\": \"AND\", \"items\": [ {put} ] }}");
            o["exit"]!["conditions"] = JsonNode.Parse($"{{ \"op\": \"OR\", \"items\": [ {Leaf("opt.atm.strict.dir:pct", "10", "<", -9)}, {Leaf("fut.strict:pct", "5", "<", -9)}, {{ \"cross\": \"Down\", \"metric\": \"opt.atm.strict.dir:pct\", \"window\": \"10\", \"level\": 0 }} ] }}");
        });
    }

    static string Pct(List<double> us)
    {
        us.Sort();
        double P(double q) => us[Math.Min(us.Count - 1, (int)Math.Ceiling(q * us.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"n={us.Count} p50={P(0.50):0.0} us p95={P(0.95):0.0} us p99={P(0.99):0.0} us max={us[^1]:0.0} us");
    }

    void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "paper-v2-perf.txt"), line + Environment.NewLine);
    }

    [Fact]
    public void PerFrameEvaluation_OneTenAndThirtyStrategies()
    {
        var docs = Enumerable.Range(1, 30).Select(i => ($"f{i}", Doc(i))).ToList();
        var set = StrategySet.Load(docs);
        Assert.Equal(30, set.Active.Count);
        var rng = new Random(5);
        var current = new ArrayFrame(); var previous = new ArrayFrame();
        foreach (var f in new[] { current, previous })
            for (var i = 0; i < f.Values.Length; i++) { f.Values[i] = (rng.NextDouble() - 0.5) * 20; f.Present[i] = rng.NextDouble() > 0.05; }

        double sink = 0;
        foreach (var n in new[] { 1, 10, 30 })
        {
            var strategies = set.Active.Take(n).ToArray();
            for (var w = 0; w < 2_000; w++)                                              // warm-up (JIT, tiered compilation)
                foreach (var s in strategies) { var ctx = new EvaluationContext(current, previous); sink += (int)s.EvaluateEntry(ctx).BuyCall + (int)s.EvaluateExit(ctx); }
            var times = new List<double>(20_000);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var it = 0; it < 20_000; it++)
            {
                var t = Stopwatch.GetTimestamp();
                var ctx = new EvaluationContext(current, previous);
                foreach (var s in strategies) { var e = s.EvaluateEntry(ctx); sink += (int)e.BuyCall + (int)e.BuyPut + (int)s.EvaluateExit(ctx); }
                times.Add((Stopwatch.GetTimestamp() - t) * 1_000_000d / Stopwatch.Frequency);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Report($"evaluate {n,2} strategies x (entry+exit, ~19 leaves each) per frame: {Pct(times)}; allocated {allocated} bytes over {times.Count} frames");
            Assert.True(times.OrderBy(x => x).ElementAt((int)(times.Count * 0.95) - 1) < 5_000, "p95 guard (target 5 ms for 30 simple strategies)");
            Assert.True(allocated < 1_000_000, "the evaluation loop must not allocate per frame (timer list excluded)");
        }

        Assert.True(sink != double.MinValue);
    }

    [Fact]
    public void ParseAndCompile_ThirtyStrategies_OneOffCost()
    {
        var docs = Enumerable.Range(1, 30).Select(i => ($"f{i}", Doc(i))).ToList();
        StrategySet.Load(docs);                                                           // warm-up
        var times = new List<double>();
        for (var i = 0; i < 50; i++)
        {
            var t = Stopwatch.GetTimestamp();
            var set = StrategySet.Load(docs);
            times.Add((Stopwatch.GetTimestamp() - t) * 1_000_000d / Stopwatch.Frequency);
            Assert.Equal(30, set.Active.Count);
        }

        Report("parse + validate + hash + compile of a 30-strategy set (session start, one-off): " + Pct(times));
        Assert.True(times.Max() < 500_000, "session-start load guard");
    }
}
