using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>Drives a full <see cref="TradingEngine"/> (entries + simulated execution + exits) inside a real <see cref="EngineLoop"/>.</summary>
internal sealed class TradeKit
{
    public const string Ce = "CE23900";

    /// <summary>The instant of bar 1 in most scenarios: 10:00:00 IST.</summary>
    public static readonly DateTimeOffset T0 = Clock.Ist(10, 0);

    private long _id;

    public TradeKit(TradingConfig config, IEnumerable<OptionContract>? universe = null, params IBaseStrategy[] strategies)
    {
        Config = config;
        var strategyMap = (strategies.Length == 0 ? new IBaseStrategy[] { ScriptedStrategy.Valid("S", 1, null) } : strategies).ToDictionary(s => s.StrategyId, s => s);
        Engine = new TradingEngine(config, Clock.Day, (universe ?? Kit.StandardUniverse()).ToList(), strategyMap);
        Loop = new EngineLoop(Engine, Engine.Variants.Schedule.DayStartUtc);
        Engine.Start(Loop);
    }

    /// <summary>Canonical family path: the engine is driven by strategy families (several variants per family).</summary>
    public TradeKit(TradingConfig config, IReadOnlyDictionary<string, NiftySignal.Trading.Core.Strategies.IStrategyFamily> families, IEnumerable<OptionContract>? universe = null)
    {
        Config = config;
        Engine = new TradingEngine(config, Clock.Day, (universe ?? Kit.StandardUniverse()).ToList(), families);
        Loop = new EngineLoop(Engine, Engine.Variants.Schedule.DayStartUtc);
        Engine.Start(Loop);
    }

    public TradingConfig Config { get; }

    public TradingEngine Engine { get; }

    public EngineLoop Loop { get; }

    /// <summary>TEST brokerage (flat Rs 20 per fill): not the user's plan, chosen only so that the golden ledger is easy to recompute.</summary>
    public static CostsConfig TestCosts(decimal brokerage = 20m, BrokerageUnit unit = BrokerageUnit.PerOrder)
        => new() { Schedules = new[] { NseIndexOptionsCostSchedules.From20260401(new FlatBrokerage { Amount = brokerage, Unit = unit }) } };

    public static TradingConfig MakeConfig(ExecutionConfig? execution = null, CostsConfig? costs = null, SessionConfig? session = null, ContractSelectionConfig? selection = null, params VariantSpec[] variants)
        => new()
        {
            ConfigVersion = "cp3-kit",
            Execution = execution ?? new ExecutionConfig(),
            Costs = costs ?? TestCosts(),
            Session = session ?? new SessionConfig { LiquidationStart = new TimeOnly(15, 5, 0) },
            Selection = selection ?? new ContractSelectionConfig(),
            Variants = variants.ToDictionary(v => v.StrategyId + "/" + v.VariantId),
        };

    public static VariantSpec Immediate(string id = "A", SizingSpec? sizing = null, ExitSpec? exit = null, decimal? dailySl = null, int? maxAttempts = null)
        => new()
        {
            VariantId = id,
            StrategyId = "S",
            EntryCondition = new NiftySignal.Trading.Core.Entry.ImmediateEntrySpec(),
            Sizing = sizing ?? new FixedQuantitySpec(),
            Exit = exit ?? new ExitSpec(),
            DailySl = dailySl,
            MaxAttemptsPerOpportunity = maxAttempts,
        };

    public DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    public QuoteEvent Q(DateTimeOffset at, decimal? bid, decimal? ask, long? bidQty = 100000, long? askQty = 100000, string token = Ce)
        => new(at, EventKind.OptionQuote, ++_id, token, bid, ask, null, bidQty, askQty);

    public void Feed(params EngineEvent[] group) => Loop.ProcessGroup(group);

    public void Bar(DateTimeOffset at, int seq, params EngineEvent[] quotes) => Loop.ProcessGroup(quotes.Append(Ev.Bar(at, seq)).ToArray());

    public IEnumerable<T> Out<T>(string? variantId = null) where T : EngineOutput
        => Engine.Outputs.OfType<T>().Where(o => variantId is null || o.VariantId == variantId);

    public IEnumerable<FillRecord> Fills(string? variantId = null) => Out<FillRecord>(variantId);

    /// <summary>Outputs of one exact variant identity (StrategyId + VariantId).</summary>
    public IEnumerable<T> Out<T>(NiftySignal.Trading.Core.Strategies.VariantKey key) where T : EngineOutput
        => Engine.Outputs.OfType<T>().Where(o => o.Variant == key);

    public Position Position(NiftySignal.Trading.Core.Strategies.VariantKey key, int index = 0) => Engine.Execution.PositionsOf(key)[index];

    public Position Position(string variantId = "A", int index = 0) => Engine.Execution.PositionsOf(Vk.S(variantId))[index];

    /// <summary>Bar 1 at T0 with an entry quote (bid = ask - 1), heartbeat past the 1 s latency: the entry order fills at T0 + 1 s.</summary>
    public static TradeKit Opened(VariantSpec spec, decimal entryAsk = 125m, ExecutionConfig? execution = null, long? askQty = 100000)
    {
        var kit = new TradeKit(MakeConfig(execution, variants: spec));
        kit.Bar(T0, 1, kit.Q(T0, entryAsk - 1m, entryAsk, askQty: askQty));
        kit.Loop.AdvanceTo(kit.At(1.5));
        return kit;
    }

    public static string Signature(IEnumerable<EngineOutput> outputs) => Kit.Signature(outputs);
}
