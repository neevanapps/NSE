namespace NiftySignal.Features;

/// <summary>
/// TrendReversion, re-windowed onto a volume clock (2026-09-17 volume-cadence plan: "multiples of
/// volume bars," not a real-time window -- the user's own correction of an earlier draft that
/// tried to keep a minute-equivalent window). Same formula and same sign convention as
/// `NiftySignal.Scoring.CoreScoreTrendReversionTracker` (signed net change / path length over the
/// window, NEGATED -- a clean recent trend reads as a REVERSION signal, not continuation, the
/// confirmed-if-counter-intuitive sign from docs/SCORE_CANDIDATES.md) -- ported here, not
/// reinvented, only the eviction rule changes (last <paramref name="windowBars"/> observations,
/// not "younger than a TimeSpan"). Already bounded [-1,1] by construction (|net| &lt;= path
/// always), same as the time-windowed version.
///
/// Deliberately a SEPARATE class from `CoreScoreTrendReversionTracker`, not a generalization of
/// it -- the two eviction rules (bar-count vs wall-clock-age) are different enough in shape that
/// forcing one class to do both would obscure which one any given caller actually gets. If the
/// volume-cadence version earns its way into the live score later, this is the class that gets
/// wired in, unchanged.
/// </summary>
public sealed class VolumeBarTrendReversionTracker(int windowBars)
{
    readonly Queue<double> _changes = new();

    /// <param name="change">This bar's own signed change (its close minus the PREVIOUS bar's close) -- null if there is no previous bar yet (the series' first bar). Not enqueued when null, matching the time-windowed version's own "a quiet cadence still reads whatever the window already holds" behavior.</param>
    public double? Observe(double? change)
    {
        if (change is { } c)
        {
            _changes.Enqueue(c);
        }

        while (_changes.Count > windowBars)
        {
            _changes.Dequeue();
        }

        if (_changes.Count == 0)
        {
            return null;
        }

        var net = _changes.Sum();
        var pathLength = _changes.Sum(Math.Abs);
        return pathLength > 0 ? -1.0 * (net / pathLength) : null;
    }
}
