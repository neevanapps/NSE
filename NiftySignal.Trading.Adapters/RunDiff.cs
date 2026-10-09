namespace NiftySignal.Trading.Adapters;

public sealed record DiffOptions
{
    /// <summary>Checkpoint spacing of the first (digest-only) pass.</summary>
    public long CheckpointEvery { get; init; } = 1_000;

    /// <summary>Capture every line in the first pass (small inputs / tests); no second pass is needed then.</summary>
    public bool FullCapture { get; init; }
}

public sealed record ChannelComparison(string Channel, bool Equal, long CountA, long CountB, string HashA, string HashB);

/// <summary>The first differing line of a channel with the identifiers and timestamps of both sides.</summary>
public sealed record Divergence(string Channel, long LineIndex, string KeyA, string KeyB, string TimeA, string TimeB, string LineA, string LineB, string Note);

public sealed record DiffReport(
    bool Equivalent,
    IReadOnlyList<ChannelComparison> Channels,
    IReadOnlyList<Divergence> Divergences,
    Divergence? First,
    IReadOnlyList<string> Exclusions,
    RunResult A,
    RunResult B);

/// <summary>
/// Compares two runs of the same recorded stream channel by channel (contract section A.16.6). Pass 1 records digests with checkpoints only, so a full
/// session needs no per-line memory; if a channel differs, pass 2 re-runs both sides capturing only the checkpoint bucket that contains the first
/// difference, and reports the first differing line with its identifiers and timestamps. Both runs must be re-runnable (deterministic factories).
/// </summary>
public static class RunDiff
{
    /// <summary>What is deliberately NOT part of the economic comparison. Printed with every report.</summary>
    public static readonly IReadOnlyList<string> Exclusions = new[]
    {
        "Transport diagnostics: pages, polls, failed polls, heartbeats, batch sizes, group-release timing (TransportDiagnostics).",
        "Exact re-deliveries of already accepted rows (overlapping polls): counted as Redelivered, never an event.",
        "Row quarantines caused by delivery order (IdOutOfOrder, LateAvailability): classified as transport; their effect (a missing quote) is still compared through the quotes channel. Row quarantines caused by the data itself are compared (rowQuarantine channel).",
        "Run labels: RunId and RunMode in audit rows and summaries.",
        "Resource figures: loop seen-event memory size, buffered rows.",
    };

    public static DiffReport Compare(Func<RecorderOptions, RunResult> runA, Func<RecorderOptions, RunResult> runB, DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runA);
        ArgumentNullException.ThrowIfNull(runB);
        var o = options ?? new DiffOptions();
        var first = new RecorderOptions { CheckpointEvery = o.CheckpointEvery, CaptureAll = o.FullCapture };
        var a = runA(first);
        var b = runB(first);
        var comparisons = RunRecorders.Names.All
            .Select(n =>
            {
                var da = a.Channels[n];
                var db = b.Channels[n];
                return new ChannelComparison(n, da.Count == db.Count && da.Hash == db.Hash, da.Count, db.Count, da.Hash, db.Hash);
            })
            .ToList();
        var differing = comparisons.Where(c => !c.Equal).Select(c => c.Channel).ToList();
        if (differing.Count == 0)
        {
            return new DiffReport(true, comparisons, Array.Empty<Divergence>(), null, Exclusions, a, b);
        }

        var ra = a;
        var rb = b;
        if (!o.FullCapture)
        {
            var windows = differing.ToDictionary(n => n, n => Bucket(a.Channels[n], b.Channels[n]), StringComparer.Ordinal);
            var second = new RecorderOptions { CheckpointEvery = o.CheckpointEvery, CaptureWindows = windows };
            ra = runA(second);
            rb = runB(second);
        }

        var divergences = new List<Divergence>();
        foreach (var channel in differing)
        {
            divergences.Add(FirstDifference(channel, ra.Captured[channel], rb.Captured[channel], a.Channels[channel], b.Channels[channel]));
        }

        var earliest = divergences
            .OrderBy(d => d.TimeA.Length == 0 ? "~" : d.TimeA, StringComparer.Ordinal)
            .ThenBy(d => d.Channel, StringComparer.Ordinal)
            .First();
        return new DiffReport(false, comparisons, divergences, earliest, Exclusions, a, b);
    }

    private static (long From, long To) Bucket(ChannelDigest a, ChannelDigest b)
    {
        var common = Math.Min(a.Checkpoints.Count, b.Checkpoints.Count);
        long start = 0;
        for (var i = 0; i < common; i++)
        {
            if (a.Checkpoints[i].CumulativeHash != b.Checkpoints[i].CumulativeHash)
            {
                return (start, a.Checkpoints[i].Count - 1);
            }

            start = a.Checkpoints[i].Count;
        }

        return (start, Math.Max(a.Count, b.Count) - 1);
    }

    private static Divergence FirstDifference(string channel, IReadOnlyList<CapturedLine> a, IReadOnlyList<CapturedLine> b, ChannelDigest da, ChannelDigest db)
    {
        var byIndexA = a.ToDictionary(x => x.Index);
        var byIndexB = b.ToDictionary(x => x.Index);
        var indexes = byIndexA.Keys.Union(byIndexB.Keys).Order().ToList();
        foreach (var index in indexes)
        {
            byIndexA.TryGetValue(index, out var la);
            byIndexB.TryGetValue(index, out var lb);
            if (la is not null && lb is not null && la.Line == lb.Line && la.Key == lb.Key)
            {
                continue;
            }

            var note = la is null ? $"stream A ends at {da.Count} lines, B continues"
                : lb is null ? $"stream B ends at {db.Count} lines, A continues"
                : "lines differ";
            return new Divergence(channel, index, la?.Key ?? "-", lb?.Key ?? "-", TimeOf(la?.Line), TimeOf(lb?.Line), la?.Line ?? "-", lb?.Line ?? "-", note);
        }

        return new Divergence(channel, -1, "-", "-", string.Empty, string.Empty, "-", "-", "digests differ but the captured window shows no difference (window did not cover the first difference)");
    }

    private static string TimeOf(string? line)
    {
        if (line is null)
        {
            return string.Empty;
        }

        var bar = line.IndexOf('|', StringComparison.Ordinal);
        var head = bar < 0 ? line : line[..bar];
        return head.Length == 28 && head[^1] == 'Z' && head[10] == 'T' ? head : string.Empty;
    }

    /// <summary>Renders a report as plain text (for the harness and the roadmap).</summary>
    public static string Render(DiffReport report)
    {
        var lines = new List<string> { report.Equivalent ? "EQUIVALENT: every compared channel is identical." : "DIVERGENT" };
        foreach (var c in report.Channels)
        {
            lines.Add($"  {c.Channel,-14} {(c.Equal ? "equal    " : "DIFFERENT")} lines A={c.CountA} B={c.CountB} hashA={c.HashA[..12]} hashB={c.HashB[..12]}");
        }

        foreach (var d in report.Divergences)
        {
            lines.Add($"  first difference in '{d.Channel}' at line {d.LineIndex}: {d.Note}");
            lines.Add($"    A key={d.KeyA} time={d.TimeA}");
            lines.Add($"    A line={d.LineA}");
            lines.Add($"    B key={d.KeyB} time={d.TimeB}");
            lines.Add($"    B line={d.LineB}");
        }

        lines.Add("Excluded from the comparison:");
        lines.AddRange(report.Exclusions.Select(e => "  - " + e));
        return string.Join("\n", lines);
    }
}
