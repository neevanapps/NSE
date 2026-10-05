using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;
using NiftySignal.Host;
using System.Text.Json;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveRestartParityTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(900)]
    public void FullReplayAfterArtificialRestart_ReproducesCompletedOutputs(int restartAfter)
    {
        var day = new DateOnly(2026, 9, 25);
        var expiry = new DateOnly(2026, 9, 29);
        var signalStart = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var session = new AdaptiveSessionDefinition(day, "FUT", "NIFTYFUT", new DateOnly(2026,9,29), 65, 100);
        var options = BuildChain(expiry);
        var anchor = BuildAnchor(options, expiry, signalStart, 100d, 0.20d, 0.05d);
        var input = BuildInput(options, expiry, signalStart.AddSeconds(-10), count: 70, rate:0.05d, vol:0.20d);

        var uninterrupted = Run(session, expiry, options, anchor, input, 0.05d, signalStart);

        // Simulate a process that died after consuming a prefix. Its partial in-memory state is
        // deliberately discarded. Recovery creates a fresh engine and replays authoritative input
        // from session start before continuing -- exactly what AdaptiveStateRecoveryService does.
        var prefixEngine = NewEngine(session, expiry, options, anchor, 0.05d, signalStart);
        var persisted = new Dictionary<int, string>();
        var prefix = input.Take(restartAfter).ToArray();
        foreach (var item in prefix)
        {
            foreach (var package in prefixEngine.Process(item.Token, item.Tick))
                persisted.Add(package.FutureBar.BarSeq, JsonSerializer.Serialize(package));
        }

        var resumedEngine = NewEngine(session, expiry, options, anchor, .05d, signalStart);
        var recovered = new List<AdaptiveCompletedBarPackage>();
        foreach (var item in prefix)
        {
            foreach (var package in resumedEngine.Process(item.Token, item.Tick))
            {
                Assert.Equal(persisted[package.FutureBar.BarSeq], JsonSerializer.Serialize(package));
                recovered.Add(package);
            }
        }
        Assert.Equal(prefixEngine.PartialBar, resumedEngine.PartialBar);
        foreach (var item in input.Skip(prefix.Length))
            recovered.AddRange(resumedEngine.Process(item.Token, item.Tick));

        Assert.Equal(uninterrupted.Count, recovered.Count);
        for (var i = 0; i < uninterrupted.Count; i++)
        {
            Assert.Equal(JsonSerializer.Serialize(uninterrupted[i]), JsonSerializer.Serialize(recovered[i]));
        }
        Assert.Equal(uninterrupted.Count, recovered.Select(x => x.FutureBar.BarSeq).Distinct().Count());
    }

    static List<AdaptiveCompletedBarPackage> Run(
        AdaptiveSessionDefinition session,
        DateOnly expiry,
        IReadOnlyList<ObserverOptionInstrument> options,
        OptionResidualAnchor anchor,
        IReadOnlyList<ObserverTokenTick> input,
        double rate,
        DateTimeOffset signalStart)
    {
        var engine = NewEngine(session, expiry, options, anchor, rate, signalStart);
        var output = new List<AdaptiveCompletedBarPackage>();
        foreach (var item in input)
        {
            output.AddRange(engine.Process(item.Token, item.Tick));
        }
        return output;
    }

    static AdaptiveObserverEngine NewEngine(
        AdaptiveSessionDefinition session,
        DateOnly expiry,
        IReadOnlyList<ObserverOptionInstrument> options,
        OptionResidualAnchor anchor,
        double rate,
        DateTimeOffset signalStart) =>
        new(session, expiry, rate, strongThreshold:null, signalStart, options, anchor);

    static IReadOnlyList<ObserverOptionInstrument> BuildChain(DateOnly expiry)
    {
        var list = new List<ObserverOptionInstrument>();
        foreach (var strike in new[] { 85d,90d,95d,100d,105d,110d,115d })
        {
            list.Add(new ObserverOptionInstrument($"C{strike}", $"C{strike}", OptionType.Call, strike, expiry, 65));
            list.Add(new ObserverOptionInstrument($"P{strike}", $"P{strike}", OptionType.Put, strike, expiry, 65));
        }
        return list;
    }

    static OptionResidualAnchor BuildAnchor(
        IReadOnlyList<ObserverOptionInstrument> chain,
        DateOnly expiry,
        DateTimeOffset anchorTime,
        double underlying,
        double vol,
        double rate)
    {
        var t = TimeToExpiry.YearsUntilExpiry(expiry, anchorTime);
        var calls = chain.Where(x => x.OptionType == OptionType.Call && x.Strike is >=90 and <=110)
            .Select(x => new OptionResidualAnchorComponent(
                x,
                BlackScholes.Calculate(OptionType.Call, underlying, x.Strike, t, rate, vol).Price,
                vol, anchorTime, 0d, x.Strike == 100d)).ToArray();
        var puts = chain.Where(x => x.OptionType == OptionType.Put && x.Strike is >=90 and <=110)
            .Select(x => new OptionResidualAnchorComponent(
                x,
                BlackScholes.Calculate(OptionType.Put, underlying, x.Strike, t, rate, vol).Price,
                vol, anchorTime, 0d, x.Strike == 100d)).ToArray();
        return new OptionResidualAnchor(anchorTime, expiry, underlying, underlying, 100d, calls, puts);
    }

    static IReadOnlyList<ObserverTokenTick> BuildInput(
        IReadOnlyList<ObserverOptionInstrument> chain,
        DateOnly expiry,
        DateTimeOffset start,
        int count,
        double rate,
        double vol)
    {
        var rows = new List<ObserverTokenTick>();
        long id = 1;

        // Option quote baselines precede the first futures tick, making the first bar's ATM±2
        // selection immediately causal and available.
        foreach (var option in chain)
        {
            var t = TimeToExpiry.YearsUntilExpiry(expiry, start);
            var px = BlackScholes.Calculate(option.OptionType, 100d, option.Strike, t, rate, vol).Price;
            rows.Add(new ObserverTokenTick(option.Token,
                new CleanObserverTick(id++, start, start, px, Math.Max(.01, px-.02), px+.02, 100,100,1000,5000)));
        }

        long futureCum = 10_000;
        rows.Add(new ObserverTokenTick("FUT",
            new CleanObserverTick(id++, start.AddMilliseconds(100), start.AddMilliseconds(100), 100d,99.99,100.01,1000,1000,futureCum,100_000)));

        for (var i=1; i<=count; i++)
        {
            var at = start.AddSeconds(i);
            var future = 100d + Math.Sin(i/5d)*0.8d + i*0.01d;

            foreach (var option in chain)
            {
                var t = TimeToExpiry.YearsUntilExpiry(expiry, at);
                var px = BlackScholes.Calculate(option.OptionType, future, option.Strike, t, rate, vol).Price;
                rows.Add(new ObserverTokenTick(option.Token,
                    new CleanObserverTick(id++, at, at, px, Math.Max(.01, px-.02), px+.02,100,100,1000+i,5000+i)));
            }

            futureCum += 30;
            rows.Add(new ObserverTokenTick("FUT",
                new CleanObserverTick(id++, at.AddMilliseconds(100), at.AddMilliseconds(100), future,
                    future-.01, future+.01,1000,1000,futureCum,100_000+i)));
        }

        return rows.OrderBy(x=>x.Tick.AvailableAt).ThenBy(x=>x.Tick.Id).ToArray();
    }
}
