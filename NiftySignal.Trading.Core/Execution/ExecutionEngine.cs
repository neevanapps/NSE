using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Costs;
using NiftySignal.Trading.Core.Exits;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Ids;
using NiftySignal.Trading.Core.Integrity;
using NiftySignal.Trading.Core.Quotes;
using NiftySignal.Trading.Core.Risk;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.Session;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Core.Strategies;

namespace NiftySignal.Trading.Core.Execution;

/// <summary>
/// Deterministic simulated execution, positions, exits and accounting (CP3). One virtual book per variant ("independent virtual
/// execution books": the fills of different variants are NOT jointly feasible in one account).
/// <para>
/// <b>Time.</b> Three different times are never mixed. <i>Scheduled deadline time</i> is the instant an order matures, expires or an exit
/// is attempted; the loop gives a deadline callback <c>Now == deadline time</c>. <i>Engine clock / watermark</i> may already be later when a
/// live heartbeat processes the deadline, but it is never used here. <i>Execution time</i> is the deadline time (or, for a resting order
/// evaluated on a new quote, that quote's AvailableAt). Quote lookup and quote age always use the execution time, and only quotes
/// that were delivered at or before it exist in <see cref="QuoteHistory"/> lookups, so a later quote can never be used.
/// </para>
/// <para>
/// <b>Equal-time rules.</b> A deadline equal to an event time fires before the event, so a quote with AvailableAt == execution time is
/// not visible to that deadline; it is evaluated afterwards as a new quote of the resting order. Among deadlines at the same instant
/// the key order is cancel &lt; expire &lt; mature (ordinal), i.e. a cancellation wins over an execution at the same instant. Fills need
/// execution time &lt; EntryClose (entries) / &lt; SessionEnd (exits) and &lt; order expiry.
/// </para>
/// </summary>
public sealed class ExecutionEngine
{
    public const string MaturePrefix = "exec-mature:";
    public const string ExpirePrefix = "exec-expire:";
    public const string CancelPrefix = "exec-cancel:";
    public const string ExitPrefix = "exec-xattempt:";
    public const string HoldPrefix = "exec-hold:";

    private sealed class Liquidity
    {
        public decimal Price;
        public long Displayed;
        public long Consumed;
    }

    private sealed class Book
    {
        public Book(VariantKey variant, int ordinal, decimal? dailySl)
        {
            Variant = variant;
            Ordinal = ordinal;
            DailySl = dailySl;
        }

        public VariantKey Variant { get; }

        public int Ordinal { get; }

        public decimal? DailySl { get; }

        public decimal Cash { get; set; }

        public List<Position> Positions { get; } = new();

        public Dictionary<(string Token, TradeSide Side), Liquidity> Liquidity { get; } = new();

        public bool Breached { get; set; }

        public decimal? WorstDailyPnl { get; set; }

        public decimal PeakExposure { get; set; }

        public long Fills { get; set; }

        public long BelowBandFills { get; set; }

        public long LateFills { get; set; }

        public int FillSeq { get; set; }

        /// <summary>Consecutive closed trades with total net P&L below zero (reset by a trade closing at or above zero).</summary>
        public int LossStreak { get; set; }
    }

    private readonly TradingConfig _config;
    private readonly ExecutionConfig _x;
    private readonly DateOnly _tradeDate;
    private readonly SessionSchedule _schedule;
    private readonly VariantEngine _variants;
    private readonly CostModel _costs;
    private readonly List<EngineOutput> _out;
    private readonly QuoteHistory _history;
    private readonly List<Book> _books = new();
    private readonly List<EntryOrder> _entryOrders = new();
    private readonly Dictionary<string, EntryOrder> _entryById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExitOrder> _exitById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _familyLossStreak = new(StringComparer.Ordinal);
    private bool _ended;

    public ExecutionEngine(TradingConfig config, DateOnly tradeDate, SessionSchedule schedule, VariantEngine variants, CostModel costs, List<EngineOutput> sink, TimeSpan? quoteHistoryRetention = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _x = config.Execution;
        _tradeDate = tradeDate;
        _schedule = schedule;
        _variants = variants;
        _costs = costs;
        _out = sink;
        _history = new QuoteHistory(quoteHistoryRetention ?? _x.MaxQuoteAge + TimeSpan.FromMinutes(1));
        foreach (var v in variants.Variants)
        {
            _books.Add(new Book(v.Key, v.Ordinal, v.Spec.DailySl));
        }
    }

    public bool Ended => _ended;

    /// <summary>The cost schedule in force for the trade date.</summary>
    public CostScheduleEntry CostSchedule => _costs.Entry;

    /// <summary>Re-checked when an entry order matures and on every later evaluation (feed integrity, kill switch, risk profile). Never consulted for exits.</summary>
    public IEntryGate? EntryGate { get; set; }

    /// <summary>Trades opened so far by the variant (positions; a partial exit is not a trade).</summary>
    public int TradesOf(VariantKey variant) => BookOf(variant).Positions.Count;

    /// <summary>Trades opened so far by a whole strategy family (all of its variants).</summary>
    public int FamilyTradesOf(string strategyId) => _books.Where(b => b.Variant.StrategyId == strategyId).Sum(b => b.Positions.Count);

    public int GlobalTrades() => _books.Sum(b => b.Positions.Count);

    /// <summary>
    /// Realized NET P&amp;L of a variant today: exit proceeds already received (a TP1 leg counts at once) minus the average-entry cost of the quantity sold, minus ALL
    /// entry and exit costs incurred so far. The unrealized part of any open quantity is never included.
    /// </summary>
    public decimal RealizedNetOf(VariantKey variant) => BookOf(variant).Positions.Sum(p => p.NetRealised);

    public decimal FamilyRealizedNet(string strategyId) => _books.Where(b => b.Variant.StrategyId == strategyId).Sum(b => b.Positions.Sum(p => p.NetRealised));

    public decimal GlobalRealizedNet() => _books.Sum(b => b.Positions.Sum(p => p.NetRealised));

