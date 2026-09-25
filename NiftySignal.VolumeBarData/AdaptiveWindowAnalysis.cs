namespace NiftySignal.VolumeBarData;

/// <summary>
/// 2026-09-25, activity-normalized rolling-context hypothesis: instead of a fixed bar count, the
/// rolling window's own bar count is chosen so its elapsed real time reaches a pre-specified
/// target (180 seconds, frozen before this experiment ran -- never swept). Pure, causal, dependency
/// -free: only ever looks at bars at or before <paramref name="endIdx"/>, so it can never use
/// future bar durations/volumes to decide the window size.
/// </summary>
public static class AdaptiveWindowAnalysis
{
    /// <summary>
    /// Walks backward from <paramref name="endIdx"/> (inclusive) until the elapsed span from the
    /// candidate start bar's OWN StartTimestamp to <paramref name="bars"/>[endIdx]'s EndTimestamp
    /// reaches <paramref name="targetSeconds"/>, or bar 0 is reached (whichever first) -- never
    /// forces a minimum/maximum window-bar count. Returns the resulting start index (inclusive);
    /// WindowBarCount = endIdx - result + 1.
    /// </summary>
    public static int FindWindowStartIndex(IReadOnlyList<FutureEventBar> bars, int endIdx, double targetSeconds)
    {
        var j = endIdx;
        while (j > 0 && (bars[endIdx].EndTimestamp - bars[j].StartTimestamp).TotalSeconds < targetSeconds)
        {
            j--;
        }
        return j;
    }
}
