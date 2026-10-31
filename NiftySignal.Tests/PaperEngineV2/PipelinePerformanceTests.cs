using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Frames;
using Xunit.Abstractions;
using static NiftySignal.Tests.PaperEngineV2.FrameFixtures;

namespace NiftySignal.Tests.PaperEngineV2;

/// <summary>Structural isolation guards for the 6b assemblies plus INDEPENDENT measurements of frame construction, queue handoff and consumer latency (in memory; no database, no network).</summary>
public class PipelineIsolationTests
{
    [Fact]
    public void FramesAssembly_IsPure_ReferencingOnlyCoreAndMarketSummary()
    {
        var asm = typeof(MarketStateFrameBuilder).Assembly;
        Assert.All(asm.GetReferencedAssemblies(), r => Assert.True(
            r.Name!.StartsWith("System", StringComparison.Ordinal) || r.Name is "mscorlib" or "netstandard" or "NiftySignal.PaperEngineV2.Core" or "NiftySignal.MarketSummary", $"unexpected reference {r.Name}"));
        Assert.DoesNotContain(asm.GetTypes().Select(t => t.FullName!), n => new[] { "Broker", "Router", "Submit", "Live", "Authoriz", "Npgsql", "EntityFramework", "Http" }.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ObserverAndTradingAssemblies_DoNotReferenceV2_SoNpt1CannotWaitOnOrFailBecauseOfIt()
    {
        foreach (var name in new[] { "NiftySignal.Host", "NiftySignal.Trading.Core", "NiftySignal.Trading.Live", "NiftySignal.Trading.Signals", "NiftySignal.Trading.Adapters", "NiftySignal.Trading.Recovery", "NiftySignal.Trading.Data", "NiftySignal.Npt1M1", "NiftySignal.AdaptiveObserver", "NiftySignal.AdaptiveObserverData" })
            Assert.DoesNotContain(Assembly.Load(name).GetReferencedAssemblies(), r => r.Name!.StartsWith("NiftySignal.PaperEngineV2", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHandoffThreadPath_OfferAndBuild_ContainsNoDatabaseJournalOrStrategyType()
    {
        // the observer-side surface is FrameHandoff only: it must not reference the journal, the consumer, the processor or any compiled strategy
        var handoff = typeof(FrameHandoff);
        var used = handoff.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Select(f => f.FieldType.Name)
            .Concat(handoff.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType.Name)));
        Assert.DoesNotContain(used, n => n.Contains("Journal", StringComparison.Ordinal) || n.Contains("Consumer", StringComparison.Ordinal) || n.Contains("Processor", StringComparison.Ordinal) || n.Contains("CompiledStrategy", StringComparison.Ordinal));
    }
}

public class PipelinePerformanceTests(ITestOutputHelper output)
{
    static string Pct(List<double> us)
    {
        us.Sort();
        double P(double q) => us[Math.Min(us.Count - 1, (int)Math.Ceiling(q * us.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"n={us.Count} p50={P(0.50):0.0} us p95={P(0.95):0.0} us p99={P(0.99):0.0} us max={us[^1]:0.0} us");
    }

    void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "paper-v2-pipeline-perf.txt"), line + Environment.NewLine);
    }

    static double Us(long t0) => (Stopwatch.GetTimestamp() - t0) * 1_000_000d / Stopwatch.Frequency;

    [Fact]
    public void FrameConstruction_FromAFullMarketSummary_Independently()
    {
        var s = Summary(130);
        for (var i = 0; i < 50; i++) new MarketStateFrameBuilder().Build(Bar(1, 130), s.Basket, s.Futures, s.Grids, s.Vix, s.Quotes);      // warm-up
        var times = new List<double>(); var serialize = new List<double>(); var hash = new List<double>(); var canonical = new List<double>();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++)
        {
            var b = new MarketStateFrameBuilder();
            var t = Stopwatch.GetTimestamp();
            var f = b.Build(Bar(1, 130), s.Basket, s.Futures, s.Grids, s.Vix, s.Quotes);
            times.Add(Us(t));
            if (i == 0) Assert.Equal(1_420, f.PresentMetricCount);
            var t2 = Stopwatch.GetTimestamp(); _ = f.Hash; canonical.Add(Us(t2));            // first use of the canonical payload + hash: the consumer-side cost
        }

        var allocPerFrame = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / 300;
        var frame = new MarketStateFrameBuilder().Build(Bar(1, 130), s.Basket, s.Futures, s.Grids, s.Vix, s.Quotes);
        for (var i = 0; i < 300; i++) { var t = Stopwatch.GetTimestamp(); _ = FrameSerializer.Read(frame.PayloadJson); serialize.Add(Us(t)); }
        for (var i = 0; i < 300; i++) { var t = Stopwatch.GetTimestamp(); _ = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(frame.PayloadJson)); hash.Add(Us(t)); }
        Report($"frame construction on the OBSERVER thread (map 1,420 metrics + validate + copy; canonical payload and hash are lazy): {Pct(times)}; ~{allocPerFrame:N0} bytes allocated per frame incl. the lazy payload");
        Report($"  first use of the canonical payload + SHA-256 (consumer thread; payload {frame.PayloadJson.Length:N0} chars): {Pct(canonical)}");
        Report("  of which SHA-256 of the payload alone: " + Pct(hash));
        Report("  deserialize + revalidate (replay path): " + Pct(serialize));
        Assert.True(times.OrderBy(x => x).ElementAt(284) < 20_000, "frame construction p95 guard (the observer-thread cost must stay far below the 250 ms poll)");
    }

