using Microsoft.Extensions.Logging;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Recovery;

namespace NiftySignal.Trading.Live;

public enum PaperLiveState
{
    /// <summary>Trading:PaperLive:Enabled is false: the path does nothing at all.</summary>
    Disabled = 0,

    /// <summary>Enabled but no strategy family is registered: nothing can trade, nothing is journaled.</summary>
    NoStrategies = 1,

    /// <summary>Enabled and waiting for a session.</summary>
    Idle = 2,

    /// <summary>Running: finalized bars and quotes are being given to the durable run.</summary>
    Live = 3,

    /// <summary>The session was finished (final close).</summary>
    Finished = 4,

    /// <summary>A persistence failure or an unexpected error stopped the run; it is being (or will be) rebuilt from durable data. No new input is accepted meanwhile.</summary>
    Faulted = 5,

    /// <summary>Recovery refused to continue (incompatible configuration, divergent replay, corrupt ledger, lost ownership). Needs an operator; never retried automatically.</summary>
    Halted = 6,
}

/// <summary>The session a live run belongs to. <see cref="SessionId"/> is the authoritative finalized-bar session (the Adaptive session id).</summary>
public sealed record LiveSessionInfo(string SessionId, DateOnly TradeDate, IReadOnlyList<OptionContract> Contracts, string FutureToken, string ReadinessSpec);

/// <summary>A top-of-book quote of one instrument in an availability group (option or the future). Zero or negative sides are "absent".</summary>
public sealed record LiveQuote(string Token, long SourceId, bool IsFuture, decimal Bid, decimal Ask, decimal Last, long BidQty, long AskQty);

/// <summary>A completed finalized bar. <paramref name="Verified"/> is false when the Observer could not establish the integrity of the bar's persisted rows; such a bar is never given to the engine.</summary>
public sealed record LiveBar(long BarSeq, DateTimeOffset EndAvailableAt, BarInfo Info, bool Verified, AdaptiveStrategyFrame? Frame = null);

/// <summary>
/// How long the paper path kept its caller (the Observer poll loop) waiting. The handoff is SYNCHRONOUS: the Observer thread runs the engine and the database commit before it
/// goes on, so a slow database delays the Observer by exactly this much. <see cref="SlowHandoffs"/> counts handoffs above <see cref="PaperLiveCoordinator.SlowHandoffThreshold"/>.
/// </summary>
public sealed record PaperLiveHandoffStats(long Handoffs, long SlowHandoffs, double LastMillis, double MaxMillis, double MaxCommitMillis, int Commits, int PeakStepInputs, int PeakStepOutputs);

public sealed record PaperLiveStatus(
    PaperLiveState State,
    string Detail,
    string? RunId,
    long? DurableBarCursor,
    long? ProcessedBarCursor,
    DateTimeOffset? DurableGroupCursor,
    DateTimeOffset? LastActivityUtc,
    int Recoveries,
    PaperLiveHandoffStats Handoff);

/// <summary>
/// The live paper bridge's brain (contract section B.6). It receives, per availability group, the group's quotes and the bars the Adaptive Observer FINALIZED in it, and turns
/// them into canonical engine input for a durable <see cref="LiveEngineRun"/>. Rules:
/// <list type="bullet">
/// <item>Only completed, verified bars are ever delivered: no partial bar, no future data, no bar whose persisted projection could not be verified. A withheld bar or a gap in bar numbers is declared to the
/// engine as a feed-health degradation, so entries stay blocked until verified bars flow again (existing exits are unaffected).</item>
/// <item>Bars are never rebuilt from ticks here: the Observer is authoritative.</item>
/// <item>It never throws into its caller (the Observer): every failure becomes a visible state. Persistence failures rebuild the run from durable data and re-offer the group; recovery halts (incompatible
/// configuration, divergent replay, corrupt derived data, lost ownership) stop trading until an operator acts.</item>
/// <item>PAPER ONLY: there is no broker, order, HTTP or ingestion reference in this assembly.</item>
/// </list>
/// Single-threaded by contract (the Observer poll loop).
/// </summary>
public sealed class PaperLiveCoordinator : IDisposable
{
    private const int MaxConsecutiveFaults = 3;

    /// <summary>
    /// A handoff slower than this is reported. PLACEHOLDER: the Observer polls every 250 ms, so a handoff longer than one poll interval makes the Observer miss a cycle; the value is the
    /// cadence itself, not a tuned number.
    /// </summary>
    public static readonly TimeSpan SlowHandoffThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SlowLogInterval = TimeSpan.FromSeconds(60);
    private long _handoffs;
    private long _slowHandoffs;
    private long _slowSinceLog;
    private double _lastHandoffMs;
    private double _maxHandoffMs;
    private DateTimeOffset _lastSlowLog = DateTimeOffset.MinValue;

