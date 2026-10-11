using NiftySignal.Tests.Trading.Core;
using NiftySignal.Tests.Trading.Data;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Data;
using NiftySignal.Trading.Recovery;
using NiftySignal.Trading.Recovery.Codec;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>
/// The live paper run against a durable store: inputs journaled with their step, replay on resume, durable cursors, crash and restart at every commit boundary, idempotent
/// re-delivery. Written once against <see cref="IStoreContractFixture"/>, so the in-memory store and the PostgreSQL store must both pass exactly these tests.
/// </summary>
public abstract class LiveEngineRunContractTests
{
    protected abstract IStoreContractFixture CreateFixture();

    private static readonly VariantKey X1 = new("X", "X1");
    private static readonly VariantKey X2 = new("X", "X2");
    private static readonly VariantKey X3 = new("X", "X3");

    /// <summary>
    /// Three concurrent variants of one family on the same instrument pair: X1 long CE with TP1 (half) and a stop, X2 short (PE) with a 20-minute max hold, X3 long CE with a
    /// wider stop. All enter on bar 1; X1 takes profit on the first move, X1 and X3 stop out on the next; X2 is closed by its max-hold clock.
    /// </summary>
    internal sealed record Scenario(TradingConfig Config, IReadOnlyList<IStrategyFamily> Families, LiveScript Script, int AfterTp1, int AfterStops, int AfterMaxHold);

    internal static Scenario XScenario(decimal capital = 10_000_000m)
    {
        var config = PaperKit.Config(
            capital,
            PaperKit.Spec("X", "X1", new ExitSpec { TradeSlPct = 0.10m, Tp1Pct = 0.10m, Tp1Proportion = 0.5m }),
            PaperKit.Spec("X", "X2", new ExitSpec { MaxHold = TimeSpan.FromMinutes(20) }),
            PaperKit.Spec("X", "X3", new ExitSpec { TradeSlPct = 0.15m }));
        var family = PaperKit.Family("X", ("X1", PaperKit.Script(new[] { (1L, 1L) })), ("X2", LiveHarness.Short((1L, 1L))), ("X3", PaperKit.Script(new[] { (1L, 1L) })));
        var script = new LiveScript();
        var t = script.Bar(10, 0, advance: 1.1);                       // bar 1: X1, X2, X3 enter
        script.Move(t.AddSeconds(2.2), ceBid: 140m);                   // X1 takes its TP1 half
        var afterTp1 = script.Count;
        script.Move(t.AddSeconds(5.2), ceBid: 100m);                   // X1 (rest) and X3 stop out
        script.Bar(10, 5);                                             // bar 2: nothing to do
        var afterStops = script.Count;
        script.Move(Clock.Ist(10, 20, 30), peBid: 124m);               // a fresh PE bid for the max-hold exit of X2
        script.Advance(Clock.Ist(10, 22));                             // X2's 20-minute clock fires
        script.Bar(10, 30);
        var afterMaxHold = script.Count;
        return new Scenario(config, new[] { family }, script, afterTp1, afterStops, afterMaxHold);
    }

    private static string Describe(LiveEngineRun run)
        => string.Join("\n", run.Engine.Outputs.Where(o => o is not (OpportunityTransition or AuditRecord)).Select(o => $"{o.At.ToOffset(TimeSpan.FromHours(5.5)):HH:mm:ss.fff} {o.GetType().Name} {o.Variant} {(o is FillRecord f ? f.Side + " " + f.Quantity + "@" + f.Price + " " + f.Reason : o is PositionClosed c ? "net=" + c.NetPnl + " " + c.FinalExitReason : o is PositionOpened po ? "qty=" + po.Quantity + " stop=" + po.StopLevel + " hold=" + po.MaxHoldDeadline : string.Empty)}"));

    private const string RunId = "live-1";

    private static LiveEngineRun Open(IStoreContractFixture f, Scenario s, string owner = "owner-1", ICrashPoints? crash = null, string runId = RunId, IReadOnlyList<IStrategyFamily>? families = null)
        => LiveEngineRun.Open(new BlockingRunStoreAdapter(f.Store), owner, LiveHarness.Spec(s.Config, families ?? s.Families, runId), crash);

