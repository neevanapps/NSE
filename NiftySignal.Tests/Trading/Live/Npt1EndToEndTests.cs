using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>
/// The three NPT1 strategies end to end through the real <see cref="PaperLiveCoordinator"/> and a durable run over an in-memory store: frames in, simultaneous independent paper positions out,
/// restart equivalence (digest equality with an uninterrupted run) at every interesting point, the gap rule, the 15:00 protocol and its idempotency.
/// </summary>
internal static class Npt1Harness
{
    private const string Ce = "CE24000";
    private const string Pe = "PE24000";
    private static readonly DateOnly Day = Clock.Day;

    public static PaperLiveOptions Options(params string[] enabled)
        => new()
        {
            Enabled = true,
            Capital = 10_000_000m,
            BrokeragePerOrder = 0m,
            OptionTickSize = 0.05m,
            Strategies = enabled.ToDictionary(e => e, _ => new StrategyToggleOptions { Enabled = true }),
        };

    public static readonly string[] All = { "NPT1-MD5", "NPT1-MDX3", "NPT1-PE5" };

    public static IReadOnlyList<OptionContract> Universe() => new[]
    {
        Npt1Kit.C(OptionSide.Call, 24000), Npt1Kit.C(OptionSide.Put, 24000), Npt1Kit.C(OptionSide.Call, 24050), Npt1Kit.C(OptionSide.Put, 23950),
    };

    public static LiveSessionInfo Session() => new("npt1-m1-v1:test", Day, Universe(), "FUT", "test-readiness/1");

    public static PaperLiveCoordinator NewCoordinator(PaperLiveOptions options, InMemoryRunStore store, string owner = "host-1")
    {
        var strategies = PaperLiveSetup.Npt1Strategies(options);
        return new PaperLiveCoordinator(options, strategies, () => store, NullLogger.Instance, owner, configFactory: _ => PaperLiveSetup.Npt1Config(options, strategies));
    }

    /// <summary>The scripted metrics of bar k (1-based): all three strategies get a CE event at bar 22 (MD5 target long, MDX3 up-cross with F &gt; 0, PE5 up-cross of x = -P).</summary>
    public static (double? M, double? W, long? P) Metrics(int k) => (k < 22 ? 0d : 1d, 1d, k < 22 ? 0L : -100L);

    public sealed class Feeder(PaperLiveCoordinator c, DateTimeOffset start)
    {
        public DateTimeOffset At(int k) => start.AddMinutes(k);

        public void Bar(int k, bool quotes = true, bool bars = true, decimal ceBid = 119m, decimal ceAsk = 120m, decimal peBid = 119m, decimal peAsk = 120m, Func<int, (double? M, double? W, long? P)>? metrics = null)
        {
            var at = At(k);
            var (m, w, p) = (metrics ?? Metrics)(k);
            var list = quotes
                ? new[]
                {
                    new LiveQuote(Ce, 2L * k - 1, false, ceBid, ceAsk, ceBid, 100000, 100000),
                    new LiveQuote(Pe, 2L * k, false, peBid, peAsk, peBid, 100000, 100000),
                }
                : Array.Empty<LiveQuote>();
            var info = new BarInfo(at.AddSeconds(-45), at, 1000, 50, true, 5, 3, true, true, "test-readiness/1");
            var frame = new AdaptiveStrategyFrame("npt1-m1-v1:test", Day, k, at.AddSeconds(-45), at, true, true, 5, "npt1-m1-v1", "test-readiness/1", "micro", m, w, p, 24000d, Day, null);
            c.OnGroup(at, list, bars ? new[] { new LiveBar(k, at, info, true, frame) } : Array.Empty<LiveBar>());
            c.OnHeartbeat(at.AddSeconds(3));
        }
    }

}

public class Npt1EndToEndTests
{
    private const string Ce = "CE24000";
    private const string Pe = "PE24000";
    private static readonly DateOnly Day = Clock.Day;

    private static IEnumerable<T> Outs<T>(PaperLiveCoordinator c, string strategy) where T : EngineOutput
        => c.Run!.Engine.Outputs.OfType<T>().Where(o => o.Variant.StrategyId == strategy);

    [Fact]
    public void ThreeStrategies_HoldThreeIndependentPositions_Simultaneously_Of650Each()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        var store = new InMemoryRunStore();
        using var c = Npt1Harness.NewCoordinator(options, store);
        c.StartSession(Npt1Harness.Session());
        var feeder = new Npt1Harness.Feeder(c, Clock.Ist(10, 0));

