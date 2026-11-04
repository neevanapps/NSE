using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Npgsql;
using NiftySignal.PaperEngineV2.Alerts;
using NiftySignal.PaperEngineV2.Core.Config;
using NiftySignal.PaperEngineV2.Core.Evaluation;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Core.Pipeline;
using NiftySignal.PaperEngineV2.Data;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Frames;
using NiftySignal.Trading.Core.Costs;

namespace NiftySignal.PaperEngineV2.Runtime;

/// <summary>Seams for failure-injection tests only (never set by the Host): wrap the durable journal or the engine to delay or fail them.</summary>
public sealed record V2RuntimeTestHooks(Func<IFrameJournal, IFrameJournal>? JournalDecorator = null, Func<IFrameProcessor, IFrameProcessor>? ProcessorDecorator = null);

public enum V2RuntimeState { Disabled, NotStarted, Running, SchemaIncompatible, ConfigInvalid, Stopped }

public sealed record V2RuntimeSnapshot(
    V2RuntimeState State, string? Detail, long NoticesAccepted, long NoticesDropped, long FramesBuilt, long BuildFailures, long ConsumerRestarts, long InputLoadFailures, HandoffStats? Handoff, ConsumerStatus? Consumer,
    long LedgerFailures, long LedgerConflicts, long AlertsEnqueued, long AlertFailures, long? SessionId);

/// <summary>
/// The V2 SHADOW pipeline behind the Host hook. <see cref="TryEnqueue"/> is the ONLY thing the observer thread calls: a bounded, lock-free channel write that never waits, never throws and never touches a database.
/// Everything else runs on this class's own tasks: frame assembly (persisted, verified sidecars only), the bounded <see cref="FrameHandoff"/>, the <see cref="FrameConsumer"/> (journal first, then the engine), the
/// ledger and the alert outbox dispatcher. A faulted consumer is rebuilt from the durable journal and keeps the same handoff, so queued frames are not lost and entries stay blocked until clean frames resume.
/// Paper only: no broker, order or live type is reachable from here.
/// </summary>
public sealed class V2Runtime : IAsyncDisposable
{
    readonly V2RuntimeOptions _options;
    readonly IV2FrameFactory _frames;
    readonly IAlertTransport? _transport;
    readonly TimeProvider _clock;
    readonly ILogger? _log;
    readonly Func<string, NpgsqlDataSource>? _dataSourceFactory;
    readonly V2RuntimeTestHooks? _hooks;
    readonly Channel<V2BarNotice> _notices;
    readonly SessionLotSizes _lots = new();
    readonly CancellationTokenSource _cts = new();
    NpgsqlDataSource? _db;
    PostgresEventStore? _store;
    AlertingEventSink? _alertSink;
    PostgresOpportunityStore? _opportunityStore;
    PostgresFrameJournal? _journal;
    IReadOnlyList<CompiledStrategy> _strategies = [];
    ExecutionOptions? _execution;
    Task? _assembler, _alerts, _marks;
    volatile LiveMarkBoard? _markBoard;
    LiveMarkPublisher? _markPublisher;
    PostgresLiveMarkStore? _markStore;
    SessionPipeline? _pipeline;
    long _accepted, _dropped, _built, _buildFailures, _restarts, _inputFailures;
    int _started;