    private static RunSnapshot Durable(IStoreContractFixture f, string runId = RunId) => f.Store.LoadAsync(runId).GetAwaiter().GetResult();

    private static IReadOnlyList<string> Reference(Scenario s, out string finalDigest, out int commits)
    {
        using var f = new InMemoryContractFixture();
        using var run = Open(f, s);
        LiveHarness.Deliver(run, s.Script.Steps);
        run.Finish();
        finalDigest = run.StateDigest;
        commits = Durable(f).Steps.Count;
        return LiveHarness.Payloads(run);
    }

    private static void AssertRiskLedgerMatchesEngine(LiveEngineRun run)
    {
        var ledger = RiskLedger.Fold(run.Engine.Outputs.Select((o, i) => { var p = OutputCodec.Encode(o); return new OutputRecord(i, 0, o.GetType().Name, o.StrategyId, o.VariantId, o.At, p, OutputCodec.ContentHash(p)); }));
        var x = run.Engine.Execution;
        Assert.Equal(x.GlobalTrades(), ledger.GlobalTrades());
        Assert.Equal(x.FamilyTradesOf("X"), ledger.FamilyTradesOf("X"));
        Assert.Equal(x.OpenExposure(), ledger.OpenExposure());
        Assert.Equal(x.GlobalRealizedNet(), ledger.GlobalRealizedNet());
        Assert.Equal(x.FamilyRealizedNet("X"), ledger.FamilyRealizedNet("X"));
        Assert.Equal(x.FamilyConsecutiveLosses("X"), ledger.FamilyConsecutiveLosses("X"));
        foreach (var v in new[] { X1, X2, X3 })
        {
            Assert.Equal(x.TradesOf(v), ledger.TradesOf(v));
            Assert.Equal(x.RealizedNetOf(v), ledger.RealizedNetOf(v));
            Assert.Equal(x.ConsecutiveLossesOf(v), ledger.ConsecutiveLossesOf(v));
        }
    }

    [ContractFact]
    public void ALiveRun_ProducesTheSameOutputsAsTheDirectEngine()
    {
        using var f = CreateFixture();
        var s = XScenario();
        using var run = LiveEngineRun.Open(new BlockingRunStoreAdapter(f.Store), "owner-1", LiveHarness.Spec(s.Config, s.Families, RunId, session: "session:2026-09-22"));      // the session label the bare engine uses by default
        LiveHarness.Deliver(run, s.Script.Steps);
        run.Finish();

        var kit = new PaperKit(s.Config, LiveHarness.Universe(), XScenario().Families.ToArray());
        var t = kit.Bar(10, 0, advance: 1.1);
        kit.Move(t.AddSeconds(2.2), ceBid: 140m);
        kit.Move(t.AddSeconds(5.2), ceBid: 100m);
        kit.Bar(10, 5);
        kit.Move(Clock.Ist(10, 20, 30), peBid: 124m);
        kit.AdvanceTo(Clock.Ist(10, 22));
        kit.Bar(10, 30);
        kit.Kit.Loop.Finish(kit.Kit.Engine.Variants.Schedule.SessionEndUtc);

        var want = kit.Kit.Engine.Outputs.Select(OutputCodec.Encode).ToList();
        var got = LiveHarness.Payloads(run);
        var firstDifference = Enumerable.Range(0, Math.Min(want.Count, got.Count)).FirstOrDefault(i => want[i] != got[i], -1);
        Assert.True(firstDifference < 0 && want.Count == got.Count, firstDifference < 0 ? $"counts {want.Count} vs {got.Count}" : $"output {firstDifference}: {want[firstDifference]} <> {got[firstDifference]}");
    }