    public int ConsecutiveLossesOf(VariantKey variant) => BookOf(variant).LossStreak;

    /// <summary>The family streak is chronological across all of its variants: updated at each final close, in the engine's deterministic close order.</summary>
    public int FamilyConsecutiveLosses(string strategyId) => _familyLossStreak.TryGetValue(strategyId, out var n) ? n : 0;

    /// <summary>Committed premium of OPEN positions only (average entry price x open quantity), over every variant: the shared capital in use.</summary>
    public decimal OpenExposure() => _books.Sum(b => b.Positions.Where(p => p.State == PositionState.Open).Sum(p => p.AvgEntryPrice * p.OpenQuantity));

    public decimal PeakPremiumOf(VariantKey variant) => BookOf(variant).PeakExposure;

    public decimal CommittedPremiumOf(VariantKey variant) => Committed(BookOf(variant));

    public (bool Breached, decimal? Worst) DailySlOf(VariantKey variant)
    {
        var book = BookOf(variant);
        return (book.Breached, book.WorstDailyPnl);
    }

    public IReadOnlyList<EntryOrder> EntryOrders => _entryOrders;

    public IReadOnlyList<Position> PositionsOf(VariantKey variant) => BookOf(variant).Positions;

    public IEnumerable<Position> AllPositions => _books.SelectMany(b => b.Positions);

    /// <summary>Cash of the variant's virtual book: -(entry notional + costs) + (exit notional - costs). With the book flat it equals the sum of closed positions' net P&amp;L.</summary>
    public decimal CashOf(VariantKey variant) => BookOf(variant).Cash;

    public string BookLabel(VariantKey variant) => "independent-virtual-book:" + variant;

    public void AppendQuote(QuoteEvent quote) => _history.Append(quote);