    public V2Runtime(V2RuntimeOptions options, IV2FrameFactory frames, IAlertTransport? transport = null, TimeProvider? clock = null, ILogger? log = null, Func<string, NpgsqlDataSource>? dataSourceFactory = null, V2RuntimeTestHooks? hooks = null)
    {
        _hooks = hooks;
        _options = options; _frames = frames; _transport = transport; _clock = clock ?? TimeProvider.System; _log = log; _dataSourceFactory = dataSourceFactory;
        _notices = Channel.CreateBounded<V2BarNotice>(new BoundedChannelOptions(Math.Max(1, options.NoticeCapacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        State = options.Enabled ? V2RuntimeState.NotStarted : V2RuntimeState.Disabled;
    }

    public V2RuntimeState State { get; private set; }

    public string? Detail { get; private set; }

    public bool Accepting => State == V2RuntimeState.Running;

    /// <summary>Validates the options, checks the schema (read-only; nothing is migrated), loads and compiles the strategies and starts the V2 tasks. Never throws: a problem leaves the runtime closed and visible.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            var errors = _options.Validate();
            if (errors.Count > 0) { Fail(V2RuntimeState.ConfigInvalid, string.Join("; ", errors)); return; }

            _db = _dataSourceFactory?.Invoke(_options.ConnectionString!) ?? NpgsqlDataSource.Create(_options.ConnectionString!);
            var verdict = await V2SchemaCheck.CheckAsync(_db, ct);
            if (!verdict.Compatible) { Fail(V2RuntimeState.SchemaIncompatible, "pev2 schema not usable (the owner script is not applied automatically): " + string.Join("; ", verdict.Problems)); return; }

            var brokerage = new FlatBrokerage { Unit = BrokerageUnit.PerOrder, Amount = _options.BrokeragePerOrder!.Value };
            try
            {
                var schedule = _options.CostScheduleVersion == "npt1-frozen-fees-v1" ? NseIndexOptionsCostSchedules.Npt1FrozenV1(brokerage) : NseIndexOptionsCostSchedules.From20260401(brokerage);
                _execution = new ExecutionOptions(_options.TerminalRetryUntil, [schedule], _lots);
            }
            catch (ArgumentException ex) { Fail(V2RuntimeState.ConfigInvalid, "cost schedules: " + ex.Message); return; }

            if (!Directory.Exists(_options.StrategyDirectory)) { Fail(V2RuntimeState.ConfigInvalid, $"strategy directory '{_options.StrategyDirectory}' does not exist"); return; }
            var docs = Directory.EnumerateFiles(_options.StrategyDirectory!, "*.json").OrderBy(x => x, StringComparer.Ordinal).Select(f => (f, File.ReadAllText(f))).ToList();
            var set = StrategySet.Load(docs);
            if (!set.SetAccepted) { Fail(V2RuntimeState.ConfigInvalid, "strategy set rejected: " + string.Join("; ", set.SetErrors.Select(e => e.Message))); return; }
            _strategies = set.Active;
            _store = new PostgresEventStore(_db);
            foreach (var cfg in set.Active.Select(a => a.Config).Concat(set.DisabledValid)) await _store.RecordConfigAsync(cfg, _clock.GetUtcNow(), ct);
            _journal = new PostgresFrameJournal(_db);
            // The opportunity log is optional: it exists only after the owner runs the AddObservability script. Without it V2 runs exactly as before and says so.
            if (await ObservabilitySchema.HasAsync(_db, "pev2_opportunities", ct)) _opportunityStore = new PostgresOpportunityStore(_db, _clock);
            else _log?.LogWarning("Paper Engine V2: pev2.pev2_opportunities is not installed; the opportunity log is off (owner-run script: AddObservability).");
            _alertSink = _options.AnyAlertEnabled ? new AlertingEventSink(_store, _db, _options.Alerts, _clock, id => _strategies.FirstOrDefault(s => s.Config.Identity.StrategyId == id)?.Config.Identity.Version) : null;
            foreach (var (src, errs) in set.Rejected) _log?.LogWarning("V2 strategy file {Source} rejected: {Errors}", src, string.Join("; ", errs.Select(e => e.Message)));

            State = V2RuntimeState.Running; Detail = $"{set.Active.Count} strategies active, {set.DisabledValid.Count} disabled, {set.Rejected.Count} rejected";
            _assembler = Task.Run(() => AssembleLoopAsync(_cts.Token));
            if (_options.AnyAlertEnabled && _transport is not null) _alerts = Task.Run(() => AlertLoopAsync(_cts.Token));
            if (_options.LiveMarksEnabled)
            {
                // Read-only indicative marks for the Dashboard. Optional: it needs the owner-run AddObservability script; without it V2 runs exactly as before and says so.
                if (await ObservabilitySchema.HasAsync(_db, "pev2_live_marks", ct))
                {
                    _markStore = new PostgresLiveMarkStore(_db);
                    _markPublisher = new LiveMarkPublisher(() => _pipeline?.Engine?.MarkPositions ?? [], () => _markBoard, _markStore, _clock, _options.LiveMarkInterval, _log);
                    _marks = Task.Run(() => _markPublisher.RunAsync(_cts.Token));
                }
                else _log?.LogWarning("Paper Engine V2: pev2.pev2_live_marks is not installed; live marks are off (owner-run script: AddObservability).");
            }
            _log?.LogInformation("Paper Engine V2 SHADOW started: {Detail}", Detail);
        }
        catch (Exception ex)
        {
            Fail(V2RuntimeState.ConfigInvalid, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    void Fail(V2RuntimeState state, string detail)
    {
        State = state; Detail = detail;
        _log?.LogError("Paper Engine V2 SHADOW is closed ({State}): {Detail}. The observer and NPT1 are unaffected.", state, detail);
    }

    /// <summary>Observer thread: O(1), never waits, never throws. A full queue drops the notice (counted); the missing bar later surfaces as a sequence gap and entries stay blocked.</summary>
    public bool TryEnqueue(V2BarNotice notice)
    {
        try
        {
            if (State != V2RuntimeState.Running) return false;
            if (_notices.Writer.TryWrite(notice)) { Interlocked.Increment(ref _accepted); return true; }
            Interlocked.Increment(ref _dropped);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The Host registers the session's mark board (all option-universe tokens). Marks are read-only and independent of the frame path.</summary>
    public void SetMarkBoard(LiveMarkBoard? board) => _markBoard = board;

    /// <summary>Observer thread: one O(1) step for the live-mark board. Never waits, never throws, never touches the frame path; a no-op while marks are off.</summary>
    public void ObserveMarkTick(string token, in NiftySignal.AdaptiveObserver.CleanObserverTick tick)
    {
        try { _markBoard?.Observe(token, in tick); }
        catch (Exception) { }
    }

    /// <summary>Live-mark diagnostics (null while marks are off or not installed).</summary>
    public (long Ticks, long Failures, string? Disabled)? MarkStatus => _markPublisher is { } p ? (p.Ticks, p.Failures, _markStore?.DisabledReason) : null;

    public V2RuntimeSnapshot Snapshot()
    {
        var p = _pipeline;
        return new V2RuntimeSnapshot(State, Detail, Interlocked.Read(ref _accepted), Interlocked.Read(ref _dropped), Interlocked.Read(ref _built), Interlocked.Read(ref _buildFailures), Interlocked.Read(ref _restarts), Interlocked.Read(ref _inputFailures),
            p?.Handoff.Stats(), p?.Consumer.Status, (_store?.Failures ?? 0), (_store?.Conflicts ?? 0), _alertSink?.Enqueued ?? 0, _alertSink?.Failures ?? 0, p?.SessionId);
    }

    /// <summary>The ledger of the current session's engine (test and diagnostics access; the durable store is the source of truth).</summary>
    public IReadOnlyList<PaperEvent> LiveEvents => _pipeline?.Engine.Events ?? [];

    async Task AssembleLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var notice in _notices.Reader.ReadAllAsync(ct))
            {
                try { await HandleNoticeAsync(notice, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _inputFailures);
                    _log?.LogError(ex, "V2 could not process the bar notice for bar {Bar}; it becomes a gap.", notice.Bar.BarSeq);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    async Task HandleNoticeAsync(V2BarNotice notice, CancellationToken ct)
    {
        var pipeline = await EnsurePipelineAsync(notice.Session, ct);
        MarketStateFrame? frame; string? problem = null;
        try { (frame, problem) = await _frames.BuildAsync(notice with { HeldTokens = pipeline.Engine.HeldTokens }, pipeline.Builder, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { frame = null; problem = $"{ex.GetType().Name}: {ex.Message}"; }
        if (frame is null)
        {
            Interlocked.Increment(ref _inputFailures);
            pipeline.Handoff.OfferBuilt(() => throw new InvalidOperationException(problem ?? "frame unavailable"));       // registers a BuildFailure signal; the consumer sees a gap
            return;
        }

        Interlocked.Increment(ref _built);
        pipeline.Handoff.Offer(frame);
    }

    async Task<SessionPipeline> EnsurePipelineAsync(V2SessionInfo session, CancellationToken ct)
    {
        if (_pipeline is { } existing && existing.SessionId == session.SessionId) return existing;
        if (_pipeline is { } old) { old.Handoff.Complete(); await old.DrainAsync(TimeSpan.FromSeconds(10)); }
        _lots.Set(session);
        var next = new SessionPipeline(this, session.SessionId);
        await next.StartAsync(ct);
        _pipeline = next;
        return next;
    }

    async Task AlertLoopAsync(CancellationToken ct)
    {
        var dispatcher = new AlertDispatcher(_db!, _transport!, _options.Alerts, _clock);
        try
        {
            await dispatcher.ResolveInterruptedAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    DispatchReport report;
                    do { report = await dispatcher.DispatchAsync(AlertDispatcher.MaxBatch, ct); } while (report.Claimed >= AlertDispatcher.MaxBatch);        // drain a backlog before sleeping
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { _log?.LogWarning(ex, "V2 alert dispatch failed; it is retried (alerts stay in the durable outbox)."); }
                await Task.Delay(_options.AlertPollInterval, _clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task StopAsync()
    {
        if (State == V2RuntimeState.Running) State = V2RuntimeState.Stopped;
        _notices.Writer.TryComplete();
        if (_assembler is not null) await Task.WhenAny(_assembler, Task.Delay(TimeSpan.FromSeconds(10)));
        if (_pipeline is { } p) { p.Handoff.Complete(); await p.DrainAsync(TimeSpan.FromSeconds(10)); }
        await _cts.CancelAsync();
        foreach (var t in new[] { _assembler, _alerts, _marks }) if (t is not null) await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch (Exception) { }
        if (_db is not null) await _db.DisposeAsync();
        _cts.Dispose();
    }

    /// <summary>One session's handoff, builder, engine and consumer; the consumer is rebuilt (from the durable journal) after a fault while the handoff, its queue and its signals stay.</summary>
    sealed class SessionPipeline(V2Runtime owner, long sessionId)
    {
        Task? _supervisor;
        readonly CancellationTokenSource _stop = new();

        public long SessionId { get; } = sessionId;

        public FrameHandoff Handoff { get; } = new(new HandoffOptions(owner._options.QueueCapacity, owner._options.MaxProcessingLag, owner._options.FramesToClearGap));

        public MarketStateFrameBuilder Builder { get; } = new();

        public PaperEngine Engine { get; private set; } = null!;

        public FrameConsumer Consumer { get; private set; } = null!;

        public async Task StartAsync(CancellationToken ct)
        {
            await BuildConsumerAsync(ct);
            _supervisor = Task.Run(SuperviseAsync);
        }

        async Task BuildConsumerAsync(CancellationToken ct)
        {
            IPaperEventSink sink = (IPaperEventSink?)owner._alertSink ?? owner._store!;
            Engine = new PaperEngine(owner._strategies, owner._execution!, sink, owner._opportunityStore);
            foreach (var f in Engine.ActivationFailures) owner._log?.LogWarning("V2 strategy {Strategy} did not activate: {Reason}", f.StrategyId, f.Detail);
            IFrameJournal journal = owner._hooks?.JournalDecorator?.Invoke(owner._journal!) ?? owner._journal!;
            IFrameProcessor processor = owner._hooks?.ProcessorDecorator?.Invoke(Engine) ?? Engine;
            Consumer = new FrameConsumer(Handoff, Handoff.Options, journal, processor, owner._clock);
            await Consumer.RecoverFromJournalAsync(SessionId, ct);      // deterministic replay into the fresh engine; the idempotent ledger and outbox accept nothing twice
        }

        async Task SuperviseAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                ConsumerExit exit;
                try { exit = await Consumer.RunAsync(_stop.Token); }
                catch (Exception ex) { owner._log?.LogError(ex, "V2 consumer crashed."); exit = ConsumerExit.Faulted; }
                if (exit != ConsumerExit.Faulted) return;
                Interlocked.Increment(ref owner._restarts);
                owner._log?.LogError("V2 consumer faulted ({Fault}); rebuilding from the durable journal after the back-off. Entries stay blocked until clean frames resume.", Consumer.Status.FaultMessage);
                try
                {
                    await Task.Delay(owner._options.RestartBackoff, owner._clock, _stop.Token);
                    await BuildConsumerAsync(_stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { owner._log?.LogError(ex, "V2 could not rebuild the consumer; retrying."); }
            }
        }

        public async Task DrainAsync(TimeSpan timeout)
        {
            if (_supervisor is null) return;
            var done = await Task.WhenAny(_supervisor, Task.Delay(timeout));
            if (done != _supervisor) await _stop.CancelAsync();
        }
    }
}