    [ContractFact]
    public void EveryInput_IsJournaledWithItsStep_AndTheDurableCursorsFollow()
    {
        using var f = CreateFixture();
        var s = XScenario();
        using var run = Open(f, s);
        Assert.Null(run.DurableBarCursor);

        LiveHarness.Deliver(run, s.Script.Steps);
        run.Finish();

        var d = Durable(f);
        Assert.Equal(Enumerable.Range(0, d.Inputs.Count).Select(i => (long)i), d.Inputs.Select(i => i.Seq));
        Assert.All(d.Inputs, i => Assert.True(i.HashIsValid));
        Assert.All(d.Inputs, i => Assert.InRange(i.StepSeq, 0, d.Steps.Count - 1));
        Assert.Equal(3, d.Inputs.Count(i => i.Kind == "BarCompleted"));
        Assert.Equal(1, d.Inputs.Count(i => i.Kind == "Finish"));
        Assert.Equal(3L, run.DurableBarCursor);
        Assert.Equal(new DateTimeOffset(d.Inputs.Where(i => InputCodec.IsEvent(i)).Max(i => i.At.UtcTicks), TimeSpan.Zero), run.DurableGroupCursor);
        Assert.True(d.Completed);
        LedgerCheck.Verify(d);
        Assert.All(d.Inputs.Where(InputCodec.IsEvent), i => Assert.Equal(i.ContentHash, InputCodec.ToRecord(i.Seq, i.StepSeq, i.GroupOrdinal, InputCodec.ToEvent(i)).ContentHash));
    }

    [ContractFact]
    public void ARestart_RebuildsTheSameState_CreatesNoNewTrade_AndReDeliveryIsIdempotent()
    {
        using var f = CreateFixture();
        var s = XScenario();
        string digestBefore;
        IReadOnlyList<string> outputsBefore;
        using (var first = Open(f, s))
        {
            LiveHarness.Deliver(first, s.Script.Steps.Take(s.AfterStops));
            digestBefore = first.StateDigest;
            outputsBefore = LiveHarness.Payloads(first);
        }

        var economicBefore = RunSnapshotDigest.Economic(Durable(f));
        using var run = Open(f, s, "owner-2");

        Assert.Equal(RunPhase.Live, run.Phase);
        Assert.Equal(digestBefore, run.StateDigest);
        Assert.Equal(outputsBefore, LiveHarness.Payloads(run));
        Assert.True(run.StepsReplayed > 0);
        Assert.Equal(economicBefore, RunSnapshotDigest.Economic(Durable(f)));            // opening and replaying wrote nothing economic
        Assert.Equal(2L, run.DurableBarCursor);

        LiveHarness.Deliver(run, s.Script.Steps.Take(s.AfterStops));                    // the source delivers everything again from the start
        Assert.Equal(economicBefore, RunSnapshotDigest.Economic(Durable(f)));
        Assert.Equal(outputsBefore, LiveHarness.Payloads(run));
        Assert.Single(run.Engine.Outputs.OfType<EntryIntent>(), i => i.Variant == X1);

        LiveHarness.Deliver(run, s.Script.Steps);
        run.Finish();
        Assert.Equal(Reference(s, out var finalDigest, out _), LiveHarness.Payloads(run));
        Assert.Equal(finalDigest, run.StateDigest);
    }

