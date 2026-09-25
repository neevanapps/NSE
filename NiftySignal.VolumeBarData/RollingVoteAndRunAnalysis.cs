namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-24, tests the two hypotheses the A/B Temporal Context Diagnostic did NOT actually
/// implement (see docs/VolumeCandle_0DTE_Findings.md's "Pattern A/B Temporal Context Diagnostic"
/// section, closed as weak/mixed on LOCAL time-windowed dominance -- a descriptive Gap measure,
/// not a real voting/run mechanism): (1) a causal, backward-only rolling A/B majority vote at
/// fixed time windows, (2) the "N consecutive same-pattern events" run hypothesis, triggered the
/// instant a run FIRST reaches a given length (never re-triggered by a longer run, never an
/// overlapping window). Pure, additive calculators over an UNMODIFIED, frozen
/// <see cref="RelationshipObservation"/> day-sequence -- no bar/threshold/classification formula
/// is touched. Every calculation here only ever looks at rows strictly BEFORE the event it is
/// computed for (no look-ahead); forward returns for validation are computed separately via the
/// existing frozen <see cref="UnderlyingOptionRelationshipSummary.ComputeFuturesChange"/>.
/// </summary>
public static class RollingVoteAndRunAnalysis
{
    public const string PatternA = ForwardValidationAnalysis.State2_BullishDivergence_PatternA;
    public const string PatternB = ForwardValidationAnalysis.State4_BearishDivergence_PatternB;

    /// <param name="CurrentEventPattern">"A"/"B"/"Other" -- the event's OWN category, recorded for reference only; the vote itself never uses it.</param>
    public sealed record VoteResult(
        DateOnly TradingDate, int EventId, DateTimeOffset Timestamp, int WindowMinutes,
        int ACount, int BCount, int TotalCount, string Winner, int AMinusB, int AbsoluteMargin, double Dominance,
        string CurrentEventPattern);

    /// <summary>
    /// One vote per row in <paramref name="rows"/> (a single day's own frozen sequence), counting
    /// ONLY strictly-prior rows (Timestamp in [current - windowMinutes, current), current event
    /// itself excluded) whose <see cref="RelationshipObservation.RelationshipCategory"/> is
    /// Pattern A or B. O(n) two-pointer sliding window over the chronological row sequence.
    /// </summary>
    public static List<VoteResult> ComputeRollingVotes(IReadOnlyList<RelationshipObservation> rows, int windowMinutes)
    {
        var results = new List<VoteResult>(rows.Count);
        var windowSpan = TimeSpan.FromMinutes(windowMinutes);
        var start = 0; // first index whose Timestamp >= (current - windowSpan) -- i.e. still inside the window.
        var aCount = 0;
        var bCount = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var cutoff = rows[i].EndTimestamp - windowSpan;
            while (start < i && rows[start].EndTimestamp < cutoff)
            {
                RemoveFromCount(rows[start].RelationshipCategory, ref aCount, ref bCount);
                start++;
            }
            // The window as currently sized covers [start, i) -- i.e. every prior row already
            // added and not yet removed. Nothing further to add here since row i-1 was added when
            // i was the loop cursor at i-1+1; see the add step below for row i itself, deferred to
            // the NEXT iteration so it is never counted in its own vote.

            var total = aCount + bCount;
            var winner = aCount == bCount ? "TIE" : aCount > bCount ? "A" : "B";
            var currentPattern = rows[i].RelationshipCategory == PatternA ? "A" : rows[i].RelationshipCategory == PatternB ? "B" : "Other";
            results.Add(new VoteResult(rows[i].TradingDate, rows[i].EventId, rows[i].EndTimestamp, windowMinutes,
                aCount, bCount, total, winner, aCount - bCount, Math.Abs(aCount - bCount), total == 0 ? 0.0 : Math.Abs(aCount - bCount) / (double)total,
                currentPattern));

            // Now add row i itself to the running window state, for use by FUTURE iterations only.
            AddToCount(rows[i].RelationshipCategory, ref aCount, ref bCount);
        }
        return results;
    }

    static void AddToCount(string category, ref int a, ref int b)
    {
        if (category == PatternA) { a++; }
        else if (category == PatternB) { b++; }
    }

    static void RemoveFromCount(string category, ref int a, ref int b)
    {
        if (category == PatternA) { a--; }
        else if (category == PatternB) { b--; }
    }

    /// <summary>
    /// One row per (run, ReachedLength) pair, for ReachedLength 1..6 -- fires the instant a
    /// maximal consecutive same-pattern run FIRST reaches that length (6 meaning "reached at
    /// least 6"; never re-fires for a 7th, 8th, ... event of the same run). A run of final length
    /// 3 contributes ReachedLength=1,2,3 only. Category change (including to a non-A/B category)
    /// always ends the run, matching <see cref="EpisodeAnalysis.DetectEpisodes"/>'s own
    /// continuity rule exactly.
    /// </summary>
    public sealed record RunLengthTrigger(DateOnly TradingDate, int EventId, DateTimeOffset Timestamp, string Pattern, int ReachedLength, int FinalRunLength);

    public static List<RunLengthTrigger> ComputeRunLengthTriggers(IReadOnlyList<RelationshipObservation> rows, int maxReachedLength = 6)
    {
        var triggers = new List<RunLengthTrigger>();
        string? currentPattern = null;
        var currentLength = 0;
        var runStartIdx = -1;

        void FlushRun(int endIdxExclusive)
        {
            if (currentPattern is null || currentLength == 0) { return; }
            for (var reach = 1; reach <= Math.Min(currentLength, maxReachedLength); reach++)
            {
                var idx = runStartIdx + reach - 1;
                triggers.Add(new RunLengthTrigger(rows[idx].TradingDate, rows[idx].EventId, rows[idx].EndTimestamp, currentPattern, reach, currentLength));
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var category = rows[i].RelationshipCategory;
            var isAOrB = category == PatternA || category == PatternB;
            if (isAOrB && category == currentPattern)
            {
                currentLength++;
            }
            else if (isAOrB)
            {
                FlushRun(i);
                currentPattern = category;
                currentLength = 1;
                runStartIdx = i;
            }
            else
            {
                FlushRun(i);
                currentPattern = null;
                currentLength = 0;
                runStartIdx = -1;
            }
        }
        FlushRun(rows.Count);
        return triggers;
    }

    /// <summary>Descriptive-only run-length distribution bucket, matching <see cref="EpisodeAnalysis.DurationBucket"/>'s spirit but at the task's own requested granularity (1/2/3/4/5/6+).</summary>
    public static string RunLengthBucket(int finalRunLength) => finalRunLength switch
    {
        1 => "1", 2 => "2", 3 => "3", 4 => "4", 5 => "5", _ => "6+",
    };
}