    [Fact]
    public async Task QueueHandoff_OfferToRead_Independently()
    {
        var frames = Enumerable.Range(1, 2_000).Select(i => Light(1, i)).ToList();
        var h = new FrameHandoff(new HandoffOptions(4_096, TimeSpan.FromHours(1), 1));
        for (var i = 0; i < 500; i++) { h.Offer(frames[i]); h.Reader.TryRead(out _); }                // warm-up
        var offer = new List<double>(); var read = new List<double>();
        var alloc0 = GC.GetAllocatedBytesForCurrentThread();
        foreach (var f in frames)
        {
            var t = Stopwatch.GetTimestamp(); h.Offer(f); offer.Add(Us(t));
            var t2 = Stopwatch.GetTimestamp(); h.Reader.TryRead(out _); read.Add(Us(t2));
        }

        var alloc = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / frames.Count;
        Report($"queue handoff Offer() on the observer thread (accepted): {Pct(offer)}; ~{alloc:N0} bytes allocated per offer+read (timer lists excluded)");
        Report("queue handoff consumer-side TryRead: " + Pct(read));

        var full = new FrameHandoff(new HandoffOptions(1, TimeSpan.FromHours(1), 1));
        full.Offer(frames[0]);
        var rejected = new List<double>();
        for (var i = 1; i < 2_000; i++) { var t = Stopwatch.GetTimestamp(); full.Offer(frames[i]); rejected.Add(Us(t)); }
        Report("queue handoff Offer() on a FULL queue (rejected, ledger + signal): " + Pct(rejected));

        // end-to-end latency through a running consumer task: offer -> processed
        var gate = new ManualClock(T0.AddHours(2));
        var proc = new StrategyDigestProcessor(StrategyDigestProcessor.Thirty());
        var stamps = new Dictionary<int, long>(); var done = new List<double>();
        proc.OnProcess = f => { lock (done) done.Add(Us(stamps[f.Identity.BarSeq])); };
        var pipe = new FrameHandoff(new HandoffOptions(1_024, TimeSpan.FromHours(10), 1));
        var consumer = new FrameConsumer(pipe, new HandoffOptions(1_024, TimeSpan.FromHours(10), 1), new InMemoryFrameJournal(), proc, gate);
        var run = Task.Run(() => consumer.RunAsync(CancellationToken.None));
        for (var i = 1; i <= 500; i++)
        {
            lock (stamps) stamps[i] = Stopwatch.GetTimestamp();
            pipe.Offer(frames[i - 1]);
            await Task.Delay(1);                                                                      // paced like a stream, so this measures wake-up + processing, not queueing
        }

        pipe.Complete(); await run;
        Report("end-to-end offer -> consumer processed (wake-up + guard + journal + disposition + 30 strategies), paced: " + Pct(done.Skip(20).ToList()));
        Assert.True(offer.OrderBy(x => x).ElementAt(1_899) < 5_000, "offer p95 guard");
    }

    [Fact]
    public async Task ConsumerLatency_GuardJournalDisposition_AndThirtyStrategies_Independently()
    {
        var s = Summary(130);
        var frames = new List<MarketStateFrame>();
        var b = new MarketStateFrameBuilder();
        for (var i = 0; i < 400; i++) frames.Add(b.Build(Bar(1, 130 + i, End(130 + i)), s.Basket, s.Futures.Select(r => r with { ToBar = 130 + i }).ToList(), s.Grids.Select(g => g with { Rows = g.Rows.Select(r => r with { ToBar = 130 + i }).ToList() }).ToList(), s.Vix, s.Quotes.Select(q => q with { AvailableAt = End(130 + i).AddMilliseconds(-300) }).ToList()));
        var clock = new ManualClock(T0.AddHours(10));
        var options = new HandoffOptions(16, TimeSpan.FromHours(24), 1);

        foreach (var strategies in new[] { 0, 1, 10, 30 })
        {
            var all = StrategyDigestProcessor.Thirty().Take(strategies).ToList();
            var proc = new StrategyDigestProcessor(all);
            var consumer = new FrameConsumer(null, options, new InMemoryFrameJournal(), proc, clock);
            for (var i = 0; i < 30; i++) await consumer.HandleAsync(frames[i], default);            // warm-up
            var times = new List<double>(); var alloc0 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 30; i < frames.Count; i++)
            {
                var t = Stopwatch.GetTimestamp();
                await consumer.HandleAsync(frames[i], default);
                times.Add(Us(t));
            }

            var perFrame = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / times.Count;
            Report($"consumer HandleAsync (guard + in-memory journal + disposition + {strategies,2} strategies, {frames[0].PresentMetricCount:N0}-metric frames): {Pct(times)}; ~{perFrame:N0} bytes allocated per frame (digest log + journal copies included)");
            Assert.Equal(frames.Count, consumer.Status.Processed);
        }
    }
}