    [ContractFact]
    public void OpenPositions_AreRestoredExactly_AtEveryLifecyclePoint_WithRiskCountersCapitalExcursionAndMaxHold()
    {
        var s = XScenario();
        foreach (var point in new[] { s.AfterTp1, s.AfterStops, s.AfterMaxHold })
        {
            using var f = CreateFixture();
            string digest;
            List<object> facts;
            using (var first = Open(f, s))
            {
                LiveHarness.Deliver(first, s.Script.Steps.Take(point));
                digest = first.StateDigest;
                facts = Facts(first);
            }

            using var run = Open(f, s, "owner-2");

            Assert.Equal(digest, run.StateDigest);
            Assert.Equal(facts, Facts(run));
            AssertRiskLedgerMatchesEngine(run);
            if (point == s.AfterTp1)
            {
                var x1 = run.Engine.Execution.PositionsOf(X1).Single();
                Assert.Equal(325, x1.OpenQuantity);                                       // half sold at TP1
                Assert.Equal(NiftySignal.Trading.Core.Execution.Tp1State.Done, x1.Tp1);
                Assert.NotNull(run.Engine.Execution.PositionsOf(X2).Single().MaxHoldDeadline);
                Assert.Equal(125m * (325 + 650 + 650), run.Engine.Execution.OpenExposure());
            }

            LiveHarness.Deliver(run, s.Script.Steps);
            run.Finish();
            Assert.Equal(Reference(s, out _, out _), LiveHarness.Payloads(run));
            Assert.Equal(3, run.Engine.Outputs.OfType<EntryIntent>().Count());           // no duplicate entry, whatever was delivered again
            Assert.Equal(3, run.Engine.Outputs.OfType<PositionOpened>().Count());
            Assert.Equal(ExitReason.MaxHold, run.Engine.Outputs.OfType<PositionClosed>().Single(c => c.Variant == X2).FinalExitReason);
            AssertRiskLedgerMatchesEngine(run);
        }
    }

