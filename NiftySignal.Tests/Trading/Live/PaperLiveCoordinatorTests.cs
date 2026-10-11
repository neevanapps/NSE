using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Tests.Trading.Recovery;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Live;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Live;

public class PaperLiveCoordinatorTests
{
    private static PaperLiveOptions Options(bool enabled = true, bool plumbing = true, decimal capital = 10_000_000m)
        => new()
        {
            Enabled = enabled,
            Capital = capital,
            BrokeragePerOrder = 20m,
            OptionTickSize = 0.05m,
            PlumbingFamily = new PlumbingFamilyOptions
            {
                Enabled = plumbing,
                Windows = new List<PlumbingWindowOptions> { new() { FromBarSeq = 1, ToBarSeq = 1, Direction = "Long" } },
                TradeSlPct = 0.10m,
            },
        };

    private static LiveSessionInfo Session(string id = "session-1") => new(id, Clock.Day, LiveHarness.Universe(), "FUT", "test-readiness/1");

    private static PaperLiveCoordinator Coordinator(PaperLiveOptions options, InMemoryRunStore store, string owner = "host-1", Func<IRunStore>? factory = null)
        => new(options, PaperLiveSetup.Strategies(options), factory ?? (() => store), NullLogger.Instance, owner);

    private static LiveBar Bar(long seq, DateTimeOffset at, bool verified = true)
        => new(seq, at, new BarInfo(at.AddSeconds(-30), at, 1000, 50, true, 5, 3, true, true, "test-readiness/1"), verified);

    private static IReadOnlyList<LiveQuote> Quotes(ref long id, decimal bid = 124m) => new[]
    {
        new LiveQuote("CE23900", ++id, false, bid, bid + 1, bid, 100000, 100000),
        new LiveQuote("PE23900", ++id, false, bid, bid + 1, bid, 100000, 100000),
    };

    [Fact]
    public void ByDefault_NothingIsEnabled_NoStoreIsTouched_AndNothingEverThrows()
    {
        Func<IRunStore> neverCreated = () => throw new InvalidOperationException("no store may be created while disabled");
        foreach (var options in new[] { new PaperLiveOptions(), Options(enabled: false) })
        {
            using var c = new PaperLiveCoordinator(options, PaperLiveSetup.Strategies(options), neverCreated, NullLogger.Instance, "host");
            long id = 0;

            c.StartSession(Session());
            c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });
            c.OnHeartbeat(Clock.Ist(10, 1));
            c.FinishSession();

