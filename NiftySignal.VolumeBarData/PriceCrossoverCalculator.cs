namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, research-only (2026-09-22 option-price moving-average-crossover task -- see
/// docs/VOLUME_BAR_FINDINGS.md's dated section). Pure fast/slow SMA crossover detector on a raw
/// PRICE series (an option's own premium, band-averaged premium, etc.) -- mirrors
/// <see cref="TradeSimulator.SimulateCrossoverDayAsync"/>'s own crossing-detection shape (rolling
/// window of the last <c>slowBars</c> readings, fast-window SMA vs whole-window SMA, a sign flip in
/// the fast-minus-slow diff that also clears a gap threshold qualifies as a crossing) but the
/// threshold here is a FRACTION of the slow MA (a percent gap), not raw score points -- price is on
/// a totally different, day-varying scale (a Rs 40 ATM premium vs a Rs 300 one) that a fixed
/// point-threshold can't sensibly span, unlike the existing -100..100 score conventions this project
/// otherwise sweeps in points. No look-ahead: each <see cref="Observe"/> call only ever uses the
/// price handed to it and prices already observed in earlier calls.
/// </summary>
public sealed class PriceCrossoverEngine
{
    readonly int _fastBars;
    readonly int _slowBars;
    readonly List<double> _window;
    double? _previousDiffFraction;

    public PriceCrossoverEngine(int fastBars, int slowBars)
    {
        if (fastBars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fastBars), fastBars, "fastBars must be > 0.");
        }
        if (slowBars <= fastBars)
        {
            throw new ArgumentOutOfRangeException(nameof(slowBars), slowBars, "slowBars must be > fastBars.");
        }

        _fastBars = fastBars;
        _slowBars = slowBars;
        _window = new List<double>(slowBars);
    }

    /// <summary>
    /// One bar's crossover reading. <see cref="FastMa"/>/<see cref="SlowMa"/>/<see cref="DiffFraction"/>
    /// are null until the rolling window has accumulated <c>slowBars</c> real readings (same
    /// "no fabricated warm-up value" discipline <see cref="TradeSimulator.SimulateCrossoverDayAsync"/>
    /// already follows for its score-based crossover).
    /// </summary>
    public readonly record struct Step(double? FastMa, double? SlowMa, double? DiffFraction, bool CrossedUp, bool CrossedDown);

    /// <summary>
    /// Observes one bar's price. <paramref name="price"/> null means no real print this bar -- the
    /// window is left untouched (never fabricates a price to keep the window full), same convention
    /// every other rolling-window metric in this project (<c>BarCountRollingMean</c>,
    /// <c>SessionRankTracker</c>) already uses for a missing reading.
    /// <paramref name="thresholdFraction"/> is the minimum |fast-slow|/slow gap (e.g. 0.01 = 1%
    /// of the slow MA) a crossing bar must clear to qualify as a real signal, not window noise --
    /// see <c>PriceCrossoverThresholdCalibration</c> for how this project derives it from the actual
    /// historical gap distribution rather than picking it by eye.
    /// </summary>
    public Step Observe(double? price, double thresholdFraction)
    {
        if (price is not { } p)
        {
            return new Step(null, null, null, false, false);
        }

        _window.Add(p);
        if (_window.Count > _slowBars)
        {
            _window.RemoveAt(0);
        }

        if (_window.Count < _slowBars)
        {
            return new Step(null, null, null, false, false);
        }

        var fastMa = _window.Skip(_window.Count - _fastBars).Average();
        var slowMa = _window.Average();
        if (slowMa == 0)
        {
            // Degenerate (shouldn't happen for a real option premium > 0) -- no crossing, but still
            // report the MAs rather than silently dropping the bar.
            return new Step(fastMa, slowMa, null, false, false);
        }

        var diffFraction = (fastMa - slowMa) / slowMa;
        var crossedUp = false;
        var crossedDown = false;
        if (_previousDiffFraction is { } prev)
        {
            crossedUp = prev <= 0 && diffFraction > 0 && Math.Abs(diffFraction) >= thresholdFraction;
            crossedDown = prev >= 0 && diffFraction < 0 && Math.Abs(diffFraction) >= thresholdFraction;
        }

        _previousDiffFraction = diffFraction;
        return new Step(fastMa, slowMa, diffFraction, crossedUp, crossedDown);
    }
}
