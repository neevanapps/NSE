using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Execution;
using NiftySignal.Trading.Core.Instruments;
using NiftySignal.Trading.Core.Scheduling;
using NiftySignal.Trading.Core.State;
using NiftySignal.Trading.Core.Strategies;
using NiftySignal.Trading.Core.Trading;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>
/// Everything that defines one LIVE paper run: the trade date and contract universe, the configuration, the registered strategy families and the identity of the
/// finalized-bar source. As for <see cref="DurableRunSpec"/>, two runs with equal specs and equal ordered inputs produce identical outputs.
/// </summary>
/// <param name="SourceSessionId">The authoritative finalized-bar session (the Adaptive session id under the Host). Every decision, entry and exit is stamped with it.</param>
/// <param name="InputManifest">A stable label of what feeds the run (finalized bars + option quotes). Part of the compatibility identity.</param>
/// <param name="ReadinessSpec">The readiness definition that produced the <see cref="BarInfo"/> flags of the bars (part of the compatibility identity).</param>
public sealed record LiveRunSpec(
    DateOnly TradeDate,
    IReadOnlyList<OptionContract> Contracts,
    TradingConfig Config,
    IReadOnlyDictionary<string, IStrategyFamily> Strategies,
    RunSettings Settings,
    string SourceSessionId,
    string InputManifest,
    string ReadinessSpec,
    BinaryProvenance? Provenance = null,
    DurableOptions? Durable = null,
    RecorderOptions? Recorder = null);

/// <summary>What a live run produced at its finish (counts only; the ledger of record is the durable outputs).</summary>
public sealed record LiveFinishResult(int Outputs, int Fills, int Positions, int UnresolvedPositions, int Quarantined);

/// <summary>
/// One durable LIVE paper-trading run (contract section B.5). It wraps the deterministic trading engine directly — there is no translator and no tick source: the
/// engine is given canonical <see cref="EngineEvent"/> groups (option quotes, completed finalized bars with their readiness evidence, feed-health and operator events),
/// clock advances and the finish. Every input is journaled in the SAME transaction as the step that processed it, so durable state is always "inputs + the outputs
/// they produced".
/// <para>
/// <b>Resume.</b> <see cref="Open"/> acquires ownership, checks the header, loads the journal and replays every durable input through a fresh engine in order. Nothing is
/// dispatched and nothing is written during the replay; each rebuilt step is compared with the durable one (outputs, the nine channel digests, the canonical state digest
/// at checkpoints) and any difference halts visibly. A replayed run therefore cannot create a new paper trade: a bar that is already in the journal produces the
/// identical outputs it produced before, and a re-delivered bar is recognised as a duplicate and dropped before it can be journaled again.
/// </para>
/// <para>
/// <b>Cursor.</b> <see cref="DurableBarCursor"/> is the highest finalized bar whose input is durable, within the run's <see cref="SourceSessionId"/> (the run is keyed by
/// session and configuration identity). Inputs that were processed in memory but not yet committed are lost by a crash and are simply delivered again by the source.
/// </para>
/// <para>
/// <b>Failure model.</b> As for <see cref="DurableRun"/>: a failed or unknown commit faults the instance; the only way forward is to dispose it and open a new one from
/// durable data. The class is single-threaded by contract.
/// </para>
/// </summary>
public sealed class LiveEngineRun : IDisposable
{
    private readonly IRunStore _store;
    private readonly LiveRunSpec _spec;
    private readonly DurableOptions _options;
    private readonly ICrashPoints? _crash;
    private readonly OwnershipToken _token;
    private readonly RunSnapshot _snapshot;
    private readonly TradingEngine _engine;
    private readonly EngineLoop _loop;
    private readonly RunRecorders _recorders;
    private readonly DateTimeOffset _horizon;
    private readonly Dictionary<long, CheckpointRecord> _durableCheckpoints;
    private readonly Dictionary<string, string> _opportunityLines = new(StringComparer.Ordinal);
    private long _nextStep;
    private long _nextGroupOrdinal;
    private long _nextInputSeq;
    private int _committedOutputs;
    private int _outputsRecorded;
    private DateTimeOffset _lastStepTime;
    private int _batchUnits;
    private long? _batchFirstOrdinal;
    private long? _batchLastOrdinal;
    private DateTimeOffset? _batchLastGroupAt;
    private long? _batchMaxSourceId;
    private long? _batchBarSeq;
    private long _offeredEvents;
    private bool _replaying;
    private bool _faulted;
    private string? _faultReason;
    private bool _disposed;
    private bool _finished;
    private long? _durableBarSeq;
    private long? _processedBarSeq;
    private DateTimeOffset? _durableGroupAt;

