using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Tests.Trading.Core;

/// <summary>Base strategy whose validity is scripted per bar sequence number.</summary>
internal sealed class ScriptedStrategy(string strategyId, Func<long, StrategyValidity> script) : IBaseStrategy
{
    public string StrategyId { get; } = strategyId;

    public string Version => "scripted";

    public StrategyValidity OnBar(BarCompletedEvent bar) => script(bar.BarSeq);

    /// <summary>Valid for bars in [from, to] (inclusive; null = open ended) on the given side.</summary>
    public static ScriptedStrategy Valid(string id, long from, long? to, OptionSide side = OptionSide.Call)
        => new(id, seq => seq >= from && (to is null || seq <= to)
            ? new StrategyValidity(true, side, "scripted")
            : new StrategyValidity(false, side, "scripted-off"));
}

/// <summary>Drives a <see cref="VariantEngine"/> inside a real <see cref="EngineLoop"/>; lets a test run actions at scheduled instants (inside the loop).</summary>
internal sealed class Kit
{
    private sealed class Handler(Kit kit) : IEngineHandler
    {
        public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler) => kit.Engine.OnEvent(engineEvent, scheduler);

        public void OnDeadline(ScheduledDeadline deadline, IEngineScheduler scheduler)
        {
            if (kit._actions.TryGetValue(deadline.Key, out var action))
            {
                action(scheduler);
                return;
            }

            kit.Engine.OnDeadline(deadline, scheduler);
        }
    }

    private readonly Dictionary<string, Action<IEngineScheduler>> _actions = new();
    private long _id;
    private int _actionSeq;

    public Kit(TradingConfig config, IEnumerable<OptionContract> universe, params IBaseStrategy[] strategies)
    {
        Config = config;
        Universe = universe.ToList();
        Engine = new VariantEngine(config, Clock.Day, Universe, strategies.ToDictionary(s => s.StrategyId, s => s));
        Loop = new EngineLoop(new Handler(this), Engine.Schedule.DayStartUtc);
        Engine.Start(Loop);
    }

    public TradingConfig Config { get; }

    public IReadOnlyList<OptionContract> Universe { get; }

    public VariantEngine Engine { get; }

    public EngineLoop Loop { get; }

    public static DateTimeOffset Bar(int k) => Clock.Ist(10, 0).AddMinutes(k);

    public static OptionContract Contract(OptionSide side, int strike, bool nextExpiry = false)
        => new(
            (side == OptionSide.Call ? "CE" : "PE") + strike + (nextExpiry ? "W2" : string.Empty),
            side,
            strike,
            nextExpiry ? new DateOnly(2026, 9, 29) : new DateOnly(2026, 9, 22),
            65,
            0.05m);

    public static IReadOnlyList<OptionContract> StandardUniverse() => new[]
    {
        Contract(OptionSide.Call, 23900),
        Contract(OptionSide.Call, 24000),
        Contract(OptionSide.Put, 23900),
        Contract(OptionSide.Put, 23800),
        Contract(OptionSide.Call, 23900, nextExpiry: true),
    };

    public static TradingConfig Config1(
        ContractSelectionConfig? selection = null,
        CandleConfig? candles = null,
        params VariantSpec[] variants)
        => new()
        {
            ConfigVersion = "kit",
            Selection = selection ?? new ContractSelectionConfig(),
            Candles = candles ?? new CandleConfig(),
            Variants = variants.ToDictionary(v => v.VariantId, v => v),
        };

    public static VariantSpec Immediate(string id, string strategy = "S", CapacityBlockedPolicy? capacity = null, int? maxAttempts = null)
        => new() { VariantId = id, StrategyId = strategy, EntryCondition = new ImmediateEntrySpec(), OnCapacityBlocked = capacity, MaxAttemptsPerOpportunity = maxAttempts };

    public static VariantSpec Ma(string id, int n, CandlePriceSource source = CandlePriceSource.AskClose, string strategy = "S", CapacityBlockedPolicy? capacity = null, int? maxAttempts = null)
        => new()
        {
            VariantId = id,
            StrategyId = strategy,
            EntryCondition = new AskBelowCandleMaSpec { N = n, PriceSource = source },
            OnCapacityBlocked = capacity,
            MaxAttemptsPerOpportunity = maxAttempts,
        };

    public QuoteEvent Q(DateTimeOffset at, string token, decimal ask, decimal spread = 1m)
        => Ev.Opt(at, ++_id, token, ask - spread, ask);

    public QuoteEvent QBidAsk(DateTimeOffset at, string token, decimal? bid, decimal? ask)
        => Ev.Opt(at, ++_id, token, bid, ask);

    public BarCompletedEvent B(int k) => Ev.Bar(Bar(k), k);

    public void Feed(params EngineEvent[] group) => Loop.ProcessGroup(group);

    /// <summary>One group at <see cref="Bar"/>(k): the given quotes (ordered before the bar by the total order) and the bar itself.</summary>
    public void BarGroup(int k, params EngineEvent[] quotes) => Loop.ProcessGroup(quotes.Append(B(k)).ToArray());

    public void At(DateTimeOffset time, Action<IEngineScheduler> action)
    {
        var key = "test:" + (++_actionSeq);
        _actions[key] = action;
        Loop.Schedule(time, DeadlineClass.StrategyTimer, 0, key);
    }

    public IEnumerable<T> Out<T>(string? variantId = null) where T : EngineOutput
        => Engine.Outputs.OfType<T>().Where(o => variantId is null || o.VariantId == variantId);

    public VariantState Variant(string id) => Engine.Variants.First(v => v.VariantId == id);

    public void Outcome(EntryIntent intent, EntryOutcomeKind kind, IEngineScheduler s)
        => Engine.ReportEntryOutcome(new EntryOutcome(intent.VariantId, intent.OpportunityId, intent.IntentId, kind), s);

    /// <summary>Canonical text of a sequence of outputs. Record ToString() is culture-sensitive, so it is rendered under the invariant culture (a test-side concern only: the engine's own strings are invariant).</summary>
    public static string Signature(IEnumerable<EngineOutput> outputs)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            return string.Join("\n", outputs.Select(o => o.ToString()));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
