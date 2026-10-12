using System.Globalization;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Signals;

/// <summary>
/// A fixed-capacity window of nullable readings (null = unavailable, NEVER zero). The mean of the last <c>k</c> readings exists only when all <c>k</c> are present;
/// the sums run oldest to newest so the result is a deterministic function of the readings.
/// </summary>
public sealed class NullableWindow(int capacity)
{
    private readonly List<double?> _values = new();

    public int Capacity { get; } = capacity;

    /// <summary>Number of readings held (at most <see cref="Capacity"/>).</summary>
    public int Count => _values.Count;

    public void Add(double? value)
    {
        _values.Add(value);
        if (_values.Count > Capacity)
        {
            _values.RemoveAt(0);
        }
    }

    public void Clear() => _values.Clear();

    /// <summary>The reading <paramref name="back"/> bars ago (0 = newest); null when unavailable or not held.</summary>
    public double? FromEnd(int back) => back >= 0 && back < _values.Count ? _values[_values.Count - 1 - back] : null;

    /// <summary>Mean of the last <paramref name="k"/> readings; null unless k readings are held and all are available.</summary>
    public double? Mean(int k)
    {
        if (k <= 0 || _values.Count < k)
        {
            return null;
        }

        double sum = 0;
        for (var i = _values.Count - k; i < _values.Count; i++)
        {
            if (_values[i] is not { } v)
            {
                return null;
            }

            sum += v;
        }

        return sum / k;
    }

    public void WriteState(StateWriter w, string name)
        => w.Line(name, string.Join(",", _values.Select(v => v is null ? "~" : v.Value.ToString("R", CultureInfo.InvariantCulture))));
}

/// <summary>
/// Shared machinery of the three NPT1 PAPER strategy families: one variant (MAIN), a per-session window state that is reset on a new trade date or any bar-sequence discontinuity, and the
/// rule that a decision needs the frame's observer-readiness flag. Pure: a deterministic function of the frames shown (no clock, no I/O). NO TRADING EDGE IS CLAIMED.
/// </summary>
public abstract class Npt1FamilyBase : IStrategyFamily, IStateContributor
{
    public const string MainVariant = "MAIN";

    private DateOnly? _tradeDate;
    private long? _lastBarSeq;

    protected Npt1FamilyBase(string strategyId)
    {
        StrategyId = strategyId;
    }

    public string StrategyId { get; }

    public abstract string Version { get; }

    public IReadOnlyCollection<string> VariantIds { get; } = new[] { MainVariant };

    /// <summary>Source bars seen since the last reset (consecutive).</summary>
    public int ConsecutiveBars { get; private set; }

    /// <summary>Number of times the windows were reset by a gap (a restart or outage that skipped bars) or a new session.</summary>
    public int Resets { get; private set; }

    public FamilyDecisions OnBar(BarCompletedEvent bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.Frame is not { } frame)
        {
            ResetWindows();
            return One(VariantDecision.Wait(MainVariant, "NoFrame", FormattableString.Invariant($"bar={bar.BarSeq} the bar carries no NPT1 frame")));
        }

        var note = string.Empty;
        if (_tradeDate != frame.TradeDate)
        {
            ResetWindows();
            _tradeDate = frame.TradeDate;
            note = "newSession ";
        }
        else if (_lastBarSeq is { } last && frame.BarSeq != last + 1)
        {
            ResetWindows();
            note = FormattableString.Invariant($"gapReset({last}->{frame.BarSeq}) ");
        }

        _lastBarSeq = frame.BarSeq;
        ConsecutiveBars++;
        var (decision, detail) = Evaluate(frame);
        var text = FormattableString.Invariant($"{note}bar={frame.BarSeq} n={ConsecutiveBars} ready={(frame.Ready ? 1 : 0)} {detail}");
        return One(decision(text));
    }

    private static FamilyDecisions One(VariantDecision decision) => new(new[] { decision });

    private void ResetWindows()
    {
        if (ConsecutiveBars > 0)
        {
            Resets++;
        }

        ConsecutiveBars = 0;
        _lastBarSeq = null;
        Reset();
    }

    /// <summary>Feeds the frame into the family's windows and returns the decision factory (which receives the audit text) and the audit detail of the inputs.</summary>
    protected abstract (Func<string, VariantDecision> Decision, string Detail) Evaluate(AdaptiveStrategyFrame frame);

    protected abstract void Reset();

    protected abstract void WriteFamilyState(StateWriter w);

    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("family/" + StrategyId);
        w.Line("tradeDate", _tradeDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        w.Line("lastBarSeq", _lastBarSeq?.ToString(CultureInfo.InvariantCulture));
        w.Line("consecutiveBars", ConsecutiveBars.ToString(CultureInfo.InvariantCulture));
        w.Line("resets", Resets.ToString(CultureInfo.InvariantCulture));
        WriteFamilyState(w);
    }

    protected static string F(double? v) => v is null ? "~" : v.Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Shared by the two MicroDev strategies: the ten-bar usable-seconds-weighted R series built from the finalized m and w of each bar.</summary>
