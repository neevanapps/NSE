using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Conditions;

/// <summary>Three-valued result: a condition over an unavailable input is Unknown, never false and never zero.</summary>
public enum Tri : byte { False = 0, True = 1, Unknown = 2 }

public static class TriLogic
{
    public static Tri And(Tri a, Tri b) => a == Tri.False || b == Tri.False ? Tri.False : a == Tri.True && b == Tri.True ? Tri.True : Tri.Unknown;

    public static Tri Or(Tri a, Tri b) => a == Tri.True || b == Tri.True ? Tri.True : a == Tri.False && b == Tri.False ? Tri.False : Tri.Unknown;

    public static Tri Of(bool value) => value ? Tri.True : Tri.False;
}

/// <summary>
/// The immutable per-bar market state a strategy sees (built once per verified completed bar, shared by all strategies; Phase 6b builds the real one from the Market Summary). A reading is available
/// only when the frame positively provides a finite value for (metric, window); otherwise the evaluator treats it as Unknown.
/// </summary>
public interface IMarketStateFrame
{
    bool TryRead(int metricHandle, SummaryWindowId window, out double value);
}

/// <summary>The current frame and (for crossovers) the previous verified frame of the same session; null when there is none (first bar, gap, unverified predecessor).</summary>
public readonly record struct EvaluationContext(IMarketStateFrame Current, IMarketStateFrame? Previous);

public enum Comparison : byte { Gt, Lt, Ge, Le, Eq, Ne, Between }

public enum CrossDirection : byte { Up, Down }

/// <summary>A compiled condition node. Evaluation allocates nothing and reads only the frames.</summary>
public abstract class ConditionNode
{
    public abstract Tri Evaluate(in EvaluationContext context);
}

public sealed class GroupNode(bool isAnd, ConditionNode[] items) : ConditionNode
{
    public bool IsAnd { get; } = isAnd;

    public IReadOnlyList<ConditionNode> Items => items;

    public override Tri Evaluate(in EvaluationContext context)
    {
        var result = IsAnd ? Tri.True : Tri.False;
        foreach (var item in items)
        {
            var v = item.Evaluate(context);
            result = IsAnd ? TriLogic.And(result, v) : TriLogic.Or(result, v);
            if (IsAnd ? result == Tri.False : result == Tri.True) break;      // short-circuit: a decided group cannot change
        }

        return result;
    }
}

/// <summary>
/// "At least N of these leaf conditions" (node version atLeast/1). Strict Unknown policy: when any listed condition is Unknown the result is Unknown, so a count is never decided on partial evidence;
/// otherwise True iff the number of True conditions is &gt;= <c>min</c>. Evaluates every item (no short-circuit, so Unknown is always seen) and allocates nothing.
/// </summary>
public sealed class AtLeastNode(int min, ConditionNode[] items) : ConditionNode
{
    public int Min { get; } = min;

    public IReadOnlyList<ConditionNode> Items => items;

    public override Tri Evaluate(in EvaluationContext context)
    {
        var trues = 0;
        var unknown = false;
        foreach (var item in items)
        {
            switch (item.Evaluate(context))
            {
                case Tri.True: trues++; break;
                case Tri.Unknown: unknown = true; break;
            }
        }

        return unknown ? Tri.Unknown : TriLogic.Of(trues >= Min);
    }
}

static class Compare
{
    public static Tri Apply(Comparison cmp, double x, double a, double b) => cmp switch
    {
        Comparison.Gt => TriLogic.Of(x > a),
        Comparison.Lt => TriLogic.Of(x < a),
        Comparison.Ge => TriLogic.Of(x >= a),
        Comparison.Le => TriLogic.Of(x <= a),
        Comparison.Eq => TriLogic.Of(x == a),             // only ever compiled for discrete metrics (validated)
        Comparison.Ne => TriLogic.Of(x != a),
        Comparison.Between => TriLogic.Of(x >= a && x <= b),
        _ => Tri.Unknown,
    };
}

/// <summary>metric (window) <c>cmp</c> constant(s).</summary>
public sealed class ThresholdNode(int metric, SummaryWindowId window, Comparison cmp, double a, double b) : ConditionNode
{
    public override Tri Evaluate(in EvaluationContext context) =>
        context.Current.TryRead(metric, window, out var x) && double.IsFinite(x) ? Compare.Apply(cmp, x, a, b) : Tri.Unknown;
}

/// <summary>metric A (window A) <c>cmp</c> metric B (window B); both must be available.</summary>
public sealed class MetricPairNode(int metricA, SummaryWindowId windowA, Comparison cmp, int metricB, SummaryWindowId windowB) : ConditionNode
{
    public override Tri Evaluate(in EvaluationContext context) =>
        context.Current.TryRead(metricA, windowA, out var x) && double.IsFinite(x) && context.Current.TryRead(metricB, windowB, out var y) && double.IsFinite(y)
            ? Compare.Apply(cmp, x, y, y) : Tri.Unknown;
}

/// <summary>
/// Crossover against a constant level or another metric of the same unit, using the PREVIOUS and CURRENT frames: <c>Up</c> = previous &lt;= level and current &gt; level; <c>Down</c> = previous &gt;= level and
/// current &lt; level. Unknown when either frame lacks a value (including no previous frame) - a crossover is never inferred across a gap.
/// </summary>
public sealed class CrossNode(CrossDirection direction, int metric, SummaryWindowId window, double constantLevel, int levelMetric, SummaryWindowId levelWindow) : ConditionNode
{
    public override Tri Evaluate(in EvaluationContext context)
    {
        if (context.Previous is not { } previous) return Tri.Unknown;
        if (!context.Current.TryRead(metric, window, out var cur) || !previous.TryRead(metric, window, out var prev) || !double.IsFinite(cur) || !double.IsFinite(prev)) return Tri.Unknown;
        double levelNow = constantLevel, levelPrev = constantLevel;
        if (levelMetric >= 0)
        {
            if (!context.Current.TryRead(levelMetric, levelWindow, out levelNow) || !previous.TryRead(levelMetric, levelWindow, out levelPrev) || !double.IsFinite(levelNow) || !double.IsFinite(levelPrev)) return Tri.Unknown;
        }

        return direction == CrossDirection.Up ? TriLogic.Of(prev <= levelPrev && cur > levelNow) : TriLogic.Of(prev >= levelPrev && cur < levelNow);
    }
}