    private readonly PaperLiveOptions _options;
    private readonly IReadOnlyList<PaperLiveStrategy> _strategies;
    private readonly Func<IRunStore> _storeFactory;
    private readonly Func<string, Task>? _beforeOpen;
    private readonly Func<LiveSessionInfo, NiftySignal.Trading.Core.Config.TradingConfig>? _configFactory;
    private readonly ILogger _log;
    private readonly string _ownerId;
    private readonly object _gate = new();
    private LiveRunSpec? _spec;
    private LiveEngineRun? _run;
    private TimedRunStore? _timed;
    private LiveSessionInfo? _session;
    private PaperLiveState _state;
    private string _detail = string.Empty;
    private DateTimeOffset? _lastActivity;
    private int _recoveries;
    private int _consecutiveFaults;
    private bool _degradedByGate;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;
    private readonly Func<DateTimeOffset> _clock;

    public PaperLiveCoordinator(PaperLiveOptions options, IReadOnlyList<PaperLiveStrategy> strategies, Func<IRunStore> storeFactory, ILogger log, string ownerId, Func<DateTimeOffset>? clock = null, Func<string, Task>? beforeOpen = null, Func<LiveSessionInfo, NiftySignal.Trading.Core.Config.TradingConfig>? configFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ownerId = ownerId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _beforeOpen = beforeOpen;
        _configFactory = configFactory;
        _state = !options.Enabled ? PaperLiveState.Disabled : strategies.Count == 0 ? PaperLiveState.NoStrategies : PaperLiveState.Idle;
        _detail = _state switch
        {
            PaperLiveState.Disabled => "Trading:PaperLive:Enabled is false",
            PaperLiveState.NoStrategies => "enabled, but no strategy family is registered: nothing can trade and nothing is journaled",
            _ => "waiting for a session",
        };

        // an invalid risk configuration is refused up front, whether or not a strategy is registered: it must never be silently ignored
        if (options.Enabled)
        {
            var riskErrors = options.ValidateRisk(strategies);
            if (riskErrors.Count > 0)
            {
                Halt("configuration invalid: " + string.Join(" ", riskErrors), null);
            }
        }
    }

    public PaperLiveStatus Status
    {
        get
        {
            lock (_gate)
            {
                var commits = _timed?.CommitMillis;
                var handoff = new PaperLiveHandoffStats(_handoffs, _slowHandoffs, _lastHandoffMs, _maxHandoffMs, commits is { Count: > 0 } ? commits.Max() : 0d, commits?.Count ?? 0, _timed?.PeakStepInputs ?? 0, _timed?.PeakStepOutputs ?? 0);
                return new PaperLiveStatus(_state, _detail, _spec?.Settings.RunId, _run?.DurableBarCursor, _run?.ProcessedBarCursor, _run?.DurableGroupCursor, _lastActivity, _recoveries, handoff);
            }
        }
    }

    public LiveEngineRun? Run => _run;

    /// <summary>The duration in milliseconds of every commit of the current run instance (measurement).</summary>
    public IReadOnlyList<double> CommitMillis => _timed?.CommitMillis ?? Array.Empty<double>();

    public static string RunIdOf(LiveSessionInfo session) => $"paper-live-v1:{session.TradeDate:yyyy-MM-dd}:{session.SessionId}";

    // ------------------------------------------------------------------ session

    /// <summary>Opens (or resumes, replaying the durable inputs) the run of this session. Never throws.</summary>
    public void StartSession(LiveSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_state is PaperLiveState.Disabled or PaperLiveState.NoStrategies or PaperLiveState.Halted)
        {
            return;
        }

