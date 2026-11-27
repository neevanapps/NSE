using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;

namespace NiftySignal.ExecutionService;

/// <summary>
/// One cycle of the execution service: ownership, Observer health, new ticks, newly published bars (in order), then the timer step. An input that carries a bar, or that changes state, is committed with its
/// ledger events in one fenced transaction; if that fails the engine is discarded and rebuilt from the journal, so memory is never ahead of the database. Single-threaded: only the service loop calls it.
/// </summary>
public sealed class ExecutionCoordinator(
    ExecutionServiceOptions options, NpgsqlDataSource db, IExecutionFeed feed, IReadOnlyList<CompiledStrategy> strategies, ExecutionOptions execution, SessionLotSizes lots,
    ServiceAlerts alerts, TimeProvider clock, ILogger? log = null, IReadOnlyList<StrategyConfig>? disabled = null,
    Func<CancellationToken, Task>? beforeCommit = null) : IAsyncDisposable          // beforeCommit: failure injection for tests only
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly TimeOnly ObserverRequiredFrom = new(9, 20), MarketTo = new(15, 30);
    const int BarBatch = 200;
    static readonly TimeSpan LagLogInterval = TimeSpan.FromMinutes(1);

    readonly ExecutionStore _store = new(db);
    readonly Dictionary<string, QuoteSnapshot> _quotes = new(StringComparer.Ordinal);
    readonly Dictionary<(int Bar, string Token), ContractAverage> _averages = [];
    readonly Dictionary<string, DateTimeOffset> _exitSince = new(StringComparer.Ordinal);
    readonly Capture _capture = new();
    ExecutionOwnership? _ownership;
    long _epoch;
    PaperEngine? _engine;
    FrameConsumer? _consumer;
    V2SessionInfo? _session;
    long? _consumerSession;
    DateOnly _day;
    int _skippedThrough;
    int _failedBar, _failedAttempts;
    string? _frameProblem;
    bool _reconciled;
    string? _reconcileDetail;
    string? _earlierDayOutstanding;
    VixReading? _vix;
    DateTimeOffset? _marketDataAt;
    DateTimeOffset? _notLiveSince;
    // Observer progress: option and future ticks read here that its watermark has not passed (oldest first), where that reading began, and how long a completed bar has been waiting for evaluation.
    readonly PriorityQueue<DateTimeOffset, DateTimeOffset> _unprocessed = new();
    DateTimeOffset? _ticksReadFrom, _barsBehindSince, _lagLoggedAt;
    bool _barsWereCurrent;
    DateTimeOffset _heartbeatAt, _marksAt;
    LiveMarkBoard? _board;
    long? _boardSession;
    LiveMarkPublisher? _marks;
    CommittedOpportunities? _opportunities;

    public ExecutionSnapshot? Snapshot { get; private set; }

    public PaperEngine? Engine => _engine;

    public bool IsOwner => _ownership is not null;

    public long Epoch => _epoch;

    /// <summary>Runs one cycle. A failure leaves no partial state behind and is rethrown to the service loop, which alerts and runs the next cycle.</summary>
    public async Task RunCycleAsync(CancellationToken ct)
    {
        try
        {
            await CycleAsync(ct);
        }
        catch (OwnershipLostException)
        {
            await ReleaseAsync();
            throw;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _engine = null;                      // whatever was in memory may not be in the database
            throw;
        }
    }

    async Task CycleAsync(CancellationToken ct)
    {
        if (_ownership is not null && !await _ownership.IsHeldAsync(ct)) await ReleaseAsync();
        if (_ownership is null && !await TakeOwnershipAsync(ct)) return;

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime((now + Ist).DateTime);
        var read = await feed.ReadSessionAsync(today, ct);
        if (_day != today) ResetDay(today);
        if (read.Session is not null) { _session = read.Session; lots.Set(read.Session); }
        if (_engine is null) await RebuildAsync(ct);
        if (_engine is null)
        {
            const string waiting = "execution state cannot be rebuilt yet (the Observer session is unreadable): entries and exits wait";
            alerts.Update(new Dictionary<string, string> { ["recovery"] = waiting });
            await WriteHeartbeatAsync(new ExecutionSnapshot(now, "Recovering", today, null, null, false, waiting, null, [], [], [waiting]), ct);
            return;
        }

        if (_session is { } known)
        {
            if (_boardSession != known.SessionId)
            {
                _board = new LiveMarkBoard(known.LotSizes.Keys); _boardSession = known.SessionId;
                ResetProgress();
            }

            // ticks come from the trading database: protective exits keep their quotes even while the Observer database is unreadable
            var readAt = clock.GetUtcNow();
            foreach (var t in await feed.ReadTicksAsync(known, readAt, ct)) Observe(t);
            _ticksReadFrom ??= readAt - options.StartupQuoteLookback;       // only once a first read has succeeded
        }

        if (read.Observer.ProcessedThroughUtc is { } through && through <= clock.GetUtcNow())
            while (_unprocessed.TryPeek(out var oldest, out _) && oldest <= through) _unprocessed.Dequeue();

        var observer = read.Observer;
        _notLiveSince = observer is { SessionExists: true, Status: AdaptiveRuntimeStatus.Live } ? null : _notLiveSince ?? clock.GetUtcNow();

        if (_session is { } session)
        {
            if (read.Session is not null) await ProcessBarsAsync(session, observer, ct);
            var watched = _engine.WatchedTokens();
            var references = _engine.Statuses().Select(s => s.Pending?.ReferenceToken).OfType<string>().Distinct().ToList();
            var averages = read.Session is null ? [] : await AveragesAsync(session, references, _consumer?.Status.LastBarSeq ?? 0, ct);
            var at = clock.GetUtcNow();
            await ApplyAsync(new ExecutionInput(at, today, session.SessionId, QuotesOf(watched), _vix, averages, Safety(observer, at)), ct);
            await PublishMarksAsync(at, ct);
        }

        var end = clock.GetUtcNow();
        var safety = Safety(observer, end);
        if (safety.EntryBlock is { } blocked && blocked.StartsWith(ProtectionPolicy.ProcessingPrefix, StringComparison.Ordinal) && (_lagLoggedAt is not { } logged || end - logged >= LagLogInterval))
        {
            _lagLoggedAt = end;
            log?.LogWarning("Paper entries are blocked: {Reason} (processed through {ProcessedThrough}, oldest waiting tick {Waiting}, last completed bar {Bar}). Exits continue.",
                blocked, observer.ProcessedThroughUtc, _unprocessed.TryPeek(out var first, out _) ? first : null, observer.LastCompletedBarSeq);
        }

        var conditions = Describe(observer, safety, end);
        alerts.Update(conditions);
        if (end - _heartbeatAt < options.HeartbeatInterval) return;
        var engine = _engine;
        await WriteHeartbeatAsync(new ExecutionSnapshot(end, !_reconciled ? "Unreconciled" : safety.Flatten is not null ? "Protecting" : safety.EntryBlock is not null ? "EntriesBlocked" : "Running", today,
            _session?.SessionId, _consumer?.Status.LastBarSeq, _reconciled, safety.EntryBlock, safety.Flatten, engine.Statuses(), engine.Runtimes.SelectMany(r => r.Positions()).ToList(), conditions.Values.ToArray()), ct);
    }

    async Task<bool> TakeOwnershipAsync(CancellationToken ct)
    {
        var verdict = await V2SchemaCheck.CheckAsync(db, ct);
        if (!verdict.Compatible || !await _store.IsInstalledAsync(ct))
        {
            var why = verdict.Compatible ? "the execution tables are missing" : string.Join("; ", verdict.Problems);
            alerts.Update(new Dictionary<string, string> { ["schema"] = $"the paper ledger schema is not ready ({why}; owner-run database script): NOTHING is executed" });
            return false;
        }

        _ownership = await ExecutionOwnership.TryAcquireAsync(db, ct, $"{Environment.MachineName}:{Environment.ProcessId}", clock.GetUtcNow());
        if (_ownership is null)
        {
            alerts.Update(new Dictionary<string, string> { ["writer"] = "another execution writer holds the ledger: this instance does nothing" });
            return false;
        }

        _engine = null;
        var previous = await _store.ReadStateAsync(ct);
        _epoch = _ownership.Epoch ?? throw new InvalidOperationException("the execution ownership carries no epoch");
        _earlierDayOutstanding = Outstanding(previous, DateOnly.FromDateTime((clock.GetUtcNow() + Ist).DateTime));
        var events = new PostgresEventStore(db, _ownership);
        foreach (var config in strategies.Select(s => s.Config).Concat(disabled ?? [])) await events.RecordConfigAsync(config, clock.GetUtcNow(), ct);
        log?.LogInformation("Execution ownership taken with epoch {Epoch}.", _epoch);
        return true;
    }

    static string? Outstanding(ExecutionStateView? previous, DateOnly today)
    {
        if (previous is null || ExecutionStore.ReadSnapshot(previous.SnapshotJson) is not { } s || s.TradeDate >= today) return null;
        var open = s.Positions.Where(p => p.State != PositionState.Closed).ToList();
        return open.Count == 0 ? null : $"{open.Count} position(s) from {s.TradeDate:yyyy-MM-dd} were never closed ({string.Join(", ", open.Select(p => $"{p.StrategyId} {p.Token} x{p.RemainingQuantity}"))}): no exit fill is recorded";
    }

    void ResetDay(DateOnly today)
    {
        _day = today; _engine = null; _session = null; _board = null; _boardSession = null; _vix = null; _marketDataAt = null; _notLiveSince = null;
        _quotes.Clear(); _averages.Clear(); _exitSince.Clear();
        ResetProgress();
    }

    void ResetProgress()
    {
        _unprocessed.Clear(); _ticksReadFrom = null; _barsBehindSince = null; _barsWereCurrent = false;
    }

    SafetyState Safety(ObserverState observer, DateTimeOffset now)
    {
        // A bar the Observer completed a moment ago is normally still being published: it gets the processing-lag allowance. After a start or a rebuild there is none until execution has caught up once.
        var behind = observer.LastCompletedBarSeq > Math.Max(_consumer?.Status.LastBarSeq ?? 0, _skippedThrough);
        if (!behind) { _barsWereCurrent = true; _barsBehindSince = null; }
        else _barsBehindSince ??= now;
        var overdue = behind && (!_barsWereCurrent || now - _barsBehindSince > options.MaxProcessingLag);

        return ProtectionPolicy.Evaluate(new ProtectionContext(
            now, TimeOnly.FromDateTime((now + Ist).DateTime), observer, _engine?.HeldTokens.Count > 0, _notLiveSince is { } since ? now - since : TimeSpan.Zero, _marketDataAt, _reconciled, _frameProblem,
            _unprocessed.TryPeek(out var oldest, out _) ? oldest : null, _ticksReadFrom, overdue), options);
    }

    void Observe(FeedTick t)
    {
        var tick = t.Tick;
        if (!t.IsVix) _unprocessed.Enqueue(tick.AvailableAt, tick.AvailableAt);
        if (t.ProgressOnly) return;
        if (_marketDataAt is not { } latest || tick.AvailableAt > latest) _marketDataAt = tick.AvailableAt;
        if (t.IsVix)
        {
            if (tick.Last > 0d && (_vix is null || tick.AvailableAt >= _vix.ObservedAt)) _vix = new VixReading(tick.Last, tick.AvailableAt);
            return;
        }

        _board?.Observe(t.Token, in tick);
        if (_quotes.TryGetValue(t.Token, out var q) && (tick.AvailableAt < q.AvailableAt || (tick.AvailableAt == q.AvailableAt && tick.Id <= q.SourceTickId))) return;
        _quotes[t.Token] = new QuoteSnapshot(t.Token, tick.Bid, tick.Ask, tick.BidQty, tick.AskQty, tick.AvailableAt, tick.Id);
    }

    IReadOnlyList<QuoteSnapshot> QuotesOf(IEnumerable<string> tokens) =>
        tokens.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).Select(t => _quotes.GetValueOrDefault(t)).OfType<QuoteSnapshot>().ToList();

    async Task<IReadOnlyList<ContractAverage>> AveragesAsync(V2SessionInfo session, IReadOnlyCollection<string> tokens, int bar, CancellationToken ct)
    {
        if (bar < 1 || tokens.Count == 0) return [];
        var missing = tokens.Where(t => !_averages.ContainsKey((bar, t))).ToList();
        if (missing.Count > 0)
        {
            foreach (var key in _averages.Keys.Where(k => k.Bar != bar).ToArray()) _averages.Remove(key);
            foreach (var a in await feed.ReadAveragesAsync(session, missing, bar, ct)) _averages[(bar, a.Token)] = a;
        }

        return tokens.Select(t => _averages.GetValueOrDefault((bar, t))).OfType<ContractAverage>().ToList();
    }

    async Task ProcessBarsAsync(V2SessionInfo session, ObserverState observer, CancellationToken ct)
    {
        if (_consumerSession != session.SessionId) { _consumer = null; _consumerSession = session.SessionId; _skippedThrough = 0; _frameProblem = null; }
        var after = Math.Max(_consumer?.Status.LastBarSeq ?? 0, _skippedThrough);
        foreach (var bar in await feed.ReadPublishedAsync(session.SessionId, after, BarBatch, ct))
        {
            var (frame, problem) = await feed.BuildFrameAsync(session, bar, ct);
            if (frame is null)
            {
                if (_failedBar != bar.BarSeq) { _failedBar = bar.BarSeq; _failedAttempts = 0; }
                _frameProblem = $"bar {bar.BarSeq} cannot be assembled: {problem}";
                if (++_failedAttempts < options.FrameBuildAttempts) return;
                log?.LogError("Skipping bar {Bar} after {Attempts} attempts: {Problem}. It is a gap; entries stay blocked until clean bars resume.", bar.BarSeq, _failedAttempts, problem);
                _skippedThrough = bar.BarSeq;
                continue;
            }

            _frameProblem = null;
            _consumer ??= NewConsumer();
            _capture.Next = null;
            await _consumer.HandleAsync(frame, ct);
            if (_consumer.Status.Faulted) throw new InvalidOperationException("frame consumer faulted: " + _consumer.Status.FaultMessage);
            if (_capture.Next is not { } accepted) continue;

            // the references include the basket centre so that an intent created by this very bar has its contract average
            var centre = accepted.Frame.Basket.Slots.Where(s => s.Offset == 0 && s.Token is not null).Select(s => s.Token!);
            var references = _engine!.Statuses().Select(s => s.Pending?.ReferenceToken).OfType<string>().Concat(centre).Distinct().ToList();
            var tokens = _engine.WatchedTokens().Concat(accepted.Frame.Basket.Slots.Select(s => s.Token).OfType<string>());
            var averages = await AveragesAsync(session, references, accepted.Frame.Identity.BarSeq, ct);
            var now = clock.GetUtcNow();
            await ApplyAsync(new ExecutionInput(now, accepted.Frame.Identity.TradeDate, session.SessionId, QuotesOf(tokens), _vix, averages, Safety(observer, now), accepted.Frame, accepted.Previous, accepted.Disposition), ct);
        }
    }

    async Task ApplyAsync(ExecutionInput input, CancellationToken ct)
    {
        _opportunities?.Discard();
        var (changed, events) = _engine!.Step(input);
        // every bar is journaled: the replayed previous-frame link and sequence must match the live ones
        if (!changed && input.Frame is null) { _opportunities?.Flush(); return; }
        var queued = new List<(string, string, PaperEvent, string)>();
        foreach (var e in events)
            if (AlertFormatter.ClassOf(e) is { } c && options.Alerts.Enabled(c))
                queued.Add((AlertFormatter.AlertId(e, c), c.ToString(), e, AlertFormatter.Format(e, VersionOf(e.StrategyId), EntryContextLookup.Resolve(db, e))));
        if (beforeCommit is not null) await beforeCommit(ct);
        await _store.CommitAsync(Owner, input, events, queued, ct);
        _opportunities?.Flush();
        // the detailed trade commentary: one structured line per committed ledger event (the Dashboard shows positions and totals only)
        foreach (var e in events)
            log?.LogInformation("Paper ledger {Kind} {StrategyId} bar {BarSeq} at {At}: {EventJson}", e.Kind, e.StrategyId, e.BarSeq, e.At, EventSerializer.Serialize(e));
        // failures are also sent directly, whatever the outbox opt-ins are
        foreach (var e in events)
            if (e is LiquidationFailed or StrategyFaulted or PositionUnresolved) alerts.Raise(AlertFormatter.Format(e, VersionOf(e.StrategyId)));
    }

    ExecutionOwnership Owner => _ownership ?? throw new OwnershipLostException("this instance does not hold the paper ledger");

    int? VersionOf(string strategyId) => strategies.FirstOrDefault(s => s.Config.Identity.StrategyId == strategyId)?.Config.Identity.Version;

    FrameConsumer NewConsumer() =>
        new(null, new HandoffOptions(1, options.MaxProcessingLag, options.FramesToClearGap), new UnkeptFrameJournal(), _capture, clock);

    static bool IsReplayFailure(Exception ex) => ex is ReplayIntegrityException or FrameValidationException or JsonException or InvalidOperationException;

    /// <summary>Replays today's journal into a fresh engine and compares the regenerated events with the stored ledger. A difference leaves the state unreconciled: entries blocked, exits and protection continue.</summary>
    async Task RebuildAsync(CancellationToken ct)
    {
        _consumer = null; _consumerSession = null; _skippedThrough = 0; _frameProblem = null; _reconciled = false; _reconcileDetail = null;
        _barsBehindSince = null; _barsWereCurrent = false;
        var page = await _store.ReadJournalPageAsync(_day, 0, options.JournalReplayBatchSize, ct);
        // the lot sizes of the frozen session are needed to reproduce the day's orders
        if (page.Count > 0 && _session is null) return;

        var opportunities = await ObservabilitySchema.HasAsync(db, "pev2_opportunities", ct) ? new CommittedOpportunities(new PostgresOpportunityStore(db, clock, Owner)) : null;
        var engine = new PaperEngine(strategies, execution, null, opportunities);
        // Of the replayed frames only the sequence guard's memory (bar number and hash) and the newest frame are kept: the day is never held in memory as a whole.
        var hashes = new Dictionary<long, Dictionary<int, string>>();
        MarketStateFrame? previous = null;
        long inputs = 0;
        var replayed = true;
        while (page.Count > 0 && replayed)
        {
            try
            {
                foreach (var (_, row) in page)
                {
                    var input = row.ToInput(previous);
                    engine.Step(input);
                    inputs++;
                    if (input.Frame is not { } frame) continue;
                    previous = frame;
                    if (!hashes.TryGetValue(frame.Identity.SessionId, out var bars)) bars = hashes[frame.Identity.SessionId] = [];
                    bars.TryAdd(frame.Identity.BarSeq, frame.Hash);
                }
            }
            catch (Exception ex) when (IsReplayFailure(ex))
            {
                replayed = false;
                _reconcileDetail = $"the execution journal cannot be replayed: {ex.GetType().Name}: {ex.Message}";
                continue;
            }

            page = await _store.ReadJournalPageAsync(_day, page[^1].Id, options.JournalReplayBatchSize, ct);
        }

        if (replayed)
        {
            try
            {
                var from = new DateTimeOffset(_day.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
                var report = await LedgerReconciler.ReconcileAsync(engine.Events, new PostgresEventStore(db), from, from.AddDays(1), ct);
                _reconciled = report.Clean;
                if (!report.Clean) _reconcileDetail = $"replay differs from the ledger (missing {report.MissingInStore}, extra {report.ExtraInStore}, different {report.Different})";
            }
            catch (Exception ex) when (IsReplayFailure(ex))
            {
                _reconcileDetail = $"the execution journal cannot be replayed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        if (previous is not null && _session is { } s && previous.Identity.SessionId == s.SessionId)
        {
            _consumer = NewConsumer();
            _consumerSession = s.SessionId;
            _consumer.Resume(previous, hashes[s.SessionId]);
        }

        if (opportunities is not null) opportunities.HoldUntilCommit = true;      // replayed inputs were already committed; from here a row follows its input
        _opportunities = opportunities;
        _engine = engine;
        _marks = options.LiveMarksEnabled && await ObservabilitySchema.HasAsync(db, "pev2_live_marks", ct)
            ? new LiveMarkPublisher(() => _engine?.MarkPositions ?? [], () => _board, new PostgresLiveMarkStore(db, () => _ownership), clock, TimeSpan.FromSeconds(1), log) : null;
        log?.LogInformation("Execution state for {Day} rebuilt from {Inputs} journaled inputs; reconciled: {Reconciled} {Detail}", _day, inputs, _reconciled, _reconcileDetail);
    }

    async Task PublishMarksAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_marks is null || now - _marksAt < TimeSpan.FromSeconds(1)) return;
        _marksAt = now;
        await _marks.TickAsync(ct);                 // contains its own failures
    }

    Dictionary<string, string> Describe(ObserverState observer, SafetyState safety, DateTimeOffset now)
    {
        var conditions = new Dictionary<string, string>(StringComparer.Ordinal);
        var engine = _engine!;
        var all = engine.Runtimes.SelectMany(r => r.Positions()).ToList();
        var positions = all.Where(p => p.State is PositionState.Open or PositionState.ExitPending).ToList();
        var ist = (now + Ist).DateTime;
        var time = TimeOnly.FromDateTime(ist);
        var marketHours = ist.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && time >= ObserverRequiredFrom && time < MarketTo;

        if (!_reconciled) conditions["reconciliation"] = $"reconciliation failed: {_reconcileDetail}. Entries are blocked; exits continue";
        if (safety.Flatten is { } why && positions.Count > 0) conditions["protection"] = $"protective flatten ({why}): {positions.Count} position(s) outstanding";
        if (marketHours && (observer.ReadFailed || !observer.SessionExists || observer.Status != AdaptiveRuntimeStatus.Live || observer.HeartbeatUtc is not { } beat || now - beat > options.ObserverHeartbeatStale))
            conditions["observer"] = $"Observer unavailable, entries blocked: {safety.EntryBlock}";
        if (_frameProblem is { } frame) conditions["frames"] = frame;
        foreach (var key in _exitSince.Keys.Where(k => positions.All(p => p.PositionId != k)).ToArray()) _exitSince.Remove(key);
        foreach (var p in positions.Where(p => p.State == PositionState.ExitPending && p.ExitReason is ExitReason.Protective or ExitReason.Terminal or ExitReason.RiskLiquidation))
        {
            if (!_exitSince.TryGetValue(p.PositionId, out var since)) since = _exitSince[p.PositionId] = now;
            if (now - since >= options.ExitStuckAfter) conditions["exit:" + p.PositionId] = $"{p.StrategyId}: {p.ExitReason} exit of {p.RemainingQuantity} {p.Token} is NOT filled";
        }

        if (time >= options.Times.LiquidationDeadline && positions.Count > 0) conditions["liquidation"] = $"session liquidation incomplete: {positions.Count} position(s) still open";
        var unresolved = all.Count(p => p.State == PositionState.Unresolved);
        if (unresolved > 0) conditions["unresolved"] = $"{unresolved} position(s) UNRESOLVED: no exit fill was recorded";
        if (_earlierDayOutstanding is { } earlier) conditions["earlier-day"] = earlier;
        if (engine.ActivationFailures.Count > 0) conditions["activation"] = "strategies not activated: " + string.Join("; ", engine.ActivationFailures.Select(a => $"{a.StrategyId} {a.Detail}"));
        return conditions;
    }

    async Task WriteHeartbeatAsync(ExecutionSnapshot snapshot, CancellationToken ct)
    {
        Snapshot = snapshot;
        if (snapshot.At - _heartbeatAt < options.HeartbeatInterval) return;
        await _store.HeartbeatAsync(Owner, snapshot, ct);
        _heartbeatAt = snapshot.At;
    }

    async Task ReleaseAsync()
    {
        _engine = null;
        if (_ownership is { } o) { _ownership = null; await o.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    /// <summary>
    /// The opportunity log is written by the engine while it evaluates, before the input is committed. Held back until the commit, so no row describes a decision that was never stored; a stop between the
    /// two loses nothing, because the replay of the committed input writes the same row (idempotent).
    /// </summary>
    sealed class CommittedOpportunities(IOpportunitySink store) : IOpportunitySink
    {
        readonly List<OpportunityRecord> _held = [];

        public bool HoldUntilCommit { get; set; }

        public void Append(OpportunityRecord record)
        {
            if (HoldUntilCommit) _held.Add(record); else store.Append(record);
        }

        public void Discard() => _held.Clear();

        public void Flush()
        {
            foreach (var record in _held) store.Append(record);          // the store contains its own failures
            _held.Clear();
        }
    }

    /// <summary>
    /// The consumer's journal port in this service keeps nothing: the durable record of every frame and disposition is the execution journal (committed with the ledger), and a repeated bar is recognised by
    /// the consumer's sequence guard before the journal is asked.
    /// </summary>
    sealed class UnkeptFrameJournal : IFrameJournal
    {
        public bool IsDurable => false;
        public ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord record, CancellationToken ct) => ValueTask.FromResult(AppendOutcome.Appended);
        public ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord record, CancellationToken ct) => ValueTask.FromResult(AppendOutcome.Appended);
        public ValueTask AppendGapAsync(GapRecord record, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct) => ValueTask.FromResult(new JournalSession([], [], []));
    }

    /// <summary>Receives what the frame consumer accepted, so the coordinator can add the quotes and the safety verdict before the engine sees it.</summary>
    sealed class Capture : IFrameProcessor
    {
        public (MarketStateFrame Frame, MarketStateFrame? Previous, FrameDisposition Disposition)? Next;

        public ValueTask ProcessAsync(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, CancellationToken ct)
        {
            if (!disposition.IsReplay) Next = (frame, previous, disposition);
            return ValueTask.CompletedTask;
        }
    }
}
