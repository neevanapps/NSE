using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>
/// The paper-execution state machine of ONE strategy (Flat -> EntryPending -> Open -> ExitPending -> Closed | Unresolved). It sees only the shared immutable frames and its own state, so strategies cannot influence each
/// other. Every fill needs a quote that became available STRICTLY after the decision (plus the configured latency); a bar of the same market update, a zero-duration sibling or an older bar can never fill a decision.
/// Entries are all-or-none at ask + slippage, exits at bid - slippage (never below zero); displayed size is recorded, never used. Buy-only: there is no sell-to-open path.
/// </summary>
public sealed class StrategyRuntime
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    sealed class Intent
    {
        public required string Id; public required OptionRight Right; public required string Token; public required double Strike; public required DateOnly Expiry;
        public required int LotSize; public required int Quantity; public required double SelectedAsk; public required DecisionKey Decision; public required DateTimeOffset ExpiresAt;
        public RejectionReason? Obstacle;
    }

    sealed class Position
    {
        public required string Id; public required OptionRight Right; public required string Token; public required double Strike; public required DateOnly Expiry; public required int Quantity;
        public required decimal EntryPrice; public required decimal EntryFees; public required int FillBarSeq;
        public PositionState State = PositionState.Open;
        public decimal Peak; public decimal Mfe; public decimal Mae; public int MarkGaps;
        public ExitReason? ExitReason; public DecisionKey? ExitDecision;
        public decimal? ExitPrice, ExitFees, Gross, Net;
        public int BarsHeld;
    }

    readonly CompiledStrategy _strategy;
    readonly StrategyConfig _config;
    readonly ExecutionOptions _options;
    readonly string _prefix;
    readonly List<Position> _positions = [];
    Intent? _pending;
    DateOnly? _day;
    CostModel? _costs;
    decimal _cash;
    decimal _realizedToday;
    int _fillsToday;
    bool _riskBreached;
    int _lastBarSeq = -1;
    long _lastSession = -1;
    long _warmupSession = -1;
    int _verifiedBars;

    public StrategyRuntime(CompiledStrategy strategy, ExecutionOptions options)
    {
        _strategy = strategy; _config = strategy.Config; _options = options;
        _prefix = $"{_config.Identity.StrategyId}:{_config.Identity.Hash[..12]}";
        _cash = (decimal)_config.VirtualCapital;
    }

    public string StrategyId => _config.Identity.StrategyId;

    public bool Faulted { get; private set; }

    public decimal Cash => _cash;

    public decimal RealizedToday => _realizedToday;

    public bool HasPendingEntry => _pending is not null;

    /// <summary>Why a strategy cannot be activated (fail closed), or null when it can.</summary>
    public static ActivationFailure? CheckActivation(StrategyConfig config, ExecutionOptions options)
    {
        if (config.Execution.FillQuote != FillQuoteRule.AsOfEligibleBarEnd)
            return new ActivationFailure(config.Identity.StrategyId, ActivationFailureKind.UnsupportedFillQuoteRule, $"fill rule {config.Execution.FillQuote} is not supported: frames hold one quote per token per bar, so only AsOfEligibleBarEnd can be simulated");
        if (!options.CostSchedules.Any(s => string.Equals(s.Version, config.FeeSchedule, StringComparison.Ordinal)))
            return new ActivationFailure(config.Identity.StrategyId, ActivationFailureKind.MissingCostSchedule, $"no cost schedule '{config.FeeSchedule}' is configured: costs are never assumed to be zero");
        return null;
    }

    public IReadOnlyList<PositionView> Positions() => _positions.Select(p => new PositionView(p.Id, StrategyId, p.Right, p.Token, p.Strike, p.Expiry, p.Quantity, p.State, p.EntryPrice, p.EntryFees, p.ExitPrice, p.ExitFees,
        p.Gross, p.Net, p.ExitReason, p.Mfe, p.Mae, p.MarkGaps, p.BarsHeld)).ToArray();

    /// <summary>Processes one frame. Never throws: a malfunction faults this strategy only (stops entries, open positions become Unresolved).</summary>
    public void Process(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, List<PaperEvent> output)
    {
        if (Faulted) return;
        var id = frame.Identity;
        if (id.SessionId == _lastSession && id.BarSeq <= _lastBarSeq) return;           // idempotent per decision key
        try
        {
            Step(frame, previous, disposition, output);
            _lastBarSeq = id.BarSeq; _lastSession = id.SessionId;
        }
        catch (Exception ex)
        {
            Faulted = true; _pending = null;
            output.Add(new StrategyFaulted($"{_prefix}:{id.SessionId}:{id.BarSeq}:fault", StrategyId, _config.Identity.Hash, id.BarSeq, id.BarEndAvailableAt, $"{ex.GetType().Name}: {ex.Message}"));
            foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
            {
                p.State = PositionState.Unresolved;
                output.Add(Unresolved(p, frame, "strategy faulted while the position was open"));
            }
        }
    }

    void Step(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, List<PaperEvent> output)
    {
        var id = frame.Identity;
        var istTime = TimeOnly.FromTimeSpan((id.BarEndAvailableAt + IstOffset).TimeOfDay);
        if (_day != id.TradeDate) NewDay(frame, output);
        if (_warmupSession != id.SessionId) { _warmupSession = id.SessionId; _verifiedBars = 0; }
        if (id.Verified) _verifiedBars++;                // completed, persisted and verified bars of this session evaluated so far (this frame included); rebuilt identically by a journal replay

        MarkPositions(frame);
        FillPendingExits(frame, output);
        if (_pending is not null && istTime >= ExecutionOptions.TerminalTime) Reject(frame, output, _pending.Right, _pending.Id, RejectionReason.OutsideEntryWindow, "entry order still pending at the terminal time", cancelPending: true);
        FillPendingEntry(frame, disposition, output);

        // positions that are still not closed after the retry cut-off are UNRESOLVED (reported, never silently dropped)
        if (istTime > _options.TerminalRetryUntil)
            foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending).ToArray())
            {
                p.State = PositionState.Unresolved;
                output.Add(Unresolved(p, frame, $"not closed by the retry cut-off {_options.TerminalRetryUntil:HH\\:mm\\:ss} IST"));
            }

        if (istTime >= ExecutionOptions.TerminalTime)
            foreach (var p in _positions.Where(p => p.State == PositionState.Open)) RequestExit(p, ExitReason.Terminal, frame, output);


        BreachCheck(frame, output);
        foreach (var p in _positions.Where(p => p.State == PositionState.Open).ToArray()) CheckOptionalExits(p, frame, output);

        if (!id.Verified) return;                       // signals are only evaluated on verified bars; quote mechanics above still ran
        var ctx = new EvaluationContext(frame, previous);
        var signals = _strategy.EvaluateEntry(ctx);
        var exitCondition = _strategy.EvaluateExit(ctx) == Tri.True;
        var oppositeExit = false;
        foreach (var p in _positions.Where(p => p.State == PositionState.Open).ToArray())
        {
            if (exitCondition) RequestExit(p, ExitReason.Discretionary, frame, output);
            else if ((p.Right == OptionRight.Put && signals.BuyCall == Tri.True) || (p.Right == OptionRight.Call && signals.BuyPut == Tri.True))
            {
                oppositeExit = true;
                RequestExit(p, ExitReason.OppositeSignal, frame, output);
            }
        }

        if (oppositeExit) return;                       // exit only, never an immediate reversal
        if (signals.BuyCall == Tri.True && signals.BuyPut == Tri.True) { Reject(frame, output, null, null, RejectionReason.ConflictingSignals, "buyCall and buyPut are both true on the same bar"); return; }
        if (signals.BuyCall == Tri.True) TryEntry(OptionRight.Call, frame, disposition, istTime, output);
        else if (signals.BuyPut == Tri.True) TryEntry(OptionRight.Put, frame, disposition, istTime, output);
    }

    void NewDay(MarketStateFrame frame, List<PaperEvent> output)
    {
        foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
        {
            p.State = PositionState.Unresolved;
            output.Add(Unresolved(p, frame, "the trading day ended with the position still open"));
        }

        _pending = null;
        _day = frame.Identity.TradeDate; _realizedToday = 0m; _fillsToday = 0; _riskBreached = false;
        var schedules = _options.CostSchedules.Where(s => string.Equals(s.Version, _config.FeeSchedule, StringComparison.Ordinal)).ToArray();
        _costs = new CostModel(new CostsConfig { Schedules = schedules }, frame.Identity.TradeDate);          // throws (=> this strategy faults) when the schedule is not effective on the date
    }

    static bool Fresh(QuoteSnapshot q, MarketStateFrame frame, int maxAgeMs) => (frame.Identity.BarEndAvailableAt - q.AvailableAt).TotalMilliseconds <= maxAgeMs;

    static QuoteSnapshot? QuoteOf(MarketStateFrame frame, string token)
    {
        foreach (var q in frame.Quotes) if (string.Equals(q.Token, token, StringComparison.Ordinal)) return q;
        return null;
    }

    /// <summary>The quote of a HELD contract: its basket quote when it is still in the basket, otherwise the frame's held quote. Never another strike, never inferred; null = unobserved at this bar.</summary>
    static QuoteSnapshot? HeldQuoteOf(MarketStateFrame frame, string token)
    {
        if (QuoteOf(frame, token) is { } q) return q;
        foreach (var h in frame.HeldQuotes) if (string.Equals(h.Token, token, StringComparison.Ordinal)) return h;
        return null;
    }

    /// <summary>Tokens of the contracts this strategy currently holds (Open or ExitPending): they must stay observable until the position is closed or Unresolved.</summary>
    public IEnumerable<string> HeldTokens() => _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending).Select(p => p.Token);

    void MarkPositions(MarketStateFrame frame)
    {
        foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
        {
            p.BarsHeld = frame.Identity.BarSeq - p.FillBarSeq;
            var q = HeldQuoteOf(frame, p.Token);
            if (q is null || !Fresh(q, frame, _config.Execution.MaxQuoteAgeMs)) { p.MarkGaps++; continue; }
            var mark = (decimal)q.Bid;
            var pnl = (mark - p.EntryPrice) * p.Quantity;
            if (pnl > p.Mfe) p.Mfe = pnl;
            if (pnl < p.Mae) p.Mae = pnl;
            if (mark > p.Peak) p.Peak = mark;
        }
    }

    void FillPendingExits(MarketStateFrame frame, List<PaperEvent> output)
    {
        var ex = _config.Execution;
        foreach (var p in _positions.Where(p => p.State == PositionState.ExitPending).ToArray())
        {
            var q = HeldQuoteOf(frame, p.Token);
            if (q is null || !Fresh(q, frame, ex.MaxQuoteAgeMs)) continue;
            if (q.AvailableAt <= p.ExitDecision!.Value.AvailableAt.AddMilliseconds(ex.LatencyMs)) continue;      // not strictly newer than the decision (+ latency)
            var price = Math.Max(0m, (decimal)q.Bid - (decimal)ex.ExitSlippage);
            var fees = _costs!.Compute(TradeSide.Sell, price * p.Quantity).Total;
            var gross = (price - p.EntryPrice) * p.Quantity;
            var net = gross - p.EntryFees - fees;
            p.State = PositionState.Closed; p.ExitPrice = price; p.ExitFees = fees; p.Gross = gross; p.Net = net;
            _cash += (price * p.Quantity) - fees;
            _realizedToday += net;
            output.Add(new ExitFilled($"{p.Id}:xfill", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, p.Id, p.ExitReason!.Value, p.Quantity, price, fees, gross, net, q.AvailableAt, q.SourceTickId, q.BidQty, p.Mfe, p.Mae));
        }
    }

    void FillPendingEntry(MarketStateFrame frame, FrameDisposition disposition, List<PaperEvent> output)
    {
        if (_pending is not { } it) return;
        var ex = _config.Execution;
        if (!disposition.EntriesAllowed || !frame.Identity.Verified)
        {
            Reject(frame, output, it.Right, it.Id, RejectionReason.EntriesBlocked, $"entries blocked before the fill ({disposition.BlockReasons})", cancelPending: true);
            return;
        }

        var q = QuoteOf(frame, it.Token);
        if (q is null) it.Obstacle = RejectionReason.MissingQuote;
        else if (!Fresh(q, frame, ex.MaxQuoteAgeMs)) it.Obstacle = RejectionReason.StaleQuote;
        else if (q.AvailableAt > it.ExpiresAt) { }                                               // too late for this order: it expires below
        else if (q.AvailableAt <= it.Decision.AvailableAt.AddMilliseconds(ex.LatencyMs)) { }       // not strictly newer than the decision (+ latency)
        else
        {
            if (!SpreadOk(q, ex)) { Reject(frame, output, it.Right, it.Id, RejectionReason.SpreadTooWide, $"spread {q.Ask - q.Bid:0.##} at the fill quote exceeds the configured limit", cancelPending: true); return; }
            if (_riskBreached) { Reject(frame, output, it.Right, it.Id, RejectionReason.RiskBreach, "daily loss limit breached before the fill", cancelPending: true); return; }
            var price = (decimal)q.Ask + (decimal)ex.EntrySlippage;
            var fees = _costs!.Compute(TradeSide.Buy, price * it.Quantity).Total;
            var cost = (price * it.Quantity) + fees;
            if (cost > _cash) { Reject(frame, output, it.Right, it.Id, RejectionReason.InsufficientCapital, $"cost {cost:0.00} exceeds available capital {_cash:0.00}", cancelPending: true); return; }
            _cash -= cost; _fillsToday++;
            var pos = new Position { Id = it.Id, Right = it.Right, Token = it.Token, Strike = it.Strike, Expiry = it.Expiry, Quantity = it.Quantity, EntryPrice = price, EntryFees = fees, FillBarSeq = frame.Identity.BarSeq, Peak = price };
            _positions.Add(pos);
            _pending = null;
            output.Add(new EntryFilled($"{it.Id}:filled", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, it.Id, pos.Id, it.Right, it.Token, it.Quantity, price, fees, q.AvailableAt, q.SourceTickId, q.AskQty));
            return;
        }

        if (frame.Identity.BarEndAvailableAt > it.ExpiresAt)
            Reject(frame, output, it.Right, it.Id, it.Obstacle ?? RejectionReason.ExpiredOpportunity, "the entry order lifetime elapsed without a qualifying fill quote", cancelPending: true);
    }

    static bool SpreadOk(QuoteSnapshot q, ExecutionPolicy ex)
    {
        var spread = q.Ask - q.Bid;
        if (ex.MaxSpreadAbsolute is { } a && spread > a) return false;
        return ex.MaxSpreadFraction is not { } f || spread <= f * ((q.Ask + q.Bid) / 2d);
    }

    void BreachCheck(MarketStateFrame frame, List<PaperEvent> output)
    {
        if (_riskBreached || _realizedToday > -(decimal)_config.Risk.MaxDailyLoss) return;
        _riskBreached = true;
        output.Add(new RiskBreached($"{_prefix}:{frame.Identity.TradeDate:yyyyMMdd}:riskbreach", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, _realizedToday, (decimal)_config.Risk.MaxDailyLoss));
        if (_pending is not null) Reject(frame, output, _pending.Right, _pending.Id, RejectionReason.RiskBreach, "daily loss limit breached", cancelPending: true);
        if (_config.Risk.Liquidation == LiquidationPolicy.ExitOpenPositions)
            foreach (var p in _positions.Where(p => p.State == PositionState.Open)) RequestExit(p, ExitReason.RiskLiquidation, frame, output);
    }

    void CheckOptionalExits(Position p, MarketStateFrame frame, List<PaperEvent> output)
    {
        var x = _config.Exits;
        if (!x.Any) return;
        if (x.MaxHoldBars is { } hold && p.BarsHeld >= hold) { RequestExit(p, ExitReason.MaxHold, frame, output); return; }
        var q = HeldQuoteOf(frame, p.Token);
        if (q is null || !Fresh(q, frame, _config.Execution.MaxQuoteAgeMs)) return;      // no usable mark: never assume a threshold price
        var mark = (decimal)q.Bid;
        if (x.StopLoss is { } sl && mark <= p.EntryPrice - Distance(sl, p.EntryPrice)) RequestExit(p, ExitReason.StopLoss, frame, output);
        else if (x.Target is { } tg && mark >= p.EntryPrice + Distance(tg, p.EntryPrice)) RequestExit(p, ExitReason.Target, frame, output);
        else if (x.Trailing is { } tr && p.Peak - mark >= Distance(tr, p.EntryPrice)) RequestExit(p, ExitReason.Trailing, frame, output);
    }

    static decimal Distance(ThresholdExit t, decimal entry) => t.Kind == DistanceKind.Points ? (decimal)t.Value : entry * (decimal)t.Value / 100m;

    void RequestExit(Position p, ExitReason reason, MarketStateFrame frame, List<PaperEvent> output)
    {
        if (p.State != PositionState.Open) return;
        p.State = PositionState.ExitPending; p.ExitReason = reason; p.ExitDecision = frame.Identity.DecisionKey;
        output.Add(new ExitRequested($"{p.Id}:xreq", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, p.Id, reason, frame.Identity.DecisionKey));
    }

    PositionUnresolved Unresolved(Position p, MarketStateFrame frame, string detail) =>
        new($"{p.Id}:unres", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, p.Id, detail);

    void Reject(MarketStateFrame frame, List<PaperEvent> output, OptionRight? right, string? intentId, RejectionReason reason, string detail, bool cancelPending = false)
    {
        if (cancelPending) _pending = null;
        var key = intentId ?? $"{_prefix}:{frame.Identity.SessionId}:{frame.Identity.BarSeq}:{(right?.ToString() ?? "any")}";
        output.Add(new EntryRejected($"{key}:rej:{reason}", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, right, intentId, reason, detail));
    }

    void TryEntry(OptionRight right, MarketStateFrame frame, FrameDisposition disposition, TimeOnly istTime, List<PaperEvent> output)
    {
        var ex = _config.Execution;
        if (!disposition.EntriesAllowed) { Reject(frame, output, right, null, RejectionReason.EntriesBlocked, $"entries blocked ({disposition.BlockReasons})"); return; }
        if (!_strategy.IsEntryTimeAllowed(istTime)) { Reject(frame, output, right, null, RejectionReason.OutsideEntryWindow, $"{istTime:HH\\:mm\\:ss} IST is outside the entry window"); return; }
        if (_config.Filters.MinCompletedBars is { } minBars && (_verifiedBars < minBars || frame.Identity.BarSeq < minBars))
        { Reject(frame, output, right, null, RejectionReason.WarmupIncomplete, $"{_verifiedBars} verified bar(s) evaluated (bar {frame.Identity.BarSeq}); {minBars} completed bars are required before an entry"); return; }
        if (_riskBreached) { Reject(frame, output, right, null, RejectionReason.RiskBreach, "daily loss limit breached"); return; }
        if (_fillsToday >= _config.Risk.MaxDailyTrades) { Reject(frame, output, right, null, RejectionReason.RiskBreach, "max daily trades reached"); return; }
        var open = _positions.Count(p => p.State is PositionState.Open or PositionState.ExitPending);
        if (_pending is not null || open + 1 > _config.MaxConcurrent) { Reject(frame, output, right, null, RejectionReason.PositionLimit, $"{open} open position(s), pending order: {_pending is not null}, limit {_config.MaxConcurrent}"); return; }

        var wantCall = right == OptionRight.Call;
        var slots = frame.Basket.Slots.Where(s => s.IsCall == wantCall && s.Token is not null && s.Strike is not null).ToArray();
        var quoted = 0; var fresh = 0; var inRange = 0; var spreadOk = 0;
        (QuoteSnapshot Q, BasketSlot S)? best = null;
        var bestKey = (0d, 0d, 0d, string.Empty);
        foreach (var s in slots)
        {
            var q = QuoteOf(frame, s.Token!);
            if (q is null) continue;
            quoted++;
            if (!Fresh(q, frame, ex.MaxQuoteAgeMs)) continue;
            fresh++;
            if (q.Ask < _config.Premium.Min || q.Ask > _config.Premium.Max) continue;
            inRange++;
            if (!SpreadOk(q, ex)) continue;
            spreadOk++;
            var key = (Math.Abs(q.Ask - _config.Premium.Target), Math.Abs(s.Strike!.Value - frame.Basket.Centre), s.Strike.Value, s.Token!);
            if (best is null || Compare(key, bestKey) < 0) { best = (q, s); bestKey = key; }
        }

        if (best is null)
        {
            var (reason, why) = slots.Length == 0 ? (RejectionReason.InvalidInstrument, "the basket has no resolved contract on this side")
                : quoted == 0 ? (RejectionReason.MissingQuote, "no basket contract of this side has a quote")
                : fresh == 0 ? (RejectionReason.StaleQuote, "every quote of this side is older than the maximum quote age")
                : inRange == 0 ? (RejectionReason.NoQualifyingContract, $"no ask within [{_config.Premium.Min}, {_config.Premium.Max}]")
                : (RejectionReason.SpreadTooWide, "every in-range contract exceeds the spread limit");
            Reject(frame, output, right, null, reason, why);
            return;
        }

        var (bq, bs) = best.Value;
        var lot = _options.LotSizes.LotSize(frame.Identity.TradeDate, bq.Token);
        if (lot is not > 0) { Reject(frame, output, right, null, RejectionReason.LotSizeViolation, $"no lot size for token {bq.Token} on {frame.Identity.TradeDate:yyyy-MM-dd}"); return; }
        var qty = checked(_config.Lots * lot.Value);
        var estimate = ((decimal)bq.Ask + (decimal)ex.EntrySlippage) * qty;
        var estCost = estimate + _costs!.Compute(TradeSide.Buy, estimate).Total;
        if (estCost > _cash) { Reject(frame, output, right, null, RejectionReason.InsufficientCapital, $"estimated cost {estCost:0.00} exceeds available capital {_cash:0.00}"); return; }

        var intentId = $"{_prefix}:{frame.Identity.SessionId}:{frame.Identity.BarSeq}:{right}";
        _pending = new Intent
        {
            Id = intentId, Right = right, Token = bq.Token, Strike = bs.Strike!.Value, Expiry = frame.Basket.Expiry, LotSize = lot.Value, Quantity = qty, SelectedAsk = bq.Ask,
            Decision = frame.Identity.DecisionKey, ExpiresAt = frame.Identity.BarEndAvailableAt.AddMilliseconds(ex.EntryOrderLifetimeMs),
        };
        output.Add(new EntryOrderPlaced($"{intentId}:placed", StrategyId, _config.Identity.Hash, frame.Identity.BarSeq, frame.Identity.BarEndAvailableAt, intentId, right, bq.Token, bs.Strike.Value, frame.Basket.Expiry,
            lot.Value, qty, (decimal)bq.Ask, frame.Identity.DecisionKey, _pending.ExpiresAt));
    }

    static int Compare((double, double, double, string) a, (double, double, double, string) b)
    {
        var c = a.Item1.CompareTo(b.Item1);
        if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2);
        if (c != 0) return c;
        c = a.Item3.CompareTo(b.Item3);
        return c != 0 ? c : string.CompareOrdinal(a.Item4, b.Item4);
    }
}