        try
        {
            DisposeRun();
            var errors = _options.Validate().Concat(_options.ValidateRisk(_strategies)).ToList();
            if (errors.Count > 0)
            {
                Halt("configuration invalid: " + string.Join(" ", errors), null);
                return;
            }

            _session = session;
            _degradedByGate = false;
            _consecutiveFaults = 0;
            var config = _configFactory?.Invoke(session) ?? PaperLiveSetup.Config(_options, _strategies);
            _spec = new LiveRunSpec(
                session.TradeDate,
                session.Contracts,
                config,
                FreshFamilies(),
                new RunSettings { RunId = RunIdOf(session), RunMode = "PaperLive" },
                session.SessionId,
                "adaptive-observer:finalized-bars+option-quotes/1",
                session.ReadinessSpec);
            OpenRun("session start");
        }
        catch (Exception e)
        {
            Fail(e, "starting the session");
        }
    }

    public void FinishSession()
    {
        if (_run is null || _state != PaperLiveState.Live)
        {
            return;
        }

        try
        {
            if (!_run.Finished)
            {
                var result = _run.Finish();
                _log.LogInformation("Paper-live session {Run} finished: {Outputs} outputs, {Fills} fills, {Positions} positions, {Unresolved} unresolved.", _spec!.Settings.RunId, result.Outputs, result.Fills, result.Positions, result.UnresolvedPositions);
            }

            Set(PaperLiveState.Finished, "session finished");
        }
        catch (Exception e)
        {
            Fail(e, "finishing the session");
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>One availability group: the quotes of the group and the bars finalized in it. Never throws.</summary>
    public void OnGroup(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            OnGroupCore(at, quotes, bars);
        }
        finally
        {
            Measure(started, "group");
        }
    }

    private void OnGroupCore(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars)
    {
        if (!Ready())
        {
            return;
        }

        for (var attempt = 0; attempt <= 1; attempt++)
        {
            try
            {
                Deliver(at, quotes, bars);
                _consecutiveFaults = 0;
                return;
            }
            catch (Exception e)
            {
                if (!Fail(e, "delivering a group") || attempt == 1)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The clock may advance to <paramref name="watermark"/>: no undelivered input is available before it. Never throws.</summary>
    public void OnHeartbeat(DateTimeOffset watermark)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (!Ready())
            {
                return;
            }

            _run!.Advance(watermark);
        }
        catch (Exception e)
        {
            Fail(e, "advancing the clock");
        }
        finally
        {
            Measure(started, "heartbeat");
        }
    }

    /// <summary>Records how long the caller was held (only while a run is open) and warns, at most once a minute, when handoffs exceeded <see cref="SlowHandoffThreshold"/>.</summary>
    private void Measure(long started, string what)
    {
        if (_run is null)
        {
            return;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        lock (_gate)
        {
            _handoffs++;
            _lastHandoffMs = elapsed.TotalMilliseconds;
            _maxHandoffMs = Math.Max(_maxHandoffMs, _lastHandoffMs);
            if (elapsed > SlowHandoffThreshold)
            {
                _slowHandoffs++;
                _slowSinceLog++;
            }
        }

        var now = _clock();
        if (_slowSinceLog > 0 && now - _lastSlowLog >= SlowLogInterval)
        {
            _log.LogWarning(
                "Paper-live handoff was slow: {Count} handoff(s) over {Threshold} ms since the last report (latest {What} {Latest:0} ms, max {Max:0} ms, slowest commit {Commit:0} ms). The Observer thread waits for this call.",
                _slowSinceLog, SlowHandoffThreshold.TotalMilliseconds, what, _lastHandoffMs, _maxHandoffMs, _timed?.CommitMillis is { Count: > 0 } c ? c.Max() : 0d);
            _slowSinceLog = 0;
            _lastSlowLog = now;
        }
    }

    private void Deliver(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars)
    {
        var run = _run!;
        if (run.DurableGroupCursor is { } durable && at <= durable)
        {
            return;                                                                         // this group is already durable (a re-delivery after a restart)
        }

        var events = new List<EngineEvent>(quotes.Count + bars.Count + 2);
        var cursor = run.ProcessedBarCursor ?? run.DurableBarCursor;
        var withheld = false;
        foreach (var bar in bars.OrderBy(b => b.BarSeq))
        {
            if (cursor is { } c && bar.BarSeq <= c)
            {
                continue;                                                                   // already processed (a re-delivery after a restart)
            }

            if (!bar.Verified)
            {
                withheld = true;
                continue;
            }

            var gap = cursor is { } last && bar.BarSeq > last + 1;
            if (gap || _degradedByGate)
            {
                // The engine missed bars (a restart, a withheld bar): block entries through the integrity machinery, then declare the feed healthy again so the recovery period starts.
                if (gap)
                {
                    events.Add(new FeedHealthEvent(at, at.UtcTicks, FeedHealthState.Degraded, FeedFaultReason.DataGapOpen, $"bars {cursor + 1}..{bar.BarSeq - 1} were not delivered"));
                }

                events.Add(new FeedHealthEvent(at, at.UtcTicks + 1, FeedHealthState.Healthy, FeedFaultReason.None, gap ? "bar sequence resumed" : "verified bars resumed"));
                _degradedByGate = false;
            }

            events.Add(new BarCompletedEvent(bar.EndAvailableAt, bar.BarSeq, _session!.FutureToken, bar.Info, bar.Frame));
            cursor = bar.BarSeq;
        }

        if (withheld && !_degradedByGate)
        {
            events.Add(new FeedHealthEvent(at, at.UtcTicks, FeedHealthState.Degraded, FeedFaultReason.DataIntegrity, "a finalized bar could not be verified and was withheld"));
            _degradedByGate = true;
        }

        foreach (var q in quotes.OrderBy(q => q.SourceId))
        {
            var bid = q.Bid > 0m ? q.Bid : (decimal?)null;
            var ask = q.Ask > 0m ? q.Ask : (decimal?)null;
            events.Add(new QuoteEvent(at, q.IsFuture ? EventKind.FutureQuote : EventKind.OptionQuote, q.SourceId, q.Token, bid, ask, q.Last > 0m ? q.Last : null, bid is null ? null : q.BidQty, ask is null ? null : q.AskQty));
        }

        if (events.Count > 0 && run.Offer(events))
        {
            lock (_gate)
            {
                _lastActivity = _clock();
            }
        }
    }

    // ------------------------------------------------------------------ state, recovery

    private bool Ready()
    {
        if (_state == PaperLiveState.Faulted && _clock() >= _retryNotBefore && _session is not null)
        {
            OpenRun("retry after a fault");
        }

        return _state == PaperLiveState.Live && _run is not null;
    }

    /// <summary>A new instance of every family: a stateful family must never carry the state of a previous (failed or replayed) run into the next one.</summary>
    private IReadOnlyDictionary<string, IStrategyFamily> FreshFamilies() => _strategies.ToDictionary(s => s.Family.StrategyId, s => s.NewFamily());

    private void OpenRun(string reason)
    {
        DisposeRun();
        try
        {
            _spec = _spec! with { Strategies = FreshFamilies() };
            _timed = new TimedRunStore(_storeFactory());
            var store = _timed;
            _beforeOpen?.Invoke(_spec!.Settings.RunId).GetAwaiter().GetResult();
            _run = LiveEngineRun.Open(store, _ownerId, _spec!);
            _recoveries++;
            Set(_run.Finished ? PaperLiveState.Finished : PaperLiveState.Live, $"{reason}: replayed {_run.StepsReplayed} steps / {_run.InputsReplayed} inputs; durable bar cursor {_run.DurableBarCursor?.ToString() ?? "none"}");
            _log.LogInformation("Paper-live run {Run} opened ({Reason}): replayed {Steps} steps and {Inputs} inputs, durable bar cursor {Cursor}.", _spec!.Settings.RunId, reason, _run.StepsReplayed, _run.InputsReplayed, _run.DurableBarCursor);
        }
        catch (Exception e)
        {
            Fail(e, reason);
        }
    }

    /// <summary>Turns a failure into a visible state. Returns true when the run was rebuilt and the caller may re-offer its input.</summary>
    private bool Fail(Exception e, string doing)
    {
        DisposeRun();
        switch (e)
        {
            case RecoveryHaltException halt:
                Halt($"{doing}: {halt.Kind}: {halt.Message} {string.Join("; ", halt.Details.Take(5))}", e);
                return false;
            case FencedOutException or OwnershipConflictException:
                Halt($"{doing}: ownership of the run was lost or is held elsewhere: {e.Message}", e);
                return false;
        }

        _consecutiveFaults++;
        if (_consecutiveFaults > MaxConsecutiveFaults)
        {
            Halt($"{doing}: {_consecutiveFaults} consecutive failures, last: {e.GetType().Name}: {e.Message}", e);
            return false;
        }

        _log.LogError(e, "Paper-live failure while {Doing}; the run is rebuilt from durable data (attempt {Attempt}).", doing, _consecutiveFaults);
        Set(PaperLiveState.Faulted, $"{doing}: {e.GetType().Name}: {e.Message}");
        _retryNotBefore = _clock().AddSeconds(2 * _consecutiveFaults);
        if (_spec is null || _session is null)
        {
            return false;
        }

        OpenRun("rebuild after a failure");
        return _state == PaperLiveState.Live;
    }

    private void Halt(string detail, Exception? e)
    {
        _log.LogCritical(e, "PAPER-LIVE HALTED (no new paper trades will be made until an operator acts): {Detail}", detail);
        Set(PaperLiveState.Halted, detail);
    }

    private void Set(PaperLiveState state, string detail)
    {
        lock (_gate)
        {
            _state = state;
            _detail = detail;
        }
    }

    private void DisposeRun()
    {
        var run = _run;
        _run = null;
        try
        {
            run?.Dispose();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Releasing the paper-live run failed (the lock disappears with its connection).");
        }
    }

    public void Dispose() => DisposeRun();
}