    public long CommitsMade { get; private set; }

    public long StepsReplayed { get; private set; }

    public long InputsReplayed { get; private set; }

    public long CheckpointsVerified { get; private set; }

    private LiveEngineRun(IRunStore store, LiveRunSpec spec, ICrashPoints? crash, OwnershipToken token, RunSnapshot snapshot)
    {
        _store = store;
        _spec = spec;
        _options = spec.Durable ?? new DurableOptions();
        _crash = crash;
        _token = token;
        _snapshot = snapshot;
        _engine = new TradingEngine(spec.Config, spec.TradeDate, spec.Contracts, spec.Strategies, spec.Settings.RunId, spec.Settings.RunMode, spec.Settings.Retention, spec.SourceSessionId);
        _loop = new EngineLoop(_engine, _engine.Variants.Schedule.DayStartUtc, spec.Settings.Retention.LoopSeenRetention);
        _engine.Start(_loop);
        _recorders = new RunRecorders(spec.Recorder);
        _horizon = spec.Settings.Horizon ?? _engine.Variants.Schedule.SessionEndUtc;
        _durableCheckpoints = snapshot.Checkpoints.ToDictionary(c => c.StepSeq);
        _lastStepTime = _loop.Now;
    }

    public TradingEngine Engine => _engine;

    public EngineLoop Loop => _loop;

    public OwnershipToken Token => _token;

    public bool Faulted => _faulted;

    public string? FaultReason => _faultReason;

    public bool Finished => _finished;

    public long DurableStepsAtOpen => _snapshot.Steps.Count;

    public string SourceSessionId => _spec.SourceSessionId;

    public DateTimeOffset Horizon => _horizon;

    /// <summary>The highest finalized bar whose input is DURABLE (committed), or null before the first bar. Bars at or below it must not be delivered again.</summary>
    public long? DurableBarCursor => _durableBarSeq;

    /// <summary>
    /// The availability time of the last input group that is DURABLE (committed), or null before the first: a source resumes strictly after it, because the engine only accepts groups in
    /// availability order. (Tick ids are NOT ordered by availability, so an id cannot serve as a cursor.) Input that was processed but not yet committed is lost by a crash and is delivered again.
    /// </summary>
    public DateTimeOffset? DurableGroupCursor => _durableGroupAt;

    /// <summary>The availability time of the last group the engine processed (a source must not deliver a new event at or before it).</summary>
    public DateTimeOffset? LastGroupTime => _loop.LastGroupTime;

    /// <summary>The highest finalized bar the engine has processed in memory (it may be ahead of the durable cursor until the next commit).</summary>
    public long? ProcessedBarCursor => _processedBarSeq;

    public RunPhase Phase => _finished ? RunPhase.Finished : _replaying ? RunPhase.Replaying : RunPhase.Live;

    /// <summary>The canonical state digest right now (loop + engine); recovery tests compare it before and after a restart.</summary>
    public string StateDigest => CaptureState().Digest;

    // ------------------------------------------------------------------ open / recover

    public static LiveEngineRun Open(IRunStore store, string ownerId, LiveRunSpec spec, ICrashPoints? crash = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(spec);
        var optionErrors = (spec.Durable ?? new DurableOptions()).Validate();
        if (optionErrors.Count > 0)
        {
            throw new ArgumentException("Invalid durable options: " + string.Join(" ", optionErrors), nameof(spec));
        }

        var token = store.Acquire(spec.Settings.RunId, ownerId);
        try
        {
            var snapshot = store.Load(spec.Settings.RunId);
            // The horizon is part of the header: take it from the engine's own session schedule unless the settings fix it.
            var horizon = spec.Settings.Horizon ?? new TradingEngine(spec.Config, spec.TradeDate, spec.Contracts, spec.Strategies, spec.Settings.RunId, spec.Settings.RunMode, spec.Settings.Retention, spec.SourceSessionId).Variants.Schedule.SessionEndUtc;
            var header = RunIdentity.BuildHeader(spec with { Settings = spec.Settings with { Horizon = horizon } }, horizon);
            if (snapshot.Header is null)
            {
                Create(store, token, header);
                snapshot = store.Load(spec.Settings.RunId);
            }
            else
            {
                RunIdentity.CheckCompatibility(snapshot.Header, header);
            }

            LedgerCheck.Verify(snapshot);
            var run = new LiveEngineRun(store, spec with { Settings = spec.Settings with { Horizon = horizon } }, crash, token, snapshot);
            store.AppendRecoveryLog(token, new RecoveryLogEntry(snapshot.RecoveryLog.Count + 1, token.Epoch, snapshot.Steps.Count, snapshot.Outputs.Count, snapshot.Inputs.Count, snapshot.Completed ? "OpenedCompleted" : "Opened", $"compatibility={header.Compatibility.EngineCompatibilityId}"));
            run.Replay();
            return run;
        }
        catch
        {
            store.Release(token);
            throw;
        }
    }

