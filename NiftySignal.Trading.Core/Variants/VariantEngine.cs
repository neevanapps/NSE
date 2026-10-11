using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Variants;

public sealed class ContractChangedException(string message) : InvalidOperationException(message);

/// <summary>Fixed-contract rule: once an opportunity has selected a contract, every later step (entry, exit) must use exactly that token.</summary>
public static class ContractFixation
{
    public static void Require(string fixedToken, string requestedToken)
    {
        if (!string.Equals(fixedToken, requestedToken, StringComparison.Ordinal))
        {
            throw new ContractChangedException($"Contract is fixed to '{fixedToken}'; '{requestedToken}' was requested.");
        }
    }
}

/// <summary>
/// CP2 entry engine: hosts N independent variants over one ordered event stream (contract sections A.3 and A.4). It consumes quotes and
/// completed bars, maintains per-contract candle histories, runs the base strategy per variant, creates opportunities, selects a
/// contract inside the entry premium band, evaluates entry conditions on eligible quote updates and emits entry intents. It performs no
/// execution: an intent only becomes a fill/cancel through <see cref="ReportEntryOutcome"/>, which the CP3 execution model (or a test
/// harness) must call from inside the loop.
/// <para>
/// Determinism: variants are processed in ascending (StrategyId, VariantId) ordinal order for every event; each strategy family is evaluated exactly once per bar; ids are hashes; time comes only from
/// the events and the scheduler passed to the callbacks.
/// </para>
/// </summary>
public sealed class VariantEngine : IEngineHandler
{
    private readonly TradingConfig _config;
    private readonly ConfigHash _configHash;
    private readonly DateOnly _tradeDate;
    private readonly IReadOnlyList<OptionContract> _universe;
    private readonly QuoteBook _quotes = new();
    private readonly CandleStore _candles;
    private readonly List<VariantState> _variants = new();
    private readonly IReadOnlyList<IStrategyFamily> _families;
    private readonly string _sourceSessionId;
    private readonly List<Opportunity> _allOpportunities = new();
    private readonly List<EngineOutput> _outputs;
    private readonly CostModel? _costModel;
    private bool _entryClosed;

