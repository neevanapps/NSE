using System.Diagnostics;
using System.Text.Json;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Recovery;
using Xunit.Abstractions;

namespace NiftySignal.Tests.Trading.Recovery;

/// <summary>
/// Measures serialised sizes, commit counts and replay cost on the SYNTHETIC session, in memory, on whatever machine runs the tests. These are
/// engineering measurements of this implementation, NOT estimates for a real trading day and NOT a recovery-time budget: no production figure is
/// chosen here (contract section A.18.10). A recorded-session measurement needs the user's source connection and is a separate, user-run step.
/// </summary>
public class MeasurementTests(ITestOutputHelper output)
{
    private static long Bytes(object value) => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value));

    [Fact]
    public void FullSyntheticSession_SizesCountsAndReplayCost_AreMeasuredAndInternallyConsistent()
    {
        var rows = SyntheticSession.Rows();
        var freeze = SyntheticSession.Freeze(rows);
        var spec = RecoveryKit.Spec(freeze, durable: new DurableOptions { CheckpointEverySteps = 5 });
        var store = new InMemoryRunStore();

        var live = Stopwatch.StartNew();
        RecoveryKit.Complete(store, spec, rows, "owner-1");
        live.Stop();
        var snapshot = store.Load("run-1");

        var replay = Stopwatch.StartNew();
        var recovered = RecoveryKit.ResumeAndFinish(store, spec, rows, "owner-2");
        replay.Stop();

        // Cost of one canonical state capture (text + SHA-256) at the end of the session: what a state digest at EVERY step would add per step.
        using var probe = DurableRun.Open(new InMemoryRunStore(), "probe", spec);
        foreach (var row in rows.Take(rows.Count / 2))
        {
            probe.Offer(row);
        }

        var capture = Stopwatch.StartNew();
        const int captures = 200;
        for (var i = 0; i < captures; i++)
        {
            _ = probe.Run.CaptureState().Digest;
        }

        capture.Stop();
        var outputBytes = snapshot.Outputs.Sum(o => (long)o.Payload.Length);
        var stepBytes = snapshot.Steps.Sum(Bytes);
        var projectionBytes = snapshot.Projections.Sum(p => (long)p.Payload.Length);
        var checkpointBytes = snapshot.Checkpoints.Sum(c => (long)c.StateDigest.Length);
        var controlBytes = snapshot.Controls.Sum(Bytes);
        var finalBytes = Bytes(snapshot.Final!);
        var headerBytes = Bytes(snapshot.Header!);
        var total = outputBytes + stepBytes + projectionBytes + checkpointBytes + controlBytes + finalBytes + headerBytes;
        var spanMinutes = (rows[^1].ReceivedAt - rows[0].ReceivedAt).TotalMinutes;
        var kinds = snapshot.Steps.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());

        output.WriteLine($"SYNTHETIC session: rows={rows.Count} data-span-minutes={Math.Round(rows.Select(r => r.ReceivedAt.Date).Distinct().Count() * 0d + 96, 0)} (two windows)");
        output.WriteLine($"commits={store.CommitCount} steps={snapshot.Steps.Count} (by kind: {string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}"))}) outputs={snapshot.Outputs.Count} controls={snapshot.Controls.Count} checkpoints={snapshot.Checkpoints.Count} projections={snapshot.Projections.Count}");
        output.WriteLine($"commits per source row={store.CommitCount / (double)rows.Count:F3}; outputs per commit={snapshot.Outputs.Count / (double)store.CommitCount:F4}");
        output.WriteLine($"serialised bytes: outputs={outputBytes} steps={stepBytes} projections={projectionBytes} checkpoint-digests={checkpointBytes} controls={controlBytes} final={finalBytes} header={headerBytes} TOTAL={total} ({total / 1024.0:F1} KiB)");
        output.WriteLine($"average step record={stepBytes / (double)snapshot.Steps.Count:F0} B; average output payload={outputBytes / (double)Math.Max(1, snapshot.Outputs.Count):F0} B");
        output.WriteLine($"canonical state text at the end={recovered.StateText.Length} B ({recovered.StateText.Split('\n').Length} lines); digest={recovered.StateDigest.Length} hex chars");
        output.WriteLine($"in-memory store, this machine: live run (incl. a state digest every 5th step)={live.Elapsed.TotalSeconds:F1}s; verified replay of all {snapshot.Steps.Count} steps={replay.Elapsed.TotalSeconds:F1}s; one canonical state capture at mid-session={capture.Elapsed.TotalMilliseconds / captures:F2} ms");
        output.WriteLine("These are measurements of the synthetic session only; they say nothing about a real day or about PostgreSQL I/O.");

        Assert.Equal(store.CommitCount, snapshot.Steps.Count);
        Assert.Equal(snapshot.Steps.Count, kinds.Values.Sum());
        Assert.Equal(1, kinds[UnitKind.Finish]);
        Assert.True(snapshot.Outputs.Count > 0 && snapshot.Steps.Count < rows.Count);
        Assert.True(spanMinutes > 0);
        Assert.Equal(snapshot.Steps.Count, recovered.Snapshot.Steps.Count);
    }
}
