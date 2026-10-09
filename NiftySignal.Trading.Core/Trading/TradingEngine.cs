using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Trading;

/// <summary>
/// The CP3 composition: <see cref="VariantEngine"/> (entry decisions) + <see cref="ExecutionEngine"/> (simulated fills, positions, exits,
/// costs). It is the single <see cref="IEngineHandler"/> a loop drives. Real-order mode is unavailable: the constructor refuses any
/// configuration that selects a broker adapter.
/// <para>
/// Per quote event the order is: (1) the quote is added to the time-indexed history, (2) resting entry orders that have matured evaluate
/// against it, (3) the variant engine runs (new opportunities / intents / cancel requests), (4) new intents and cancel requests are
/// handed to execution, (5) stop / target triggers of open positions evaluate. Deadlines are routed by key prefix.
/// </para>
/// A runnable end-to-end configuration must state a liquidation start (no 15:05 is assumed), at or after the entry close, and must have a
/// cost schedule covering the trade date.
/// </summary>
public sealed class TradingEngine : IEngineHandler
{
    private readonly List<EngineOutput> _outputs = new();
    private int _routed;

    public TradingEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IBaseStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>(config.Validate());
        if (config.Session.LiquidationStart is not { } liquidation)
        {
            errors.Add("A liquidation start must be configured for a runnable trading engine (none is assumed).");
        }
        else if (liquidation < config.Session.EntryClose)
        {
            errors.Add("LiquidationStart must not be before EntryClose.");
        }

        if (!CostModel.IsConfigured(config.Costs, tradeDate))
        {
            errors.Add($"No cost schedule is effective on {tradeDate:yyyy-MM-dd}; costs are never assumed to be zero.");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException("Trading engine cannot start: " + string.Join(" ", errors), nameof(config));
        }

        Config = config;
        Variants = new VariantEngine(config, tradeDate, universe, strategies, _outputs);
        Execution = new ExecutionEngine(config, tradeDate, Variants.Schedule, Variants, new CostModel(config.Costs, tradeDate), _outputs);
    }

    public TradingConfig Config { get; }

    public VariantEngine Variants { get; }

    public ExecutionEngine Execution { get; }

    /// <summary>Every output in the order it was produced (decisions, intents, fills, positions, reports).</summary>
    public IReadOnlyList<EngineOutput> Outputs => _outputs;

    public void Start(IEngineScheduler scheduler) => Variants.Start(scheduler);

    public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler)
    {
        var option = engineEvent is QuoteEvent { Kind: EventKind.OptionQuote } optionQuote ? optionQuote : null;
        if (option is not null)
        {
            Execution.AppendQuote(option);
            Execution.OnQuoteForOrders(option, scheduler);
        }

        Variants.OnEvent(engineEvent, scheduler);
        Route(scheduler);
        if (option is not null)
        {
            Execution.OnQuoteForPositions(option, scheduler);
        }
    }

    public void OnDeadline(ScheduledDeadline deadline, IEngineScheduler scheduler)
    {
        if (deadline.Key.StartsWith("exec-", StringComparison.Ordinal))
        {
            Execution.OnDeadline(deadline, scheduler);
            return;
        }

        Variants.OnDeadline(deadline, scheduler);
        Route(scheduler);
        if (deadline.Class == DeadlineClass.SessionBoundary && SessionSchedule.TryParseBoundaryKey(deadline.Key, out var kind))
        {
            Execution.OnSessionBoundary(kind, scheduler);
        }
    }

    /// <summary>Hands newly produced entry intents and cancel requests to execution (each exactly once, in output order).</summary>
    private void Route(IEngineScheduler scheduler)
    {
        while (_routed < _outputs.Count)
        {
            var output = _outputs[_routed++];
            switch (output)
            {
                case EntryIntent intent:
                    Execution.RegisterEntry(intent, scheduler);
                    break;
                case CancelRequest cancel:
                    Execution.RequestCancel(cancel, scheduler);
                    break;
            }
        }
    }
}
