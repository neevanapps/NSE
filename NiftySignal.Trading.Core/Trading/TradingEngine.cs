using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Reporting;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Variants;

namespace NiftySignal.Trading.Core.Trading;

/// <summary>
/// The composition a loop drives: <see cref="VariantEngine"/> (entry decisions) + <see cref="ExecutionEngine"/> (simulated fills, positions, exits,
/// costs) + <see cref="IntegrityManager"/> (global feed health, manual kill switch) + the risk profile gate. Real-order mode is unavailable: the
/// constructor refuses a broker adapter and the RealOrder risk profile.
/// <para>
/// Per quote event the order is: (1) a pending recovery may complete, (2) the quote joins the time-indexed history, (3) resting entry orders that have
/// matured evaluate against it, (4) the variant engine runs, (5) new intents and cancel requests are handed to execution, (6) stop / target triggers of
/// open positions evaluate. Control and feed-health events rank before data at the same instant. Deadlines are routed by key prefix.
/// </para>
/// A runnable configuration must state a liquidation start (no 15:05 is assumed), at or after the entry close, and have a cost schedule covering the
/// trade date.
/// </summary>
/// <summary>
/// Memory retention of the engine's history structures (contract section A.16.7). These settings affect resources only, never results, as long as
/// the retention is not shorter than the processing needs documented there; tests prove equivalence with <see cref="Unbounded"/>.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>Delivered-event memory of the <c>EngineLoop</c> (duplicate / conflict recognition window). Used by adapters when they create the loop.</summary>
    public TimeSpan? LoopSeenRetention { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Per-token option quote history behind the latest quote. <c>null</c> = <c>MaxQuoteAge + 1 minute</c>.</summary>
    public TimeSpan? QuoteHistoryRetention { get; init; }

    public static RetentionPolicy Default { get; } = new();

    public static RetentionPolicy Unbounded { get; } = new() { LoopSeenRetention = null, QuoteHistoryRetention = TimeSpan.FromDays(2) };
}

public sealed class TradingEngine : IEngineHandler
{
    private sealed class CompositeGate(TradingEngine owner) : IEntryGate
    {
        public IReadOnlyList<RejectionReason> Evaluate(VariantKey variant, DateTimeOffset now, decimal newPremium, string? excludeOpportunityId)
        {
            var reasons = new List<RejectionReason>();
            reasons.AddRange(owner.Integrity.KillReasons(variant));
            reasons.AddRange(owner.Integrity.IntegrityReasons());

            var profile = owner.Config.Risk;
            if (profile.Name == RiskProfile.PaperLiveV1Name)
            {
                // Every counter is derived from the fills and closed trades of this run; the capital is the ONE shared pool: open positions at average entry price
                // x open quantity plus the outstanding entry intents of every variant (the intent being decided is excluded: it is the new premium).
                var execution = owner.Execution;
                var strategyId = variant.StrategyId;
                var state = new ScopedRiskState(
                    new ScopeCounters(execution.TradesOf(variant), execution.RealizedNetOf(variant), execution.ConsecutiveLossesOf(variant)),
                    new ScopeCounters(execution.FamilyTradesOf(strategyId), execution.FamilyRealizedNet(strategyId), execution.FamilyConsecutiveLosses(strategyId)),
                    new ScopeCounters(execution.GlobalTrades(), execution.GlobalRealizedNet(), 0),
                    execution.OpenExposure() + owner.Variants.OutstandingIntentPremium(excludeOpportunityId),
                    newPremium);
                reasons.AddRange(ScopedRisk.Evaluate(profile, strategyId, owner.Variants.GetVariant(variant).Spec.Limits, state));
            }

            return reasons.Distinct().OrderBy(r => (int)r).ToList();
        }
    }

    private readonly List<EngineOutput> _outputs = new();
    private int _routed;

    public TradingEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IStrategyFamily> families,
        string runId = "run-unspecified",
        string runMode = "Historical",
        RetentionPolicy? retention = null,
        string? sourceSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>(config.Validate());
        errors.AddRange(config.Risk.StartBlockers());
        if (config.Session.LiquidationStart is not { } liquidation)
        {
            errors.Add("A liquidation start must be configured for a runnable trading engine (none is assumed).");
        }
        else if (liquidation < config.Session.EntryClose)
        {
            errors.Add("LiquidationStart must not be before EntryClose.");
        }
        else if (liquidation.ToTimeSpan() + config.Execution.Latency >= config.Session.FinalClose.ToTimeSpan())
        {
            errors.Add($"LiquidationStart {liquidation:HH:mm:ss} plus the order latency {config.Execution.Latency} reaches FinalClose {config.Session.FinalClose:HH:mm:ss}: no close attempt could execute before the final close.");
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
        TradeDate = tradeDate;
        RunId = runId;
        RunMode = runMode;
        Variants = new VariantEngine(config, tradeDate, universe, families, _outputs, sourceSessionId);
        Retention = retention ?? RetentionPolicy.Default;
        Execution = new ExecutionEngine(config, tradeDate, Variants.Schedule, Variants, new CostModel(config.Costs, tradeDate), _outputs, Retention.QuoteHistoryRetention);
        Integrity = new IntegrityManager(config.Integrity);
        var gate = new CompositeGate(this);
        Variants.EntryGate = gate;
        Execution.EntryGate = gate;
        Variants.BarClock = Execution;
        Variants.StrategyExitRequested = (variantKey, scheduler) => Execution.RequestStrategyExitForVariant(variantKey, scheduler);
        Admission = new EventAdmission(q => _outputs.Add(q));
    }