public abstract class MicroDevFamilyBase : Npt1FamilyBase
{
    public const int RWindowBars = 10;

    protected readonly NullableWindow M = new(RWindowBars);
    protected readonly NullableWindow W = new(RWindowBars);
    protected readonly NullableWindow R = new(RWindowBars);

    protected MicroDevFamilyBase(string strategyId) : base(strategyId)
    {
    }

    /// <summary>R[t] = SUM(m*w over the last 10 bars) / SUM(w): needs all ten m and w available and a positive total weight; otherwise unavailable (never zero).</summary>
    public static double? RollingR(NullableWindow m, NullableWindow w)
    {
        if (m.Count < RWindowBars || w.Count < RWindowBars)
        {
            return null;
        }

        double numerator = 0;
        double weight = 0;
        for (var back = RWindowBars - 1; back >= 0; back--)
        {
            if (m.FromEnd(back) is not { } mv || w.FromEnd(back) is not { } wv)
            {
                return null;
            }

            numerator += mv * wv;
            weight += wv;
        }

        return weight > 0 ? numerator / weight : null;
    }

    /// <summary>Feeds m and w and returns the new R (or null).</summary>
    protected double? PushMicro(AdaptiveStrategyFrame frame)
    {
        M.Add(frame.MicroDevTimeWeighted);
        W.Add(frame.MicroDevUsableSeconds);
        var r = RollingR(M, W);
        R.Add(r);
        return r;
    }

    protected override void Reset()
    {
        M.Clear();
        W.Clear();
        R.Clear();
        ResetStrategy();
    }

    protected abstract void ResetStrategy();

    protected override void WriteFamilyState(StateWriter w)
    {
        M.WriteState(w, "m");
        W.WriteState(w, "w");
        R.WriteState(w, "R");
        WriteStrategyState(w);
    }

    protected abstract void WriteStrategyState(StateWriter w);
}

/// <summary>NPT1-MD5: target +1 (CE) when the mean of the last five R is positive, -1 (PE) when negative, the previous target when exactly zero; unavailable A5 skips the decision.</summary>
public sealed class Npt1Md5Family() : MicroDevFamilyBase("NPT1-MD5")
{
    public const int MinimumBars = 14;
    private int _target;

    public override string Version => "npt1-md5-v1";

    public int Target => _target;

    protected override (Func<string, VariantDecision>, string) Evaluate(AdaptiveStrategyFrame frame)
    {
        var r = PushMicro(frame);
        var a5 = R.Mean(5);
        var inputs = $"m={F(frame.MicroDevTimeWeighted)} w={F(frame.MicroDevUsableSeconds)} R={F(r)} A5={F(a5)} target={_target}";
        if (a5 is not { } a)
        {
            return (t => VariantDecision.Wait(MainVariant, "A5Unavailable", t), inputs + " skip=A5Unavailable");
        }

        if (!frame.Ready)
        {
            return (t => VariantDecision.Wait(MainVariant, "ObserverNotReady", t), inputs + " skip=NotReady");
        }

        if (a > 0)
        {
            _target = 1;
        }
        else if (a < 0)
        {
            _target = -1;
        }

        var detail = $"m={F(frame.MicroDevTimeWeighted)} w={F(frame.MicroDevUsableSeconds)} R={F(r)} A5={F(a5)} target={_target}";
        if (_target == 0)
        {
            return (t => VariantDecision.Wait(MainVariant, "NoTarget", t), detail + " skip=NoTarget");
        }

        var direction = _target > 0 ? StrategyDirection.Long : StrategyDirection.Short;
        return (t => VariantDecision.Enter(MainVariant, direction, direction == StrategyDirection.Long ? "TargetLong" : "TargetShort", t), detail);
    }

    protected override void ResetStrategy() => _target = 0;

    protected override void WriteStrategyState(StateWriter w) => w.Line("target", _target.ToString(CultureInfo.InvariantCulture));
}

/// <summary>NPT1-MDX3: F = mean of the last 3 R, S = mean of the last 10 R, d = F - S; CE on d[t-1] &lt;= 0 and d[t] &gt; 0 and F &gt; 0, PE on d[t-1] &gt;= 0 and d[t] &lt; 0 and F &lt; 0.</summary>
public sealed class Npt1Mdx3Family() : MicroDevFamilyBase("NPT1-MDX3")
{
    public const int MinimumBars = 20;
    private double? _previousD;

