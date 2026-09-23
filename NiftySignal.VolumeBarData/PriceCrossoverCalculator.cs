namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, research-only (2026-09-22 option-price moving-average-crossover task -- see
/// docs/VOLUME_BAR_FINDINGS.md's dated section). Pure fast/slow crossover detector on a raw
/// PRICE series (an option's own premium, band-averaged premium, etc.) -- mirrors
/// <see cref="TradeSimulator.SimulateCrossoverDayAsync"/>'s own crossing-detection shape (rolling
/// window of the last <c>slowBars</c> readings, fast-window MA vs whole-window MA, a sign flip in
/// the fast-minus-slow diff that also clears a gap threshold qualifies as a crossing) but the
/// threshold here is a FRACTION of the slow MA (a percent gap), not raw score points -- price is on
/// a totally different, day-varying scale (a Rs 40 ATM premium vs a Rs 300 one) that a fixed
/// point-threshold can't sensibly span, unlike the existing -100..100 score conventions this project
/// otherwise sweeps in points. No look-ahead: each <see cref="Observe"/> call only ever uses the
/// price handed to it and prices already observed in earlier calls.
///
/// 2026-09-23: EACH LEG CAN INDEPENDENTLY BE SMA OR EMA (<see cref="MaType"/>, the same enum
/// <see cref="MaSpreadEngine"/> already defines for the correlation-research path). Defaults to
/// SMA/SMA, which reproduces the ORIGINAL 2026-09-22 behavior exactly, bar for bar -- every
/// existing caller/test that doesn't pass a type is byte-for-byte unaffected. The EMA seeding/
/// recursion convention (seed at the bar the window first fills to <c>slowBars</c> using that
/// bar's own SMA, standard alpha=2/(N+1) recursion on every subsequent real price, the seeding bar
/// itself not double-counted) is copy-identical to <see cref="MaSpreadEngine.Observe"/>'s own --
/// this class was previously kept SMA-only deliberately because an EARLIER task's rules required
/// leaving it unchanged while `MaSpreadEngine` was built alongside it for correlation research
/// only; that constraint doesn't apply to this task, which explicitly wants a tradeable EMA
/// crossover backtest, so this is the natural place to add EMA support rather than duplicating the
/// whole entry/exit/P&amp;L simulator a second time around `MaSpreadEngine`.
/// </summary>
public sealed class PriceCrossoverEngine
{
    readonly int _fastBars;
    readonly int _slowBars;
    readonly MaType _fastType;
    readonly MaType _slowType;
    readonly List<double> _window;
    double? _fastEma;
    double? _slowEma;
    double? _previousDiffFraction;

    public PriceCrossoverEngine(int fastBars, int slowBars, MaType fastType = MaType.Sma, MaType slowType = MaType.Sma)
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
        _fastType = fastType;
        _slowType = slowType;
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

        var wasSeeded = _fastEma is not null;
        _window.Add(p);
        if (_window.Count > _slowBars)
        {
            _window.RemoveAt(0);
        }

        // EMA recursion, identical convention to MaSpreadEngine.Observe: seed at the bar the window
        // first fills to slowBars (using that bar's own SMA as the seed), standard alpha=2/(N+1)
        // recursion on every subsequent real price -- the seeding bar does not also apply the
        // recursion step. Computed unconditionally (even when both legs are SMA) so a caller can't
        // observe any behavior difference by later reading _fastEma/_slowEma; harmless extra work,
        // never read when _fastType/_slowType are both Sma.
        if (!wasSeeded && _window.Count == _slowBars)
        {
            _fastEma = _window.Skip(_window.Count - _fastBars).Average();
            _slowEma = _window.Average();
        }
        else if (wasSeeded)
        {
            var alphaFast = 2.0 / (_fastBars + 1);
            _fastEma = (p - _fastEma!.Value) * alphaFast + _fastEma.Value;
            var alphaSlow = 2.0 / (_slowBars + 1);
            _slowEma = (p - _slowEma!.Value) * alphaSlow + _slowEma.Value;
        }

        if (_window.Count < _slowBars)
        {
            return new Step(null, null, null, false, false);
        }

        var fastSma = _window.Skip(_window.Count - _fastBars).Average();
        var slowSma = _window.Average();
        var fastMa = _fastType == MaType.Sma ? fastSma : _fastEma!.Value;
        var slowMa = _slowType == MaType.Sma ? slowSma : _slowEma!.Value;
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

    /// <summary>
    /// 2026-09-23, correctness-review fix (see docs/Price_Based_Findings.md's "Correctness review"
    /// section): callers must invoke this whenever the underlying instrument being observed changes
    /// (e.g. the rolling-ATM strike this engine's price came from just rolled to a different strike)
    /// -- it clears the rolling window and EMA state back to fresh-construction, so a subsequent
    /// <see cref="Observe"/> call can never average this bar's new instrument's price together with
    /// an earlier bar's DIFFERENT instrument's price. Does not reset <see cref="_fastBars"/>/
    /// <see cref="_slowBars"/>/<see cref="_fastType"/>/<see cref="_slowType"/> (the engine's own
    /// configuration, unaffected by which instrument it happens to be tracking).
    /// </summary>
    public void Reset()
    {
        _window.Clear();
        _fastEma = null;
        _slowEma = null;
        _previousDiffFraction = null;
    }
}