        for (var k = 1; k <= 23; k++)
        {
            feeder.Bar(k);
        }

        Assert.True(c.Status.State == PaperLiveState.Live, c.Status.Detail);
        foreach (var id in Npt1Harness.All)
        {
            var opened = Assert.Single(Outs<PositionOpened>(c, id));
            Assert.Equal(650, opened.Quantity);
            Assert.Equal(Ce, opened.Token);
            Assert.Equal(22, opened.EntryBarSeq); Assert.Equal(feeder.At(22), opened.EntryDecisionAt);                      // the signal bar; the fill is on the next completed bar
            var fill = Assert.Single(Outs<FillRecord>(c, id));
            Assert.Equal(feeder.At(23), fill.At);
            Assert.Equal(120m, fill.Price);
            Assert.Empty(Outs<PositionClosed>(c, id));
        }

        Assert.Equal(3, c.Run!.Engine.Outputs.OfType<PositionOpened>().Count());
    }

    [Fact]
    public void NoStrategyEntersBeforeItsWindowsAreFull()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        using var c = Npt1Harness.NewCoordinator(options, new InMemoryRunStore());
        c.StartSession(Npt1Harness.Session());
        var feeder = new Npt1Harness.Feeder(c, Clock.Ist(10, 0));
        for (var k = 1; k <= 21; k++)
        {
            feeder.Bar(k, metrics: _ => (5d, 1d, -1000L));                        // a strongly one-sided but constant input: no crossover, MD5 needs 14 bars
        }

        Assert.DoesNotContain(c.Run!.Engine.Outputs.OfType<FillRecord>(), f => f.Variant.StrategyId != "NPT1-MD5");
        Assert.All(c.Run.Engine.Outputs.OfType<FillRecord>(), f => Assert.Equal("NPT1-MD5", f.Variant.StrategyId));
    }

    public static IEnumerable<object[]> RestartPoints()
    {
        yield return new object[] { 15, "A: flat, windows warm" };
        yield return new object[] { 22, "C: signal bar done, entry pending for the next bar" };
        yield return new object[] { 23, "B/E: three positions open" };
        yield return new object[] { 30, "B/E: positions open, later bars" };
    }

    [Theory]
    [MemberData(nameof(RestartPoints))]
    public void ARestart_AtAnyPoint_ResumesToTheSameStateAsAnUninterruptedRun(int restartAfterBar, string scenario)
    {
        _ = scenario;
        var options = Npt1Harness.Options(Npt1Harness.All);
        var script = (Func<Npt1Harness.Feeder, int, int, int>)((feeder, from, to) =>
        {
            for (var k = from; k <= to; k++)
            {
                feeder.Bar(k);
            }

            return to;
        });

        string uninterrupted;
        {
            using var c = Npt1Harness.NewCoordinator(options, new InMemoryRunStore());
            c.StartSession(Npt1Harness.Session());
            script(new Npt1Harness.Feeder(c, Clock.Ist(10, 0)), 1, 34);
            uninterrupted = c.Run!.StateDigest;
        }

        var store = new InMemoryRunStore();
        using (var first = Npt1Harness.NewCoordinator(options, store))
        {
            first.StartSession(Npt1Harness.Session());
            script(new Npt1Harness.Feeder(first, Clock.Ist(10, 0)), 1, restartAfterBar);
        }

        using var second = Npt1Harness.NewCoordinator(options, store, "host-2");
        second.StartSession(Npt1Harness.Session());
        var f2 = new Npt1Harness.Feeder(second, Clock.Ist(10, 0));
        // the source hands over the bar it already delivered (overlap) and then continues
        script(f2, restartAfterBar, 34);

        Assert.Equal(PaperLiveState.Live, second.Status.State);
        Assert.Equal(uninterrupted, second.Run!.StateDigest);
        Assert.Equal(3, second.Run.Engine.Outputs.OfType<PositionOpened>().Count());
    }

    /// <summary>Flow that reverses: all three strategies go long at bar 22 and later reverse, so positions exit on opposite signals (and re-enter).</summary>
    private static (double? M, double? W, long? P) Reversing(int k) => (k < 22 ? 0d : k < 30 ? 1d : -3d, 1d, k < 22 ? 0L : k < 30 ? -100L : k < 34 ? 400L : -300L);

    [Fact]
    public void ARestart_WhileASignalExitIsPending_ResumesToTheSameStateAsAnUninterruptedRun()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        const int bars = 70;
        List<int> exitRequestBars;
        string uninterrupted;
        {
            using var c = Npt1Harness.NewCoordinator(options, new InMemoryRunStore());
            c.StartSession(Npt1Harness.Session());
            var f = new Npt1Harness.Feeder(c, Clock.Ist(10, 0));
            for (var k = 1; k <= bars; k++)
            {
                f.Bar(k, metrics: Reversing);
            }

            uninterrupted = c.Run!.StateDigest;
            exitRequestBars = c.Run.Engine.Outputs.OfType<AuditRecord>().Where(a => a.Stage == "SignalExit" && a.Outcome == "Requested")
                .Select(a => (int)Math.Round((a.At - Clock.Ist(10, 0)).TotalMinutes)).Distinct().Order().ToList();
            Assert.NotEmpty(exitRequestBars);                                      // the scenario really produces pending exits
            Assert.Contains(c.Run.Engine.Outputs.OfType<PositionClosed>(), x => x.FinalExitReason == ExitReason.StrategyExit);
        }

        foreach (var requested in exitRequestBars.Take(4))
        {
            var store = new InMemoryRunStore();
            using (var first = Npt1Harness.NewCoordinator(options, store))
            {
                first.StartSession(Npt1Harness.Session());
                var f1 = new Npt1Harness.Feeder(first, Clock.Ist(10, 0));
                for (var k = 1; k <= requested; k++)
                {
                    f1.Bar(k, metrics: Reversing);
                }
            }

            using var second = Npt1Harness.NewCoordinator(options, store, "host-2");
            second.StartSession(Npt1Harness.Session());
            var f2 = new Npt1Harness.Feeder(second, Clock.Ist(10, 0));
            for (var k = requested; k <= bars; k++)
            {
                f2.Bar(k, metrics: Reversing);
            }

            Assert.True(second.Status.State == PaperLiveState.Live, second.Status.Detail);
            Assert.Equal(uninterrupted, second.Run!.StateDigest);
        }
    }

    [Fact]
    public void ADowntimeGap_FabricatesNoTrade_ResetsTheStrategyWindows_AndNeedsARewarm()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        var store = new InMemoryRunStore();
        using (var first = Npt1Harness.NewCoordinator(options, store))
        {
            first.StartSession(Npt1Harness.Session());
            var f = new Npt1Harness.Feeder(first, Clock.Ist(10, 0));
            for (var k = 1; k <= 21; k++)
            {
                f.Bar(k, metrics: _ => (0d, 1d, 0L));                                  // windows full of flat readings, no position
            }
        }

        using var second = Npt1Harness.NewCoordinator(options, store, "host-2");
        second.StartSession(Npt1Harness.Session());
        var f2 = new Npt1Harness.Feeder(second, Clock.Ist(10, 0));
        // bars 22..39 completed while the host was down and are NOT delivered; the metrics would have produced signals at bar 22
        for (var k = 40; k <= 70; k++)
        {
            f2.Bar(k, metrics: j => (j < 40 ? 0d : 1d, 1d, j < 40 ? 0L : -100L));
        }

        Assert.Equal(PaperLiveState.Live, second.Status.State);
        Assert.DoesNotContain(second.Run!.Engine.Outputs.OfType<FillRecord>(), x => x.At < f2.At(40));       // nothing fabricated for the downtime
        Assert.Contains(second.Run.Engine.Outputs.OfType<AuditRecord>(), a => a.Outcome.Contains("gapReset", StringComparison.Ordinal) || a.Detail.Contains("gapReset", StringComparison.Ordinal));
        // after the gap the strategies re-warm from bar 40 on: no entry can happen on the first bars after the gap
        Assert.DoesNotContain(second.Run.Engine.Outputs.OfType<FillRecord>(), x => x.At <= f2.At(52));
    }

    [Fact]
    public void At1500_EveryOpenPositionIsClosedAtTheObservedBid_OnceOnly_AndARestartAfterwardsDoesNotCloseAgain()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        var store = new InMemoryRunStore();
        // bars 22 min apart end up around 14:30..; the entry fills at bar 23 (14:53) and stays open until 15:00
        var start = Clock.Ist(14, 30);
        using (var c = Npt1Harness.NewCoordinator(options, store))
        {
            c.StartSession(Npt1Harness.Session());
            var f = new Npt1Harness.Feeder(c, start);
            for (var k = 1; k <= 23; k++)
            {
                f.Bar(k, ceBid: 128m, ceAsk: 129m);
            }

            Assert.Equal(3, c.Run!.Engine.Outputs.OfType<PositionOpened>().Count());
            Assert.Empty(c.Run.Engine.Outputs.OfType<PositionClosed>());
            c.OnHeartbeat(Clock.Ist(15, 0, 1));

            var closed = c.Run.Engine.Outputs.OfType<PositionClosed>().ToList();
            Assert.Equal(3, closed.Count);
            Assert.All(closed, x =>
            {
                Assert.Equal(ExitReason.TerminalClose, x.FinalExitReason);
                Assert.Equal(128m, x.AvgExitPrice);                               // the observed bid, not a retrospective price
                Assert.Equal(Clock.Ist(15, 0), x.At);
            });
        }

        using var again = Npt1Harness.NewCoordinator(options, store, "host-2");
        again.StartSession(Npt1Harness.Session());
        again.OnHeartbeat(Clock.Ist(15, 0, 5));
        again.OnHeartbeat(Clock.Ist(15, 1));
        Assert.Equal(3, again.Run!.Engine.Outputs.OfType<PositionClosed>().Count());
        Assert.Equal(3, again.Run.Engine.Outputs.OfType<FillRecord>().Count(x => x.Side == NiftySignal.Trading.Core.Costs.TradeSide.Sell));
    }

    [Fact]
    public void AFailedCommitMidSession_RebuildsFreshStatefulStrategies_AndEndsInTheSameStateAsAnUninterruptedRun()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        string uninterrupted;
        {
            using var c = Npt1Harness.NewCoordinator(options, new InMemoryRunStore());
            c.StartSession(Npt1Harness.Session());
            var f = new Npt1Harness.Feeder(c, Clock.Ist(10, 0));
            for (var k = 1; k <= 30; k++)
            {
                f.Bar(k);
            }

            uninterrupted = c.Run!.StateDigest;
        }

        var store = new InMemoryRunStore();
        using var faulty = Npt1Harness.NewCoordinator(options, store);
        faulty.StartSession(Npt1Harness.Session());
        var feeder = new Npt1Harness.Feeder(faulty, Clock.Ist(10, 0));
        for (var k = 1; k <= 30; k++)
        {
            if (k is 18 or 24)
            {
                // the commit of this bar fails after it was applied in memory: the run is rebuilt from the journal, and the families it uses must be NEW instances (their windows are replayed too)
                store.Inject(new StoreFault(StoreOperation.Commit, store.CallsOf(StoreOperation.Commit) + 1, StoreFaultMode.FailDuringApply));
            }

            feeder.Bar(k);
        }

        Assert.True(faulty.Status.State == PaperLiveState.Live, faulty.Status.Detail);
        Assert.True(faulty.Status.Recoveries >= 3);
        Assert.Equal(uninterrupted, faulty.Run!.StateDigest);
        Assert.Equal(3, faulty.Run.Engine.Outputs.OfType<PositionOpened>().Count());
    }

    [Fact]
    public void PerStrategyPnl_IsSeparate_AndTheFrozenFeeScheduleIsApplied()
    {
        var options = Npt1Harness.Options(Npt1Harness.All);
        using var c = Npt1Harness.NewCoordinator(options, new InMemoryRunStore());
        c.StartSession(Npt1Harness.Session());
        var start = Clock.Ist(14, 30);
        var f = new Npt1Harness.Feeder(c, start);
        for (var k = 1; k <= 23; k++)
        {
            f.Bar(k, ceBid: 119m, ceAsk: 120m);
        }

        c.OnHeartbeat(Clock.Ist(15, 0, 1));
        foreach (var id in Npt1Harness.All)
        {
            var closed = Assert.Single(Outs<PositionClosed>(c, id));
            Assert.Equal(650 * (119m - 120m), closed.GrossPnl);
            // buy: exchange 0.03503%, SEBI 0.0001%, stamp 0.003%, GST 18% of (exchange + SEBI); sell: STT 0.15%, exchange, SEBI, GST
            var buyNotional = 650 * 120m;
            var sellNotional = 650 * 119m;
            decimal R(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
            var buy = R(buyNotional * 0.0003503m) + R(buyNotional * 0.000001m) + R(buyNotional * 0.00003m) + R(0.18m * (R(buyNotional * 0.0003503m) + R(buyNotional * 0.000001m)));
            var sell = R(sellNotional * 0.0015m) + R(sellNotional * 0.0003503m) + R(sellNotional * 0.000001m) + R(0.18m * (R(sellNotional * 0.0003503m) + R(sellNotional * 0.000001m)));
            Assert.Equal(buy + sell, closed.TotalCosts);
            Assert.Equal(closed.GrossPnl - closed.TotalCosts, closed.NetPnl);
        }
    }
}
