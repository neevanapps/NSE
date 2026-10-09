using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>Throws <see cref="SimulatedProcessDeath"/> at the Nth time a crash point is reached (and models nothing else: no flush, no cleanup).</summary>
internal sealed class CrashAt(CrashPoint point, int nth, Func<long, string, bool>? when = null) : ICrashPoints
{
    private int _seen;

    public bool Fired { get; private set; }

    public void Reached(CrashPoint reached, long stepSeq, string detail)
    {
        if (reached != point || Fired || (when is not null && !when(stepSeq, detail)))
        {
            return;
        }

        if (++_seen == nth)
        {
            Fired = true;
            throw new SimulatedProcessDeath($"{reached}@{stepSeq}:{detail}");
        }
    }
}

internal sealed class Counting : ICrashPoints
{
    public Dictionary<CrashPoint, int> Counts { get; } = new();

    public void Reached(CrashPoint point, long stepSeq, string detail) => Counts[point] = Counts.GetValueOrDefault(point) + 1;
}

internal static class RecoveryKit
{
    public static readonly DateTimeOffset Horizon = SyntheticSession.Ist(15, 30);

    public static IReadOnlyList<SourceTick> ShortRows(int seed = 7)
        => SyntheticSession.Rows(new SyntheticSession.Options
        {
            Seed = seed,
            Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(9, 44)) },
        });

    public static DurableRunSpec Spec(
        SessionFreeze freeze,
        string runId = "run-1",
        string manifest = "synthetic;seed=7",
        DurableOptions? durable = null,
        Func<TradingConfig, TradingConfig>? tweak = null,
        IReadOnlyDictionary<string, IBaseStrategy>? strategies = null,
        TranslatorOptions? translator = null,
        RecorderOptions? recorder = null)
        => new(
            freeze,
            translator ?? SyntheticSession.Translator(freeze),
            SyntheticSession.Config(tweak),
            strategies ?? SyntheticSession.Strategies(),
            new RunSettings { RunId = runId, RunMode = "Historical", Horizon = Horizon },
            manifest,
            recorder ?? new RecorderOptions { CaptureAll = true },
            null,
            durable ?? new DurableOptions { CheckpointEverySteps = 5 });

    /// <summary>The CP5 reference: the same spec run through the unchanged historical adapter, with no durability at all.</summary>
    public static RunResult Plain(DurableRunSpec spec, IReadOnlyList<SourceTick> rows)
        => HistoricalAdapter.Run(
            new TradingRun(spec.Freeze, spec.Translator, spec.Config, spec.Strategies, spec.Settings, spec.Recorder, Array.Empty<EngineEvent>()),
            rows);

    /// <summary>Feeds every row (from the first) and finishes. Returns the result; the caller disposes the run.</summary>
    public static RunResult FeedAll(DurableRun run, IReadOnlyList<SourceTick> rows)
    {
        foreach (var row in rows)
        {
            run.Offer(row);
        }

        return run.Finish();
    }

    public static RunResult Complete(IRunStore store, DurableRunSpec spec, IReadOnlyList<SourceTick> rows, string owner = "owner-1", ICrashPoints? crash = null)
    {
        using var run = DurableRun.Open(store, owner, spec, crash);
        return FeedAll(run, rows);
    }

    /// <summary>Runs until the crash seam kills the process. Returns the rows fed before the death.</summary>
    public static int RunUntilDeath(InMemoryRunStore store, DurableRunSpec spec, IReadOnlyList<SourceTick> rows, ICrashPoints crash, string owner = "owner-1", Action<DurableRun, int>? afterRow = null)
    {
        var run = DurableRun.Open(store, owner, spec, crash);
        var fed = 0;
        try
        {
            foreach (var row in rows)
            {
                run.Offer(row);
                fed++;
                afterRow?.Invoke(run, fed);
            }

            run.Finish();
        }
        catch (SimulatedProcessDeath)
        {
            store.SimulateProcessDeath(owner);
            return fed;
        }

        throw new InvalidOperationException("The crash point was never reached.");
    }

    /// <summary>Everything an uninterrupted-versus-resumed comparison looks at.</summary>
    public sealed record Outcome(RunResult Result, RunSnapshot Snapshot, string StateText, string StateDigest, InMemoryRunStore Store);

    public static Outcome Uninterrupted(DurableRunSpec spec, IReadOnlyList<SourceTick> rows)
        => ResumeAndFinish(new InMemoryRunStore(), spec, rows, "owner-ref");

    /// <summary>Opens (or recovers) the run in the store, re-feeds every row from the first, finishes and captures the final canonical state.</summary>
    public static Outcome ResumeAndFinish(InMemoryRunStore store, DurableRunSpec spec, IReadOnlyList<SourceTick> rows, string owner = "owner-2", ICrashPoints? crash = null, Action<DurableRun>? declare = null)
    {
        using var run = DurableRun.Open(store, owner, spec, crash);
        foreach (var row in rows)
        {
            run.Offer(row);
        }

        declare?.Invoke(run);
        var result = run.Finish();
        var state = run.Run.CaptureState();
        return new Outcome(result, store.Load(spec.Settings.RunId), state.Text, state.Digest, store);
    }

    public static void AssertSameOutcome(Outcome reference, Outcome resumed)
    {
        Assert.Equal(ChannelHashes(reference.Result), ChannelHashes(resumed.Result));
        Assert.Equal(reference.Snapshot.Outputs.Select(o => o.ContentHash), resumed.Snapshot.Outputs.Select(o => o.ContentHash));
        Assert.Equal(reference.Snapshot.Steps.Select(x => x.ContentHash), resumed.Snapshot.Steps.Select(x => x.ContentHash));
        Assert.Equal(reference.Snapshot.Projections.Select(p => (p.Table, p.Key, p.ContentHash)), resumed.Snapshot.Projections.Select(p => (p.Table, p.Key, p.ContentHash)));
        Assert.Equal(reference.Snapshot.Final!.ContentHash, resumed.Snapshot.Final!.ContentHash);
        Assert.Equal(reference.StateDigest, resumed.StateDigest);
        Assert.Equal(reference.StateText, resumed.StateText);
        var a = ProjectionBuilder.Fold(reference.Snapshot.Outputs);
        var b = ProjectionBuilder.Fold(resumed.Snapshot.Outputs);
        Assert.True(a.SameAs(b));
        Assert.Equal(a.FilledQuantity, b.FilledQuantity);
        Assert.Equal(a.Costs, b.Costs);
        Assert.Equal(reference.Result.Outputs, resumed.Result.Outputs);
        Assert.Equal(reference.Result.Fills, resumed.Result.Fills);
        Assert.Equal(reference.Result.Positions, resumed.Result.Positions);
        Assert.True(resumed.Snapshot.Completed);
    }

    public static IReadOnlyDictionary<string, string> ChannelHashes(RunResult r)
        => r.Channels.ToDictionary(c => c.Key, c => c.Value.Count + ":" + c.Value.Hash, StringComparer.Ordinal);
}