    /// <summary>Compatibility constructor for single-case strategies: wraps each in <see cref="LegacyBaseStrategyFamily"/> and uses the one canonical family path.</summary>
    public TradingEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IBaseStrategy> strategies,
        string runId = "run-unspecified",
        string runMode = "Historical",
        RetentionPolicy? retention = null,
        string? sourceSessionId = null)
        : this(config, tradeDate, universe, LegacyStrategies.AsFamilies(config, strategies), runId, runMode, retention, sourceSessionId)
    {
    }

    public TradingConfig Config { get; }

    public DateOnly TradeDate { get; }

    public string RunId { get; }

    public string RunMode { get; }

    public RetentionPolicy Retention { get; }

    public VariantEngine Variants { get; }

    public ExecutionEngine Execution { get; }

    public IntegrityManager Integrity { get; }

    /// <summary>Front door for adapters: rejects late / conflicting / inconsistent events with an <see cref="EventQuarantined"/> record before they reach the loop.</summary>
    public EventAdmission Admission { get; }

    /// <summary>Every output in the order it was produced (decisions, intents, fills, positions, reports, integrity transitions).</summary>
    public IReadOnlyList<EngineOutput> Outputs => _outputs;

    public void Start(IEngineScheduler scheduler) => Variants.Start(scheduler);

    /// <summary>
    /// Writes the whole trading engine's behaviour-relevant state (variants, execution, integrity, admission and the routing cursor) in a fixed
    /// order. The configuration, run labels and the retention setting are not state (they are part of the run's identity and header).
    /// </summary>
    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("engine");
        w.Line("tradeDate", TradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        w.Line("routed", _routed);
        w.Line("outputs", _outputs.Count);
        Variants.WriteState(w);
        Execution.WriteState(w);
        Integrity.WriteState(w);
        Admission.WriteState(w);
    }

    /// <summary>Per-variant daily summaries as of <paramref name="asOf"/> (see <see cref="DailySummaryBuilder"/>).</summary>
    public IReadOnlyList<VariantDailySummary> DailySummaries(DateTimeOffset asOf) => DailySummaryBuilder.Build(this, asOf);

    /// <summary>The structured audit trail derived from the outputs (see <see cref="AuditTrail"/>).</summary>
    public IReadOnlyList<AuditEntry> AuditTrailEntries() => AuditTrail.Build(this);

    public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler)
    {
        switch (engineEvent)
        {
            case OperatorCommandEvent command:
                HandleOperator(command, scheduler);
                return;
            case FeedHealthEvent health:
                HandleFeedHealth(health, scheduler);
                return;
        }

        // Recovery evidence comes from the futures / adaptive side only (never from an option quote), and is applied before the variants see this
        // event, so a ready bar that completes the recovery can already be acted on.
        IntegrityTransition? recovery = engineEvent switch
        {
            QuoteEvent { Kind: EventKind.FutureQuote } futureQuote => Integrity.OnFutureQuote(futureQuote),
            BarCompletedEvent bar => Integrity.OnBar(bar),
            _ => null,
        };
        if (recovery is not null)
        {
            _outputs.Add(recovery);
        }

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
            if (kind == SessionBoundaryKind.SessionEnd)
            {
                Variants.FlushAllSummaries(scheduler.Now);
            }
        }
    }

    // ------------------------------------------------------------------ integrity and kill switch

    private void HandleFeedHealth(FeedHealthEvent health, IEngineScheduler scheduler)
    {
        var transition = Integrity.OnFeedHealth(health);
        if (transition is null)
        {
            return;
        }

        _outputs.Add(transition);
        if (transition.To == IntegrityState.Degraded)
        {
            // Affected scope = every variant (a global fault). Pending attempts are cancelled; reservations stay until the outcome is reported.
            Variants.RequestCancelPending("*", CancelReason.IntegrityDegraded, health.AvailableAt);
            Route(scheduler);
        }
    }

    private void HandleOperator(OperatorCommandEvent command, IEngineScheduler scheduler)
    {
        var at = command.AvailableAt;
        var known = KillScopes.IsKnown(command.Scope, Variants.Variants.Select(v => v.Key));
        if (!known)
        {
            _outputs.Add(new KillSwitchChanged(at, command.Scope, command.Command, command.Mode, command.Actor, command.Reason, "Rejected:UnknownVariant", 0, 0, 0));
            return;
        }

        if (command.Command == OperatorCommand.Reset)
        {
            var effect = Integrity.Reset(command.Scope);
            _outputs.Add(new KillSwitchChanged(at, command.Scope, command.Command, command.Mode, command.Actor, command.Reason, effect, 0, 0, 0));
            return;
        }

        var changed = Integrity.Kill(command.Scope);
        var waiting = Variants.CancelWaiting(command.Scope, "KillSwitch", at);
        var pending = Variants.RequestCancelPending(command.Scope, CancelReason.KillSwitch, at);
        var liquidations = command.Mode == KillMode.BlockEntriesAndLiquidate
            ? Execution.LiquidateScope(command.Scope, ExitReason.ManualKill, command.SourceId, scheduler)
            : 0;
        _outputs.Add(new KillSwitchChanged(at, command.Scope, command.Command, command.Mode, command.Actor, command.Reason, changed ? "Killed" : "AlreadyKilled", pending, waiting, liquidations));
        Route(scheduler);
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
                    if (Variants.GetVariant(intent.Variant).Spec.EntryCondition is not Entry.NextCompletedBarEntrySpec)
                    {
                        Execution.RegisterEntry(intent, scheduler);
                    }

                    break;
                case CancelRequest cancel:
                    Execution.RequestCancel(cancel, scheduler);
                    break;
            }
        }
    }
}