    private static void Create(IRunStore store, OwnershipToken token, RunHeader header)
    {
        try
        {
            store.CreateRun(token, header);
        }
        catch (UnknownCommitOutcomeException)
        {
            store.CreateRun(token, header);
        }
    }

    private void Replay()
    {
        if (_snapshot.Steps.Count == 0)
        {
            return;
        }

        _replaying = true;
        try
        {
            var byStep = _snapshot.Inputs.GroupBy(i => i.StepSeq).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var step in _snapshot.Steps)
            {
                if (!byStep.TryGetValue(step.StepSeq, out var records) || records.Count == 0)
                {
                    Halt(HaltKind.MissingInput, $"durable step {step.StepSeq} has no journaled input", $"step {step.StepSeq}: no inputs");
                }

                foreach (var unit in records!.GroupBy(r => r.GroupOrdinal).OrderBy(g => g.Key))
                {
                    var ordered = unit.OrderBy(r => r.Seq).ToList();
                    InputsReplayed += ordered.Count;
                    ReplayUnit(ordered);
                }

                if (_nextStep != step.StepSeq + 1)
                {
                    Halt(HaltKind.ReplayDivergence, "Replaying the durable inputs did not end the step where the durable run ended it.", $"step {step.StepSeq}: the replayed units did not close the step (next step {_nextStep})");
                }
            }
        }
        finally
        {
            _replaying = false;
        }
    }

    private void ReplayUnit(IReadOnlyList<InputRecord> records)
    {
        var first = records[0];
        switch (first.Kind)
        {
            case InputRecord.AdvanceKind:
                ApplyAdvance(first.At, records);
                break;
            case InputRecord.FinishKind:
                ApplyFinish(records);
                break;
            default:
            {
                var events = new List<EngineEvent>(records.Count);
                foreach (var r in records)
                {
                    EngineEvent e;
                    try
                    {
                        e = InputCodec.ToEvent(r);
                    }
                    catch (Exception ex) when (ex is NotSupportedException or System.Text.Json.JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
                    {
                        Halt(HaltKind.CorruptLedger, "A durable input cannot be decoded.", $"input {r.Seq}: {ex.Message}");
                        throw;
                    }

                    if (InputCodec.ToRecord(r.Seq, r.StepSeq, r.GroupOrdinal, e).ContentHash != r.ContentHash)
                    {
                        Halt(HaltKind.CorruptLedger, "A durable input is not in canonical form.", $"input {r.Seq}: re-encoding the decoded event gives a different hash");
                    }

                    events.Add(e);
                }

                ApplyGroup(events, records);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>
    /// Offers one availability group (every event with the same <see cref="EngineEvent.AvailableAt"/>). Exact duplicates of events already delivered, and groups after the
    /// horizon, are dropped before they can be journaled. Returns whether any input was accepted. The group is processed in memory at once and committed according
    /// to the commit policy (always when it produced an output or completed a bar).
    /// </summary>
    public bool Offer(IReadOnlyList<EngineEvent> group)
    {
        Guard();
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count == 0)
        {
            return false;
        }

        var at = group[0].AvailableAt;
        if (group.Any(e => e.AvailableAt != at))
        {
            throw new ArgumentException("A group must contain events of exactly one AvailableAt.", nameof(group));
        }

        if (group.Select(e => e.Identity).Distinct().Count() != group.Count)
        {
            throw new ArgumentException("A group must not contain the same event identity twice.", nameof(group));
        }

        if (at > _horizon)
        {
            return false;
        }

        var fresh = group.Where(e => !(_loop.TryGetSeen(e.Identity, out var seen) && seen.Equals(e))).OrderBy(e => e, EventOrdering.Instance).ToList();
        if (fresh.Count == 0)
        {
            return false;
        }

        var records = fresh.Select((e, i) => InputCodec.ToRecord(_nextInputSeq + i, -1, _nextGroupOrdinal, e)).ToList();
        Protect(() => ApplyGroup(fresh, records));
        return true;
    }

    /// <summary>
    /// Advances the engine clock to <paramref name="watermark"/> (the caller asserts that no undelivered event is available before it) and fires the deadlines before it,
    /// but ONLY when that fires at least one deadline: an advance that changes nothing is neither applied nor journaled. Returns whether it was applied.
    /// </summary>
    public bool Advance(DateTimeOffset watermark)
    {
        Guard();
        var next = _loop.PendingDeadlines.FirstOrDefault();
        var effective = watermark < _horizon ? watermark : _horizon;
        if (next is null || next.Time >= effective)
        {
            return false;
        }

        var records = new[] { InputCodec.AdvanceRecord(_nextInputSeq, -1, _nextGroupOrdinal, watermark) };
        Protect(() => ApplyAdvance(watermark, records));
        return true;
    }

    /// <summary>Ends the session: fires every deadline up to the horizon (final close included), writes the final audit and summary record and completes the run.</summary>
    public LiveFinishResult Finish()
    {
        Guard();
        var records = new[] { InputCodec.FinishRecord(_nextInputSeq, -1, _nextGroupOrdinal, _horizon) };
        Protect(() => ApplyFinish(records));
        _store.AppendRecoveryLog(_token, new RecoveryLogEntry(_snapshot.RecoveryLog.Count + 2, _token.Epoch, _nextStep, _committedOutputs, _nextInputSeq, "Finished", $"replayed={StepsReplayed} committed={CommitsMade}"));
        var positions = _engine.Execution.AllPositions.ToList();
        return new LiveFinishResult(_engine.Outputs.Count, _engine.Outputs.OfType<FillRecord>().Count(), positions.Count, positions.Count(p => p.State == PositionState.Unresolved), _engine.Admission.QuarantinedCount);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _store.Release(_token);
        }
    }

    // ------------------------------------------------------------------ units

    private void ApplyGroup(IReadOnlyList<EngineEvent> events, IReadOnlyList<InputRecord> records)
    {
        var ordinal = _nextGroupOrdinal++;
        _nextInputSeq += records.Count;
        _offeredEvents += records.Count;
        _pendingRecords.AddRange(records);
        var admitted = _engine.Admission.Admit(_loop, events);
        if (admitted.Count > 0)
        {
            _loop.ProcessGroup(admitted);
        }

        RecordQuotesAndBars(events);
        RecordOutputs();
        var hasBar = events.OfType<BarCompletedEvent>().Select(b => (long?)b.BarSeq).DefaultIfEmpty().Max();
        if (hasBar is { } bar)
        {
            _processedBarSeq = _processedBarSeq is { } p ? Math.Max(p, bar) : bar;
        }

        var at = events[0].AvailableAt;
        var kind = events.All(e => e.Kind is EventKind.Control or EventKind.FeedHealth) ? UnitKind.ControlOnly : UnitKind.DataGroup;
        var maxSource = events.Where(e => e.Kind is EventKind.SpotQuote or EventKind.OptionQuote or EventKind.FutureQuote).Select(e => (long?)e.SourceId).DefaultIfEmpty().Max();
        OnUnit(kind, ordinal, at, maxSource, hasBar);
    }

    private readonly List<InputRecord> _pendingRecords = new();

    private void ApplyAdvance(DateTimeOffset watermark, IReadOnlyList<InputRecord> records)
    {
        var ordinal = _nextGroupOrdinal++;
        _nextInputSeq += records.Count;
        _offeredEvents += records.Count;
        _pendingRecords.AddRange(records);
        _loop.AdvanceTo(watermark < _horizon ? watermark : _horizon);
        RecordOutputs();
        OnUnit(UnitKind.Advance, ordinal, null, null, null);
    }

    private void ApplyFinish(IReadOnlyList<InputRecord> records)
    {
        var ordinal = _nextGroupOrdinal++;
        _nextInputSeq += records.Count;
        _offeredEvents += records.Count;
        _pendingRecords.AddRange(records);
        _loop.Finish(_horizon);
        RecordOutputs();
        RecordFinalChannels();
        OnUnit(UnitKind.Finish, ordinal, null, null, null);
    }

    /// <summary>
    /// Called after every completed unit. A unit that produced an output, completed a bar, is not a plain data group (control-only delivery, advance, finish), or follows
    /// enough engine time / units since the last commit ends the step at once. Every one of those conditions depends only on journaled input and deterministic
    /// engine results, so a replay closes exactly the same steps.
    /// </summary>
    private void OnUnit(UnitKind kind, long ordinal, DateTimeOffset? groupAt, long? maxSourceId, long? barSeq)
    {
        _batchUnits++;
        _batchFirstOrdinal ??= ordinal;
        _batchLastOrdinal = ordinal;
        _batchLastGroupAt = groupAt ?? _batchLastGroupAt;
        if (maxSourceId is { } id)
        {
            _batchMaxSourceId = _batchMaxSourceId is null ? id : Math.Max(_batchMaxSourceId.Value, id);
        }

        if (barSeq is { } bar)
        {
            _batchBarSeq = _batchBarSeq is null ? bar : Math.Max(_batchBarSeq.Value, bar);
        }

        var outputs = _engine.Outputs.Count;
        var due = kind != UnitKind.DataGroup
            || outputs > _committedOutputs
            || barSeq is not null
            || _batchUnits >= _options.MaxUnitsPerCommit
            || _loop.Now - _lastStepTime >= _options.MaxEngineTimeBetweenCommits;
        if (due)
        {
            EndStep(kind);
        }
    }

    private void EndStep(UnitKind kind)
    {
        var stepSeq = _nextStep++;
        var outputCount = _engine.Outputs.Count;
        var outputs = new List<OutputRecord>(Math.Max(0, outputCount - _committedOutputs));
        for (var i = _committedOutputs; i < outputCount; i++)
        {
            var o = _engine.Outputs[i];
            var payload = OutputCodec.Encode(o);
            outputs.Add(new OutputRecord(i, stepSeq, o.GetType().Name, o.StrategyId, o.VariantId, o.At, payload, OutputCodec.ContentHash(payload)));
        }

        var inputs = _pendingRecords.Select(r => r with { StepSeq = stepSeq }).ToList();
        var digests = _recorders.Digests().OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new ChannelDigestRecord(d.Key, d.Value.Count, d.Value.Hash)).ToList();
        var step = StepRecord.Create(new StepRecord(
            stepSeq,
            kind,
            _loop.Now,
            _offeredEvents,
            _batchUnits,
            _batchFirstOrdinal,
            _batchLastOrdinal,
            _batchLastGroupAt,
            _batchMaxSourceId,
            _committedOutputs,
            outputCount,
            Array.Empty<string>(),
            digests,
            string.Empty));
        var barSeq = _batchBarSeq;
        var groupAt = _batchLastGroupAt;
        var checkpointDue = kind == UnitKind.Finish || (stepSeq + 1) % Math.Max(1, _options.CheckpointEverySteps) == 0;
        FinalRecord? final = null;
        if (kind == UnitKind.Finish)
        {
            var (audit, summaries) = FinalChannelLines();
            final = FinalRecord.Create(audit, summaries);
        }

        _batchUnits = 0;
        _batchFirstOrdinal = null;
        _batchLastOrdinal = null;
        _batchLastGroupAt = null;
        _batchMaxSourceId = null;
        _batchBarSeq = null;
        _pendingRecords.Clear();
        if (stepSeq < _snapshot.Steps.Count)
        {
            VerifyReplayed(step, outputs, inputs, final);
            StepsReplayed++;
        }
        else
        {
            CommitStep(step, outputs, inputs, final, checkpointDue);
        }

        if (barSeq is { } b)
        {
            _durableBarSeq = _durableBarSeq is { } d ? Math.Max(d, b) : b;
        }

        if (groupAt is { } g)
        {
            _durableGroupAt = _durableGroupAt is { } dg && dg > g ? dg : g;
        }

        _committedOutputs = outputCount;
        _lastStepTime = _loop.Now;
        if (kind == UnitKind.Finish)
        {
            _finished = true;
        }
    }

    private void VerifyReplayed(StepRecord step, List<OutputRecord> outputs, List<InputRecord> inputs, FinalRecord? final)
    {
        var durable = _snapshot.Steps[(int)step.StepSeq];
        var problems = new List<string>();
        if (durable.ContentHash != step.ContentHash)
        {
            foreach (var d in durable.Digests)
            {
                var mine = step.Digest(d.Channel);
                if (mine.Count != d.Count || mine.Hash != d.Hash)
                {
                    problems.Add($"step {step.StepSeq}: channel '{d.Channel}' differs (durable count {d.Count}, replayed {mine.Count})");
                }
            }

            if (durable.Kind != step.Kind || durable.EngineTime != step.EngineTime || durable.OutputFrom != step.OutputFrom || durable.OutputToExclusive != step.OutputToExclusive || durable.OfferedRows != step.OfferedRows)
            {
                problems.Add($"step {step.StepSeq}: shape differs (kind {durable.Kind}/{step.Kind}, time {durable.EngineTime:O}/{step.EngineTime:O}, outputs [{durable.OutputFrom},{durable.OutputToExclusive})/[{step.OutputFrom},{step.OutputToExclusive}), inputs {durable.OfferedRows}/{step.OfferedRows})");
            }

            if (problems.Count == 0)
            {
                problems.Add($"step {step.StepSeq}: content hash differs");
            }
        }

        foreach (var o in outputs)
        {
            if (o.Seq >= _snapshot.Outputs.Count || _snapshot.Outputs[(int)o.Seq].ContentHash != o.ContentHash)
            {
                problems.Add($"output {o.Seq} ({o.Type}) differs from the durable one");
            }
        }

        foreach (var i in inputs)
        {
            if (i.Seq >= _snapshot.Inputs.Count || _snapshot.Inputs[(int)i.Seq].ContentHash != i.ContentHash || _snapshot.Inputs[(int)i.Seq].StepSeq != i.StepSeq)
            {
                problems.Add($"input {i.Seq} differs from the durable one (or belongs to another step)");
            }
        }

        if (_durableCheckpoints.TryGetValue(step.StepSeq, out var checkpoint))
        {
            var state = CaptureState();
            if (state.Digest != checkpoint.StateDigest)
            {
                problems.Add($"step {step.StepSeq}: canonical state digest differs");
            }
            else
            {
                CheckpointsVerified++;
            }
        }

        if (step.Kind == UnitKind.Finish && _snapshot.Final is { } durableFinal && final is not null && durableFinal.ContentHash != final.ContentHash)
        {
            problems.Add("the final audit / summary record differs");
        }

        if (problems.Count > 0)
        {
            Halt(HaltKind.ReplayDivergence, "Replaying the durable inputs did not reproduce the durable state.", problems.ToArray());
        }
    }

    private void CommitStep(StepRecord step, List<OutputRecord> outputs, List<InputRecord> inputs, FinalRecord? final, bool checkpointDue)
    {
        CheckpointRecord? checkpoint = null;
        if (checkpointDue)
        {
            var state = CaptureState();
            checkpoint = new CheckpointRecord(step.StepSeq, _loop.Now, state.Digest, _options.KeepStateText ? state.Text : null);
        }

        var batch = new CommitBatch(step, outputs, ProjectionBuilder.Build(outputs), checkpoint, final) { Inputs = inputs, PositionSnapshots = SnapshotsOf(step) };
        _crash?.Reached(CrashPoint.BeforeCommit, step.StepSeq, step.Kind.ToString());
        try
        {
            _store.Commit(_token, batch);
        }
        catch (UnknownCommitOutcomeException unknown)
        {
            ResolveUnknown(batch, unknown);
        }
        catch (Exception e) when (e is not SimulatedProcessDeath)
        {
            Fault($"commit of step {step.StepSeq} failed: {e.Message}");
            throw new PersistenceFaultException($"Step {step.StepSeq} could not be committed; the instance was discarded and must be reconstructed from durable data.", e);
        }

        CommitsMade++;
        _crash?.Reached(CrashPoint.AfterCommit, step.StepSeq, step.Kind.ToString());
    }

    /// <summary>The observer cache of every position that is open or changed state in this step (state as of the end of the step).</summary>
    private List<PositionSnapshotRow> SnapshotsOf(StepRecord step)
    {
        var rows = new List<PositionSnapshotRow>();
        var closedNow = _engine.Outputs.Skip((int)step.OutputFrom).Take((int)(step.OutputToExclusive - step.OutputFrom)).OfType<PositionClosed>().Select(c => c.PositionId).ToHashSet(StringComparer.Ordinal);
        var now = _loop.Now;
        foreach (var p in _engine.Execution.AllPositions)
        {
            if (p.State == PositionState.Closed && !closedNow.Contains(p.Id))
            {
                continue;
            }

            var e = PositionExcursion.From(p);
            var valuation = p.State == PositionState.Closed ? null : _engine.Execution.Value(p, now);
            decimal? unrealized = valuation?.Bid is { } bid ? (bid - p.AvgEntryPrice) * p.OpenQuantity : null;
            rows.Add(new PositionSnapshotRow(
                p.Id,
                p.Variant.StrategyId,
                p.Variant.VariantId,
                now.UtcTicks,
                p.State.ToString(),
                p.OpenQuantity,
                p.ReservedExitQuantity,
                p.ExitPending,
                p.Tp1.ToString(),
                p.Tp1FilledQuantity,
                valuation?.Bid is { } b ? DecimalText.Format(b) : null,
                valuation?.Bid is null ? null : (p.LastBidObservationAt ?? now).UtcTicks,
                unrealized is { } u ? DecimalText.Format(u) : null,
                Text(e.Mfe),
                Text(e.Mae),
                Text(e.MfePct),
                Text(e.MaePct),
                Text(e.MaxBid),
                Text(e.MinBid),
                e.Observations,
                e.Gaps,
                e.Coverage.ToString()));
        }

        return rows;
    }

    private static string? Text(decimal? value) => value is { } v ? DecimalText.Format(v) : null;

    private void ResolveUnknown(CommitBatch batch, UnknownCommitOutcomeException unknown)
    {
        try
        {
            var seen = _store.Load(_token.RunId);
            if (seen.Steps.Count > batch.Step.StepSeq)
            {
                if (seen.Steps[(int)batch.Step.StepSeq].ContentHash != batch.Step.ContentHash)
                {
                    throw new ConflictFaultException($"Step {batch.Step.StepSeq} exists durably with different content than the one this instance computed.");
                }

                return;
            }

            _store.Commit(_token, batch);
        }
        catch (Exception e) when (e is not SimulatedProcessDeath)
        {
            Fault($"commit of step {batch.Step.StepSeq} has an unresolved outcome: {e.Message}");
            throw new PersistenceFaultException($"The outcome of step {batch.Step.StepSeq} could not be resolved; the instance was discarded.", unknown);
        }
    }

    // ------------------------------------------------------------------ state, recording

    private StateWriter CaptureState()
    {
        var writer = new StateWriter();
        _loop.WriteState(writer);
        _engine.WriteState(writer);
        return writer;
    }

    private void RecordQuotesAndBars(IReadOnlyList<EngineEvent> events)
    {
        var quotes = _recorders[RunRecorders.Names.Quotes];
        var bars = _recorders[RunRecorders.Names.Bars];
        var readiness = _recorders[RunRecorders.Names.Readiness];
        foreach (var e in events.OrderBy(x => x, EventOrdering.Instance))
        {
            switch (e)
            {
                case QuoteEvent q:
                    quotes.Add($"{q.Kind}:{q.SourceId}", FormattableString.Invariant($"{Canonical.Time(q.AvailableAt)}|{q.Kind}|{q.SourceId}|{q.Token}|{q.Bid}|{q.Ask}|{q.Last}|{q.BidQty}|{q.AskQty}"));
                    break;
                case BarCompletedEvent b when b.Info is { } info:
                    bars.Add($"b{b.BarSeq}", $"{Canonical.Time(b.AvailableAt)}|b{b.BarSeq}|{Canonical.Json(info)}");
                    readiness.Add($"b{b.BarSeq}", FormattableString.Invariant($"{Canonical.Time(b.AvailableAt)}|b{b.BarSeq}|ready={info.Ready}|valid={info.IsValid}|consecutive={info.ConsecutiveValidBars}/{info.RequiredValidBars}|signalStarted={info.SignalStarted}|{info.ReadinessSpec}"));
                    break;
                case BarCompletedEvent b:
                    bars.Add($"b{b.BarSeq}", $"{Canonical.Time(b.AvailableAt)}|b{b.BarSeq}|no-info");
                    break;
            }
        }

        foreach (var b in events.OfType<BarCompletedEvent>())
        {
            var candles = _engine.Variants.Candles.CandlesPublishedFor(b.BarSeq);
            _recorders[RunRecorders.Names.Candles].Add($"b{b.BarSeq}", $"{Canonical.Time(b.AvailableAt)}|b{b.BarSeq}|" + Canonical.Json(candles.ToArray()));
        }
    }

    private void RecordOutputs()
    {
        var outputs = _engine.Outputs;
        var channel = _recorders[RunRecorders.Names.Outputs];
        var changed = _outputsRecorded < outputs.Count;
        for (; _outputsRecorded < outputs.Count; _outputsRecorded++)
        {
            var o = outputs[_outputsRecorded];
            channel.Add($"O{_outputsRecorded}:{o.GetType().Name}:{o.Variant}", $"{Canonical.Time(o.At)}|{Canonical.Typed(o)}");
        }

        if (!changed)
        {
            return;
        }

        var opportunities = _recorders[RunRecorders.Names.Opportunities];
        foreach (var o in _engine.Variants.AllOpportunities)
        {
            var line = $"{o.Variant}|{o.Id}|{o.State}|{o.Contract?.Token}|attempts={o.Attempts}|selection={o.SelectionAttempts}|cancel={o.CancelReason}|terminal={o.TerminalReason}";
            if (!_opportunityLines.TryGetValue(o.Id, out var previous) || previous != line)
            {
                _opportunityLines[o.Id] = line;
                opportunities.Add($"{o.Variant}:{o.Id}", $"{Canonical.Time(_loop.Now)}|{line}");
            }
        }
    }

    private (IReadOnlyList<string> Audit, IReadOnlyList<string> Summaries) FinalChannelLines()
    {
        var audit = _engine.AuditTrailEntries()
            .Select(a => $"{Canonical.Time(a.At)}|{a.AccountId}|{a.StrategyId}|{a.VariantId}|{a.OpportunityId}|{a.OrderId}|{a.PositionId}|{a.ConfigHash}|{a.VariantHash}|{a.Category}|{a.Reason}|{a.Event}|{a.Detail}")
            .ToList();
        var summaries = _engine.DailySummaries(_horizon).Select(s => Canonical.Json(s with { RunId = string.Empty })).ToList();
        return (audit, summaries);
    }

    private void RecordFinalChannels()
    {
        var (audit, summaries) = FinalChannelLines();
        foreach (var (line, i) in audit.Select((l, i) => (l, i)))
        {
            _recorders[RunRecorders.Names.Audit].Add($"A{i}", line);
        }

        foreach (var (line, i) in summaries.Select((l, i) => (l, i)))
        {
            _recorders[RunRecorders.Names.Summaries].Add($"S{i}", line);
        }
    }

    // ------------------------------------------------------------------ plumbing

    private void Halt(HaltKind kind, string message, params string[] details)
    {
        Fault($"{kind}: {details[0]}");
        try
        {
            _store.AppendRecoveryLog(_token, new RecoveryLogEntry(_snapshot.RecoveryLog.Count + 2, _token.Epoch, _snapshot.Steps.Count, _snapshot.Outputs.Count, _snapshot.Inputs.Count, "Halted:" + kind, details[0]));
        }
        catch (Exception e) when (e is StoreUnavailableException or UnknownCommitOutcomeException or FencedOutException)
        {
            // the diagnostic is best effort: the halt itself is what matters
        }

        throw new RecoveryHaltException(kind, message, details);
    }

    private void Guard()
    {
        if (_faulted)
        {
            throw new InstanceFaultedException($"This instance was discarded ({_faultReason}); reconstruct it from durable data.");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LiveEngineRun));
        }

        if (_finished)
        {
            throw new InvalidOperationException("The run has finished.");
        }
    }

    private void Protect(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not SimulatedProcessDeath)
        {
            if (!_faulted)
            {
                Fault(e.Message);
            }

            throw;
        }
    }

    private void Fault(string reason)
    {
        _faulted = true;
        _faultReason ??= reason;
    }
}