    public VariantEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IStrategyFamily> families,
        List<EngineOutput>? outputSink = null,
        string? sourceSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(families);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid trading config: " + string.Join(" ", errors), nameof(config));
        }

        _config = config;
        _outputs = outputSink ?? new List<EngineOutput>();
        _configHash = config.ComputeHash();
        _tradeDate = tradeDate;
        _sourceSessionId = string.IsNullOrWhiteSpace(sourceSessionId) ? FormattableString.Invariant($"session:{tradeDate:yyyy-MM-dd}") : sourceSessionId;
        Schedule = SessionSchedule.Create(config.Session, tradeDate);
        _universe = universe.OrderBy(c => c.Token, StringComparer.Ordinal).ToList();
        _candles = new CandleStore(config.Candles);
        _costModel = CostModel.IsConfigured(config.Costs, tradeDate) ? new CostModel(config.Costs, tradeDate) : null;

        var sizingErrors = config.Variants.Values
            .OrderBy(x => x.GetKey())
            .SelectMany(x => Sizer.ValidateFixedQuantities(x.GetKey().ToString(), x.Sizing, universe))
            .ToList();
        if (sizingErrors.Count > 0)
        {
            throw new ArgumentException("Invalid sizing for the contract universe: " + string.Join(" ", sizingErrors), nameof(config));
        }

        var ordinal = 0;
        foreach (var spec in config.Variants.Values.OrderBy(x => x.GetKey()))
        {
            var key = spec.GetKey();
            if (!families.TryGetValue(spec.StrategyId, out var family))
            {
                throw new ArgumentException($"Variant '{key}' references unknown strategy '{spec.StrategyId}'.", nameof(families));
            }

            if (!string.Equals(family.StrategyId, spec.StrategyId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Strategy family registered as '{spec.StrategyId}' reports StrategyId '{family.StrategyId}'.", nameof(families));
            }

            if (!family.VariantIds.Contains(spec.VariantId, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Strategy family '{spec.StrategyId}' does not declare variant '{spec.VariantId}'.", nameof(families));
            }

            _variants.Add(new VariantState(spec, ordinal++, family, config.ComputeVariantHash(key).Value, capacity: 1));
        }

        // Families in ascending StrategyId order; each is evaluated once per bar even when it serves several variants.
        _families = _variants.Select(v => v.Family).Distinct().OrderBy(f => f.StrategyId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Compatibility constructor for single-case strategies (see <see cref="LegacyBaseStrategyFamily"/>).</summary>
    public VariantEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IBaseStrategy> strategies,
        List<EngineOutput>? outputSink = null,
        string? sourceSessionId = null)
        : this(config, tradeDate, universe, LegacyStrategies.AsFamilies(config, strategies), outputSink, sourceSessionId)
    {
    }

    public SessionSchedule Schedule { get; }

    /// <summary>Optional veto on NEW entries (feed integrity, kill switch, risk profile). Checked before every entry intent; never consulted by exits.</summary>
    public IEntryGate? EntryGate { get; set; }

    public ConfigHash ConfigHash => _configHash;

    public IReadOnlyList<VariantState> Variants => _variants;

    public IReadOnlyList<EngineOutput> Outputs => _outputs;

    public IReadOnlyList<Opportunity> AllOpportunities => _allOpportunities;

    public CandleStore Candles => _candles;

    public QuoteBook Quotes => _quotes;

    /// <summary>Registers the session-boundary deadlines on the scheduler. Call once, before the first event.</summary>
    public void Start(IEngineScheduler scheduler) => Schedule.ScheduleBoundaries(scheduler);

    /// <summary>
    /// Writes every variant's episode, capacity and opportunity state, the latest quotes and the candle store. The opportunity's contract-selection
    /// evaluation list is audit-only (nothing reads it after selection) and is excluded. A strategy that owns state contributes it through
    /// <see cref="IStateContributor"/>; a stateless one adds nothing.
    /// </summary>
    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("variants");
        w.Line("entryClosed", _entryClosed);
        foreach (var v in _variants)
        {
            using var vs = w.Scope(v.Key.ToString());
            w.Line("ordinal", v.Ordinal);
            w.Line("valid", v.Valid);
            w.Line("episodeSide", v.EpisodeSide);
            w.Line("episodeId", v.EpisodeId);
            w.Line("episodeStartBarSeq", v.EpisodeStartBarSeq);
            w.Line("episodeSpent", v.EpisodeSpent);
            w.Line("episodeBlockNoted", v.EpisodeBlockNoted);
            w.Line("episodeRejection", v.EpisodeRejectionKey);
            w.Line("rejectionOrdinal", v.RejectionOrdinal);
            w.Line("active", v.Active?.Id);
            foreach (var slot in v.Slots)
            {
                w.Line($"slot[{slot.SlotNo}]", $"{slot.State}|{slot.OwnerOpportunityId ?? "~"}");
            }

            w.Line("lastDecision", v.LastDecision?.CanonicalText());
            w.Line("episodeDecision", v.EpisodeDecision?.CanonicalText());
        }

        foreach (var family in _families)
        {
            if (family is IStateContributor contributor)
            {
                using var ss = w.Scope("strategy/" + family.StrategyId);
                contributor.WriteState(w);
            }
        }

        foreach (var o in _allOpportunities)
        {
            using var os = w.Scope("opportunity/" + o.Id);
            w.Line("variant", o.Variant.ToString());
            w.Line("entryDecision", o.EntryDecision?.CanonicalText());
            w.Line("episode", o.EpisodeId);
            w.Line("episodeStart", o.EpisodeStartBarSeq);
            w.Line("side", o.Side);
            w.Line("createdAt", o.CreatedAt);
            w.Line("state", o.State);
            w.Line("contract", o.Contract is null ? null : $"{o.Contract.Token}|{o.Contract.Side}|{StateWriter.Dec(o.Contract.Strike)}|{o.Contract.Expiry:yyyy-MM-dd}|{o.Contract.LotSize}|{StateWriter.Dec(o.Contract.TickSize)}");
            w.Line("attempts", o.Attempts);
            w.Line("intentPremium", o.IntentPremium);
            w.Line("lastRejection", o.LastRejectionKey);
            w.Line("activeIntent", o.ActiveIntentId);
            w.Line("cancelRequested", o.CancelRequested);
            w.Line("cancelReason", o.CancelReason);
            w.Line("terminalReason", o.TerminalReason);
            w.Line("selectionAttempts", o.SelectionAttempts);
            w.Line("evaluations", o.Evaluations);
            w.Line("lastEvaluationKey", o.LastEvaluationKey);
            foreach (var (key, n) in o.NoteCounts.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                w.Line($"note[{key}]", $"{n.Count}|{n.First.UtcTicks}|{n.Last.UtcTicks}");
            }
        }

        _quotes.WriteState(w);
        _candles.WriteState(w);
    }

    public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler)
    {
        switch (engineEvent)
        {
            case QuoteEvent quote:
                OnQuote(quote);
                break;
            case BarCompletedEvent bar:
                OnBar(bar, scheduler);
                break;
        }
    }

    public void OnDeadline(ScheduledDeadline deadline, IEngineScheduler scheduler)
    {
        if (deadline.Class != DeadlineClass.SessionBoundary || !SessionSchedule.TryParseBoundaryKey(deadline.Key, out var kind))
        {
            return;
        }

        if (kind == SessionBoundaryKind.EntryClose)
        {
            OnEntryClose(deadline.Time);
        }
    }

    // ------------------------------------------------------------------ events

    private void OnQuote(QuoteEvent quote)
    {
        _quotes.Update(quote);
        if (quote.Kind != EventKind.OptionQuote)
        {
            return;
        }

        _candles.Observe(quote);
        var snapshot = _quotes.Get(quote.Token)!;
        foreach (var v in _variants)
        {
            if (v.Active is { State: OpportunityState.Waiting, Contract: { } contract } opportunity
                && string.Equals(contract.Token, quote.Token, StringComparison.Ordinal))
            {
                EvaluateEntry(v, opportunity, snapshot, quote.AvailableAt, "EntryQuote", quote.SourceId);
            }
        }
    }

    /// <summary>
    /// Raised for a family Exit decision of a variant: the execution model closes that variant's own open position (if any) through the normal exit path.
    /// Set by the trading engine; null in entry-only harnesses (the decision is then only recorded).
    /// </summary>
    public Action<VariantKey, IEngineScheduler>? StrategyExitRequested { get; set; }

    private void OnBar(BarCompletedEvent bar, IEngineScheduler scheduler)
    {
        // Publish first: candles up to this bar are visible to everything that runs after this event, and to nothing that ran before it
        // (quotes of the same availability group precede the bar event in the total order).
        _candles.PublishBar(bar.BarSeq, bar.AvailableAt);

        // Every family sees every bar exactly once and answers for all of its variants. A decision for an id the family does not declare, or two
        // decisions for one variant, is a defect of the family and fails loudly rather than being guessed around.
        var decisions = new Dictionary<VariantKey, VariantDecision>();
        foreach (var family in _families)
        {
            var answer = family.OnBar(bar) ?? FamilyDecisions.None;
            foreach (var d in answer.Decisions)
            {
                var errors = d.Validate();
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException($"Strategy '{family.StrategyId}' returned an invalid decision: {string.Join(" ", errors)}");
                }

                if (!family.VariantIds.Contains(d.VariantId, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Strategy '{family.StrategyId}' returned a decision for undeclared variant '{d.VariantId}'.");
                }

                if (!decisions.TryAdd(new VariantKey(family.StrategyId, d.VariantId), d))
                {
                    throw new InvalidOperationException($"Strategy '{family.StrategyId}' returned two decisions for variant '{d.VariantId}' on bar {bar.BarSeq}.");
                }
            }
        }

        foreach (var v in _variants)
        {
            var decision = decisions.TryGetValue(v.Key, out var d) ? d : VariantDecision.Wait(v.VariantId, "NoDecision");
            OnBarForVariant(v, bar, decision, scheduler);
        }
    }

    private void OnBarForVariant(VariantState v, BarCompletedEvent bar, VariantDecision decision, IEngineScheduler scheduler)
    {
        var now = bar.AvailableAt;
        var attributed = new AttributedDecision(v.Key, decision.Action, decision.Direction, decision.ReasonCode, decision.Detail, _sourceSessionId, bar.BarSeq, bar.AvailableAt);
        v.LastDecision = attributed;

        // Long -> Call and Short -> Put is the engine's mapping, below the strategy layer.
        var wantsEntry = decision.Action == StrategyAction.Enter;
        var validity = new StrategyValidity(wantsEntry, decision.Direction == StrategyDirection.Short ? OptionSide.Put : OptionSide.Call, decision.ReasonCode);
        if (validity.IsValid && bar.Info is { Ready: false })
        {
            // Trading eligibility = the strategy's validity AND the adaptive readiness evidence carried by the bar (frozen session, enough
            // consecutive valid bars, signal start reached). The strategy still sees every bar (its own warm-up state is unaffected).
            validity = new StrategyValidity(false, validity.Side, "ReadinessNotMet");
        }

        if (decision.Action == StrategyAction.Exit)
        {
            StrategyExitRequested?.Invoke(v.Key, scheduler);
        }

        if (!validity.IsValid)
        {
            if (v.Active is { } ending)
            {
                Invalidate(v, ending, now, "StrategyInvalid");
            }

            v.Valid = false;
            v.EpisodeId = null;
            v.EpisodeSpent = false;
            v.EpisodeBlockNoted = false;
            return;
        }

        if (!v.Valid || v.EpisodeSide != validity.Side)
        {
            // A new base-strategy validity episode (invalid -> valid, or valid on the other side).
            if (v.Active is { } previous)
            {
                Invalidate(v, previous, now, "SideChanged");
            }

            v.Valid = true;
            v.EpisodeSide = validity.Side;
            v.EpisodeId = FormattableString.Invariant($"b{bar.BarSeq}");
            v.EpisodeStartBarSeq = bar.BarSeq;
            v.EpisodeSpent = false;
            v.EpisodeBlockNoted = false;
            v.EpisodeRejectionKey = null;
            v.EpisodeDecision = attributed;
        }

        if (v.EpisodeSpent)
        {
            return;
        }

        if (_entryClosed || !Schedule.IsEntryEligible(now))
        {
            if (v.Active is null && !v.EpisodeBlockNoted)
            {
                v.EpisodeBlockNoted = true;
                Audit(now, v, null, "OpportunityCreate", "SessionNotEligible", "No opportunity: outside the entry window.");
                Reject(v, null, now, new[] { WindowReason(now) }, "Window", "Bar", bar.BarSeq);
            }

            return;
        }

        if (v.Active is null)
        {
            var id = EngineIds.Opportunity(v.Key, new ConfigHash(v.VariantHash), _tradeDate, v.EpisodeId!);
            var opportunity = new Opportunity(id, v.Key, v.EpisodeId!, v.EpisodeStartBarSeq, v.EpisodeSide, now, v.EpisodeDecision);
            v.Active = opportunity;
            _allOpportunities.Add(opportunity);
            Transition(v, opportunity, OpportunityState.AwaitingContract, now, "EpisodeValid");
        }

        if (v.Active.State == OpportunityState.AwaitingContract)
        {
            TrySelectContract(v, v.Active, now, bar.BarSeq);
        }
    }

    // ------------------------------------------------------------------ selection and entry

    /// <summary>Contract selection runs at opportunity creation and again on each later completed bar while the opportunity is still awaiting a contract; never on quotes.</summary>
    private void TrySelectContract(VariantState v, Opportunity opportunity, DateTimeOffset now, long barSeq)
    {
        opportunity.SelectionAttempts++;
        var candidates = _universe.Select(c => new ContractCandidate(c, _quotes.Get(c.Token))).ToList();
        var result = ContractSelector.Select(candidates, opportunity.Side, _tradeDate, now, _config.Selection);
        if (result.Outcome == ContractSelectionOutcome.NoEligibleContract)
        {
            var reasons = string.Join(
                ",",
                result.Evaluations.GroupBy(e => e.Reason).OrderBy(g => (int)g.Key).Select(g => FormattableString.Invariant($"{g.Key}={g.Count()}")));
            Audit(now, v, opportunity.Id, "ContractSelection", "NoEligibleContract", FormattableString.Invariant($"attempt={opportunity.SelectionAttempts} {reasons}"));
            Reject(v, opportunity, now, new[] { RejectionReason.NoEligibleContract }, "Contract", "Bar", barSeq);
            return;
        }

        if (opportunity.Contract is not null)
        {
            ContractFixation.Require(opportunity.Contract.Token, result.Selected!.Token);
        }

        opportunity.Contract = result.Selected;
        opportunity.Selection = result;
        Audit(
            now,
            v,
            opportunity.Id,
            "ContractSelection",
            "Selected",
            FormattableString.Invariant($"token={result.Selected!.Token} strike={result.Selected.Strike} expiry={result.Expiry:yyyy-MM-dd} ask={result.SelectedQuote!.Ask} attempt={opportunity.SelectionAttempts}"));
        Transition(v, opportunity, OpportunityState.Waiting, now, "ContractSelected");
        if (v.Spec.EntryCondition is ImmediateEntrySpec)
        {
            EvaluateEntry(v, opportunity, result.SelectedQuote, now, "EntryBar", barSeq);
        }
    }

    /// <summary>
    /// Every entry intent passes through here: session eligibility, strategy validity, fresh executable ask, premium band, entry condition,
    /// attempt limit and capacity are all re-checked at this instant, in this order. A contract that has left the band is simply not
    /// entered (the opportunity keeps its contract and may qualify again); exits are never subject to the band.
    /// </summary>
    private void EvaluateEntry(VariantState v, Opportunity opportunity, QuoteSnapshot quote, DateTimeOffset now, string triggerKind, long triggerSeq)
    {
        if (opportunity.State != OpportunityState.Waiting)
        {
            return;
        }

        opportunity.Evaluations++;
        var selection = _config.Selection;
        if (_entryClosed)
        {
            Terminal(v, opportunity, OpportunityState.Expired, now, "EntryClose");
            return;
        }

        if (!Schedule.IsEntryEligible(now))
        {
            Note(v, opportunity, now, "Blocked:SessionNotEligible", "outside the entry window");
            Reject(v, opportunity, now, new[] { WindowReason(now) }, "Window", triggerKind, triggerSeq);
            return;
        }

        if (!v.Valid || v.EpisodeId != opportunity.EpisodeId)
        {
            Note(v, opportunity, now, "Blocked:StrategyNotValid", "base strategy not valid for this episode");
            return;
        }

        var gateReasons = EntryGate?.Evaluate(v.Key, now, EstimatePremium(v, opportunity, quote), opportunity.Id) ?? Array.Empty<RejectionReason>();
        if (gateReasons.Count > 0)
        {
            Note(v, opportunity, now, "Blocked:" + RejectionReasons.NoteName(gateReasons[0]), "new entries are blocked for this scope");
            Reject(v, opportunity, now, gateReasons, "Gate", triggerKind, triggerSeq);
            return;
        }

        var age = quote.Age(now);
        if (age < TimeSpan.Zero)
        {
            Note(v, opportunity, now, "Blocked:QuoteNotCausal", "quote is after the decision time");
            Reject(v, opportunity, now, new[] { RejectionReason.NoExecutableAsk }, "Quote", triggerKind, triggerSeq);
            return;
        }

        if (age > selection.MaxQuoteAge)
        {
            Note(v, opportunity, now, "Blocked:QuoteStale", FormattableString.Invariant($"age={age.TotalMilliseconds}ms"));
            Reject(v, opportunity, now, new[] { RejectionReason.NoExecutableAsk }, "Quote", triggerKind, triggerSeq);
            return;
        }

        if (!quote.IsExecutable)
        {
            Note(v, opportunity, now, "Blocked:QuoteNotExecutable", "quote is not two-sided");
            Reject(v, opportunity, now, new[] { RejectionReason.NoExecutableAsk }, "Quote", triggerKind, triggerSeq);
            return;
        }

        var ask = quote.Ask!.Value;
        if (ask < selection.MinimumEntryPremium || ask > selection.MaximumEntryPremium)
        {
            Note(
                v,
                opportunity,
                now,
                ask < selection.MinimumEntryPremium ? "Blocked:AskBelowBand" : "Blocked:AskAboveBand",
                FormattableString.Invariant($"ask={ask} band=[{selection.MinimumEntryPremium},{selection.MaximumEntryPremium}] (contract kept)"));
            Reject(v, opportunity, now, new[] { RejectionReason.PremiumOutsideBand }, "Band", triggerKind, triggerSeq);
            return;
        }

        var history = _candles.ViewFor(opportunity.Contract!.Token);
        var condition = EntryConditions.Evaluate(v.Spec.EntryCondition, new ConditionContext(opportunity.Contract.Token, quote, history));
        if (condition.Outcome != ConditionOutcome.Satisfied)
        {
            Note(
                v,
                opportunity,
                now,
                (condition.Outcome == ConditionOutcome.NotEvaluable ? "NotEvaluable:" : "NotSatisfied:") + condition.Reason,
                FormattableString.Invariant($"ask={condition.Ask} average={condition.Average} n={condition.N}"));
            return;
        }

        if (v.Spec.MaxAttemptsPerOpportunity is { } maxAttempts && opportunity.Attempts >= maxAttempts)
        {
            Terminal(v, opportunity, OpportunityState.Expired, now, "MaxAttempts");
            return;
        }

        var sizing = Sizer.Size(v.Spec.Sizing, opportunity.Contract.LotSize, ask, _costModel);
        if (sizing.Status == SizingStatus.InvalidQuantity)
        {
            Audit(now, v, opportunity.Id, "Sizing", "InvalidQuantity", sizing.Detail);
            Terminal(v, opportunity, OpportunityState.Rejected, now, "InvalidQuantity");
            return;
        }

        if (sizing.Status == SizingStatus.Unaffordable)
        {
            Note(v, opportunity, now, "Blocked:InsufficientBudget", sizing.Detail);
            return;
        }

        var slot = v.Slots.FirstOrDefault(s => s.State == SlotState.Free);
        if (slot is null)
        {
            Reject(v, opportunity, now, new[] { RejectionReason.VariantCapacity }, "Capacity", triggerKind, triggerSeq);
            if (v.Spec.ResolveCapacityPolicy() == CapacityBlockedPolicy.Reject)
            {
                Terminal(v, opportunity, OpportunityState.Rejected, now, "CapacityBusy");
            }
            else
            {
                Note(v, opportunity, now, "Blocked:CapacityBusy", "slot occupied; waiting");
            }

            return;
        }

        IssueIntent(v, opportunity, slot, quote, condition, sizing, now, triggerKind, triggerSeq);
    }

    private void IssueIntent(
        VariantState v,
        Opportunity opportunity,
        CapacitySlot slot,
        QuoteSnapshot quote,
        ConditionResult condition,
        SizingResult sizing,
        DateTimeOffset now,
        string triggerKind,
        long triggerSeq)
    {
        slot.State = SlotState.Reserved;
        slot.OwnerOpportunityId = opportunity.Id;
        opportunity.Attempts++;
        // The variant hash (spec + shared policy), not the whole-config hash: adding or removing another variant must not re-identify this one's decisions.
        var decisionId = EngineIds.Decision(new ConfigHash(v.VariantHash), v.Key, _tradeDate, triggerKind, triggerSeq, opportunity.Attempts);
        var intentId = EngineIds.Order(decisionId, "entry", opportunity.Attempts);
        var contract = opportunity.Contract!;
        opportunity.ActiveIntentId = intentId;
        opportunity.LastEvaluationKey = "Issued";
        opportunity.LastRejectionKey = null;
        var limit = quote.Ask!.Value + _config.Execution.EntrySlippage;
        opportunity.IntentPremium = limit * sizing.Quantity;
        _outputs.Add(new EntryIntent(
            now,
            v.Key,
            intentId,
            decisionId,
            opportunity.Id,
            opportunity.Attempts,
            slot.SlotNo,
            contract,
            sizing.Quantity,
            sizing.Lots,
            contract.LotSize,
            LimitPrice: limit,
            QualifyingAsk: quote.Ask.Value,
            _config.Selection.MinimumEntryPremium,
            _config.Selection.MaximumEntryPremium,
            quote.AvailableAt,
            quote.SourceId,
            v.Spec.EntryCondition.Kind,
            condition.Average,
            condition.N,
            condition.PriceSource));
        Audit(now, v, opportunity.Id, "EntryIntent", "Issued", FormattableString.Invariant($"attempt={opportunity.Attempts} token={contract.Token} ask={quote.Ask} slot={slot.SlotNo} condition={condition.Reason}"));
        Transition(v, opportunity, OpportunityState.OrderPending, now, "IntentIssued");
    }

    // ------------------------------------------------------------------ feedback from execution (CP3) and position lifecycle

    /// <summary>
    /// Reports the terminal result of an outstanding entry intent. Must be called from inside the loop (a deadline or event callback) so
    /// that <c>scheduler.Now</c> is the decision time of the resolution. A fill (full or partial) consumes the opportunity and
    /// keeps the slot occupied; no fill frees the slot and the opportunity either re-arms (strategy still valid, in session, attempts
    /// left, previous attempt terminal) or ends.
    /// </summary>
    public void ReportEntryOutcome(EntryOutcome outcome, IEngineScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(scheduler);
        var now = scheduler.Now;
        var v = FindVariant(outcome.Variant);
        if (v.Active is not { } opportunity
            || opportunity.Id != outcome.OpportunityId
            || opportunity.State != OpportunityState.OrderPending
            || opportunity.ActiveIntentId != outcome.IntentId)
        {
            throw new InvalidOperationException($"No outstanding entry intent {outcome.IntentId} for opportunity {outcome.OpportunityId} of variant {outcome.Variant}.");
        }

        var slot = v.Slots.First(s => s.OwnerOpportunityId == opportunity.Id);
        opportunity.ActiveIntentId = null;
        opportunity.IntentPremium = 0m;
        if (outcome.Kind is EntryOutcomeKind.Filled or EntryOutcomeKind.PartiallyFilled)
        {
            slot.State = SlotState.Occupied;
            Terminal(v, opportunity, OpportunityState.Consumed, now, outcome.Kind.ToString());
            return;
        }

        slot.State = SlotState.Free;
        slot.OwnerOpportunityId = null;
        if (opportunity.CancelRequested)
        {
            switch (opportunity.CancelReason)
            {
                case CancelReason.EntryClose:
                    Terminal(v, opportunity, OpportunityState.Expired, now, "EntryClose");
                    return;
                case CancelReason.KillSwitch:
                    Terminal(v, opportunity, OpportunityState.Cancelled, now, "KillSwitch");
                    return;
                case CancelReason.IntegrityDegraded:
                    // A feed fault cancels the attempt but not the idea: fall through to the re-arm checks; entries stay blocked until Healthy.
                    opportunity.CancelRequested = false;
                    opportunity.CancelReason = null;
                    break;
                default:
                    Terminal(v, opportunity, OpportunityState.Cancelled, now, "StrategyInvalidated");
                    return;
            }
        }

        if (_entryClosed || !Schedule.IsEntryEligible(now))
        {
            Terminal(v, opportunity, OpportunityState.Expired, now, "EntryClose");
            return;
        }

        if (!v.Valid || v.EpisodeId != opportunity.EpisodeId)
        {
            Terminal(v, opportunity, OpportunityState.Cancelled, now, "StrategyInvalidated");
            return;
        }

        if (v.Spec.MaxAttemptsPerOpportunity is { } maxAttempts && opportunity.Attempts >= maxAttempts)
        {
            Terminal(v, opportunity, OpportunityState.Expired, now, "MaxAttempts");
            return;
        }

        opportunity.LastEvaluationKey = null;
        Transition(v, opportunity, OpportunityState.Waiting, now, FormattableString.Invariant($"Rearmed:{outcome.Kind}"));
    }

    /// <summary>Frees the slot of a position that has been fully closed (CP3 reports this). The episode stays spent: a new validity episode is required for another opportunity.</summary>
    public void ReportPositionClosed(VariantKey variant, string opportunityId, IEngineScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        var v = FindVariant(variant);
        var slot = v.Slots.FirstOrDefault(s => s.State == SlotState.Occupied && s.OwnerOpportunityId == opportunityId)
            ?? throw new InvalidOperationException($"Variant {variant} has no occupied slot owned by {opportunityId}.");
        slot.State = SlotState.Free;
        slot.OwnerOpportunityId = null;
        Audit(scheduler.Now, v, opportunityId, "Position", "Closed", FormattableString.Invariant($"slot={slot.SlotNo} freed"));
    }

    /// <summary>True while the base strategy is valid for the episode that owns this opportunity (used by execution to revalidate a pending entry at maturity).</summary>
    public bool IsStrategyValidFor(VariantKey variant, string opportunityId)
    {
        var v = FindVariant(variant);
        return v.Valid && v.Active is { } active && active.Id == opportunityId && active.EpisodeId == v.EpisodeId;
    }

    /// <summary>The contract token fixed for an opportunity, or null when it has none yet.</summary>
    public string? FixedContractToken(string opportunityId)
        => _allOpportunities.FirstOrDefault(o => o.Id == opportunityId)?.Contract?.Token;

    public VariantState GetVariant(VariantKey variant) => FindVariant(variant);

    public string VariantHashOf(VariantKey variant) => FindVariant(variant).VariantHash;

    /// <summary>Exits must use the contract fixed by the opportunity; throws <see cref="ContractChangedException"/> otherwise. Premium band limits do not apply to exits.</summary>
    public void RequireFixedContract(string opportunityId, string token)
    {
        var opportunity = _allOpportunities.FirstOrDefault(o => o.Id == opportunityId)
            ?? throw new KeyNotFoundException($"Unknown opportunity {opportunityId}.");
        ContractFixation.Require(opportunity.Contract?.Token ?? throw new InvalidOperationException("Opportunity has no contract yet."), token);
    }

    // ------------------------------------------------------------------ invalidation and session

    private void Invalidate(VariantState v, Opportunity opportunity, DateTimeOffset now, string reason)
    {
        switch (opportunity.State)
        {
            case OpportunityState.AwaitingContract:
            case OpportunityState.Waiting:
                Terminal(v, opportunity, OpportunityState.Cancelled, now, "StrategyInvalidated:" + reason);
                break;
            case OpportunityState.OrderPending:
                if (!opportunity.CancelRequested)
                {
                    opportunity.CancelRequested = true;
                    opportunity.CancelReason = CancelReason.StrategyInvalidated;
                    _outputs.Add(new CancelRequest(now, v.Key, opportunity.ActiveIntentId!, opportunity.Id, CancelReason.StrategyInvalidated));
                    Audit(now, v, opportunity.Id, "EntryIntent", "CancelRequested", "StrategyInvalidated:" + reason + "; reservation retained until the outcome is reported");
                }

                break;
        }
    }

    private void OnEntryClose(DateTimeOffset at)
    {
        _entryClosed = true;
        foreach (var v in _variants)
        {
            if (v.Active is not { } opportunity)
            {
                continue;
            }

            switch (opportunity.State)
            {
                case OpportunityState.AwaitingContract:
                case OpportunityState.Waiting:
                    Terminal(v, opportunity, OpportunityState.Expired, at, "EntryClose");
                    break;
                case OpportunityState.OrderPending:
                    if (!opportunity.CancelRequested)
                    {
                        opportunity.CancelRequested = true;
                        opportunity.CancelReason = CancelReason.EntryClose;
                        _outputs.Add(new CancelRequest(at, v.Key, opportunity.ActiveIntentId!, opportunity.Id, CancelReason.EntryClose));
                        Audit(at, v, opportunity.Id, "EntryIntent", "CancelRequested", "EntryClose; reservation retained until the outcome is reported");
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------------ safety actions (integrity / kill switch)

    private IEnumerable<VariantState> InScope(string scope) => _variants.Where(x => Integrity.KillScopes.Covers(scope, x.Key));

    /// <summary>Requests cancellation of the pending entry attempts (OrderPending) of the scope. Reservations stay until the outcome is reported. Returns how many requests were issued.</summary>
    public int RequestCancelPending(string scope, CancelReason reason, DateTimeOffset at)
    {
        var count = 0;
        foreach (var v in InScope(scope))
        {
            if (v.Active is { State: OpportunityState.OrderPending, CancelRequested: false } opportunity)
            {
                opportunity.CancelRequested = true;
                opportunity.CancelReason = reason;
                _outputs.Add(new CancelRequest(at, v.Key, opportunity.ActiveIntentId!, opportunity.Id, reason));
                Audit(at, v, opportunity.Id, "EntryIntent", "CancelRequested", reason + "; reservation retained until the outcome is reported");
                count++;
            }
        }

        return count;
    }

    /// <summary>Ends the not-yet-ordered opportunities (AwaitingContract / Waiting) of the scope as Cancelled with the given reason. Returns how many ended.</summary>
    public int CancelWaiting(string scope, string reason, DateTimeOffset at)
    {
        var count = 0;
        foreach (var v in InScope(scope).ToList())
        {
            if (v.Active is { State: OpportunityState.AwaitingContract or OpportunityState.Waiting } opportunity)
            {
                Terminal(v, opportunity, OpportunityState.Cancelled, at, reason);
                count++;
            }
        }

        return count;
    }

    /// <summary>Emits the pending evaluation-count summaries of every still-active opportunity (call at session end).</summary>
    public void FlushAllSummaries(DateTimeOffset at)
    {
        foreach (var v in _variants)
        {
            if (v.Active is { } opportunity)
            {
                FlushSummaries(v, opportunity, at);
            }
        }
    }

    private void FlushSummaries(VariantState v, Opportunity opportunity, DateTimeOffset at)
    {
        foreach (var (reason, info) in opportunity.NoteCounts.OrderBy(x => x.Key, StringComparer.Ordinal).ToList())
        {
            _outputs.Add(new EvaluationSummary(at, v.Key, opportunity.Id, "Opportunity", opportunity.Id, reason, info.Count, info.First, info.Last));
        }

        opportunity.NoteCounts.Clear();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Premium (limit x quantity) of every entry intent currently outstanding (opportunities in OrderPending), optionally excluding one opportunity. Counts against the shared capital.</summary>
    public decimal OutstandingIntentPremium(string? excludeOpportunityId = null)
        => _allOpportunities.Where(o => o.State == OpportunityState.OrderPending && o.Id != excludeOpportunityId).Sum(o => o.IntentPremium);

    private decimal EstimatePremium(VariantState v, Opportunity opportunity, QuoteSnapshot quote)
    {
        if (!quote.IsExecutable || opportunity.Contract is null)
        {
            return 0m;
        }

        var ask = quote.Ask!.Value;
        var sizing = Sizer.Size(v.Spec.Sizing, opportunity.Contract.LotSize, ask, _costModel);
        return sizing.Status == SizingStatus.InvalidQuantity ? 0m : sizing.Quantity * (ask + _config.Execution.EntrySlippage);
    }

    private RejectionReason WindowReason(DateTimeOffset now) => now < Schedule.EntryOpenUtc ? RejectionReason.BeforeEntryWindow : RejectionReason.EntryWindowClosed;

    /// <summary>
    /// Records, once per distinct reason set, that an Enter decision was blocked: typed reasons in stable order, with the decision id and the finalized source bar
    /// (session id and BarSeq) the block belongs to. <paramref name="opportunity"/> is null when no opportunity could be created.
    /// </summary>
    private void Reject(VariantState v, Opportunity? opportunity, DateTimeOffset now, IEnumerable<RejectionReason> reasons, string stage, string triggerKind, long triggerSeq)
    {
        var text = RejectionReasons.Canonical(reasons);
        var key = stage + ":" + text;
        if (opportunity is null)
        {
            if (v.EpisodeRejectionKey == key)
            {
                return;
            }

            v.EpisodeRejectionKey = key;
        }
        else
        {
            if (opportunity.LastRejectionKey == key)
            {
                return;
            }

            opportunity.LastRejectionKey = key;
        }

        EmitRejection(v, opportunity?.Id, now, stage, text, triggerKind, triggerSeq);
    }

    private void EmitRejection(VariantState v, string? opportunityId, DateTimeOffset now, string stage, string reasons, string triggerKind, long triggerSeq)
    {
        v.RejectionOrdinal++;
        var decisionId = EngineIds.Decision(new ConfigHash(v.VariantHash), v.Key, _tradeDate, "Reject:" + triggerKind, triggerSeq, v.RejectionOrdinal);
        var decision = v.LastDecision;
        _outputs.Add(new EntryRejected(now, v.Key, opportunityId, decisionId, decision?.SourceSessionId ?? _sourceSessionId, decision?.BarSeq ?? triggerSeq, stage, reasons));
    }

    /// <summary>The execution model reports an entry that was cancelled at fill time by the gate (trade limit, daily loss, capital, kill, integrity ...).</summary>
    public void ReportFillTimeRejection(VariantKey variant, string opportunityId, IReadOnlyList<RejectionReason> reasons, DateTimeOffset at, long triggerSeq)
    {
        var v = FindVariant(variant);
        var opportunity = _allOpportunities.FirstOrDefault(o => o.Id == opportunityId);
        Reject(v, opportunity, at, reasons, "Fill", "Fill", triggerSeq);
    }

    private VariantState FindVariant(VariantKey variant)
        => _variants.FirstOrDefault(x => x.Key == variant)
           ?? throw new KeyNotFoundException($"Unknown variant '{variant}'.");

    private void Transition(VariantState v, Opportunity opportunity, OpportunityState to, DateTimeOffset at, string reason)
    {
        var from = opportunity.State;
        opportunity.State = to;
        _outputs.Add(new OpportunityTransition(at, v.Key, opportunity.Id, from, to, reason));
    }

    private void Terminal(VariantState v, Opportunity opportunity, OpportunityState to, DateTimeOffset at, string reason)
    {
        opportunity.TerminalReason = reason;
        Transition(v, opportunity, to, at, reason);
        FlushSummaries(v, opportunity, at);
        v.Active = null;
        v.History.Add(opportunity);
        if (v.EpisodeId == opportunity.EpisodeId)
        {
            v.EpisodeSpent = true;
        }
    }

    /// <summary>Audit only when the evaluation outcome of an opportunity changes, so per-quote evaluation does not flood the trail.</summary>
    private void Note(VariantState v, Opportunity opportunity, DateTimeOffset at, string key, string detail)
    {
        opportunity.NoteCounts[key] = opportunity.NoteCounts.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.First, at) : (1, at, at);
        if (string.Equals(opportunity.LastEvaluationKey, key, StringComparison.Ordinal))
        {
            return;
        }

        opportunity.LastEvaluationKey = key;
        Audit(at, v, opportunity.Id, "EntryEvaluation", key, detail);
    }

    private void Audit(DateTimeOffset at, VariantState v, string? opportunityId, string stage, string outcome, string detail)
        => _outputs.Add(new AuditRecord(at, v.Key, opportunityId, stage, outcome, detail));
}
