using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Signals;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>
/// Test harness for the PaperLiveV1 engine: scripted TEST/PLUMBING families, a CE and a PE quote inside the premium band on every bar, and helpers that move
/// the market so that entries, stops, TP1 and liquidations can be provoked at exact instants. Nothing here claims a trading edge.
/// </summary>
internal sealed class PaperKit
{
    public const string Ce = "CE23900";
    public const string Pe = "PE23900";

    private int _seq;

    public PaperKit(TradingConfig config, params IStrategyFamily[] families)
    {
        Kit = new TradeKit(config, families.ToDictionary(f => f.StrategyId, f => f));
    }

    public TradeKit Kit { get; }

    public TradingEngineView Engine => new(Kit);

    public int LastBarSeq => _seq;

    public static VariantSpec Spec(string strategy, string variant, ExitSpec? exit = null, ScopeLimits? limits = null, CapacityBlockedPolicy? capacity = null)
        => new()
        {
            StrategyId = strategy,
            VariantId = variant,
            EntryCondition = new ImmediateEntrySpec(),
            Exit = exit ?? new ExitSpec(),
            Limits = limits,
            OnCapacityBlocked = capacity,
        };

    public static TradingConfig Config(RiskProfile risk, params VariantSpec[] variants)
        => TradeKit.MakeConfig(session: SessionConfig.PaperLiveV1(), variants: variants) with { Risk = risk };

    public static TradingConfig Config(decimal capital, params VariantSpec[] variants) => Config(RiskProfile.PaperLiveV1(capital), variants);

    public static PlumbingVariantScript Script(IEnumerable<(long From, long To)> longWindows, params long[] exits)
        => new(longWindows.Select(w => new PlumbingWindow(w.From, w.To, StrategyDirection.Long)).ToList(), exits.Length == 0 ? null : exits);

    public static PlumbingFamily Family(string strategyId, params (string Variant, PlumbingVariantScript Script)[] variants)
        => new(strategyId, variants.ToDictionary(v => v.Variant, v => v.Script));

    /// <summary>Starts the next bar at the given IST time (its sequence number is 1, 2, 3 ... in call order), quotes both contracts, then lets 1.5 s pass.</summary>
    public DateTimeOffset Bar(int hour, int minute, int second = 0, int millisecond = 0, decimal ceBid = 124m, decimal ceAsk = 125m, decimal peBid = 124m, decimal peAsk = 125m, double advance = 1.5, params EngineEvent[] first)
    {
        var at = Clock.Ist(hour, minute, second, millisecond);
        _seq++;
        Kit.Feed(first);
        Kit.Bar(at, _seq, Kit.Q(at, ceBid, ceAsk, token: Ce), Kit.Q(at, peBid, peAsk, token: Pe));
        Kit.Loop.AdvanceTo(at.AddSeconds(advance));
        return at;
    }

    /// <summary>A market move: new quotes for the contracts at the given time, then 1.5 s pass (stops, targets and exits fire from quotes).</summary>
    public DateTimeOffset Move(DateTimeOffset at, decimal? ceBid = null, decimal? peBid = null, decimal spread = 1m)
    {
        var events = new List<EngineEvent>();
        if (ceBid is { } c)
        {
            events.Add(Kit.Q(at, c, c + spread, token: Ce));
        }

        if (peBid is { } p)
        {
            events.Add(Kit.Q(at, p, p + spread, token: Pe));
        }

        Kit.Feed(events.ToArray());
        Kit.Loop.AdvanceTo(at.AddSeconds(1.5));
        return at;
    }

    public void Feed(params EngineEvent[] events) => Kit.Feed(events);

    public void AdvanceTo(DateTimeOffset at) => Kit.Loop.AdvanceTo(at);

    public IEnumerable<T> Out<T>(VariantKey key) where T : EngineOutput => Kit.Out<T>(key);

    public IReadOnlyList<EntryRejected> Rejections(VariantKey key) => Kit.Out<EntryRejected>(key).ToList();

    public static EngineEvent Kill(DateTimeOffset at, long seq, string scope, KillMode mode = KillMode.BlockEntries)
        => new OperatorCommandEvent(at, seq, OperatorCommand.Kill, scope, "tester", "test", mode);

    public static EngineEvent Reset(DateTimeOffset at, long seq, string scope)
        => new OperatorCommandEvent(at, seq, OperatorCommand.Reset, scope, "tester", "test");

    /// <summary>Thin read-only view over the engine used by the tests.</summary>
    internal sealed class TradingEngineView(TradeKit kit)
    {
        public NiftySignal.Trading.Core.Execution.ExecutionEngine Execution => kit.Engine.Execution;

        public NiftySignal.Trading.Core.Variants.VariantEngine Variants => kit.Engine.Variants;

        public NiftySignal.Trading.Core.Integrity.IntegrityManager Integrity => kit.Engine.Integrity;
    }
}
