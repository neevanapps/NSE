using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Metrics;

namespace NiftySignal.PaperEngineV2.Core.Evaluation;

/// <summary>Entry signals of one strategy on one frame. Both can be True on the same frame; arbitration (no entry on a conflict) belongs to the execution state machine, not to this pure evaluator.</summary>
public readonly record struct EntrySignals(Tri BuyCall, Tri BuyPut);

/// <summary>
/// A strategy compiled once at activation into allocation-free condition nodes. Evaluation is a pure function of the frames: it holds no state, reads no clock and cannot see another strategy.
/// <c>requireFreshVix</c> is an entry-only filter (exits are never blocked by it).
/// </summary>
public sealed class CompiledStrategy
{
    static readonly int VixFreshHandle = MetricRegistry.Get("vix.fresh").Handle;

    readonly ConditionNode? _buyCall;
    readonly ConditionNode? _buyPut;
    readonly ConditionNode? _exit;

    CompiledStrategy(StrategyConfig config, ConditionNode? buyCall, ConditionNode? buyPut, ConditionNode? exit)
    {
        Config = config;
        _buyCall = buyCall; _buyPut = buyPut; _exit = exit;
    }

    public StrategyConfig Config { get; }

    public StrategyIdentity Identity => Config.Identity;

    public static CompiledStrategy Compile(StrategyConfig config) =>
        new(config, config.BuyCall is { } c ? Build(c) : null, config.BuyPut is { } p ? Build(p) : null, config.ExitConditions is { } x ? Build(x) : null);

    /// <summary>Evaluates the buyCall / buyPut groups. A side without a group is False (the strategy never trades it); Unknown inputs give Unknown, never an entry.</summary>
    public EntrySignals EvaluateEntry(in EvaluationContext context)
    {
        var call = _buyCall?.Evaluate(context) ?? Tri.False;
        var put = _buyPut?.Evaluate(context) ?? Tri.False;
        if (Config.Filters.RequireFreshVix)
        {
            var fresh = context.Current.TryRead(VixFreshHandle, SummaryWindowId.W5, out var v) && double.IsFinite(v) ? TriLogic.Of(v == 1d) : Tri.Unknown;
            call = TriLogic.And(call, fresh);
            put = TriLogic.And(put, fresh);
        }

        return new EntrySignals(call, put);
    }

    /// <summary>The discretionary exit condition (False when none is configured; the mandatory exits are the execution layer's business).</summary>
    public Tri EvaluateExit(in EvaluationContext context) => _exit?.Evaluate(context) ?? Tri.False;

    /// <summary>The entry time window check (IST, evaluated against the verified bar's end instant by the caller).</summary>
    public bool IsEntryTimeAllowed(TimeOnly barEndIst) => barEndIst >= Config.Filters.EntryFrom && barEndIst < Config.Filters.EntryBefore;

    static ConditionNode Build(ConditionSpec spec) => spec switch
    {
        GroupSpec g => new GroupNode(g.IsAnd, g.Items.Select(Build).ToArray()),
        ThresholdSpec t => new ThresholdNode(t.Metric.Handle, t.Window, t.Cmp, t.A, t.B),
        MetricPairSpec m => new MetricPairNode(m.MetricA.Handle, m.WindowA, m.Cmp, m.MetricB.Handle, m.WindowB),
        CrossSpec c => new CrossNode(c.Direction, c.Metric.Handle, c.Window, c.ConstantLevel ?? 0d, c.LevelMetric?.Handle ?? -1, c.LevelWindow),
        _ => throw new InvalidOperationException($"Unsupported condition spec {spec.GetType().Name}."),
    };
}
