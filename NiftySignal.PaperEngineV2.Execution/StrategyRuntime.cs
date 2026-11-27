using NiftySignal.PaperEngineV2.Core.Conditions;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.PaperEngineV2.Execution;

/// <summary>
/// The paper-execution state machine of ONE strategy (Flat -> EntryPending -> Open -> ExitPending -> Closed | Unresolved). It advances one <see cref="ExecutionInput"/> at a time - a completed bar, a quote update,
/// a timer tick or a safety verdict - and decides from that input and its own state ONLY, so strategies cannot influence each other and a journal of the state-changing inputs replays identically.
/// Every fill needs a quote that became available STRICTLY after the decision (plus the configured latency). Entries are all-or-none at ask + slippage, exits at bid - slippage (never below zero); displayed size is
/// recorded, never used. Buy-only: there is no sell-to-open path. Entry gates never apply to exits.
/// </summary>
public sealed class StrategyRuntime
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    /// <summary>
    /// A directional entry intent that outlives its signal bar: waiting for the held position to close and / or for the Avg5 condition. Consumed by one fill, or cancelled. For Avg5 timing a temporary
    /// eligibility failure only suspends it; after an order ended without a fill, <see cref="RetryAfter"/> requires quotes that arrived later than that end.
    /// </summary>
    sealed class Waiting
    {
        public required string Id; public required OptionRight Right; public required DateTimeOffset SignalAt; public string? ReferenceToken;
        public int Orders;
        public DateTimeOffset? RetryAfter;
        public readonly HashSet<RejectionReason> Reported = [];
    }

    sealed class Intent
    {
        public required string Id; public required OptionRight Right; public required string Token; public required double Strike; public required DateOnly Expiry;
        public required int LotSize; public required int Quantity; public required double SelectedAsk; public required DecisionKey Decision; public required DateTimeOffset ExpiresAt;
        public RejectionReason? Obstacle;
        public Waiting? Parent;
    }

    enum Partial { Off, Armed, Requested, Filled, Cancelled }

    sealed class Position
    {
        public required string Id; public required OptionRight Right; public required string Token; public required double Strike; public required DateOnly Expiry; public required int Quantity;
        public required decimal EntryPrice; public required decimal EntryFees; public required int FillBarSeq; public DateTimeOffset EntryAt;
        public PositionState State = PositionState.Open;
        public decimal Peak; public decimal Mfe; public decimal Mae; public int MarkGaps;
        public ExitReason? ExitReason; public DecisionKey? ExitDecision;
        public decimal? ExitPrice, ExitFees, Gross, Net;
        public int BarsHeld;
        public required int Remaining; public required decimal EntryFeesRemaining;
        public Partial PartialState; public int PartialQuantity; public decimal? PartialTarget; public DecisionKey? PartialDecision;
        public decimal PartialGross, PartialNet;
        public bool LiquidationReported;
    }

    readonly CompiledStrategy _strategy;
    readonly StrategyConfig _config;
    readonly ExecutionOptions _options;
    readonly string _prefix;
    readonly List<Position> _positions = [];
    Intent? _pending;
    Waiting? _waiting;
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
    // the latest completed bar among the inputs processed so far
    long _session;
    int _barSeq;
    long _decisionTick;
    BasketIdentity? _basket;
    bool _barAllowsEntries;
    IntegrityFlags _barBlock;
    // the current input only
    bool _dirty;
    string? _entryBlock;
    string? _invalidate;
    Dictionary<string, QuoteSnapshot> _quotes = new(StringComparer.Ordinal);

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

    public bool HasPendingEntry => _pending is not null || _waiting is not null;

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
        p.Gross, p.Net, p.ExitReason, p.Mfe, p.Mae, p.MarkGaps, p.BarsHeld, p.Remaining, p.PartialGross, p.PartialNet, p.PartialTarget, p.PartialState.ToString(), p.EntryAt)).ToArray();

    public StrategyStatus Status() => new(
        StrategyId, _config.Identity.Version, _config.Timing, Faulted, _fillsToday, _config.Risk.MaxDailyTrades, _realizedToday, _cash, _riskBreached, _verifiedBars,
        _waiting is { } w ? new PendingEntryView(w.Id, w.Right, w.SignalAt, w.ReferenceToken, false, null)
            : _pending is { } o ? new PendingEntryView(o.Id, o.Right, o.Parent?.SignalAt ?? o.Decision.AvailableAt, o.Parent?.ReferenceToken, true, o.Token) : null);

    /// <summary>What the bar's signal evaluation saw, kept only so the opportunity log can describe it after the step (observation only; no decision reads it).</summary>
    sealed record Obs(Tri BuyCall, Tri BuyPut, bool HoldingCall, bool HoldingPut, bool HadPending);

    Obs? _obs;

    /// <summary>Processes one completed bar (the bar-driven form).</summary>
    public void Process(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, List<PaperEvent> output, List<OpportunityRecord>? opportunities = null) =>
        Step(ExecutionInput.FromFrame(frame, previous, disposition), output, opportunities);

    /// <summary>
    /// Processes one input. Returns true when it changed this strategy's state or produced events (and therefore has to be journaled). Never throws: a malfunction faults this strategy only (stops entries, open
    /// positions become Unresolved). A bar already processed is ignored.
    /// </summary>
    public bool Step(ExecutionInput input, List<PaperEvent> output, List<OpportunityRecord>? opportunities = null)
    {
        if (Faulted) return false;
        var frame = input.Frame;
        if (frame is not null && frame.Identity.SessionId == _lastSession && frame.Identity.BarSeq <= _lastBarSeq) return false;           // idempotent per decision key
        try
        {
            var start = output.Count;
            _obs = null;
            _dirty = false;
            StepCore(input, output);
            if (frame is not null) { _lastBarSeq = frame.Identity.BarSeq; _lastSession = frame.Identity.SessionId; }
            if (opportunities is not null)
            {
                try { Classify(input, output, start, opportunities); }
                catch (Exception) { /* the opportunity log is observation only: a failure here never reaches the simulation */ }
            }

            return _dirty || output.Count > start;
        }
        catch (Exception ex)
        {
            Faulted = true; _pending = null; _waiting = null;
            output.Add(new StrategyFaulted($"{_prefix}:{input.SessionId}:{_barSeq}:fault", StrategyId, _config.Identity.Hash, _barSeq, input.Now, $"{ex.GetType().Name}: {ex.Message}"));
            foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
            {
                p.State = PositionState.Unresolved;
                output.Add(Unresolved(p, input, "strategy faulted while the position was open"));
            }

            return true;
        }
    }

    void StepCore(ExecutionInput input, List<PaperEvent> output)
    {
        var frame = input.Frame;
        var istTime = IstTime(input.Now);
        if (_day != input.TradeDate) NewDay(input, output);
        _session = input.SessionId;
        _quotes = new Dictionary<string, QuoteSnapshot>(StringComparer.Ordinal);
        foreach (var q in input.Quotes) _quotes.TryAdd(q.Token, q);              // first wins: a frame lists its basket quotes before its held quotes
        _entryBlock = input.Safety.EntryBlock ?? input.Safety.Flatten ?? input.Safety.Invalidate;
        _invalidate = input.Safety.Invalidate ?? input.Safety.Flatten;
        if (frame is not null)
        {
            var id = frame.Identity;
            var disposition = input.Disposition ?? throw new InvalidOperationException("a bar input needs its integrity disposition");
            if (_warmupSession != id.SessionId) { _warmupSession = id.SessionId; _verifiedBars = 0; }
            if (id.Verified) _verifiedBars++;                // completed, persisted and verified bars of this session evaluated so far (this one included); rebuilt identically by a journal replay
            _barSeq = id.BarSeq; _decisionTick = id.LastSourceTickId;
            _barAllowsEntries = disposition.EntriesAllowed && id.Verified;
            _barBlock = disposition.BlockReasons;
            if (id.Verified) _basket = frame.Basket;
            _dirty = true;
        }

        MarkPositions(input);
        FillPendingExits(input, output);
        FillPartialExits(input, output);

        var times = _options.Times;
        if (istTime >= times.EntryCutoff) CancelEntries(input, output, RejectionReason.OutsideEntryWindow, "entry still pending at the entry cut-off");
        if (_invalidate is { } invalid) CancelEntries(input, output, RejectionReason.EntriesBlocked, $"entry signal invalidated ({invalid})");
        else if (_entryBlock is { } block) SuspendEntries(input, output, $"entries blocked before the fill ({block})");
        else if (frame is not null && !_barAllowsEntries) SuspendEntries(input, output, $"entries blocked before the fill ({_barBlock})");
        FillPendingEntry(input, output);

        if (istTime >= times.LiquidationDeadline)
            foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending && !p.LiquidationReported))
            {
                p.LiquidationReported = true;
                output.Add(new LiquidationFailed($"{p.Id}:liqfail", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, p.Remaining,
                    $"not confirmed closed by {times.LiquidationDeadline:HH\\:mm\\:ss} IST; the exit keeps being retried"));
            }

        // positions that are still not closed after the retry cut-off are UNRESOLVED (reported, never silently dropped)
        if (istTime > _options.TerminalRetryUntil)
            foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending).ToArray())
            {
                p.State = PositionState.Unresolved;
                output.Add(Unresolved(p, input, $"not closed by the retry cut-off {_options.TerminalRetryUntil:HH\\:mm\\:ss} IST"));
            }

        if (istTime >= times.LiquidationStart)
            foreach (var p in _positions.Where(p => p.State == PositionState.Open)) RequestExit(p, ExitReason.Terminal, input, output);
        if (input.Safety.Flatten is not null)
            foreach (var p in _positions.Where(p => p.State == PositionState.Open)) RequestExit(p, ExitReason.Protective, input, output);

        BreachCheck(input, output);
        foreach (var p in _positions.Where(p => p.State == PositionState.Open).ToArray()) CheckOptionalExits(p, input, output);
        foreach (var p in _positions.Where(p => p.State == PositionState.Open)) CheckPartialTarget(p, input, output);

        if (frame is not null && frame.Identity.Verified) EvaluateSignals(input, frame, istTime, output);     // signals are only evaluated on verified bars; quote mechanics above still ran
        AdvanceWaiting(input, istTime, output);
    }

    static TimeOnly IstTime(DateTimeOffset at) => TimeOnly.FromTimeSpan((at + IstOffset).TimeOfDay);

    bool Holding() => _positions.Any(p => p.State is PositionState.Open or PositionState.ExitPending);

    bool Holding(OptionRight right) => _positions.Any(p => p.State is PositionState.Open or PositionState.ExitPending && p.Right == right);

    void EvaluateSignals(ExecutionInput input, MarketStateFrame frame, TimeOnly istTime, List<PaperEvent> output)
    {
        var ctx = new EvaluationContext(frame, input.Previous);
        var signals = _strategy.EvaluateEntry(ctx);
        var exitCondition = _strategy.EvaluateExit(ctx) == Tri.True;
        _obs = new Obs(signals.BuyCall, signals.BuyPut, Holding(OptionRight.Call), Holding(OptionRight.Put), HasPendingEntry);
        var direction = signals.BuyCall == Tri.True && signals.BuyPut != Tri.True ? OptionRight.Call
            : signals.BuyPut == Tri.True && signals.BuyCall != Tri.True ? OptionRight.Put : (OptionRight?)null;
        var reverses = _config.OnOppositeEntrySignal == OnOppositeEntrySignal.ExitThenEnterOpposite;

        // an opposite signal ends a pending entry of the other direction
        if (direction is { } d)
        {
            if (_waiting is { } w && w.Right != d) CancelWaiting(input, output, "an opposite signal arrived");
            if (_pending is { } o && o.Right != d && (reverses || o.Parent is not null))
            {
                var parent = o.Parent;
                Reject(input, output, o.Right, o.Id, RejectionReason.OppositeSignal, "an opposite signal arrived before the fill", cancelPending: true);
                if (parent is not null) { _waiting = parent; CancelWaiting(input, output, "an opposite signal arrived"); }
            }
        }

        var oppositeExit = false;
        foreach (var p in _positions.Where(p => p.State == PositionState.Open).ToArray())
        {
            if (exitCondition) RequestExit(p, ExitReason.Discretionary, input, output);
            else if ((p.Right == OptionRight.Put && signals.BuyCall == Tri.True) || (p.Right == OptionRight.Call && signals.BuyPut == Tri.True))
            {
                oppositeExit = true;
                RequestExit(p, ExitReason.OppositeSignal, input, output);
            }
        }

        if (oppositeExit)
        {
            // the exit never depends on a new entry; with reversal enabled the same signal creates an intent that waits until the position is confirmed closed
            if (reverses && direction is { } opposite) CreateWaiting(opposite, input, istTime, awaitingFlat: true, output);
            return;
        }

        if (signals.BuyCall == Tri.True && signals.BuyPut == Tri.True) { Reject(input, output, null, null, RejectionReason.ConflictingSignals, "buyCall and buyPut are both true on the same bar"); return; }
        if (direction is not { } right) return;
        if (_waiting is not null) return;                                  // a repeated signal never duplicates an intent
        if (_config.Timing == EntryTiming.Avg5Pullback)
        {
            if (_pending is not null) return;
            if (Holding(right)) { Reject(input, output, right, null, RejectionReason.PositionLimit, "a position of this direction is already held"); return; }
            CreateWaiting(right, input, istTime, awaitingFlat: Holding(), output);
        }
        else TryEntry(right, input, istTime, output);
    }

    /// <summary>The gates that apply to creating any new entry. Returns false after recording why.</summary>
    bool SignalGates(OptionRight right, ExecutionInput input, TimeOnly istTime, List<PaperEvent> output)
    {
        if (_entryBlock is { } block) { Reject(input, output, right, null, RejectionReason.EntriesBlocked, $"entries blocked ({block})"); return false; }
        if (!_barAllowsEntries) { Reject(input, output, right, null, RejectionReason.EntriesBlocked, $"entries blocked ({_barBlock})"); return false; }
        if (istTime >= _options.Times.EntryCutoff || !_strategy.IsEntryTimeAllowed(istTime)) { Reject(input, output, right, null, RejectionReason.OutsideEntryWindow, $"{istTime:HH\\:mm\\:ss} IST is outside the entry window"); return false; }
        if (_config.Filters.MinCompletedBars is { } minBars && (_verifiedBars < minBars || _barSeq < minBars))
        { Reject(input, output, right, null, RejectionReason.WarmupIncomplete, $"{_verifiedBars} verified bar(s) evaluated (bar {_barSeq}); {minBars} completed bars are required before an entry"); return false; }
        if (_riskBreached) { Reject(input, output, right, null, RejectionReason.RiskBreach, "daily loss limit breached"); return false; }
        if (_fillsToday >= _config.Risk.MaxDailyTrades) { Reject(input, output, right, null, RejectionReason.RiskBreach, "max daily trades reached"); return false; }
        return true;
    }

    void CreateWaiting(OptionRight right, ExecutionInput input, TimeOnly istTime, bool awaitingFlat, List<PaperEvent> output)
    {
        if (_waiting is not null || _pending is not null) return;
        if (!SignalGates(right, input, istTime, output)) return;
        string? reference = null;
        if (_config.Timing == EntryTiming.Avg5Pullback)
        {
            // the Option Price Averages grid's selection: the exact contract of this side at the bar's centre strike, frozen for the life of the intent
            reference = _basket?.Slots.FirstOrDefault(s => s.Offset == 0 && s.IsCall == (right == OptionRight.Call))?.Token;
            if (reference is null) { Reject(input, output, right, null, RejectionReason.InvalidInstrument, "no reference contract is resolved at the centre strike"); return; }
        }

        _waiting = new Waiting { Id = $"{_prefix}:{input.SessionId}:{_barSeq}:{right}", Right = right, SignalAt = input.Now, ReferenceToken = reference };
        output.Add(new EntryIntentCreated($"{_waiting.Id}:intent", StrategyId, _config.Identity.Hash, _barSeq, input.Now, _waiting.Id, right, reference, awaitingFlat));
    }

    void CancelWaiting(ExecutionInput input, List<PaperEvent> output, string why)
    {
        if (_waiting is not { } w) return;
        _waiting = null;
        output.Add(new EntryIntentCancelled($"{w.Id}:intentcancel", StrategyId, _config.Identity.Hash, _barSeq, input.Now, w.Id, w.Right, why));
    }

    /// <summary>
    /// A temporary eligibility failure: an outstanding order is cancelled and nothing may fill. An Avg5 intent keeps waiting; an immediate entry (reversal included) consumes its signal, so it never becomes an
    /// indefinitely waiting entry.
    /// </summary>
    void SuspendEntries(ExecutionInput input, List<PaperEvent> output, string detail)
    {
        if (_pending is not null) DropOrder(input, output, RejectionReason.EntriesBlocked, detail, keepIntent: true);
        if (_config.Timing != EntryTiming.Avg5Pullback) CancelWaiting(input, output, detail);
    }

    /// <summary>A terminal invalidation (safety, cut-off, daily limits): the pending order and the waiting intent are both consumed.</summary>
    void CancelEntries(ExecutionInput input, List<PaperEvent> output, RejectionReason reason, string detail)
    {
        if (_pending is { } o)
        {
            var parent = o.Parent;
            Reject(input, output, o.Right, o.Id, reason, detail, cancelPending: true);
            if (parent is not null) _waiting = parent;
        }

        CancelWaiting(input, output, detail);
    }

    /// <summary>The Avg5 condition: the frozen reference contract's fresh, coherent live mid is STRICTLY below that same contract's average ending at the latest completed bar.</summary>
    bool Avg5Met(string token, ExecutionInput input, out double mid, out double average)
    {
        mid = 0d; average = 0d;
        if (QuoteOf(token) is not { } q || !Fresh(q, input.Now) || q.Bid <= 0d || q.Ask < q.Bid) return false;
        var a = input.Averages.FirstOrDefault(x => x.Token == token && x.BarSeq == _barSeq);
        if (a?.Average is not { } v || !double.IsFinite(v) || v <= 0d) return false;          // unavailable or stale: wait
        mid = (q.Bid + q.Ask) / 2d; average = v;
        return mid < average;
    }

    void AdvanceWaiting(ExecutionInput input, TimeOnly istTime, List<PaperEvent> output)
    {
        if (_waiting is not { } w || _pending is not null) return;
        if (_riskBreached) { CancelWaiting(input, output, "daily loss limit breached"); return; }
        if (_fillsToday >= _config.Risk.MaxDailyTrades) { CancelWaiting(input, output, "max daily trades reached"); return; }
        var open = _positions.Count(p => p.State is PositionState.Open or PositionState.ExitPending);
        if (open >= _config.MaxConcurrent || Holding(w.Right == OptionRight.Call ? OptionRight.Put : OptionRight.Call)) return;
        if (!_barAllowsEntries || _entryBlock is not null || istTime >= _options.Times.EntryCutoff || !_strategy.IsEntryTimeAllowed(istTime)) return;

        if (_config.Timing == EntryTiming.Avg5Pullback)
        {
            if (w.ReferenceToken is null || !Avg5Met(w.ReferenceToken, input, out _, out _)) return;
            // never retry against the quotes of an order that already ended: both the reference and the contract need a later quote
            if (w.RetryAfter is { } after && QuoteOf(w.ReferenceToken)!.AvailableAt <= after) return;
            if (Select(w.Right, input, w.RetryAfter).Best is not { } pick) return;           // no executable contract yet: keep waiting
            Place(pick, w.Right, input, output, w);
        }
        else
        {
            var selection = Select(w.Right, input);
            if (selection.Best is not { } pick) { _waiting = null; Reject(input, output, w.Right, null, selection.Reason, selection.Why); return; }
            Place(pick, w.Right, input, output, w);
        }
    }

    static int DecisionBarOf(string intentId)
    {
        var parts = intentId.Split(':');
        return parts.Length >= 3 && int.TryParse(parts[^2], out var b) ? b : -1;
    }

    /// <summary>
    /// Builds the opportunity rows of ONE input from the events this strategy produced in it. Outcome rows (a placed order's fill / non-fill) are written whenever they resolve; the Decision row only for a verified bar.
    /// Pure observation: it reads the already-produced events and the signal snapshot, and changes nothing.
    /// </summary>
    void Classify(ExecutionInput input, List<PaperEvent> output, int start, List<OpportunityRecord> sink)
    {
        var mine = output.Skip(start).Where(e => e.StrategyId == StrategyId).ToList();
        var at = input.Frame?.Identity.BarEndAvailableAt ?? input.Now;
        var frameRef = $"{input.SessionId}:{_barSeq}";
        var coverage = _barBlock.ToString();
        var allowed = input.Disposition?.EntriesAllowed ?? _barAllowsEntries;
        OpportunityRecord Make(OpportunityStage stage, int bar, string dir, OpportunityCategory cat, string reason, string detail, bool inPos, bool eligible, bool attempted, string result,
            string? intent, string? token, string? evRef) =>
            new(OpportunityRecord.IdOf(StrategyId, input.SessionId, bar, stage), StrategyId, _config.Identity.Version, _config.Identity.Hash, input.SessionId, input.TradeDate, bar, stage, _barSeq, at,
                dir, cat, reason, detail, inPos, eligible, attempted, result, intent, token, evRef, frameRef, allowed, coverage);

        // Outcomes of orders placed EARLIER, resolved now.
        foreach (var e in mine)
        {
            if (e is EntryFilled f && DecisionBarOf(f.IntentId) is var fb and >= 0)
                sink.Add(Make(OpportunityStage.Outcome, fb, f.Right == OptionRight.Call ? "CE" : "PE", OpportunityCategory.OrderFilled, "Filled", $"filled at {f.FillPrice:0.00} x{f.Quantity}", false, true, true, "Filled", f.IntentId, f.Token, f.EventId));
            else if (e is EntryRejected { IntentId: { } iid } r && DecisionBarOf(iid) is var rb and >= 0)
            {
                var cat = OpportunityClassifier.OfOrderRejection(r.Reason);
                sink.Add(Make(OpportunityStage.Outcome, rb, r.Right == OptionRight.Call ? "CE" : r.Right == OptionRight.Put ? "PE" : "Unknown", cat, r.Reason.ToString(), r.Detail, false, true, true, cat == OpportunityCategory.OrderAcceptedNotFilled ? "ExpiredUnfilled" : "Rejected", iid, null, r.EventId));
            }
        }

        if (input.Frame is not { Identity.Verified: true } || _obs is not { } o) return;
        var dir = OpportunityClassifier.Direction(o.BuyCall, o.BuyPut);
        var right = o.BuyCall == Tri.True && o.BuyPut != Tri.True ? OptionRight.Call : o.BuyPut == Tri.True && o.BuyCall != Tri.True ? OptionRight.Put : (OptionRight?)null;
        var holdingSame = right is OptionRight.Call ? o.HoldingCall : right is OptionRight.Put && o.HoldingPut;
        var holding = o.HoldingCall || o.HoldingPut;
        var exitOpposite = mine.OfType<ExitRequested>().FirstOrDefault(x => x.Reason == ExitReason.OppositeSignal);
        var placed = mine.OfType<EntryOrderPlaced>().FirstOrDefault();
        var rejected = mine.OfType<EntryRejected>().FirstOrDefault(x => x.IntentId is null);

        OpportunityRecord decision;
        if (exitOpposite is not null)
            decision = Make(OpportunityStage.Decision, _barSeq, dir, OpportunityCategory.OppositeSignalExit, "OppositeSignal", "the opposite signal requested an exit of the held position", holding, false, false, "None", null, null, exitOpposite.EventId);
        else if (placed is not null)
            decision = Make(OpportunityStage.Decision, _barSeq, dir, OpportunityCategory.ValidDirectionalSignal, "OrderPlaced", $"entry order placed for {placed.Token}", holding, true, true, "Placed", placed.IntentId, placed.Token, placed.EventId);
        else if (rejected is not null)
        {
            var (cat, why) = OpportunityClassifier.OfDecisionRejection(rejected.Reason, holdingSame);
            var attempted = cat == OpportunityCategory.OrderRejectedQuoteOrContract;
            decision = Make(OpportunityStage.Decision, _barSeq, dir, cat, rejected.Reason.ToString(), $"{why}: {rejected.Detail}", holding, attempted, attempted, attempted ? "Rejected" : "None", null, null, rejected.EventId);
        }
        else if (mine.OfType<EntryIntentCreated>().FirstOrDefault() is { } created)
            decision = Make(OpportunityStage.Decision, _barSeq, dir, OpportunityCategory.ValidDirectionalSignal, "IntentCreated", "a pending entry intent was created", holding, true, false, "None", created.IntentId, created.ReferenceToken, created.EventId);
        else if (right is not null && o.HadPending)
            decision = Make(OpportunityStage.Decision, _barSeq, dir, OpportunityCategory.ValidDirectionalSignal, "IntentPending", "an entry intent is already pending", holding, true, false, "None", null, null, null);
        else
            decision = Make(OpportunityStage.Decision, _barSeq, dir, OpportunityCategory.NoDirectionalSignal, dir == "Unknown" ? "MetricUnavailable" : "NoSignal",
                dir == "Unknown" ? "a signal metric was unavailable on this bar" : "neither entry condition was true", holding, false, false, "None", null, null, null);
        sink.Add(decision);
    }

    void NewDay(ExecutionInput input, List<PaperEvent> output)
    {
        foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
        {
            p.State = PositionState.Unresolved;
            output.Add(Unresolved(p, input, "the trading day ended with the position still open"));
        }

        _pending = null; _waiting = null; _basket = null; _barAllowsEntries = false;
        _day = input.TradeDate; _realizedToday = 0m; _fillsToday = 0; _riskBreached = false; _dirty = true;
        var schedules = _options.CostSchedules.Where(s => string.Equals(s.Version, _config.FeeSchedule, StringComparison.Ordinal)).ToArray();
        _costs = new CostModel(new CostsConfig { Schedules = schedules }, input.TradeDate);          // throws (=> this strategy faults) when the schedule is not effective on the date
    }

    bool Fresh(QuoteSnapshot q, DateTimeOffset now) => q.AvailableAt <= now && (now - q.AvailableAt).TotalMilliseconds <= _config.Execution.MaxQuoteAgeMs;

    /// <summary>The quote of exactly this contract in the current input. Never another strike, never inferred, never remembered; null = unobserved.</summary>
    QuoteSnapshot? QuoteOf(string token) => _quotes.GetValueOrDefault(token);

    /// <summary>A fresh quote that became available strictly after the decision (plus the configured latency).</summary>
    QuoteSnapshot? FillQuoteOf(string token, DecisionKey decision, DateTimeOffset now) =>
        QuoteOf(token) is { } q && Fresh(q, now) && q.AvailableAt > decision.AvailableAt.AddMilliseconds(_config.Execution.LatencyMs) ? q : null;

    /// <summary>An immutable copy of today's positions for the read-only mark publisher (no engine state escapes). The cost schedule is a pure function, so the captured delegate is safe on another thread.</summary>
    public IReadOnlyList<MarkPosition> MarkSnapshot()
    {
        if (_day is not { } day || _costs is not { } costs) return [];
        var slip = (decimal)_config.Execution.ExitSlippage;
        return _positions.Select(p => new MarkPosition(
            StrategyId, _config.Identity.Version, day, _session, p.Id, p.Right, p.Token, p.Strike, p.Expiry, p.State == PositionState.Closed ? p.Quantity : p.Remaining, _config.Lots, p.EntryPrice,
            p.State == PositionState.Closed ? p.EntryFees : p.EntryFeesRemaining, p.EntryAt, p.State,
            _config.Execution.MaxQuoteAgeMs, slip, value => costs.Compute(TradeSide.Sell, value).Total, _realizedToday, p.ExitPrice, p.Gross, p.Net)).ToArray();
    }

    /// <summary>Tokens of the contracts this strategy currently holds (Open or ExitPending): they must stay observable until the position is closed or Unresolved.</summary>
    public IEnumerable<string> HeldTokens() => _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending).Select(p => p.Token);

    /// <summary>Every contract the next step may need a quote of: held, ordered, the Avg5 reference and the latest basket.</summary>
    public IEnumerable<string> WatchedTokens()
    {
        foreach (var t in HeldTokens()) yield return t;
        if (_pending is { } o)
        {
            yield return o.Token;
            if (o.Parent?.ReferenceToken is { } r) yield return r;
        }

        if (_waiting?.ReferenceToken is { } w) yield return w;
        if (_basket is { } b)
            foreach (var slot in b.Slots)
                if (slot.Token is { } t) yield return t;
    }

    void MarkPositions(ExecutionInput input)
    {
        foreach (var p in _positions.Where(p => p.State is PositionState.Open or PositionState.ExitPending))
        {
            if (input.Frame is { } frame) p.BarsHeld = frame.Identity.BarSeq - p.FillBarSeq;
            var q = QuoteOf(p.Token);
            if (q is null || !Fresh(q, input.Now))
            {
                if (input.Frame is not null) p.MarkGaps++;
                continue;
            }

            var mark = (decimal)q.Bid;
            var pnl = p.PartialGross + ((mark - p.EntryPrice) * p.Remaining);
            if (pnl > p.Mfe) { p.Mfe = pnl; _dirty = true; }
            if (pnl < p.Mae) { p.Mae = pnl; _dirty = true; }
            if (mark > p.Peak) { p.Peak = mark; _dirty = true; }
        }
    }

    void FillPendingExits(ExecutionInput input, List<PaperEvent> output)
    {
        var ex = _config.Execution;
        foreach (var p in _positions.Where(p => p.State == PositionState.ExitPending).ToArray())
        {
            if (FillQuoteOf(p.Token, p.ExitDecision!.Value, input.Now) is not { } q) continue;
            var quantity = p.Remaining;
            var price = Math.Max(0m, (decimal)q.Bid - (decimal)ex.ExitSlippage);
            var fees = _costs!.Compute(TradeSide.Sell, price * quantity).Total;
            var legGross = (price - p.EntryPrice) * quantity;
            var legNet = legGross - p.EntryFeesRemaining - fees;
            // the closed trade's P&L is the whole position (an earlier partial included); only this leg is newly realized
            p.State = PositionState.Closed; p.ExitPrice = price; p.ExitFees = fees; p.Gross = p.PartialGross + legGross; p.Net = p.PartialNet + legNet;
            p.Remaining = 0; p.EntryFeesRemaining = 0m;
            _cash += (price * quantity) - fees;
            _realizedToday += legNet;
            output.Add(new ExitFilled($"{p.Id}:xfill", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, p.ExitReason!.Value, quantity, price, fees, p.Gross.Value, p.Net.Value, q.AvailableAt, q.SourceTickId, q.BidQty, p.Mfe, p.Mae));
        }
    }

    void CheckPartialTarget(Position p, ExecutionInput input, List<PaperEvent> output)
    {
        if (p.PartialState != Partial.Armed || p.PartialTarget is not { } target) return;
        if (QuoteOf(p.Token) is not { } q || !Fresh(q, input.Now) || q.AvailableAt <= p.EntryAt || (decimal)q.Bid < target) return;
        var decision = new DecisionKey(input.Now, _decisionTick);
        p.PartialState = Partial.Requested; p.PartialDecision = decision;
        output.Add(new PartialExitRequested($"{p.Id}:preq", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, p.PartialQuantity, (decimal)q.Bid, target, decision));
    }

    void FillPartialExits(ExecutionInput input, List<PaperEvent> output)
    {
        // a mandatory exit of the whole position takes precedence over an unfilled partial
        if (input.Safety.Flatten is not null || IstTime(input.Now) >= _options.Times.LiquidationStart) return;
        foreach (var p in _positions.Where(p => p.State == PositionState.Open && p.PartialState == Partial.Requested).ToArray())
        {
            if (FillQuoteOf(p.Token, p.PartialDecision!.Value, input.Now) is not { } q) continue;
            var quantity = p.PartialQuantity;
            var price = Math.Max(0m, (decimal)q.Bid - (decimal)_config.Execution.ExitSlippage);
            var fees = _costs!.Compute(TradeSide.Sell, price * quantity).Total;
            var entryShare = p.EntryFeesRemaining * quantity / p.Remaining;
            var gross = (price - p.EntryPrice) * quantity;
            var net = gross - entryShare - fees;
            p.Remaining -= quantity; p.EntryFeesRemaining -= entryShare; p.PartialGross += gross; p.PartialNet += net; p.PartialState = Partial.Filled;
            _cash += (price * quantity) - fees;
            _realizedToday += net;
            output.Add(new PartialExitFilled($"{p.Id}:pfill", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, quantity, p.Remaining, price, fees, gross, net, q.AvailableAt, q.SourceTickId));
        }
    }

    /// <summary>Freezes the partial-profit policy for a position from the values at its entry fill. Nothing is recalculated afterwards.</summary>
    void AssignPartialTarget(Position p, int lot, ExecutionInput input, List<PaperEvent> output)
    {
        if (_config.PartialProfit is not { } policy) return;
        var dte = p.Expiry.DayNumber - input.TradeDate.DayNumber;               // calendar days to the expiry of the contract actually held
        var vix = input.Vix is { } v && double.IsFinite(v.Value) && v.Value > 0d && v.ObservedAt <= input.Now && (input.Now - v.ObservedAt).TotalSeconds <= policy.MaxVixAgeSeconds ? v : null;
        var bucket = policy.Targets.FirstOrDefault(t => t.Dte.Contains(dte));
        var quantity = (int)Math.Floor(p.Quantity * policy.ExitQuantityPercent / 100d / lot) * lot;
        var disabled = vix is null ? "India VIX is missing or stale at entry"
            : bucket is null ? $"no profit target is configured for DTE {dte}"
            : quantity < lot || p.Quantity - quantity < lot ? $"quantity {p.Quantity} cannot be split into whole lots of {lot} leaving a remainder"
            : null;
        var side = vix is null ? null : vix.Value >= policy.VixThreshold ? "AtOrAboveThreshold" : "BelowThreshold";
        double? percent = disabled is null ? (vix!.Value >= policy.VixThreshold ? bucket!.AtOrAboveVixPercent : bucket!.BelowVixPercent) : null;
        decimal? target = percent is { } pc ? p.EntryPrice * (1m + ((decimal)pc / 100m)) : null;
        if (disabled is null) { p.PartialState = Partial.Armed; p.PartialQuantity = quantity; p.PartialTarget = target; }
        output.Add(new PartialTargetAssigned($"{p.Id}:ptarget", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, dte, vix?.Value, vix?.ObservedAt, side, percent, target,
            disabled is null ? quantity : 0, p.EntryPrice, p.Quantity, p.Expiry, disabled));
    }

    void FillPendingEntry(ExecutionInput input, List<PaperEvent> output)
    {
        if (_pending is not { } it) return;
        var ex = _config.Execution;
        var q = QuoteOf(it.Token);
        if (q is null) Obstacle(it, RejectionReason.MissingQuote);
        else if (!Fresh(q, input.Now)) Obstacle(it, RejectionReason.StaleQuote);
        else if (q.AvailableAt > it.ExpiresAt) { }                                               // too late for this order: it expires below
        else if (q.AvailableAt <= it.Decision.AvailableAt.AddMilliseconds(ex.LatencyMs)) { }       // not strictly newer than the decision (+ latency)
        else
        {
            if (q.Ask < _config.Premium.Min || q.Ask > _config.Premium.Max) { DropOrder(input, output, RejectionReason.NoQualifyingContract, $"ask {q.Ask:0.##} at the fill quote is outside [{_config.Premium.Min}, {_config.Premium.Max}]", keepIntent: true); return; }
            if (!SpreadOk(q, ex)) { DropOrder(input, output, RejectionReason.SpreadTooWide, $"spread {q.Ask - q.Bid:0.##} at the fill quote exceeds the configured limit", keepIntent: true); return; }
            if (_riskBreached) { DropOrder(input, output, RejectionReason.RiskBreach, "daily loss limit breached before the fill", keepIntent: false); return; }
            if (_fillsToday >= _config.Risk.MaxDailyTrades) { DropOrder(input, output, RejectionReason.RiskBreach, "max daily trades reached before the fill", keepIntent: false); return; }
            var price = (decimal)q.Ask + (decimal)ex.EntrySlippage;
            var fees = _costs!.Compute(TradeSide.Buy, price * it.Quantity).Total;
            var cost = (price * it.Quantity) + fees;
            if (cost > _cash) { DropOrder(input, output, RejectionReason.InsufficientCapital, $"cost {cost:0.00} exceeds available capital {_cash:0.00}", keepIntent: true); return; }
            var mid = 0d; var average = 0d;
            var reference = it.Parent?.ReferenceToken;
            if (reference is not null && !Avg5Met(reference, input, out mid, out average)) { DropOrder(input, output, RejectionReason.Avg5NotMet, "the reference contract's live mid is no longer below its average, or either is unavailable", keepIntent: true); return; }
            _cash -= cost; _fillsToday++;
            var pos = new Position
            {
                Id = it.Id, Right = it.Right, Token = it.Token, Strike = it.Strike, Expiry = it.Expiry, Quantity = it.Quantity, EntryPrice = price, EntryFees = fees, FillBarSeq = _barSeq, Peak = price, EntryAt = input.Now,
                Remaining = it.Quantity, EntryFeesRemaining = fees,
            };
            _positions.Add(pos);
            _pending = null;
            output.Add(new EntryFilled($"{it.Id}:filled", StrategyId, _config.Identity.Hash, _barSeq, input.Now, it.Id, pos.Id, it.Right, it.Token, it.Quantity, price, fees, q.AvailableAt, q.SourceTickId, q.AskQty));
            if (reference is not null)
                output.Add(new Avg5EntryEvidence($"{it.Id}:avg5", StrategyId, _config.Identity.Hash, _barSeq, input.Now, pos.Id, it.Parent!.SignalAt, reference, mid, average, _barSeq, it.Token, (input.Now - it.Parent.SignalAt).TotalSeconds));
            AssignPartialTarget(pos, it.LotSize, input, output);
            return;
        }

        if (input.Now > it.ExpiresAt)
            DropOrder(input, output, it.Obstacle ?? RejectionReason.ExpiredOpportunity, "the entry order lifetime elapsed without a qualifying fill quote", keepIntent: true);
    }

    void Obstacle(Intent it, RejectionReason reason)
    {
        if (it.Obstacle == reason) return;
        it.Obstacle = reason; _dirty = true;
    }

    /// <summary>Ends the pending order (paper fills are all-or-none, so it ended with no fill). An Avg5 intent survives a transient failure and waits again; every other order consumes its signal.</summary>
    void DropOrder(ExecutionInput input, List<PaperEvent> output, RejectionReason reason, string detail, bool keepIntent)
    {
        var it = _pending!;
        Reject(input, output, it.Right, it.Id, reason, detail, cancelPending: true);
        if (it.Parent is not { } parent || _config.Timing != EntryTiming.Avg5Pullback) return;
        parent.RetryAfter = input.Now;
        _waiting = parent;
        if (!keepIntent) CancelWaiting(input, output, detail);
    }

    static bool SpreadOk(QuoteSnapshot q, ExecutionPolicy ex)
    {
        var spread = q.Ask - q.Bid;
        if (ex.MaxSpreadAbsolute is { } a && spread > a) return false;
        return ex.MaxSpreadFraction is not { } f || spread <= f * ((q.Ask + q.Bid) / 2d);
    }

    void BreachCheck(ExecutionInput input, List<PaperEvent> output)
    {
        if (_riskBreached || _realizedToday > -(decimal)_config.Risk.MaxDailyLoss) return;
        _riskBreached = true;
        output.Add(new RiskBreached($"{_prefix}:{input.TradeDate:yyyyMMdd}:riskbreach", StrategyId, _config.Identity.Hash, _barSeq, input.Now, _realizedToday, (decimal)_config.Risk.MaxDailyLoss));
        CancelEntries(input, output, RejectionReason.RiskBreach, "daily loss limit breached");
        if (_config.Risk.Liquidation == LiquidationPolicy.ExitOpenPositions)
            foreach (var p in _positions.Where(p => p.State == PositionState.Open)) RequestExit(p, ExitReason.RiskLiquidation, input, output);
    }

    void CheckOptionalExits(Position p, ExecutionInput input, List<PaperEvent> output)
    {
        var x = _config.Exits;
        if (!x.Any) return;
        if (x.MaxHoldBars is { } hold && p.BarsHeld >= hold) { RequestExit(p, ExitReason.MaxHold, input, output); return; }
        if (QuoteOf(p.Token) is not { } q || !Fresh(q, input.Now)) return;      // no usable mark: never assume a threshold price
        var mark = (decimal)q.Bid;
        if (x.StopLoss is { } sl && mark <= p.EntryPrice - Distance(sl, p.EntryPrice)) RequestExit(p, ExitReason.StopLoss, input, output);
        else if (x.Target is { } tg && mark >= p.EntryPrice + Distance(tg, p.EntryPrice)) RequestExit(p, ExitReason.Target, input, output);
        else if (x.Trailing is { } tr && p.Peak - mark >= Distance(tr, p.EntryPrice)) RequestExit(p, ExitReason.Trailing, input, output);
    }

    static decimal Distance(ThresholdExit t, decimal entry) => t.Kind == DistanceKind.Points ? (decimal)t.Value : entry * (decimal)t.Value / 100m;

    /// <summary>One full-exit request per position; it supersedes an unfilled partial exit.</summary>
    void RequestExit(Position p, ExitReason reason, ExecutionInput input, List<PaperEvent> output)
    {
        if (p.State != PositionState.Open) return;
        var decision = new DecisionKey(input.Now, _decisionTick);
        p.State = PositionState.ExitPending; p.ExitReason = reason; p.ExitDecision = decision;
        if (p.PartialState is Partial.Armed or Partial.Requested) p.PartialState = Partial.Cancelled;
        output.Add(new ExitRequested($"{p.Id}:xreq", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, reason, decision));
    }

    PositionUnresolved Unresolved(Position p, ExecutionInput input, string detail) =>
        new($"{p.Id}:unres", StrategyId, _config.Identity.Hash, _barSeq, input.Now, p.Id, detail);

    void Reject(ExecutionInput input, List<PaperEvent> output, OptionRight? right, string? intentId, RejectionReason reason, string detail, bool cancelPending = false)
    {
        if (cancelPending) _pending = null;
        var key = intentId ?? $"{_prefix}:{input.SessionId}:{_barSeq}:{(right?.ToString() ?? "any")}";
        output.Add(new EntryRejected($"{key}:rej:{reason}", StrategyId, _config.Identity.Hash, _barSeq, input.Now, right, intentId, reason, detail));
    }

    readonly record struct Selection((QuoteSnapshot Quote, BasketSlot Slot)? Best, RejectionReason Reason, string Why);

    /// <summary>The contract of the latest verified basket from the current quotes: fresh, ask in the premium range, spread within the limit, then the configured ranking.</summary>
    Selection Select(OptionRight right, ExecutionInput input, DateTimeOffset? newerThan = null)
    {
        if (_basket is not { } basket) return new Selection(null, RejectionReason.InvalidInstrument, "no verified basket is available");
        var wantCall = right == OptionRight.Call;
        var slots = basket.Slots.Where(s => s.IsCall == wantCall && s.Token is not null && s.Strike is not null).ToArray();
        var quoted = 0; var fresh = 0; var inRange = 0;
        (QuoteSnapshot Q, BasketSlot S)? best = null;
        var bestKey = (0d, 0d, 0d, string.Empty);
        foreach (var s in slots)
        {
            var q = QuoteOf(s.Token!);
            if (q is null) continue;
            quoted++;
            if (!Fresh(q, input.Now) || (newerThan is { } after && q.AvailableAt <= after)) continue;
            fresh++;
            if (q.Ask < _config.Premium.Min || q.Ask > _config.Premium.Max) continue;
            inRange++;
            if (!SpreadOk(q, _config.Execution)) continue;
            var key = (Math.Abs(q.Ask - _config.Premium.Target), Math.Abs(s.Strike!.Value - basket.Centre), s.Strike.Value, s.Token!);
            if (best is null || Compare(key, bestKey) < 0) { best = (q, s); bestKey = key; }
        }

        if (best is { } found) return new Selection(found, default, string.Empty);
        var (reason, why) = slots.Length == 0 ? (RejectionReason.InvalidInstrument, "the basket has no resolved contract on this side")
            : quoted == 0 ? (RejectionReason.MissingQuote, "no basket contract of this side has a quote")
            : fresh == 0 ? (RejectionReason.StaleQuote, "every quote of this side is older than the maximum quote age")
            : inRange == 0 ? (RejectionReason.NoQualifyingContract, $"no ask within [{_config.Premium.Min}, {_config.Premium.Max}]")
            : (RejectionReason.SpreadTooWide, "every in-range contract exceeds the spread limit");
        return new Selection(null, reason, why);
    }

    void TryEntry(OptionRight right, ExecutionInput input, TimeOnly istTime, List<PaperEvent> output)
    {
        if (!SignalGates(right, input, istTime, output)) return;
        var open = _positions.Count(p => p.State is PositionState.Open or PositionState.ExitPending);
        if (_pending is not null || open + 1 > _config.MaxConcurrent) { Reject(input, output, right, null, RejectionReason.PositionLimit, $"{open} open position(s), pending order: {_pending is not null}, limit {_config.MaxConcurrent}"); return; }
        var selection = Select(right, input);
        if (selection.Best is not { } pick) { Reject(input, output, right, null, selection.Reason, selection.Why); return; }
        Place(pick, right, input, output, null);
    }

    void Place((QuoteSnapshot Quote, BasketSlot Slot) pick, OptionRight right, ExecutionInput input, List<PaperEvent> output, Waiting? parent)
    {
        var ex = _config.Execution;
        var (bq, bs) = pick;
        var lot = _options.LotSizes.LotSize(input.TradeDate, bq.Token);
        (RejectionReason Reason, string Why)? rejection = lot is not > 0 ? (RejectionReason.LotSizeViolation, $"no lot size for token {bq.Token} on {input.TradeDate:yyyy-MM-dd}") : null;
        var qty = 0;
        if (rejection is null)
        {
            qty = checked(_config.Lots * lot!.Value);
            var estimate = ((decimal)bq.Ask + (decimal)ex.EntrySlippage) * qty;
            var estCost = estimate + _costs!.Compute(TradeSide.Buy, estimate).Total;
            if (estCost > _cash) rejection = (RejectionReason.InsufficientCapital, $"estimated cost {estCost:0.00} exceeds available capital {_cash:0.00}");
        }

        if (rejection is { } r)
        {
            // no order was placed. An Avg5 intent stays (this is not a terminal condition) and the reason is reported once, not on every quote
            var keep = parent is not null && _config.Timing == EntryTiming.Avg5Pullback;
            if (!keep || parent!.Reported.Add(r.Reason)) Reject(input, output, right, null, r.Reason, r.Why);
            if (parent is not null && !keep) { _waiting = parent; CancelWaiting(input, output, r.Why); }
            return;
        }

        var intentId = parent is null ? $"{_prefix}:{input.SessionId}:{_barSeq}:{right}" : parent.Orders++ == 0 ? parent.Id : $"{parent.Id}#{parent.Orders}";
        var decision = new DecisionKey(input.Now, _decisionTick);
        _waiting = null;
        _pending = new Intent
        {
            Id = intentId, Right = right, Token = bq.Token, Strike = bs.Strike!.Value, Expiry = _basket!.Expiry, LotSize = lot!.Value, Quantity = qty, SelectedAsk = bq.Ask,
            Decision = decision, ExpiresAt = input.Now.AddMilliseconds(ex.EntryOrderLifetimeMs), Parent = parent,
        };
        output.Add(new EntryOrderPlaced($"{intentId}:placed", StrategyId, _config.Identity.Hash, _barSeq, input.Now, intentId, right, bq.Token, bs.Strike.Value, _basket.Expiry,
            lot.Value, qty, (decimal)bq.Ask, decision, _pending.ExpiresAt));
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

/// <summary>A pending entry as reported (observation only).</summary>
public sealed record PendingEntryView(string IntentId, OptionRight Right, DateTimeOffset SignalAt, string? ReferenceToken, bool OrderPlaced, string? OrderToken);

/// <summary>One strategy's current standing, including a flat or waiting one (observation only).</summary>
public sealed record StrategyStatus(string StrategyId, int Version, EntryTiming Timing, bool Faulted, int TradesToday, int MaxDailyTrades, decimal RealizedNetToday, decimal Cash, bool RiskBreached, int VerifiedBars, PendingEntryView? Pending);