    public override string Version => "npt1-mdx3-v1";

    protected override (Func<string, VariantDecision>, string) Evaluate(AdaptiveStrategyFrame frame)
    {
        var r = PushMicro(frame);
        var f = R.Mean(3);
        var s = R.Mean(10);
        double? d = f is { } fv && s is { } sv ? fv - sv : null;
        var previous = _previousD;
        _previousD = d;
        var detail = $"m={F(frame.MicroDevTimeWeighted)} w={F(frame.MicroDevUsableSeconds)} R={F(r)} F={F(f)} S={F(s)} d={F(d)} dPrev={F(previous)}";
        if (d is not { } dv || previous is not { } dp || f is not { } ff)
        {
            return (t => VariantDecision.Wait(MainVariant, "WindowUnavailable", t), detail + " skip=WindowUnavailable");
        }

        if (!frame.Ready)
        {
            return (t => VariantDecision.Wait(MainVariant, "ObserverNotReady", t), detail + " skip=NotReady");
        }

        if (dp <= 0 && dv > 0 && ff > 0)
        {
            return (t => VariantDecision.Enter(MainVariant, StrategyDirection.Long, "CrossUpFPositive", t), detail + " event=CE");
        }

        if (dp >= 0 && dv < 0 && ff < 0)
        {
            return (t => VariantDecision.Enter(MainVariant, StrategyDirection.Short, "CrossDownFNegative", t), detail + " event=PE");
        }

        return (t => VariantDecision.Wait(MainVariant, "NoEvent", t), detail + " event=none");
    }

    protected override void ResetStrategy() => _previousD = null;

    protected override void WriteStrategyState(StateWriter w) => w.Line("previousD", F(_previousD));
}

/// <summary>NPT1-PE5: x = -P (strict PE contract delta); F = mean of the last 5 x, S = mean of the last 10 x, d = F - S, margin = 0.20 * |S|; crossover of d through +/- the previous bar's own margin. No sign gate.</summary>
public sealed class Npt1Pe5Family() : Npt1FamilyBase("NPT1-PE5")
{
    public const int MinimumBars = 11;
    public const double MarginFraction = 0.20;

    private readonly NullableWindow _x = new(10);
    private double? _previousD;
    private double? _previousMargin;

    public override string Version => "npt1-pe5-v1";

    protected override (Func<string, VariantDecision>, string) Evaluate(AdaptiveStrategyFrame frame)
    {
        double? x = frame.PeBandContractStrictDelta is { } p ? -(double)p : null;
        _x.Add(x);
        var f = _x.Mean(5);
        var s = _x.Mean(10);
        double? d = f is { } fv && s is { } sv ? fv - sv : null;
        double? margin = s is { } sm ? MarginFraction * Math.Abs(sm) : null;
        var dPrev = _previousD;
        var marginPrev = _previousMargin;
        _previousD = d;
        _previousMargin = margin;
        var detail = $"P={(frame.PeBandContractStrictDelta is { } pp ? pp.ToString(CultureInfo.InvariantCulture) : "~")} x={F(x)} F={F(f)} S={F(s)} d={F(d)} margin={F(margin)} dPrev={F(dPrev)} marginPrev={F(marginPrev)}";
        if (d is not { } dv || margin is not { } mv || dPrev is not { } dp || marginPrev is not { } mp)
        {
            return (t => VariantDecision.Wait(MainVariant, "WindowUnavailable", t), detail + " skip=WindowUnavailable");
        }

        if (!frame.Ready)
        {
            return (t => VariantDecision.Wait(MainVariant, "ObserverNotReady", t), detail + " skip=NotReady");
        }

        if (dp <= mp && dv > mv)
        {
            return (t => VariantDecision.Enter(MainVariant, StrategyDirection.Long, "CrossUpMargin", t), detail + " event=CE");
        }

        if (dp >= -mp && dv < -mv)
        {
            return (t => VariantDecision.Enter(MainVariant, StrategyDirection.Short, "CrossDownMargin", t), detail + " event=PE");
        }

        return (t => VariantDecision.Wait(MainVariant, "NoEvent", t), detail + " event=none");
    }

    protected override void Reset()
    {
        _x.Clear();
        _previousD = null;
        _previousMargin = null;
    }

    protected override void WriteFamilyState(StateWriter w)
    {
        _x.WriteState(w, "x");
        w.Line("previousD", F(_previousD));
        w.Line("previousMargin", F(_previousMargin));
    }
}
