using NiftySignal.Trading.Core.Candles;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Entry;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Selection;
using NiftySignal.Trading.Core.Sizing;
using NiftySignal.Trading.Core.Session;

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
/// Determinism: variants are processed in ascending VariantId (ordinal) order for every event; ids are hashes; time comes only from
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
    private readonly List<Opportunity> _allOpportunities = new();
    private readonly List<EngineOutput> _outputs;
    private readonly CostModel? _costModel;
    private bool _entryClosed;

    public VariantEngine(
        TradingConfig config,
        DateOnly tradeDate,
        IReadOnlyList<OptionContract> universe,
        IReadOnlyDictionary<string, IBaseStrategy> strategies,
        List<EngineOutput>? outputSink = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(universe);
        ArgumentNullException.ThrowIfNull(strategies);
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid trading config: " + string.Join(" ", errors), nameof(config));
        }

        _config = config;
        _outputs = outputSink ?? new List<EngineOutput>();
        _configHash = config.ComputeHash();
        _tradeDate = tradeDate;
        Schedule = SessionSchedule.Create(config.Session, tradeDate);
        _universe = universe.OrderBy(c => c.Token, StringComparer.Ordinal).ToList();
        _candles = new CandleStore(config.Candles);
        _costModel = CostModel.IsConfigured(config.Costs, tradeDate) ? new CostModel(config.Costs, tradeDate) : null;

        var sizingErrors = config.Variants.Values
            .OrderBy(x => x.VariantId, StringComparer.Ordinal)
            .SelectMany(x => Sizer.ValidateFixedQuantities(x.VariantId, x.Sizing, universe))
            .ToList();
        if (sizingErrors.Count > 0)
        {
            throw new ArgumentException("Invalid sizing for the contract universe: " + string.Join(" ", sizingErrors), nameof(config));
        }

        var ordinal = 0;
        foreach (var id in config.Variants.Keys.Order(StringComparer.Ordinal))
        {
            var spec = config.Variants[id];
            if (!strategies.TryGetValue(spec.StrategyId, out var strategy))
            {
                throw new ArgumentException($"Variant '{id}' references unknown strategy '{spec.StrategyId}'.", nameof(strategies));
            }

            _variants.Add(new VariantState(spec, ordinal++, strategy, config.ComputeVariantHash(id).Value, capacity: 1));
        }
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

    public void OnEvent(EngineEvent engineEvent, IEngineScheduler scheduler)
    {
        switch (engineEvent)
        {
            case QuoteEvent quote:
                OnQuote(quote);
                break;
            case BarCompletedEvent bar:
                OnBar(bar);
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

    private void OnBar(BarCompletedEvent bar)
    {
        // Publish first: candles up to this bar are visible to everything that runs after this event, and to nothing that ran before it
        // (quotes of the same availability group precede the bar event in the total order).
        _candles.PublishBar(bar.BarSeq, bar.AvailableAt);
        foreach (var v in _variants)
        {
            OnBarForVariant(v, bar);
        }
    }

    private void OnBarForVariant(VariantState v, BarCompletedEvent bar)
    {
        var now = bar.AvailableAt;
        var validity = v.Strategy.OnBar(bar);
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
            }

            return;
        }

        if (v.Active is null)
        {
            var id = EngineIds.Opportunity(v.VariantId, new ConfigHash(v.VariantHash), _tradeDate, v.EpisodeId!);
            var opportunity = new Opportunity(id, v.VariantId, v.EpisodeId!, v.EpisodeStartBarSeq, v.EpisodeSide, now);
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
            return;
        }

        if (!v.Valid || v.EpisodeId != opportunity.EpisodeId)
        {
            Note(v, opportunity, now, "Blocked:StrategyNotValid", "base strategy not valid for this episode");
            return;
        }

        var gateBlock = EntryGate?.BlockReason(v.VariantId, now);
        if (gateBlock is not null)
        {
            Note(v, opportunity, now, "Blocked:" + gateBlock, "new entries are blocked for this scope");
            return;
        }

        var age = quote.Age(now);
        if (age < TimeSpan.Zero)
        {
            Note(v, opportunity, now, "Blocked:QuoteNotCausal", "quote is after the decision time");
            return;
        }

        if (age > selection.MaxQuoteAge)
        {
            Note(v, opportunity, now, "Blocked:QuoteStale", FormattableString.Invariant($"age={age.TotalMilliseconds}ms"));
            return;
        }

        if (!quote.IsExecutable)
        {
            Note(v, opportunity, now, "Blocked:QuoteNotExecutable", "quote is not two-sided");
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
        var decisionId = EngineIds.Decision(new ConfigHash(v.VariantHash), v.VariantId, v.Spec.StrategyId, _tradeDate, triggerKind, triggerSeq, opportunity.Attempts);
        var intentId = EngineIds.Order(decisionId, "entry", opportunity.Attempts);
        var contract = opportunity.Contract!;
        opportunity.ActiveIntentId = intentId;
        opportunity.LastEvaluationKey = "Issued";
        _outputs.Add(new EntryIntent(
            now,
            v.VariantId,
            intentId,
            decisionId,
            opportunity.Id,
            opportunity.Attempts,
            slot.SlotNo,
            contract,
            sizing.Quantity,
            sizing.Lots,
            contract.LotSize,
            LimitPrice: quote.Ask!.Value,
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
        var v = FindVariant(outcome.VariantId);
        if (v.Active is not { } opportunity
            || opportunity.Id != outcome.OpportunityId
            || opportunity.State != OpportunityState.OrderPending
            || opportunity.ActiveIntentId != outcome.IntentId)
        {
            throw new InvalidOperationException($"No outstanding entry intent {outcome.IntentId} for opportunity {outcome.OpportunityId} of variant {outcome.VariantId}.");
        }

        var slot = v.Slots.First(s => s.OwnerOpportunityId == opportunity.Id);
        opportunity.ActiveIntentId = null;
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
    public void ReportPositionClosed(string variantId, string opportunityId, IEngineScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        var v = FindVariant(variantId);
        var slot = v.Slots.FirstOrDefault(s => s.State == SlotState.Occupied && s.OwnerOpportunityId == opportunityId)
            ?? throw new InvalidOperationException($"Variant {variantId} has no occupied slot owned by {opportunityId}.");
        slot.State = SlotState.Free;
        slot.OwnerOpportunityId = null;
        Audit(scheduler.Now, v, opportunityId, "Position", "Closed", FormattableString.Invariant($"slot={slot.SlotNo} freed"));
    }

    /// <summary>True while the base strategy is valid for the episode that owns this opportunity (used by execution to revalidate a pending entry at maturity).</summary>
    public bool IsStrategyValidFor(string variantId, string opportunityId)
    {
        var v = FindVariant(variantId);
        return v.Valid && v.Active is { } active && active.Id == opportunityId && active.EpisodeId == v.EpisodeId;
    }

    /// <summary>The contract token fixed for an opportunity, or null when it has none yet.</summary>
    public string? FixedContractToken(string opportunityId)
        => _allOpportunities.FirstOrDefault(o => o.Id == opportunityId)?.Contract?.Token;

    public VariantState GetVariant(string variantId) => FindVariant(variantId);

    public string VariantHashOf(string variantId) => FindVariant(variantId).VariantHash;

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
                    _outputs.Add(new CancelRequest(now, v.VariantId, opportunity.ActiveIntentId!, opportunity.Id, CancelReason.StrategyInvalidated));
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
                        _outputs.Add(new CancelRequest(at, v.VariantId, opportunity.ActiveIntentId!, opportunity.Id, CancelReason.EntryClose));
                        Audit(at, v, opportunity.Id, "EntryIntent", "CancelRequested", "EntryClose; reservation retained until the outcome is reported");
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------------ safety actions (integrity / kill switch)

    private IEnumerable<VariantState> InScope(string scope)
        => scope == "*" ? _variants : _variants.Where(x => string.Equals(x.VariantId, scope, StringComparison.Ordinal));

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
                _outputs.Add(new CancelRequest(at, v.VariantId, opportunity.ActiveIntentId!, opportunity.Id, reason));
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
            _outputs.Add(new EvaluationSummary(at, v.VariantId, opportunity.Id, "Opportunity", opportunity.Id, reason, info.Count, info.First, info.Last));
        }

        opportunity.NoteCounts.Clear();
    }

    // ------------------------------------------------------------------ helpers

    private VariantState FindVariant(string variantId)
        => _variants.FirstOrDefault(x => string.Equals(x.VariantId, variantId, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"Unknown variant '{variantId}'.");

    private void Transition(VariantState v, Opportunity opportunity, OpportunityState to, DateTimeOffset at, string reason)
    {
        var from = opportunity.State;
        opportunity.State = to;
        _outputs.Add(new OpportunityTransition(at, v.VariantId, opportunity.Id, from, to, reason));
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
        => _outputs.Add(new AuditRecord(at, v.VariantId, opportunityId, stage, outcome, detail));
}
