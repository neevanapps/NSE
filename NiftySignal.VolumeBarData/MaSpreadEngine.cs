namespace NiftySignal.VolumeBarData;

/// <summary>
/// PROVISIONAL, RESEARCH-ONLY (2026-09-22 SMA-vs-EMA option-premium task, Parts 9-15 -- see
/// docs/VOLUME_BAR_FINDINGS.md's dated section). Generalizes <see cref="PriceCrossoverEngine"/>'s
/// fast/slow spread concept to support EMA as well as SMA on EITHER leg independently (SMA/SMA,
/// EMA/EMA, EMA-fast/SMA-slow, etc.) -- a NEW class, not a modification of
/// <see cref="PriceCrossoverEngine"/> itself, because that engine backs
/// <see cref="TradeSimulator.SimulatePriceCrossoverDayAsync"/>'s existing simulator behavior, which
/// this task's own experiment rules require left unchanged. No look-ahead: each <see cref="Observe"/>
/// call only ever uses the price handed to it and prices/EMA state from earlier calls.
/// </summary>
public enum MaType { Sma, Ema }

public sealed class MaSpreadEngine
{
    readonly int _fastBars;
    readonly int _slowBars;
    readonly MaType _fastType;
    readonly MaType _slowType;
    readonly List<double> _window; // raw price history, capped at slowBars, used for both SMA legs and EMA seeding
    double? _fastEma;
    double? _slowEma;
    int _count;

    public MaSpreadEngine(int fastBars, int slowBars, MaType fastType, MaType slowType)
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

    public readonly record struct Step(double? FastMa, double? SlowMa, double? DiffFraction);

    /// <summary>
    /// Same warm-up discipline as <see cref="PriceCrossoverEngine"/>: null price leaves state
    /// untouched, and no reading is produced until <c>slowBars</c> real prices have been observed
    /// (both SMA and EMA legs share this warm-up gate, so EMA doesn't get an unfair head start from
    /// a shorter warm-up than its SMA counterpart in a side-by-side comparison).
    /// </summary>
    public Step Observe(double? price)
    {
        if (price is not { } p)
        {
            return new Step(null, null, null);
        }

        var wasSeeded = _fastEma is not null;
        _window.Add(p);
        if (_window.Count > _slowBars)
        {
            _window.RemoveAt(0);
        }
        _count++;

        // EMA recursion: seed at the point the window first fills to slowBars (same warm-up bar as
        // SMA, using that bar's own SMA as the seed -- the standard convention), then standard
        // alpha=2/(N+1) recursion on every subsequent real price. The seeding bar itself does NOT
        // also apply the recursion step (that would double-count price p).
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
            return new Step(null, null, null);
        }

        var fastSma = _window.Skip(_window.Count - _fastBars).Average();
        var slowSma = _window.Average();

        var fastMa = _fastType == MaType.Sma ? fastSma : _fastEma!.Value;
        var slowMa = _slowType == MaType.Sma ? slowSma : _slowEma!.Value;

        if (slowMa == 0)
        {
            return new Step(fastMa, slowMa, null);
        }

        return new Step(fastMa, slowMa, (fastMa - slowMa) / slowMa);
    }
}