            Assert.Equal(PaperLiveState.Disabled, c.Status.State);
        }

        var enabledWithoutStrategies = new PaperLiveOptions { Enabled = true, Capital = 1m, BrokeragePerOrder = 0m, OptionTickSize = 0.05m };
        using var idle = new PaperLiveCoordinator(enabledWithoutStrategies, PaperLiveSetup.Strategies(enabledWithoutStrategies), neverCreated, NullLogger.Instance, "host");
        idle.StartSession(Session());
        Assert.Equal(PaperLiveState.NoStrategies, idle.Status.State);                      // enabled, but there is nothing to trade and nothing is journaled
        Assert.Empty(PaperLiveSetup.Strategies(new PaperLiveOptions { Enabled = true, Capital = 1m }));
    }

    [Fact]
    public void AnEnabledInstanceWithAMissingRequiredValue_RefusesToStart_Visibly()
    {
        var options = Options();
        options.BrokeragePerOrder = null;
        options.OptionTickSize = null;
        using var c = new PaperLiveCoordinator(options, PaperLiveSetup.Strategies(options), () => throw new InvalidOperationException("must not open a store"), NullLogger.Instance, "host");

        c.StartSession(Session());

        Assert.Equal(PaperLiveState.Halted, c.Status.State);
        Assert.Contains("BrokeragePerOrder", c.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("OptionTickSize", c.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizedBarsAndQuotes_DriveAPlumbingEntry_ThatIsDurable_AndTheRestartResumesWithoutANewTrade()
    {
        var store = new InMemoryRunStore();
        string digest;
        using (var c = Coordinator(Options(), store))
        {
            long id = 0;
            c.StartSession(Session());
            Assert.Equal(PaperLiveState.Live, c.Status.State);
            c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });
            c.OnHeartbeat(Clock.Ist(10, 0, 3));

            Assert.Single(c.Run!.Engine.Outputs.OfType<PositionOpened>());
            Assert.Equal(1L, c.Status.DurableBarCursor);
            digest = c.Run.StateDigest;
        }

        using var restarted = Coordinator(Options(), store, "host-2");
        restarted.StartSession(Session());
        long again = 0;
        restarted.OnGroup(Clock.Ist(10, 0), Quotes(ref again), new[] { Bar(1, Clock.Ist(10, 0)) });          // the observer's recovery hands the same bar over again

        Assert.Equal(PaperLiveState.Live, restarted.Status.State);
        Assert.Equal(digest, restarted.Run!.StateDigest);
        Assert.Single(restarted.Run.Engine.Outputs.OfType<PositionOpened>());
        Assert.Single(restarted.Run.Engine.Outputs.OfType<EntryIntent>());
    }

    [Fact]
    public void AnUnverifiedBar_IsNeverDelivered_AndEntriesStayBlockedUntilVerifiedBarsFlowAgain()
    {
        var store = new InMemoryRunStore();
        var options = Options();
        options.PlumbingFamily.Windows = new List<PlumbingWindowOptions> { new() { FromBarSeq = 1, ToBarSeq = 3, Direction = "Long" } };
        using var c = Coordinator(options, store);
        long id = 0;
        c.StartSession(Session());

        c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0), verified: false) });

        Assert.Empty(c.Run!.Engine.Outputs.OfType<EntryIntent>());                         // the withheld bar produced no decision
        Assert.Null(c.Run.ProcessedBarCursor);
        Assert.Contains(store.Load(c.Status.RunId!).Inputs, i => i.Kind == "FeedHealth");

        c.OnGroup(Clock.Ist(10, 5), Quotes(ref id), new[] { Bar(2, Clock.Ist(10, 5)) });

        Assert.Equal(2L, c.Run.ProcessedBarCursor);
        Assert.Empty(c.Run.Engine.Outputs.OfType<EntryIntent>());                          // healthy again, but the integrity recovery period still blocks entries
    }

    [Fact]
    public void AGapInTheBarSequence_IsDeclaredToTheEngine_BeforeTheNextBar()
    {
        var store = new InMemoryRunStore();
        using var c = Coordinator(Options(plumbing: true), store);
        long id = 0;
        c.StartSession(Session());
        c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });

        c.OnGroup(Clock.Ist(10, 30), Quotes(ref id), new[] { Bar(9, Clock.Ist(10, 30)) });         // bars 2..8 never arrived

        var health = c.Run!.Engine.Outputs.OfType<NiftySignal.Trading.Core.Integrity.IntegrityTransition>().ToList();
        Assert.Contains(health, h => h.To == NiftySignal.Trading.Core.Integrity.IntegrityState.Degraded);
        Assert.Equal(9L, c.Run.ProcessedBarCursor);
        Assert.Equal(PaperLiveState.Live, c.Status.State);
    }

    [Fact]
    public void AFailedCommit_RebuildsTheRunFromDurableData_AndTheGroupIsOfferedAgain()
    {
        var store = new InMemoryRunStore();
        using var c = Coordinator(Options(), store);
        long id = 0;
        c.StartSession(Session());
        store.Inject(new StoreFault(StoreOperation.Commit, store.CallsOf(StoreOperation.Commit) + 1, StoreFaultMode.FailDuringApply));

        c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });
        c.OnHeartbeat(Clock.Ist(10, 0, 3));

        Assert.Equal(PaperLiveState.Live, c.Status.State);
        Assert.True(c.Status.Recoveries >= 2);
        Assert.Single(c.Run!.Engine.Outputs.OfType<PositionOpened>());
        Assert.Equal(1L, c.Status.DurableBarCursor);
    }

    [Fact]
    public void AChangedConfiguration_HaltsTheRestart_InsteadOfAbandoningTheRun()
    {
        var store = new InMemoryRunStore();
        using (var c = Coordinator(Options(), store))
        {
            long id = 0;
            c.StartSession(Session());
            c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });
        }

        using var other = Coordinator(Options(capital: 5_000_000m), store, "host-2");
        other.StartSession(Session());
        long again = 0;
        other.OnGroup(Clock.Ist(10, 1), Quotes(ref again), new[] { Bar(2, Clock.Ist(10, 1)) });         // ignored: nothing is accepted while halted

        Assert.Equal(PaperLiveState.Halted, other.Status.State);
        Assert.Contains("Compatibility", other.Status.Detail, StringComparison.Ordinal);
        Assert.Null(other.Run);
    }

    [Fact]
    public void ASessionThatEnds_IsFinished_AndFurtherInputIsIgnored()
    {
        var store = new InMemoryRunStore();
        using var c = Coordinator(Options(), store);
        long id = 0;
        c.StartSession(Session());
        c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });

        c.FinishSession();
        c.OnGroup(Clock.Ist(10, 5), Quotes(ref id), new[] { Bar(2, Clock.Ist(10, 5)) });

        Assert.Equal(PaperLiveState.Finished, c.Status.State);
        Assert.True(store.Load(c.Status.RunId!).Completed);
    }

    [Fact]
    public void AStoreThatCannotBeReached_NeverThrowsIntoTheCaller_AndEndsInAVisibleState()
    {
        using var c = new PaperLiveCoordinator(Options(), PaperLiveSetup.Strategies(Options()), () => throw new InvalidOperationException("database down"), NullLogger.Instance, "host");
        long id = 0;

        c.StartSession(Session());
        c.OnGroup(Clock.Ist(10, 0), Quotes(ref id), new[] { Bar(1, Clock.Ist(10, 0)) });
        c.OnHeartbeat(Clock.Ist(10, 1));

        Assert.Contains(c.Status.State, new[] { PaperLiveState.Faulted, PaperLiveState.Halted });
        Assert.Contains("database down", c.Status.Detail, StringComparison.Ordinal);
    }
}