    /// <summary>
    /// Writes the simulated exchange's mutable state: per variant book (cash, counters, consumed displayed liquidity, every position with its TP1
    /// progress, reservations and exit orders including the per-order charge state), every entry order with its deadline handles, the ended flag
    /// and the retained quote history.
    /// </summary>
    public void WriteState(StateWriter w)
    {
        using var scope = w.Scope("execution");
        w.Line("ended", _ended);
        foreach (var (family, streak) in _familyLossStreak.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            w.Line("familyLossStreak/" + family, streak);
        }

        foreach (var book in _books)
        {
            using var bs = w.Scope("book/" + book.Variant);
            w.Line("cash", book.Cash);
            w.Line("breached", book.Breached);
            w.Line("worstDailyPnl", book.WorstDailyPnl);
            w.Line("peakExposure", book.PeakExposure);
            w.Line("fills", book.Fills);
            w.Line("belowBandFills", book.BelowBandFills);
            w.Line("lateFills", book.LateFills);
            w.Line("fillSeq", book.FillSeq);
            w.Line("lossStreak", book.LossStreak);
            foreach (var ((token, side), l) in book.Liquidity.OrderBy(x => x.Key.Token, StringComparer.Ordinal).ThenBy(x => (int)x.Key.Side))
            {
                w.Line($"liquidity[{token}][{side}]", $"{StateWriter.Dec(l.Price)}|{l.Displayed}|{l.Consumed}");
            }

            foreach (var p in book.Positions)
            {
                using var ps = w.Scope("position/" + p.Id);
                w.Line("opportunity", p.OpportunityId);
                w.Line("entryDecision", p.EntryDecisionId);
                w.Line("contract", $"{p.Contract.Token}|{p.Contract.Side}|{StateWriter.Dec(p.Contract.Strike)}|{p.Contract.Expiry:yyyy-MM-dd}|{p.Contract.LotSize}|{StateWriter.Dec(p.Contract.TickSize)}");
                w.Line("slot", p.SlotNo);
                w.Line("openedAt", p.OpenedAt);
                w.Line("closedAt", p.ClosedAt);
                w.Line("state", p.State);
                w.Line("entryQuantity", p.EntryQuantity);
                w.Line("entryCostBasis", p.EntryCostBasis);
                w.Line("openQuantity", p.OpenQuantity);
                w.Line("reservedExitQuantity", p.ReservedExitQuantity);
                w.Line("exitedQuantity", p.ExitedQuantity);
                w.Line("exitProceeds", p.ExitProceeds);
                w.Line("entryCosts", p.EntryCosts);
                w.Line("exitCosts", p.ExitCosts);
                w.Line("tp1", p.Tp1);
                w.Line("tp1Target", p.Tp1TargetQuantity);
                w.Line("tp1Filled", p.Tp1FilledQuantity);
                w.Line("exitSeq", p.ExitSeq);
                w.Line("maxHoldDeadline", p.MaxHoldDeadline);
                w.Line("bidObservations", p.BidObservations);
                w.Line("maxBid", p.MaxBid);
                w.Line("minBid", p.MinBid);
                w.Line("firstBidAt", p.FirstBidObservationAt);
                w.Line("lastBidAt", p.LastBidObservationAt);
                w.Line("bidGaps", p.BidObservationGaps);
                w.Line("firstExitFillAt", p.FirstExitFillAt);
                w.Line("maxBidAtFirstExit", p.MaxBidAtFirstExit);
                w.Line("minBidAtFirstExit", p.MinBidAtFirstExit);
                foreach (var x in p.ExitOrders)
                {
                    using var xs = w.Scope("exit/" + x.Id);
                    w.Line("reason", x.Reason);
                    w.Line("requested", x.RequestedQuantity);
                    w.Line("filled", x.FilledQuantity);
                    w.Line("threshold", x.TriggerThreshold);
                    w.Line("createdAt", x.CreatedAt);
                    w.Line("attempts", x.Attempts);
                    w.Line("status", x.Status);
                    w.Line("charged", $"{x.Charged.Fills}|{StateWriter.Dec(x.Charged.Turnover)}|{StateWriter.Dec(x.Charged.BrokerageCharged)}");
                    w.Line("lastNote", x.LastNote);
                    w.Line("attemptHandle", x.AttemptHandle?.Id);
                    foreach (var (key, n) in x.NoteCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        w.Line($"note[{key}]", $"{n.Count}|{n.First.UtcTicks}|{n.Last.UtcTicks}");
                    }
                }
            }
        }

        foreach (var o in _entryOrders)
        {
            using var os = w.Scope("entry/" + o.Id);
            var i = o.Intent;
            w.Line("intent", $"{i.DecisionId}|{i.OpportunityId}|{i.Attempt}|{i.SlotNo}|{i.Contract.Token}|{i.RequestedQuantity}|{i.Lots}|{i.LotSize}|{StateWriter.Dec(i.LimitPrice)}|{StateWriter.Dec(i.QualifyingAsk)}|{StateWriter.Dec(i.MinimumEntryPremium)}|{StateWriter.Dec(i.MaximumEntryPremium)}|{i.QuoteAvailableAt.UtcTicks}|{i.QuoteSourceId}|{i.ConditionKind}");
            w.Line("maturityAt", o.MaturityAt);
            w.Line("expiresAt", o.ExpiresAt);
            w.Line("status", o.Status);
            w.Line("lastNote", o.LastNote);
            w.Line("handles", string.Join(",", o.Handles.Select(h => h.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            foreach (var (key, n) in o.NoteCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.Line($"note[{key}]", $"{n.Count}|{n.First.UtcTicks}|{n.Last.UtcTicks}");
            }
        }

        _history.WriteState(w);
    }

    // ------------------------------------------------------------------ entry orders

    public void RegisterEntry(EntryIntent intent, IEngineScheduler s)
    {
        var order = new EntryOrder(intent, intent.At + _x.Latency, intent.At + _x.EntryOrderLifetime);
        _entryOrders.Add(order);
        _entryById.Add(order.Id, order);
        var ordinal = BookOf(intent.Variant).Ordinal;
        order.Handles.Add(s.Schedule(order.MaturityAt, DeadlineClass.ExecutionMaturity, ordinal, MaturePrefix + order.Id));
        order.Handles.Add(s.Schedule(order.ExpiresAt, DeadlineClass.ExecutionMaturity, ordinal, ExpirePrefix + order.Id));
        Touch(BookOf(intent.Variant));
    }

    public void RequestCancel(CancelRequest request, IEngineScheduler s)
    {
        if (!_entryById.TryGetValue(request.IntentId, out var order) || order.Status != OrderStatus.Working)
        {
            return;
        }

        if (request.Reason == CancelReason.EntryClose || _x.CancelLatency == TimeSpan.Zero)
        {
            // The 15:00 cut-off is a session rule of the simulated exchange: effective at once, no cancel latency.
            Resolve(order, EntryOutcomeKind.Cancelled, request.Reason.ToString(), s);
            return;
        }

        order.Handles.Add(s.Schedule(s.Now + _x.CancelLatency, DeadlineClass.ExecutionMaturity, BookOf(order.Intent.Variant).Ordinal, CancelPrefix + order.Id));
    }

    /// <summary>A resting entry order that has matured sees every new quote of its contract (execution time = the quote's AvailableAt).</summary>
    public void OnQuoteForOrders(QuoteEvent quote, IEngineScheduler s)
    {
        if (_ended)
        {
            return;
        }

        var snapshot = QuoteSnapshot.From(quote);
        foreach (var order in _entryOrders.ToList())
        {
            if (order.Status == OrderStatus.Working
                && order.Intent.Contract.Token == quote.Token
                && quote.AvailableAt >= order.MaturityAt
                && quote.AvailableAt < order.ExpiresAt)
            {
                TryExecuteEntry(order, snapshot, quote.AvailableAt, s);
            }
        }
    }

    private void TryExecuteEntry(EntryOrder order, QuoteSnapshot? quote, DateTimeOffset execTime, IEngineScheduler s)
    {
        if (order.Status != OrderStatus.Working || _ended)
        {
            return;
        }

        var intent = order.Intent;

        // Revalidation that CANCELS the order: the session closed, the contract is not the fixed one, the strategy is no longer valid.
        if (!_schedule.IsEntryEligible(execTime))
        {
            Resolve(order, EntryOutcomeKind.Cancelled, "SessionNotEligible", s);
            return;
        }

        if (_variants.FixedContractToken(intent.OpportunityId) != intent.Contract.Token || (quote is not null && quote.Token != intent.Contract.Token))
        {
            Resolve(order, EntryOutcomeKind.Cancelled, "ContractIdentity", s);
            return;
        }

        if (!_variants.IsStrategyValidFor(intent.Variant, intent.OpportunityId))
        {
            Resolve(order, EntryOutcomeKind.Cancelled, "StrategyInvalidated", s);
            return;
        }

        var gateReasons = EntryGate?.Evaluate(intent.Variant, execTime, intent.LimitPrice * intent.RequestedQuantity, intent.OpportunityId) ?? Array.Empty<RejectionReason>();
        if (gateReasons.Count > 0)
        {
            _variants.ReportFillTimeRejection(intent.Variant, intent.OpportunityId, gateReasons, execTime, intent.Attempt);
            Resolve(order, EntryOutcomeKind.Cancelled, "Gate:" + RejectionReasons.NoteName(gateReasons[0]), s);
            return;
        }

        // Quote-quality conditions that keep the order WORKING (they may improve before expiry).
        var rejection = CheckEntryQuote(quote, execTime);
        if (rejection is not null)
        {
            NoteEntry(order, rejection, execTime);
            return;
        }

        if (_x.SignalQuoteReuse == SignalQuoteReusePolicy.RequireQuoteAfterDecision && quote!.AvailableAt <= intent.At)
        {
            NoteEntry(order, "AwaitingNewerQuote", execTime);
            return;
        }

        var ask = quote!.Ask!.Value;
        var price = ask + _x.EntrySlippage;                      // the executable premium actually paid: ask plus the configured slippage
        var selection = _config.Selection;
        if (price > intent.LimitPrice)
        {
            NoteEntry(order, "AskAboveLimit", execTime);
            return;
        }

        if (price > selection.MaximumEntryPremium)
        {
            NoteEntry(order, "AskAboveBand", execTime);
            return;
        }

        var belowBand = price < selection.MinimumEntryPremium;
        if (belowBand && _x.BelowBand == BelowBandMode.ResearchEnforceBand)
        {
            NoteEntry(order, "AskBelowBand", execTime);
            return;
        }

        var book = BookOf(intent.Variant);
        var usable = UsableQuantity(book, quote.Token, TradeSide.Buy, ask, quote.AskQty);
        var lots = (int)Math.Min(intent.Lots, usable / intent.LotSize);
        if (lots <= 0)
        {
            NoteEntry(order, "NoLiquidity", execTime);
            return;
        }

        var quantity = (long)lots * intent.LotSize;
        Consume(book, quote.Token, TradeSide.Buy, ask, quote.AskQty, quantity);
        OpenPosition(book, order, quantity, price, belowBand, quote, execTime, s);
        Resolve(order, lots == intent.Lots ? EntryOutcomeKind.Filled : EntryOutcomeKind.PartiallyFilled, belowBand ? "FilledBelowBand" : "Filled", s);
        Touch(book);
    }

    private string? CheckEntryQuote(QuoteSnapshot? quote, DateTimeOffset execTime)
    {
        if (quote is null)
        {
            return "NoQuote";
        }

        var age = execTime - quote.AvailableAt;
        if (age < TimeSpan.Zero)
        {
            return "QuoteNotCausal";
        }

        if (age > _x.MaxQuoteAge)
        {
            return "QuoteStale";
        }

        if (!quote.IsExecutable)
        {
            return "QuoteNotExecutable";
        }

        var spread = quote.Ask!.Value - quote.Bid!.Value;
        var mid = (quote.Ask.Value + quote.Bid.Value) / 2m;
        if (_x.MaxSpreadAbsolute is { } maxAbs && spread > maxAbs)
        {
            return "SpreadTooWide";
        }

        if (_x.MaxSpreadFraction is { } maxFraction && spread > maxFraction * mid)
        {
            return "SpreadTooWide";
        }

        return null;
    }

    private void OpenPosition(Book book, EntryOrder order, long quantity, decimal price, bool belowBand, QuoteSnapshot quote, DateTimeOffset execTime, IEngineScheduler s)
    {
        var intent = order.Intent;
        var variant = _variants.GetVariant(intent.Variant);
        var positionId = EngineIds.Position(new ConfigHash(variant.VariantHash), intent.Variant, _tradeDate, intent.DecisionId);
        var position = new Position(positionId, intent.Variant, intent.OpportunityId, intent.DecisionId, intent.Contract, intent.SlotNo, execTime)
        {
            EntryQuantity = quantity,
            OpenQuantity = quantity,
            EntryCostBasis = price * quantity,
        };
        var notional = price * quantity;
        var costs = _costs.Compute(TradeSide.Buy, notional);
        position.EntryCosts = costs.Total;
        book.Positions.Add(position);
        ObserveBid(position, quote, execTime);
        book.Cash -= notional + costs.Total;
        book.Fills++;
        if (belowBand)
        {
            book.BelowBandFills++;
        }

        _out.Add(new FillRecord(
            execTime,
            intent.Variant,
            FillId(book, order.Id),
            order.Id,
            positionId,
            TradeSide.Buy,
            intent.Contract.Token,
            quantity,
            price,
            notional,
            costs,
            belowBand,
            false,
            null,
            null,
            quote.SourceId,
            BookLabel(intent.Variant)));
        _out.Add(new PositionOpened(execTime, intent.Variant, positionId, intent.OpportunityId, intent.Contract.Token, quantity, position.AvgEntryPrice));

        // Max hold: the clock starts at the entry fill and is never reset or extended by a partial exit.
        if (variant.Spec.Exit.MaxHold is { } maxHold)
        {
            position.MaxHoldDeadline = execTime + maxHold;
            position.MaxHoldHandle = s.Schedule(position.MaxHoldDeadline.Value, DeadlineClass.RiskRetry, book.Ordinal, HoldPrefix + positionId);
        }
    }

    private void OnMaxHold(string positionId, IEngineScheduler s)
    {
        var position = _books.SelectMany(b => b.Positions).FirstOrDefault(p => p.Id == positionId);
        if (position is null || position.State != PositionState.Open)
        {
            return;
        }

        var available = position.OpenQuantity - position.ReservedExitQuantity;
        if (available > 0)
        {
            CreateExit(BookOf(position.Variant), position, ExitReason.MaxHold, available, null, 0, s);
        }
    }

    private void Resolve(EntryOrder order, EntryOutcomeKind kind, string reason, IEngineScheduler s)
    {
        if (order.Status != OrderStatus.Working)
        {
            return;
        }

        order.Status = kind switch
        {
            EntryOutcomeKind.Filled or EntryOutcomeKind.PartiallyFilled => OrderStatus.Filled,
            EntryOutcomeKind.Expired => OrderStatus.Expired,
            _ => OrderStatus.Cancelled,
        };
        foreach (var handle in order.Handles)
        {
            s.Cancel(handle);
        }

        order.Handles.Clear();
        FlushNotes(order.NoteCounts, order.Intent.Variant, order.Intent.OpportunityId, "EntryOrder", order.Id, s.Now);
        _out.Add(new AuditRecord(s.Now, order.Intent.Variant, order.Intent.OpportunityId, "EntryOrder", kind.ToString(), reason));
        _variants.ReportEntryOutcome(new EntryOutcome(order.Intent.Variant, order.Intent.OpportunityId, order.Id, kind), s);
    }

    private void NoteEntry(EntryOrder order, string note, DateTimeOffset at)
    {
        order.NoteCounts[note] = order.NoteCounts.TryGetValue(note, out var seen) ? (seen.Count + 1, seen.First, at) : (1, at, at);
        if (order.LastNote == note)
        {
            return;
        }

        order.LastNote = note;
        _out.Add(new AuditRecord(at, order.Intent.Variant, order.Intent.OpportunityId, "EntryOrder", "Working:" + note, "no fill at this execution time; the order keeps working until it expires"));
    }

    // ------------------------------------------------------------------ deadlines

    public void OnDeadline(ScheduledDeadline deadline, IEngineScheduler s)
    {
        var key = deadline.Key;
        if (key.StartsWith(MaturePrefix, StringComparison.Ordinal))
        {
            if (_entryById.TryGetValue(key[MaturePrefix.Length..], out var order) && order.Status == OrderStatus.Working)
            {
                // Execution time is the deadline time; the lookup sees only quotes delivered at or before it.
                var quote = _history.LatestAtOrBefore(order.Intent.Contract.Token, s.Now);
                TryExecuteEntry(order, quote, s.Now, s);
            }
        }
        else if (key.StartsWith(ExpirePrefix, StringComparison.Ordinal))
        {
            if (_entryById.TryGetValue(key[ExpirePrefix.Length..], out var order))
            {
                Resolve(order, EntryOutcomeKind.Expired, "OrderLifetimeElapsed", s);
            }
        }
        else if (key.StartsWith(CancelPrefix, StringComparison.Ordinal))
        {
            if (_entryById.TryGetValue(key[CancelPrefix.Length..], out var order))
            {
                Resolve(order, EntryOutcomeKind.Cancelled, "CancelRequested", s);
            }
        }
        else if (key.StartsWith(HoldPrefix, StringComparison.Ordinal))
        {
            OnMaxHold(key[HoldPrefix.Length..], s);
        }
        else if (key.StartsWith(ExitPrefix, StringComparison.Ordinal))
        {
            if (_exitById.TryGetValue(key[ExitPrefix.Length..], out var exit))
            {
                AttemptExit(exit, s);
            }
        }
    }

    // ------------------------------------------------------------------ exits

    /// <summary>Evaluates stop / target triggers on a new quote of the held contract. Independent of completed bars.</summary>
    public void OnQuoteForPositions(QuoteEvent quote, IEngineScheduler s)
    {
        if (_ended)
        {
            return;
        }

        var snapshot = QuoteSnapshot.From(quote);
        foreach (var book in _books)
        {
            foreach (var position in book.Positions.ToList())
            {
                if (position.State == PositionState.Open && position.Contract.Token == quote.Token)
                {
                    ObserveBid(position, snapshot, s.Now);
                    EvaluateExitTriggers(book, position, snapshot, s);
                }
            }

            if (book.DailySl is not null && book.Positions.Any(p => p.State == PositionState.Open && p.Contract.Token == quote.Token))
            {
                UpdateDailySl(book, s.Now);
            }
        }
    }

    private bool IsEligibleBid(QuoteSnapshot? quote, DateTimeOffset time)
        => quote is { IsExecutable: true } && time - quote.AvailableAt >= TimeSpan.Zero && time - quote.AvailableAt <= _x.MaxQuoteAge;

    private void EvaluateExitTriggers(Book book, Position position, QuoteSnapshot quote, IEngineScheduler s)
    {
        if (!IsEligibleBid(quote, s.Now))
        {
            return;
        }

        var available = position.OpenQuantity - position.ReservedExitQuantity;
        if (available <= 0)
        {
            return;
        }

        var spec = _variants.GetVariant(position.Variant).Spec.Exit;
        var bid = quote.Bid!.Value;
        var avg = position.AvgEntryPrice;

        decimal? stopLevel = spec.TradeSlPct is { } sl ? avg * (1m - sl) : null;
        if (stopLevel is not null && position.Tp1 == Tp1State.Done && spec.RemainderSl == RemainderSlMode.MoveToBreakeven)
        {
            stopLevel = avg;
        }

        if (stopLevel is { } stop && bid <= stop)
        {
            CreateExit(book, position, ExitReason.TradeSl, available, stop, quote.SourceId, s);
            return;
        }

        if (spec.FinalTpPct is { } finalPct && bid >= avg * (1m + finalPct))
        {
            CreateExit(book, position, ExitReason.FinalTarget, available, avg * (1m + finalPct), quote.SourceId, s);
            return;
        }

        if (spec.Tp1Pct is { } tp1Pct && spec.Tp1Proportion is { } proportion && position.Tp1 == Tp1State.NotTriggered && bid >= avg * (1m + tp1Pct))
        {
            // Deterministic whole-lot rounding, DOWN, from the lots open at the first trigger. 10 lots @ 0.5 -> 5; 5 lots @ 0.5 -> 2;
            // 3 lots @ 0.4 -> 1; 1 lot @ any p < 1 -> 0 (TP1 skipped, the position continues under its other rules).
            var lot = position.Contract.LotSize;
            var openLots = position.OpenQuantity / lot;
            var targetLots = (long)Math.Floor(proportion * openLots);
            if (targetLots <= 0)
            {
                position.Tp1 = Tp1State.Skipped;
                _out.Add(new Tp1Resolved(s.Now, position.Variant, position.Id, Tp1State.Skipped, 0, openLots));
                return;
            }

            var quantity = Math.Min(targetLots * lot, available);
            if (quantity <= 0)
            {
                return;
            }

            position.Tp1 = Tp1State.Triggered;
            position.Tp1TargetQuantity = quantity;
            _out.Add(new Tp1Resolved(s.Now, position.Variant, position.Id, Tp1State.Triggered, quantity, openLots));
            CreateExit(book, position, ExitReason.Tp1, quantity, avg * (1m + tp1Pct), quote.SourceId, s);
        }
    }

    /// <summary>Later signals close positions through the same execution path: whole lots only, never more than the unreserved quantity.</summary>
    public void RequestStrategyExit(VariantKey variant, string positionId, long? quantity, IEngineScheduler s)
    {
        var book = BookOf(variant);
        var position = book.Positions.FirstOrDefault(p => p.Id == positionId) ?? throw new KeyNotFoundException($"Unknown position {positionId}.");
        if (position.State != PositionState.Open)
        {
            throw new InvalidOperationException($"Position {positionId} is {position.State}.");
        }

        var available = position.OpenQuantity - position.ReservedExitQuantity;
        var wanted = quantity ?? available;
        if (wanted <= 0 || wanted > available || wanted % position.Contract.LotSize != 0)
        {
            throw new ArgumentException($"Strategy exit of {wanted} is not a positive whole number of lots within the unreserved quantity {available}.", nameof(quantity));
        }

        CreateExit(book, position, ExitReason.StrategyExit, wanted, null, 0, s);
    }

    /// <summary>
    /// Family Exit decision: closes the variant's own open position(s) through the normal exit path (ExitReason.StrategyExit), whole unreserved lots only.
    /// Positions whose quantity is already fully reserved by live exit orders are left alone (the exit is already under way). Never touches a sibling variant.
    /// </summary>
    public int RequestStrategyExitForVariant(VariantKey variant, IEngineScheduler s)
    {
        var book = BookOf(variant);
        var requested = 0;
        foreach (var position in book.Positions.Where(p => p.State == PositionState.Open).ToList())
        {
            if (position.OpenQuantity - position.ReservedExitQuantity > 0)
            {
                RequestStrategyExit(variant, position.Id, null, s);
                requested++;
            }
        }

        return requested;
    }

    private void CreateExit(Book book, Position position, ExitReason reason, long quantity, decimal? threshold, long triggerSeq, IEngineScheduler s)
    {
        position.ExitSeq++;
        var variant = _variants.GetVariant(position.Variant);
        var decisionId = EngineIds.Decision(new ConfigHash(variant.VariantHash), position.Variant, _tradeDate, "Exit" + reason + "@" + position.Id, triggerSeq, position.ExitSeq);   // the position id keeps exit ids unique across the several positions a variant opens in a day
        var orderId = EngineIds.Order(decisionId, "exit", 1);
        var order = new ExitOrder(orderId, position, reason, quantity, threshold, s.Now);
        position.ExitOrders.Add(order);
        position.ReservedExitQuantity += quantity;
        _exitById.Add(orderId, order);
        var first = s.Now + _x.Latency;
        order.AttemptHandle = s.Schedule(first, DeadlineClass.ExecutionMaturity, book.Ordinal, ExitPrefix + orderId);
        _out.Add(new ExitOrderCreated(s.Now, position.Variant, orderId, position.Id, reason, quantity, first, threshold));
    }

    private void AttemptExit(ExitOrder order, IEngineScheduler s)
    {
        if (_ended || order.Status != OrderStatus.Working)
        {
            return;
        }

        var position = order.Position;
        var book = BookOf(position.Variant);
        var execTime = s.Now;
        order.Attempts++;
        string? failure = null;
        if (execTime >= _schedule.SessionEndUtc)
        {
            failure = "SessionEnded";
        }
        else
        {
            var quote = _history.LatestAtOrBefore(position.Contract.Token, execTime);
            failure = CheckBidForFill(quote, execTime);
            if (failure is null)
            {
                var bid = quote!.Bid!.Value;
                var usable = UsableQuantity(book, quote.Token, TradeSide.Sell, bid, quote.BidQty);
                var lots = Math.Min(order.RemainingQuantity / position.Contract.LotSize, usable / position.Contract.LotSize);
                if (lots <= 0)
                {
                    failure = "NoLiquidity";
                }
                else
                {
                    FillExit(book, order, lots * position.Contract.LotSize, bid, quote, execTime, s);
                }
            }
        }

        if (order.Status == OrderStatus.Working)
        {
            if (failure is not null)
            {
                order.NoteCounts[failure] = order.NoteCounts.TryGetValue(failure, out var seen) ? (seen.Count + 1, seen.First, execTime) : (1, execTime, execTime);
            }

            if (failure is not null && order.LastNote != failure)
            {
                order.LastNote = failure;
                _out.Add(new AuditRecord(execTime, position.Variant, position.OpportunityId, "ExitOrder", "Retry:" + failure, "order " + order.Id));
                if (order.Reason is ExitReason.SessionLiquidation or ExitReason.ManualKill)
                {
                    // A mandatory exit cannot fill yet: nothing is fabricated, the order keeps retrying, and the unresolved condition is made visible.
                    _out.Add(new MandatoryExitPending(execTime, position.Variant, position.Id, order.Id, order.Reason, order.RemainingQuantity, order.Attempts, failure));
                }
            }

            order.AttemptHandle = s.Schedule(execTime + _x.ExitRetryInterval, DeadlineClass.RiskRetry, book.Ordinal, ExitPrefix + order.Id);
        }
    }

    private string? CheckBidForFill(QuoteSnapshot? quote, DateTimeOffset execTime)
    {
        if (quote is null)
        {
            return "NoQuote";
        }

        var age = execTime - quote.AvailableAt;
        if (age < TimeSpan.Zero)
        {
            return "QuoteNotCausal";
        }

        if (age > _x.MaxQuoteAge)
        {
            return "QuoteStale";
        }

        return quote.IsExecutable ? null : "QuoteNotExecutable";
    }

    private void FillExit(Book book, ExitOrder order, long quantity, decimal bid, QuoteSnapshot quote, DateTimeOffset execTime, IEngineScheduler s)
    {
        var position = order.Position;
        var fillPrice = Math.Max(bid - _x.ExitSlippage, position.Contract.TickSize);      // a sell receives the executable bid minus the configured slippage (never below one tick)
        var notional = fillPrice * quantity;
        var costs = _costs.Compute(TradeSide.Sell, notional, order.Charged);
        order.Charged = new OrderChargeState(order.Charged.Fills + 1, order.Charged.Turnover + notional, order.Charged.BrokerageCharged + costs.Brokerage);
        Consume(book, quote.Token, TradeSide.Sell, bid, quote.BidQty, quantity);
        position.OpenQuantity -= quantity;
        position.ReservedExitQuantity -= quantity;
        position.ExitedQuantity += quantity;
        position.ExitProceeds += notional;
        position.ExitCosts += costs.Total;
        order.FilledQuantity += quantity;
        if (position.FirstExitFillAt is null)
        {
            position.FirstExitFillAt = execTime;
            position.MaxBidAtFirstExit = position.MaxBid;
            position.MinBidAtFirstExit = position.MinBid;
        }

        book.Cash += notional - costs.Total;
        book.Fills++;
        var late = execTime >= _schedule.FinalCloseUtc;
        if (late)
        {
            book.LateFills++;
        }

        _out.Add(new FillRecord(
            execTime,
            position.Variant,
            FillId(book, order.Id),
            order.Id,
            position.Id,
            TradeSide.Sell,
            position.Contract.Token,
            quantity,
            fillPrice,
            notional,
            costs,
            false,
            late,
            order.Reason,
            order.TriggerThreshold,
            quote.SourceId,
            BookLabel(position.Variant)));
        if (order.Reason == ExitReason.Tp1)
        {
            position.Tp1FilledQuantity += quantity;
            if (position.Tp1FilledQuantity >= position.Tp1TargetQuantity)
            {
                position.Tp1 = Tp1State.Done;
            }
        }

        if (order.RemainingQuantity == 0)
        {
            order.Status = OrderStatus.Filled;
            FlushNotes(order.NoteCounts, position.Variant, position.OpportunityId, "ExitOrder", order.Id, execTime);
        }

        if (position.OpenQuantity == 0)
        {
            position.State = PositionState.Closed;
            position.ClosedAt = execTime;
            var gross = position.GrossRealised;
            _out.Add(new PositionClosed(execTime, position.Variant, position.Id, position.Contract.Token, position.EntryQuantity, position.AvgEntryPrice, gross, position.TotalCosts, position.NetRealised, PositionExcursion.From(position)));
            if (position.NetRealised < 0m)
            {
                book.LossStreak++;
                _familyLossStreak[position.Variant.StrategyId] = FamilyConsecutiveLosses(position.Variant.StrategyId) + 1;
            }
            else
            {
                book.LossStreak = 0;
                _familyLossStreak[position.Variant.StrategyId] = 0;
            }

            _variants.ReportPositionClosed(position.Variant, position.OpportunityId, s);
        }

        Touch(book);
        if (book.DailySl is not null)
        {
            UpdateDailySl(book, execTime);
        }
    }

    // ------------------------------------------------------------------ session boundaries

    public void OnSessionBoundary(SessionBoundaryKind kind, IEngineScheduler s)
    {
        switch (kind)
        {
            case SessionBoundaryKind.LiquidationStart:
                foreach (var book in _books)
                {
                    foreach (var position in book.Positions.Where(p => p.State == PositionState.Open).ToList())
                    {
                        var available = position.OpenQuantity - position.ReservedExitQuantity;
                        if (available > 0)
                        {
                            CreateExit(book, position, ExitReason.SessionLiquidation, available, null, (long)SessionBoundaryKind.LiquidationStart, s);
                        }
                    }
                }

                break;
            case SessionBoundaryKind.FinalClose:
                foreach (var book in _books)
                {
                    foreach (var position in book.Positions.Where(p => p.State == PositionState.Open))
                    {
                        var (bid, age) = LastEligibleBid(position.Contract.Token, s.Now);
                        _out.Add(new UnresolvedAtClose(
                            s.Now,
                            position.Variant,
                            position.Id,
                            position.Contract.Token,
                            position.OpenQuantity,
                            position.ReservedExitQuantity,
                            position.ExitOrders.Count(o => o.Status == OrderStatus.Working),
                            bid,
                            age));
                    }
                }

                break;
            case SessionBoundaryKind.SessionEnd:
                EndSession(s);
                break;
        }
    }

    /// <summary>Requests an exit of the unreserved quantity of every open position of the scope ("*" or a variant) through the normal exit path. Returns the number of orders created.</summary>
    public int LiquidateScope(string scope, ExitReason reason, long triggerSeq, IEngineScheduler s)
    {
        var count = 0;
        foreach (var book in _books.Where(b => Integrity.KillScopes.Covers(scope, b.Variant)))
        {
            foreach (var position in book.Positions.Where(p => p.State == PositionState.Open).ToList())
            {
                var available = position.OpenQuantity - position.ReservedExitQuantity;
                if (available > 0)
                {
                    CreateExit(book, position, reason, available, null, triggerSeq, s);
                    count++;
                }
            }
        }

        return count;
    }

    private static decimal Committed(Book book, IEnumerable<EntryOrder> working)
        => book.Positions.Where(p => p.State == PositionState.Open).Sum(p => p.AvgEntryPrice * p.OpenQuantity)
           + working.Sum(o => o.Intent.LimitPrice * o.Intent.RequestedQuantity);

    private decimal Committed(Book book)
        => Committed(book, _entryOrders.Where(o => o.Status == OrderStatus.Working && o.Intent.Variant == book.Variant));

    private void Touch(Book book) => book.PeakExposure = Math.Max(book.PeakExposure, Committed(book));

    private void ObserveBid(Position position, QuoteSnapshot quote, DateTimeOffset now)
    {
        if (!IsEligibleBid(quote, now))
        {
            return;
        }

        var bid = quote.Bid!.Value;
        if (position.LastBidObservationAt is { } last && now - last > _x.MaxQuoteAge)
        {
            position.BidObservationGaps++;
        }

        position.BidObservations++;
        position.MaxBid = position.MaxBid is { } max ? Math.Max(max, bid) : bid;
        position.MinBid = position.MinBid is { } min ? Math.Min(min, bid) : bid;
        position.FirstBidObservationAt ??= now;
        position.LastBidObservationAt = now;
    }

    private void FlushNotes(Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)> counts, VariantKey variant, string? opportunityId, string scope, string subject, DateTimeOffset at)
    {
        foreach (var (reason, info) in counts.OrderBy(x => x.Key, StringComparer.Ordinal).ToList())
        {
            _out.Add(new EvaluationSummary(at, variant, opportunityId, scope, subject, reason, info.Count, info.First, info.Last));
        }

        counts.Clear();
    }

    private void EndSession(IEngineScheduler s)
    {
        foreach (var order in _entryOrders.Where(o => o.Status == OrderStatus.Working).ToList())
        {
            Resolve(order, EntryOutcomeKind.Cancelled, "SessionEnd", s);
        }

        foreach (var book in _books)
        {
            var unresolved = 0;
            foreach (var position in book.Positions.Where(p => p.State == PositionState.Open))
            {
                unresolved++;
                position.State = PositionState.Unresolved;
                foreach (var order in position.ExitOrders.Where(o => o.Status == OrderStatus.Working))
                {
                    order.Status = OrderStatus.Cancelled;
                    FlushNotes(order.NoteCounts, position.Variant, position.OpportunityId, "ExitOrder", order.Id, s.Now);
                    if (order.AttemptHandle is { } handle)
                    {
                        s.Cancel(handle);
                    }
                }

                var (bid, age) = LastEligibleBid(position.Contract.Token, s.Now);
                _out.Add(new UnresolvedAtSessionEnd(s.Now, position.Variant, position.Id, position.Contract.Token, position.OpenQuantity, bid, age, PositionExcursion.From(position)));
            }

            if (book.DailySl is not null)
            {
                UpdateDailySl(book, s.Now);
            }

            _out.Add(new SessionEndReport(
                s.Now,
                book.Variant,
                book.Positions.Sum(p => p.NetRealised),
                book.Fills,
                book.BelowBandFills,
                book.LateFills,
                unresolved,
                book.WorstDailyPnl,
                book.Breached,
                BookLabel(book.Variant)));
        }

        _ended = true;
    }

    private (decimal? Bid, TimeSpan? Age) LastEligibleBid(string token, DateTimeOffset now)
    {
        var quote = _history.LatestAtOrBefore(token, now);
        return quote is { IsExecutable: true } ? (quote.Bid, now - quote.AvailableAt) : (null, quote is null ? null : now - quote.AvailableAt);
    }

    // ------------------------------------------------------------------ valuation and daily SL (record only)

    public PositionValuation Value(Position position, DateTimeOffset now)
    {
        var quote = _history.LatestAtOrBefore(position.Contract.Token, now);
        if (quote is null)
        {
            return new PositionValuation(position.Id, position.OpenQuantity, ValuationStatus.Missing, null, null, null, null, null);
        }

        var age = now - quote.AvailableAt;
        if (age > _x.MaxQuoteAge)
        {
            return new PositionValuation(position.Id, position.OpenQuantity, ValuationStatus.Stale, quote.Bid, age, null, null, null);
        }

        if (!quote.IsExecutable)
        {
            return new PositionValuation(position.Id, position.OpenQuantity, ValuationStatus.NotExecutable, quote.Bid, age, null, null, null);
        }

        var gross = quote.Bid!.Value * position.OpenQuantity;
        var exitCosts = _costs.Compute(TradeSide.Sell, gross).Total;
        return new PositionValuation(position.Id, position.OpenQuantity, ValuationStatus.Marked, quote.Bid, age, gross, exitCosts, gross - exitCosts);
    }

    /// <summary>Realised net of all positions plus the liquidation value of open ones; an unmarkable open position is conservatively valued at zero (flagged).</summary>
    public (decimal Pnl, bool Conservative) DailyPnl(VariantKey variant, DateTimeOffset now)
    {
        var book = BookOf(variant);
        var pnl = 0m;
        var conservative = false;
        foreach (var position in book.Positions)
        {
            pnl += position.NetRealised;
            if (position.State is PositionState.Open or PositionState.Unresolved && position.OpenQuantity > 0)
            {
                var valuation = Value(position, now);
                if (valuation.Status == ValuationStatus.Marked)
                {
                    pnl += valuation.LiquidationValue!.Value - (position.AvgEntryPrice * position.OpenQuantity);
                }
                else
                {
                    pnl -= position.AvgEntryPrice * position.OpenQuantity;
                    conservative = true;
                }
            }
        }

        return (pnl, conservative);
    }

    private void UpdateDailySl(Book book, DateTimeOffset now)
    {
        var (pnl, conservative) = DailyPnl(book.Variant, now);
        book.WorstDailyPnl = book.WorstDailyPnl is { } worst ? Math.Min(worst, pnl) : pnl;
        if (!book.Breached && book.DailySl is { } limit && pnl <= -limit)
        {
            book.Breached = true;
            _out.Add(new DailySlBreached(now, book.Variant, pnl, limit, conservative));
        }
    }

    // ------------------------------------------------------------------ liquidity and helpers

    private long UsableQuantity(Book book, string token, TradeSide side, decimal price, long? displayed)
    {
        if (displayed is null or <= 0)
        {
            return _x.RequireDisplayedQuantity ? 0 : long.MaxValue / 4;
        }

        var available = displayed.Value;
        if (book.Liquidity.TryGetValue((token, side), out var used) && used.Price == price && used.Displayed == displayed.Value)
        {
            available = Math.Max(0, displayed.Value - used.Consumed);
        }

        return (long)Math.Floor(available * _x.FillFractionOfDisplayed);
    }

    private static void Consume(Book book, string token, TradeSide side, decimal price, long? displayed, long quantity)
    {
        if (displayed is null or <= 0)
        {
            return;
        }

        if (book.Liquidity.TryGetValue((token, side), out var used) && used.Price == price && used.Displayed == displayed.Value)
        {
            used.Consumed += quantity;
        }
        else
        {
            // A different displayed state (price or size) is new liquidity; an identical one is the SAME liquidity and stays consumed.
            book.Liquidity[(token, side)] = new Liquidity { Price = price, Displayed = displayed.Value, Consumed = quantity };
        }
    }

    private string FillId(Book book, string orderId)
        => DeterministicId.Create("fil", "fill", orderId, ++book.FillSeq);

    private Book BookOf(VariantKey variant)
        => _books.FirstOrDefault(b => b.Variant == variant)
           ?? throw new KeyNotFoundException($"Unknown variant '{variant}'.");
}
