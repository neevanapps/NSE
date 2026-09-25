namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-25, rolling 5-bar (2600-contract base bar) contextual-window state hypothesis. Pure
/// state-machine over an already-computed sequence of per-window RollingState labels ("A"/"B"/
/// "Other") -- treats a run of consecutive same-state windows as ONE state episode (the windows
/// overlap 4-of-5 base bars and are highly correlated, so counting every overlapping window as an
/// independent signal would be wrong), not as N independent signals. Mirrors
/// <see cref="EpisodeAnalysis"/>'s "maximal run" convention and <see cref="RollingVoteAndRunAnalysis"/>'s
/// causal-only design, generalized to expose the full state-age/entry/exit annotation this
/// experiment's own spec requires for every row (not just trigger points).
/// </summary>
public static class RollingStateAnalysis
{
    public sealed record StateAnnotation(
        string RollingState, string PreviousState,
        bool IsStateEntry, bool IsStateContinuation, bool IsStateExit,
        int StateAge, int StateEpisodeId);

    /// <summary>
    /// Annotates each element of <paramref name="states"/> (in chronological order) with its
    /// entry/continuation/exit status, age, and episode id. "Other" rows always get StateAge=0 and
    /// StateEpisodeId=-1 (no active episode). A direct A-to-B (or B-to-A) transition marks BOTH
    /// IsStateExit and IsStateEntry true on the same row, per the spec's own worked example.
    /// </summary>
    public static List<StateAnnotation> Annotate(IReadOnlyList<string> states)
    {
        var result = new List<StateAnnotation>(states.Count);
        var prev = "Other";
        var age = 0;
        var episodeId = -1;
        var nextEpisodeId = 0;

        foreach (var cur in states)
        {
            var isAB = cur is "A" or "B";
            var prevIsAB = prev is "A" or "B";
            var isEntry = isAB && cur != prev;
            var isContinuation = isAB && cur == prev;
            var isExit = prevIsAB && cur != prev;

            if (isEntry) { age = 1; episodeId = nextEpisodeId++; }
            else if (isContinuation) { age++; }
            else { age = 0; episodeId = -1; }

            result.Add(new StateAnnotation(cur, prev, isEntry, isContinuation, isExit, age, episodeId));
            prev = cur;
        }
        return result;
    }
}
