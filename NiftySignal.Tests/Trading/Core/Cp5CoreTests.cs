using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

public class ReadinessGateTests
{
    private static BarCompletedEvent BarWith(DateTimeOffset end, long seq, bool ready)
        => new(end, seq, "FUT", new BarInfo(end.AddSeconds(-30), end, 100, 10, ready, ready ? 10 : 3, 10, true, ready, "test-readiness"));

    [Fact]
    public void ABarWithoutReadyEvidence_NeverCreatesAnOpportunity_EvenWhenTheStrategyIsValid()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()));
        kit.Feed(kit.Q(TradeKit.T0, 124m, 125m), BarWith(TradeKit.T0, 1, ready: false));

        Assert.Empty(kit.Engine.Variants.AllOpportunities);
        Assert.Empty(kit.Out<EntryIntent>("A"));
    }

    [Fact]
    public void WarmUpEndsWithTheFirstReadyBar_AndTheNextNotReadyBarInvalidatesTheOpportunity()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()));
        kit.Feed(kit.Q(kit.At(0), 124m, 125m), BarWith(kit.At(0), 1, ready: false));
        Assert.Empty(kit.Engine.Variants.AllOpportunities);                             // warm-up: quotes and candles accumulate, nothing trades

        kit.Feed(kit.Q(kit.At(10), 124m, 125m), BarWith(kit.At(10), 2, ready: true));
        var opportunity = Assert.Single(kit.Engine.Variants.AllOpportunities);
        Assert.NotEqual(OpportunityState.AwaitingContract, opportunity.State);
        Assert.Single(kit.Out<EntryIntent>("A"));

        // the existing candle history kept accumulating through the warm-up
        Assert.Equal(2, kit.Engine.Variants.Candles.LatestBarSeq);
    }

    [Fact]
    public void ABarWithoutInfo_IsTreatedAsNoGate()
    {
        var kit = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()));
        kit.Bar(TradeKit.T0, 1, kit.Q(TradeKit.T0, 124m, 125m));

        Assert.Single(kit.Engine.Variants.AllOpportunities);
    }

    [Fact]
    public void BarInfo_MustMatchTheEventTime_AndHasValueEquality()
    {
        var at = Clock.Ist(10, 0);
        var info = new BarInfo(at.AddSeconds(-5), at, 100, 4, true, 10, 10, true, true, "spec");

        Assert.Throws<ArgumentException>(() => new BarCompletedEvent(at.AddSeconds(1), 1, "FUT", info));
        Assert.Equal(new BarCompletedEvent(at, 1, "FUT", info), new BarCompletedEvent(at, 1, "FUT", info with { }));
        Assert.NotEqual(new BarCompletedEvent(at, 1, "FUT", info), new BarCompletedEvent(at, 1, "FUT", info with { Ready = false }));
    }
}

public class LiquidationFeasibilityTests
{
    private static ArgumentException Build(TimeOnly liquidation, TimeSpan latency)
        => Assert.Throws<ArgumentException>(() => new TradeKit(TradeKit.MakeConfig(
            execution: new ExecutionConfig { Latency = latency },
            session: new SessionConfig { LiquidationStart = liquidation },
            variants: TradeKit.Immediate())));

    [Fact]
    public void ALiquidationStartWhoseFirstAttemptCannotMatureBeforeFinalClose_IsRejected()
    {
        var error = Build(new TimeOnly(15, 9, 59, 500), TimeSpan.FromSeconds(1));
        Assert.Contains("no close attempt could execute before the final close", error.Message, StringComparison.Ordinal);

        Assert.Contains("final close", Build(new TimeOnly(15, 5, 0), TimeSpan.FromMinutes(5)).Message, StringComparison.Ordinal);
        Assert.Contains("final close", Build(new TimeOnly(15, 9, 59), TimeSpan.FromSeconds(1)).Message, StringComparison.Ordinal);   // exactly at the boundary: the attempt would execute AT 15:10
    }

    [Fact]
    public void ALiquidationStartWithRoomForTheLatency_IsAccepted()
    {
        _ = new TradeKit(TradeKit.MakeConfig(
            execution: new ExecutionConfig { Latency = TimeSpan.FromSeconds(1) },
            session: new SessionConfig { LiquidationStart = new TimeOnly(15, 9, 58) },
            variants: TradeKit.Immediate()));
        _ = new TradeKit(TradeKit.MakeConfig(variants: TradeKit.Immediate()));          // the 15:05 test harness setting
    }
}

