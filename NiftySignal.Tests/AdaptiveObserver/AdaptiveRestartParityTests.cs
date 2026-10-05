using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;
using NiftySignal.Pricing;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveRestartParityTests
{
    [Fact]
    public void FullReplayAfterArtificialRestart_ReproducesCompletedOutputs()
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
        foreach (var item in input.Take(input.Count / 2))
        {
            prefixEngine.Process(item.Token, item.Tick);
        }

        var recovered = Run(session, expiry, options, anchor, input, 0.05d, signalStart);

        Assert.Equal(uninterrupted.Count, recovered.Count);
        for (var i = 0; i < uninterrupted.Count; i++)
        {
            AssertPackageEqual(uninterrupted[i], recovered[i]);
        }
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

    static void AssertPackageEqual(AdaptiveCompletedBarPackage a, AdaptiveCompletedBarPackage b)
    {
        Assert.Equal(a.FutureBar.BarSeq, b.FutureBar.BarSeq);
        Assert.Equal(a.FutureBar.StartAvailableAtUtc, b.FutureBar.StartAvailableAtUtc);
        Assert.Equal(a.FutureBar.EndAvailableAtUtc, b.FutureBar.EndAvailableAtUtc);
        Assert.Equal(a.FutureBar.Volume, b.FutureBar.Volume);
        Assert.Equal(a.FutureBar.StrictDelta, b.FutureBar.StrictDelta);
        Assert.Equal(a.FutureBar.EnrichedDelta, b.FutureBar.EnrichedDelta);
        Assert.Equal(a.FutureBar.Close, b.FutureBar.Close, 10);

        Assert.Equal(a.FlowState.State, b.FlowState.State);
        Assert.Equal(a.FlowState.StrictDominanceEvolution, b.FlowState.StrictDominanceEvolution);
        if (a.FlowState.Rolling is null || b.FlowState.Rolling is null)
        {
            Assert.Equal(a.FlowState.Rolling is null, b.FlowState.Rolling is null);
        }
        else
        {
            Assert.Equal(a.FlowState.Rolling.StrictDelta, b.FlowState.Rolling.StrictDelta);
            Assert.Equal(a.FlowState.Rolling.StrictDeltaRatioTotal, b.FlowState.Rolling.StrictDeltaRatioTotal, 12);
            Assert.Equal(a.FlowState.Rolling.PriceDisplacement, b.FlowState.Rolling.PriceDisplacement, 12);
        }

        Assert.Equal(a.OptionBand.Selection?.CenterStrike, b.OptionBand.Selection?.CenterStrike);
        Assert.Equal(a.OptionBand.Call?.ContractStrictDelta, b.OptionBand.Call?.ContractStrictDelta);
        Assert.Equal(a.OptionBand.Put?.ContractStrictDelta, b.OptionBand.Put?.ContractStrictDelta);
        Assert.Equal(a.OptionBand.Call?.NotionalStrictDelta, b.OptionBand.Call?.NotionalStrictDelta, 8);
        Assert.Equal(a.OptionBand.Put?.NotionalStrictDelta, b.OptionBand.Put?.NotionalStrictDelta, 8);

        Assert.Equal(a.Residuals.Count, b.Residuals.Count);
        for (var j = 0; j < a.Residuals.Count; j++)
        {
            Assert.Equal(a.Residuals[j].Variant, b.Residuals[j].Variant);
            Assert.Equal(a.Residuals[j].IsAvailable, b.Residuals[j].IsAvailable);
            if (a.Residuals[j].Reading is { } ar && b.Residuals[j].Reading is { } br)
            {
                Assert.Equal(ar.DirectionalResidualPct, br.DirectionalResidualPct, 10);
                Assert.Equal(ar.CEResidual, br.CEResidual, 10);
                Assert.Equal(ar.PEResidual, br.PEResidual, 10);
            }
        }
    }

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