    private static List<object> Facts(LiveEngineRun run)
        => new[] { X1, X2, X3 }.SelectMany(v => run.Engine.Execution.PositionsOf(v)).OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => (object)(p.Id, p.State, p.OpenQuantity, p.ReservedExitQuantity, p.Tp1, p.Tp1FilledQuantity, p.MaxHoldDeadline, p.AvgEntryPrice, p.NetRealised, PositionExcursion.From(p)))
            .Append(run.Engine.Execution.OpenExposure()).Append(run.Engine.Execution.GlobalTrades()).Append(run.Engine.Execution.FamilyConsecutiveLosses("X")).ToList();

    [ContractFact]
    public void ACrashAtEveryCommitBoundary_ConvergesToTheUninterruptedRun()
    {
        var s = XScenario();
        var reference = Reference(s, out var finalDigest, out var commits);
        Assert.True(commits >= 8);
        using var f = CreateFixture();
        var n = 0;
        foreach (var point in new[] { CrashPoint.BeforeCommit, CrashPoint.AfterCommit })
        {
            for (var k = 1; k <= commits; k++)
            {
                var runId = $"crash-{n++}";
                var crash = new LiveHarness.CrashAt(point, k);
                LiveEngineRun? dead = null;
                try
                {
                    dead = Open(f, s, "doomed", crash, runId);
                    LiveHarness.Deliver(dead, s.Script.Steps);
                    dead.Finish();
                }
                catch (SimulatedProcessDeath)
                {
                }

                Assert.True(crash.Fired, $"{point} {k} was never reached");
                f.SimulateProcessDeath("doomed");                                           // abrupt: nothing was released or flushed
                using var survivor = Open(f, s, "survivor", null, runId);
                if (!survivor.Finished)
                {
                    LiveHarness.Deliver(survivor, s.Script.Steps);
                    survivor.Finish();
                }

                Assert.Equal(reference, LiveHarness.Payloads(survivor));
                Assert.Equal(finalDigest, survivor.StateDigest);
                var durable = Durable(f, runId);
                Assert.Equal(reference, durable.Outputs.Select(o => o.Payload));
                LedgerCheck.Verify(durable);
                Assert.True(durable.Completed);
            }
        }
    }

    [ContractFact]
    public void TheMandatoryLiquidation_StillWorksAfterARestart()
    {
        var config = PaperKit.Config(10_000_000m, PaperKit.Spec("L", "L1"));
        var family = PaperKit.Family("L", ("L1", PaperKit.Script(new[] { (1L, 1L) })));
        var script = new LiveScript();
        script.Bar(14, 50);
        script.Move(Clock.Ist(15, 0), ceBid: 126m, peBid: 124m);
        script.Move(Clock.Ist(15, 10, 5), ceBid: 126m, peBid: 124m, advance: 3);
        script.Advance(Clock.Ist(15, 12));
        var s = new Scenario(config, new[] { family }, script, 0, 0, 0);
        using var f = CreateFixture();
        using (var first = Open(f, s))
        {
            LiveHarness.Deliver(first, script.Steps.Take(4));                              // up to 15:00 with the position open
            Assert.Single(first.Engine.Outputs.OfType<PositionOpened>());
            Assert.Empty(first.Engine.Outputs.OfType<PositionClosed>());
        }

        using var run = Open(f, s, "owner-2");
        LiveHarness.Deliver(run, script.Steps);
        run.Finish();

        var closed = Assert.Single(run.Engine.Outputs.OfType<PositionClosed>());
        Assert.Equal(ExitReason.SessionLiquidation, closed.FinalExitReason);
        Assert.DoesNotContain(run.Engine.Execution.AllPositions, p => p.State == PositionState.Unresolved);
    }

    [ContractFact]
    public void ADifferentConfiguration_IsRefusedByTheCompatibilityCheck_AndADivergingStrategy_HaltsTheReplay()
    {
        using var f = CreateFixture();
        var s = XScenario();
        using (var first = Open(f, s))
        {
            LiveHarness.Deliver(first, s.Script.Steps.Take(s.AfterStops));
        }

        var other = s with { Config = XScenario(5_000_000m).Config };
        var halt = Assert.Throws<RecoveryHaltException>(() => Open(f, other, "owner-2"));
        Assert.Equal(HaltKind.Compatibility, halt.Kind);

        // same identity (family id and version), different behaviour: the replay must notice
        var diverging = PaperKit.Family("X", ("X1", PaperKit.Script(new[] { (2L, 2L) })), ("X2", LiveHarness.Short((1L, 1L))), ("X3", PaperKit.Script(new[] { (1L, 1L) })));
        var replayHalt = Assert.Throws<RecoveryHaltException>(() => Open(f, s, "owner-3", families: new[] { diverging }));
        Assert.Equal(HaltKind.ReplayDivergence, replayHalt.Kind);

        using var healthy = Open(f, s, "owner-4");                                           // the halts released ownership and changed nothing
        Assert.Equal(2L, healthy.DurableBarCursor);
    }

    [ContractFact]
    public void AFailedCommit_FaultsTheInstance_AndAFreshOneRecoversFromDurableData()
    {
        using var f = CreateFixture();
        var s = XScenario();
        var run = Open(f, s);
        f.Inject(StoreOperation.Commit, 2, StoreFaultMode.FailDuringApply);

        Assert.ThrowsAny<Exception>(() => LiveHarness.Deliver(run, s.Script.Steps));

        Assert.True(run.Faulted);
        Assert.Throws<InstanceFaultedException>(() => run.Offer(new[] { (EngineEvent)new BarCompletedEvent(Clock.Ist(11, 0), 99, "FUT") }));
        run.Dispose();
        using var fresh = Open(f, s, "owner-2");
        LiveHarness.Deliver(fresh, s.Script.Steps);
        fresh.Finish();
        Assert.Equal(Reference(s, out _, out _), LiveHarness.Payloads(fresh));
    }

    [ContractFact]
    public void AFinishedRun_ReopensAsFinished_AndRefusesInput()
    {
        using var f = CreateFixture();
        var s = XScenario();
        using (var run = Open(f, s))
        {
            LiveHarness.Deliver(run, s.Script.Steps);
            run.Finish();
        }

        using var again = Open(f, s, "owner-2");

        Assert.Equal(RunPhase.Finished, again.Phase);
        Assert.Throws<InvalidOperationException>(() => again.Offer(new[] { (EngineEvent)new BarCompletedEvent(Clock.Ist(15, 40), 50, "FUT") }));
        Assert.Equal(Reference(s, out _, out _), LiveHarness.Payloads(again));
    }
}

public sealed class InMemoryLiveEngineRunTests : LiveEngineRunContractTests
{
    protected override IStoreContractFixture CreateFixture() => new InMemoryContractFixture();
}

[Collection("PostgresTradingTests")]
public sealed class PostgresLiveEngineRunTests : LiveEngineRunContractTests
{
    protected override IStoreContractFixture CreateFixture() => new PostgresContractFixture();
}