public class RetentionTests
{
    [Fact]
    public void LoopSeenMemory_IsBounded_AndDuplicatesInsideTheRetentionAreStillRecognised()
    {
        var handler = new RecordingHandler();
        var loop = new EngineLoop(handler, Clock.Ist(9, 0), TimeSpan.FromMinutes(1));
        var t0 = Clock.Ist(10, 0);
        for (var i = 0; i < 1000; i++)
        {
            loop.ProcessGroup(new EngineEvent[] { Ev.Opt(t0.AddSeconds(i), i + 1) });
        }

        Assert.InRange(loop.SeenCount, 55, 62);                                           // ~60 s of events, not 1000
        loop.ProcessGroup(new EngineEvent[] { Ev.Opt(t0.AddSeconds(999), 1000) });         // identical recent event: ignored as a duplicate
        Assert.Equal(1, loop.Stats.DuplicatesIgnored);

        Assert.Throws<LateEventException>(() => loop.ProcessGroup(new EngineEvent[] { Ev.Opt(t0, 1) }));   // beyond the retention: no longer recognised, rejected as late
    }

    [Fact]
    public void LoopSeenMemory_IsUnboundedByDefault()
    {
        var loop = new EngineLoop(new RecordingHandler(), Clock.Ist(9, 0));
        var t0 = Clock.Ist(10, 0);
        for (var i = 0; i < 300; i++)
        {
            loop.ProcessGroup(new EngineEvent[] { Ev.Opt(t0.AddSeconds(i), i + 1) });
        }

        Assert.Equal(300, loop.SeenCount);
        loop.ProcessGroup(new EngineEvent[] { Ev.Opt(t0, 1) });                            // an old exact duplicate is still ignored
        Assert.Equal(1, loop.Stats.DuplicatesIgnored);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EngineLoop(new RecordingHandler(), t0, TimeSpan.Zero));
    }

    [Fact]
    public void BoundedAndUnboundedRetention_ProduceIdenticalTradingOutputs()
    {
        string Run(RetentionPolicy retention)
        {
            var config = TradeKit.MakeConfig(variants: new[]
            {
                TradeKit.Immediate("A", exit: new NiftySignal.Trading.Core.Exits.ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.08m, Tp1Proportion = 0.5m, FinalTpPct = 0.2m }),
                TradeKit.Immediate("B"),
            });
            var engine = new TradingEngine(config, Clock.Day, Kit.StandardUniverse().ToList(),
                new Dictionary<string, IBaseStrategy> { ["S"] = ScriptedStrategy.Valid("S", 1, null) }, retention: retention);
            var loop = new EngineLoop(engine, engine.Variants.Schedule.DayStartUtc, retention.LoopSeenRetention);
            engine.Start(loop);
            var rng = new Random(11);
            long id = 0;
            var price = 125m;
            for (var s = 0; s < 1500; s++)
            {
                var at = Clock.Ist(10, 0).AddSeconds(s * 0.9);
                price = Math.Max(90m, price + (rng.Next(-3, 4) * 0.5m));
                var group = new List<EngineEvent> { new QuoteEvent(at, EventKind.OptionQuote, ++id, "CE23900", price - 1m, price, null, 100000, 100000) };
                if (s % 7 == 0)
                {
                    group.Add(Ev.Bar(at, s / 7 + 1));
                }

                loop.ProcessGroup(group);
            }

            loop.Finish(engine.Variants.Schedule.SessionEndUtc);
            return TradeKit.Signature(engine.Outputs) + "|" + engine.DailySummaries(engine.Variants.Schedule.SessionEndUtc).Count;
        }

        var bounded = Run(new RetentionPolicy { LoopSeenRetention = TimeSpan.FromSeconds(30), QuoteHistoryRetention = TimeSpan.FromSeconds(5) });
        var unbounded = Run(RetentionPolicy.Unbounded);

        Assert.Contains("FillRecord", bounded, StringComparison.Ordinal);                  // the scenario actually trades
        Assert.Equal(unbounded, bounded);
    }
}
