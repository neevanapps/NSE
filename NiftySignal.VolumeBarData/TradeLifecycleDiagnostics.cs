using NiftySignal.Domain.Enums;

namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, diagnostic-only follow-up to the frozen trade simulation (see
/// docs/VolumeCandle_0DTE_Findings.md's "Trade-Lifecycle Diagnostics" section). Pure,
/// deterministic helpers over an ALREADY-PRODUCED <see cref="PatternRelationshipTradeSimulator.SimulationResult"/> --
/// no new signal/entry/exit logic, no change to the frozen simulator's own trading behaviour.
/// The holding-time buckets below are exactly the 8 buckets specified by the task itself (not
/// invented), reused everywhere a "quick vs long" split is needed instead of a new threshold.
/// </summary>
public static class TradeLifecycleDiagnostics
{
    /// <summary>The 8 holding-time buckets specified by the task, in display order.</summary>
    public static readonly string[] HoldingTimeBucketOrder =
        ["<1 min", "1-2 min", "2-5 min", "5-10 min", "10-15 min", "15-30 min", "30-60 min", ">=60 min"];

    public static string BucketHoldingTime(TimeSpan holding)
    {
        var minutes = holding.TotalMinutes;
        return minutes switch
        {
            < 1 => "<1 min",
            < 2 => "1-2 min",
            < 5 => "2-5 min",
            < 10 => "5-10 min",
            < 15 => "10-15 min",
            < 30 => "15-30 min",
            < 60 => "30-60 min",
            _ => ">=60 min",
        };
    }

    /// <summary>
    /// Number of A/B pattern alternations among <paramref name="patterns"/> STRICTLY BEFORE
    /// <paramref name="index"/> -- e.g. sequence [A,B,A] has 0 alternations before index 0, 1
    /// before index 1 (A-&gt;B), 2 before index 2 (A-&gt;B-&gt;A).
    /// </summary>
    public static int CountAlternationsBefore(IReadOnlyList<string> patterns, int index)
    {
        var count = 0;
        for (var i = 1; i < index && i < patterns.Count; i++)
        {
            if (patterns[i] != patterns[i - 1]) { count++; }
        }
        return count;
    }

    /// <summary>The episode (from an unmodified <see cref="EpisodeAnalysis.DetectEpisodes"/> call) whose [StartEventId, EndEventId] contains <paramref name="eventId"/>, or null if none (e.g. a different pattern's own row).</summary>
    public static EpisodeAnalysis.Episode? FindContainingEpisode(IReadOnlyList<EpisodeAnalysis.Episode> episodes, int eventId)
        => episodes.FirstOrDefault(e => eventId >= e.StartEventId && eventId <= e.EndEventId);

    /// <summary>Counts every occurrence of the exact pattern-label n-gram (e.g. ["PatternA","PatternB","PatternA"]) as a contiguous subsequence of <paramref name="patterns"/>.</summary>
    public static int CountNGram(IReadOnlyList<string> patterns, IReadOnlyList<string> nGram)
    {
        if (nGram.Count == 0 || patterns.Count < nGram.Count) { return 0; }
        var count = 0;
        for (var i = 0; i <= patterns.Count - nGram.Count; i++)
        {
            var match = true;
            for (var j = 0; j < nGram.Count; j++)
            {
                if (patterns[i + j] != nGram[j]) { match = false; break; }
            }
            if (match) { count++; }
        }
        return count;
    }

    public readonly record struct HoldingTimeBucketStats(string Bucket, int Count, int Wins, decimal AvgPnl, decimal MedianPnl, decimal TotalPnl, decimal AvgMae, decimal AvgMfe);

    public static List<HoldingTimeBucketStats> BucketTrades(IReadOnlyList<PatternRelationshipTradeSimulator.TradeRow> trades)
    {
        var result = new List<HoldingTimeBucketStats>();
        foreach (var bucket in HoldingTimeBucketOrder)
        {
            var members = trades.Where(t => BucketHoldingTime(t.HoldingDuration) == bucket).ToList();
            if (members.Count == 0) { continue; }
            var sortedPnl = members.Select(t => t.NetPnl).OrderBy(v => v).ToList();
            result.Add(new HoldingTimeBucketStats(
                bucket, members.Count, members.Count(t => t.NetPnl > 0),
                members.Average(t => t.NetPnl), sortedPnl[sortedPnl.Count / 2], members.Sum(t => t.NetPnl),
                members.Average(t => t.MaeRupees), members.Average(t => t.MfeRupees)));
        }
        return result;
    }
}
