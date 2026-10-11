using System.Globalization;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Config;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Core.Variants;
using NiftySignal.Trading.Recovery.Codec;

namespace NiftySignal.Trading.Recovery;

/// <summary>
/// Policy knobs of the durable layer. None changes engine behaviour; they decide how much is stored and where the commit boundaries fall. The two
/// commit bounds are PLACEHOLDERS taken from the CP6 plan (contract A.17.4), chosen to bound storage volume, not tuned against any data; the commit
/// bounds are part of the compatibility identity (they define what a step is), the checkpoint knobs are not.
/// </summary>
public sealed record DurableOptions
{
    /// <summary>
    /// A unit that produced no output is not committed on its own; it is committed once this much engine time has passed since the last commit, so that
    /// the cursor and the cumulative digests still advance in a quiet market. PLACEHOLDER (30 s, contract A.17.4).
    /// </summary>
    public TimeSpan MaxEngineTimeBetweenCommits { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the number of units in one step. PLACEHOLDER (20,000, contract A.17.4).</summary>
    public int MaxUnitsPerCommit { get; init; } = 20_000;

    /// <summary>A canonical state digest is stored (and, on recovery, verified) every this many committed steps, and always at the finish. 1 = every step.</summary>
    public int CheckpointEverySteps { get; init; } = 20;

    /// <summary>Also store the state text next to the digest (diagnostics: a mismatch can then be reported at the first differing line). Costly; off by default.</summary>
    public bool KeepStateText { get; init; }

    public string CommitPolicyText => string.Create(CultureInfo.InvariantCulture, $"unit-batches/1;maxEngineTimeTicks={MaxEngineTimeBetweenCommits.Ticks};maxUnits={MaxUnitsPerCommit}");

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxEngineTimeBetweenCommits <= TimeSpan.Zero)
        {
            errors.Add("MaxEngineTimeBetweenCommits must be positive.");
        }

        if (MaxUnitsPerCommit < 1)
        {
            errors.Add("MaxUnitsPerCommit must be at least 1.");
        }

        if (CheckpointEverySteps < 1)
        {
            errors.Add("CheckpointEverySteps must be at least 1.");
        }

        return errors;
    }
}

/// <summary>Everything that defines one run (the inputs of the pure core). Two runs with equal specs and equal ordered inputs produce identical outputs.</summary>
public sealed record DurableRunSpec(
    SessionFreeze Freeze,
    TranslatorOptions Translator,
    TradingConfig Config,
    IReadOnlyDictionary<string, NiftySignal.Trading.Core.Strategies.IStrategyFamily> Strategies,
    RunSettings Settings,
    string SourceManifest,
    RecorderOptions? Recorder = null,
    BinaryProvenance? Provenance = null,
    DurableOptions? Durable = null);

public enum CrashPoint
{
    /// <summary>A control / progress record is durable but has not been applied in memory.</summary>
    JournalWritten = 0,

    /// <summary>A unit completed in memory; nothing of it is durable yet.</summary>
    BeforeCommit = 1,

    /// <summary>A unit's commit is durable; the in-memory side has not continued.</summary>
    AfterCommit = 2,
}

/// <summary>Test seam: tests throw <see cref="SimulatedProcessDeath"/> from here to model abrupt process death at an exact point. Never set in production.</summary>
public interface ICrashPoints
{
    void Reached(CrashPoint point, long stepSeq, string detail);
}

public enum RunPhase
{
    /// <summary>Re-feeding the source: every unit is verified against the durable steps; nothing is written.</summary>
    Replaying = 0,

    /// <summary>Past the cursor: completed units are committed in steps according to the commit policy.</summary>
    Live = 1,

    /// <summary>The run was finished (now, or by an earlier incarnation).</summary>
    Finished = 2,
}

/// <summary>
/// One durable trading run (contract section A.18.4). It wraps the CP5 translator + engine unchanged and adds: a write-ahead control journal, one atomic
/// commit per step (a batch of completed units — availability groups, control-only deliveries, effective clock advances, the finish — see <see cref="DurableOptions"/>), and verified replay on
/// resume. <b>Resume model (Mode A):</b> the caller re-feeds the source from its first row; units up to the durable cursor are re-derived and compared
/// with what is stored (outputs, the nine channel digests, applied controls and, at checkpoints, the canonical state digest) and any difference halts
/// the run; past the cursor the same instance continues live and commits. Nothing is ever inserted twice and nothing is dispatched during replay.
/// <para>
/// <b>Failure model.</b> A failed or unknown commit means the in-memory engine has run ahead of the durable state. An unknown outcome is resolved
/// once by re-reading the store and comparing by deterministic identity; a definite failure, a fenced-out writer or anything unresolved FAULTS the
/// instance: it accepts no further input, and the only way forward is to dispose it and <see cref="Open"/> a new one from durable data.
/// </para>
/// </summary>
public sealed class DurableRun : IDisposable
{
    private readonly IRunStore _store;
    private readonly DurableRunSpec _spec;
    private readonly DurableOptions _options;
    private readonly ICrashPoints? _crash;
    private readonly OwnershipToken _token;
    private readonly TradingRun _run;
    private readonly RunSnapshot _snapshot;
    private readonly Dictionary<string, ControlRecord> _journal = new(StringComparer.Ordinal);
    private readonly List<ControlRecord> _pendingProgress = new();
    private readonly HashSet<string> _applied = new(StringComparer.Ordinal);
    private readonly List<string> _appliedSinceUnit = new();
    private readonly Dictionary<long, CheckpointRecord> _durableCheckpoints;
    private long _offered;
    private long _nextStep;
    private DateTimeOffset _lastStepTime;
    private int _batchUnits;
    private long? _batchFirstOrdinal;
    private long? _batchLastOrdinal;
    private DateTimeOffset? _batchLastGroupAt;
    private long? _batchMaxSourceId;
    private readonly List<string> _batchApplied = new();
    private int _committedOutputs;
    private bool _faulted;
    private string? _faultReason;
    private bool _disposed;
    private bool _finishing;
    private RunResult? _result;

    public long CommitsMade { get; private set; }

    public long StepsReplayed { get; private set; }

    public long ControlsJournaled { get; private set; }

    public long CheckpointsVerified { get; private set; }

    public long OutputBytesCommitted { get; private set; }

    private DurableRun(IRunStore store, DurableRunSpec spec, ICrashPoints? crash, OwnershipToken token, RunSnapshot snapshot, TradingRun run)
    {
        _store = store;
        _spec = spec;
        _options = spec.Durable ?? new DurableOptions();
        _crash = crash;
        _token = token;
        _snapshot = snapshot;
        _run = run;
        _durableCheckpoints = snapshot.Checkpoints.ToDictionary(c => c.StepSeq);
        _lastStepTime = run.Loop.Now;
    }

    public TradingRun Run => _run;

    public OwnershipToken Token => _token;

    public bool Faulted => _faulted;

    public string? FaultReason => _faultReason;

    public long DurableStepsAtOpen => _snapshot.Steps.Count;

    public RunPhase Phase => _result is not null || _snapshot.Completed && _nextStep >= _snapshot.Steps.Count ? RunPhase.Finished : _nextStep < _snapshot.Steps.Count ? RunPhase.Replaying : RunPhase.Live;

    public long OfferedRows => _offered;

    // ------------------------------------------------------------------ open / recover

    public static DurableRun Open(IRunStore store, string ownerId, DurableRunSpec spec, ICrashPoints? crash = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(spec);
        var optionErrors = (spec.Durable ?? new DurableOptions()).Validate();
        if (optionErrors.Count > 0)
        {
            throw new ArgumentException("Invalid durable options: " + string.Join(" ", optionErrors), nameof(spec));
        }

        var header = RunIdentity.BuildHeader(spec);
        var token = store.Acquire(spec.Settings.RunId, ownerId);
        try
        {
            var snapshot = store.Load(spec.Settings.RunId);
            if (snapshot.Header is null)
            {
                Create(store, token, header);
                snapshot = store.Load(spec.Settings.RunId);
            }
            else
            {
                CheckCompatibility(snapshot.Header, header);
            }

            LedgerCheck.Verify(snapshot);
            var controls = snapshot.Controls
                .Where(c => c.Kind is ControlKind.Operator or ControlKind.Health)
                .Select(ControlCodec.ToEvent)
                .ToList();
            var run = new TradingRun(spec.Freeze, spec.Translator, spec.Config, spec.Strategies, spec.Settings, spec.Recorder, controls);
            var durable = new DurableRun(store, spec, crash, token, snapshot, run);
            foreach (var c in snapshot.Controls)
            {
                durable._journal[c.Id] = c;
                if (c.Kind is ControlKind.SourceProgress or ControlKind.Advance)
                {
                    durable._pendingProgress.Add(c);
                }
            }

            run.UnitObserver = durable.OnUnit;
            store.AppendRecoveryLog(token, new RecoveryLogEntry(snapshot.RecoveryLog.Count + 1, token.Epoch, snapshot.Steps.Count, snapshot.Outputs.Count, snapshot.Controls.Count, snapshot.Completed ? "OpenedCompleted" : "Opened", $"compatibility={header.Compatibility.EngineCompatibilityId}"));
            durable.ApplyJournaledProgress();
            return durable;
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

    private static void CheckCompatibility(RunHeader durable, RunHeader running)
    {
        if (!durable.HashIsValid)
        {
            throw new RecoveryHaltException(HaltKind.CorruptLedger, "The durable run header is corrupt (hash mismatch).", new[] { "header hash" });
        }

        var diffs = durable.Compatibility.DifferencesFrom(running.Compatibility).ToList();
        void Check(string name, string a, string b)
        {
            if (!string.Equals(a, b, StringComparison.Ordinal))
            {
                diffs.Add($"{name} differs from the durable header");
            }
        }

        Check(nameof(RunHeader.RunId), durable.RunId, running.RunId);
        Check(nameof(RunHeader.TradeDate), durable.TradeDate.ToString("O", CultureInfo.InvariantCulture), running.TradeDate.ToString("O", CultureInfo.InvariantCulture));
        Check(nameof(RunHeader.Horizon), durable.Horizon.UtcTicks.ToString(CultureInfo.InvariantCulture), running.Horizon.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Check(nameof(RunHeader.ConfigJson), durable.ConfigJson, running.ConfigJson);
        Check(nameof(RunHeader.FreezeSnapshot), durable.FreezeSnapshot, running.FreezeSnapshot);
        Check(nameof(RunHeader.SourceManifest), durable.SourceManifest, running.SourceManifest);
        if (diffs.Count > 0)
        {
            throw new RecoveryHaltException(HaltKind.Compatibility, "The durable run is not compatible with this build / configuration; resume refused.", diffs);
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>Offers the next source row (in source order). During replay the row is verified through the units it completes; past the cursor it is processed and committed.</summary>
    public void Offer(SourceTick row)
    {
        Guard();
        Protect(() =>
        {
            _offered++;
            _run.Offer(row);
            _run.Pump(heartbeat: false);
            ApplyJournaledProgress();
        });
    }

    /// <summary>
    /// Declares "the source has delivered every row received before <paramref name="through"/>" (this is what lets a quiet market release groups).
    /// Journaled write-ahead only when it is effective; a repeated or older declaration is a no-op, so duplicate delivery is harmless.
    /// </summary>
    public bool DeclareSourceProgress(DateTimeOffset through)
    {
        Guard();
        var effective = _run.Translator.Boundary == DateTimeOffset.MinValue || through - _spec.Translator.StableOrderingLag > _run.Translator.Boundary;
        if (!effective)
        {
            return false;
        }

        return ApplyNew(ControlCodec.ProgressRecord(ControlKind.SourceProgress, through, _offered), r =>
        {
            _run.Translator.AdvanceSource(through);
            _run.Pump(heartbeat: false);
        });
    }

    /// <summary>
    /// Advances the engine clock to <paramref name="watermark"/> (it must not exceed the translator's stable boundary). Journaled and applied only when
    /// it fires at least one deadline (a quiet-market commit); otherwise it is a no-op and nothing is stored.
    /// </summary>
    public bool AdvanceClock(DateTimeOffset watermark)
    {
        Guard();
        if (watermark > _run.Translator.Boundary)
        {
            throw new ArgumentException("The watermark is beyond the stable boundary: groups before it may not have been released.", nameof(watermark));
        }

        if (!_run.AdvanceWouldFire(watermark))
        {
            return false;
        }

        return ApplyNew(ControlCodec.ProgressRecord(ControlKind.Advance, watermark, _offered), r => _run.AdvanceClock(watermark));
    }

    /// <summary>
    /// Submits an operator command or a feed-health event. It is journaled (write-ahead) before it is given to the engine; a duplicate delivery (same
    /// kind and sequence number) is recognised and ignored; the same identity with a different payload is a <see cref="ConflictFaultException"/>.
    /// An event must not be available before the translator's stable boundary: that position has already been decided by a past delivery.
    /// </summary>
    public bool Control(EngineEvent control)
    {
        Guard();
        var record = ControlCodec.ToRecord(control);
        if (_journal.TryGetValue(record.Id, out var existing))
        {
            if (existing.ContentHash != record.ContentHash)
            {
                throw new ConflictFaultException($"Control '{record.Id}' was journaled with different content.");
            }

            return false;
        }

        if (Phase == RunPhase.Replaying)
        {
            throw new InvalidOperationException("A new control cannot be accepted while the durable steps are still being replayed.");
        }

        if (control.AvailableAt < _run.Translator.Boundary)
        {
            throw new ArgumentException($"Control '{record.Id}' is available at {control.AvailableAt:O}, before the stable boundary {_run.Translator.Boundary:O}: its position was already decided.", nameof(control));
        }

        Journal(record);
        Protect(() => _run.AddControl(control));
        return true;
    }

    /// <summary>Ends the input. During replay it additionally proves that the source supplied every durable step (a shortened source halts as MissingInput).</summary>
    public RunResult Finish()
    {
        Guard();
        RunResult result = null!;
        Protect(() =>
        {
            ApplyJournaledProgress();
            _finishing = true;
            result = _run.Finish();
        });
        if (_nextStep < _snapshot.Steps.Count)
        {
            Fault($"{_snapshot.Steps.Count - _nextStep} durable step(s) were not reproduced");
            throw new RecoveryHaltException(HaltKind.MissingInput, "The source ended before every durable step was reproduced (missing input).", new[] { $"durable steps {_snapshot.Steps.Count}, reproduced {_nextStep}" });
        }

        _result = result;
        _store.AppendRecoveryLog(_token, new RecoveryLogEntry(_snapshot.RecoveryLog.Count + 2, _token.Epoch, _nextStep, _committedOutputs, _journal.Count, "Finished", $"replayed={StepsReplayed} committed={CommitsMade}"));
        return result;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _store.Release(_token);
        }
    }

    // ------------------------------------------------------------------ journal

    private bool ApplyNew(ControlRecord record, Action<ControlRecord> apply)
    {
        if (_journal.ContainsKey(record.Id))
        {
            return false;
        }

        if (Phase == RunPhase.Replaying)
        {
            // The durable journal is authoritative while replaying: a declaration the first incarnation never made would change history.
            throw new InvalidOperationException($"'{record.Id}' is not part of the durable journal, but the durable steps are still being replayed.");
        }

        Journal(record);
        _journal[record.Id] = record;
        _applied.Add(record.Id);
        Protect(() =>
        {
            _appliedSinceUnit.Add(record.Id);
            apply(record);
        });
        return true;
    }

    private void Journal(ControlRecord record)
    {
        try
        {
            _store.AppendControl(_token, record);
        }
        catch (UnknownCommitOutcomeException)
        {
            // Unknown: the record may or may not be durable. Re-appending is idempotent by identity; if that fails too, stop trading.
            try
            {
                _store.AppendControl(_token, record);
            }
            catch (Exception inner) when (inner is not SimulatedProcessDeath)
            {
                Fault($"journal append of '{record.Id}' unresolved: {inner.Message}");
                throw new PersistenceFaultException($"The journal append of '{record.Id}' could not be resolved; the instance was discarded.", inner);
            }
        }
        catch (Exception e) when (e is not SimulatedProcessDeath and not ConflictFaultException)
        {
            Fault($"journal append of '{record.Id}' failed: {e.Message}");
            throw new PersistenceFaultException($"The journal append of '{record.Id}' failed; the instance was discarded.", e);
        }

        _journal[record.Id] = record;
        ControlsJournaled++;
        _crash?.Reached(CrashPoint.JournalWritten, _nextStep, record.Id);
    }

    private void ApplyJournaledProgress()
    {
        for (var i = 0; i < _pendingProgress.Count;)
        {
            var record = _pendingProgress[i];
            if (record.AfterOfferedRows != _offered)
            {
                i++;
                continue;
            }

            _pendingProgress.RemoveAt(i);
            if (!_applied.Add(record.Id))
            {
                continue;
            }

            _appliedSinceUnit.Add(record.Id);
            var time = ControlCodec.ParseTime(System.Text.Json.JsonDocument.Parse(record.Payload).RootElement.GetProperty("time").GetString()!);
            if (record.Kind == ControlKind.SourceProgress)
            {
                _run.Translator.AdvanceSource(time);
                _run.Pump(heartbeat: false);
            }
            else
            {
                _run.AdvanceClock(time);
            }

            i = 0;
        }
    }

    // ------------------------------------------------------------------ units

    /// <summary>
    /// Called after every completed unit. A unit that produced output, a control-only delivery, an effective advance and the finish end the current
    /// step at once; an output-less data group only ends it once enough engine time or enough units have passed (so that the cursor and the cumulative
    /// digests still advance in a quiet market). A step therefore always ends at a unit boundary, the boundary depends only on deterministic values,
    /// and replay reproduces exactly the same boundaries.
    /// </summary>
    private void OnUnit(UnitInfo unit)
    {
        _batchUnits++;
        if (unit.GroupOrdinal is { } ordinal)
        {
            _batchFirstOrdinal ??= ordinal;
            _batchLastOrdinal = ordinal;
        }

        _batchLastGroupAt = unit.GroupAt ?? _batchLastGroupAt;
        if (unit.MaxSourceId is { } maxId)
        {
            _batchMaxSourceId = _batchMaxSourceId is null ? maxId : Math.Max(_batchMaxSourceId.Value, maxId);
        }

        _batchApplied.AddRange(_appliedSinceUnit);
        _appliedSinceUnit.Clear();
        foreach (var e in unit.Controls)
        {
            var kind = e is OperatorCommandEvent ? ControlKind.Operator : ControlKind.Health;
            _batchApplied.Add(ControlRecord.IdOf(kind, e.SourceId));
        }

        var due = unit.Kind != UnitKind.DataGroup
            || unit.OutputCount > _committedOutputs
            || _batchUnits >= _options.MaxUnitsPerCommit
            || unit.EngineNow - _lastStepTime >= _options.MaxEngineTimeBetweenCommits;
        if (!due)
        {
            return;
        }

        EndStep(unit);
    }

    private void EndStep(UnitInfo unit)
    {
        var stepSeq = _nextStep++;
        var outputs = new List<OutputRecord>(Math.Max(0, unit.OutputCount - _committedOutputs));
        for (var i = _committedOutputs; i < unit.OutputCount; i++)
        {
            var o = _run.Engine.Outputs[i];
            var payload = OutputCodec.Encode(o);
            outputs.Add(new OutputRecord(i, stepSeq, o.GetType().Name, o.StrategyId, o.VariantId, o.At, payload, OutputCodec.ContentHash(payload)));
        }

        var digests = _run.Recorders.Digests().OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => new ChannelDigestRecord(d.Key, d.Value.Count, d.Value.Hash)).ToList();
        var step = StepRecord.Create(new StepRecord(
            stepSeq,
            unit.Kind,
            unit.EngineNow,
            _offered,
            _batchUnits,
            _batchFirstOrdinal,
            _batchLastOrdinal,
            _batchLastGroupAt,
            _batchMaxSourceId,
            _committedOutputs,
            unit.OutputCount,
            _batchApplied.ToList(),
            digests,
            string.Empty));
        var checkpointDue = unit.Kind == UnitKind.Finish || (stepSeq + 1) % Math.Max(1, _options.CheckpointEverySteps) == 0;
        FinalRecord? final = null;
        if (unit.Kind == UnitKind.Finish)
        {
            var (audit, summaries) = _run.FinalChannelLines();
            final = FinalRecord.Create(audit, summaries);
        }

        _batchUnits = 0;
        _batchFirstOrdinal = null;
        _batchLastOrdinal = null;
        _batchLastGroupAt = null;
        _batchMaxSourceId = null;
        _batchApplied.Clear();
        if (stepSeq < _snapshot.Steps.Count)
        {
            VerifyReplayed(step, outputs, final);
            StepsReplayed++;
        }
        else
        {
            CommitStep(step, outputs, final, checkpointDue, unit);
        }

        _committedOutputs = unit.OutputCount;
        _lastStepTime = unit.EngineNow;
    }
    private void VerifyReplayed(StepRecord step, List<OutputRecord> outputs, FinalRecord? final)
    {
        var durable = _snapshot.Steps[(int)step.StepSeq];
        if (step.Kind == UnitKind.Finish && durable.Kind != UnitKind.Finish)
        {
            var detail = $"the source was finished after {step.OfferedRows} rows, but durable step {step.StepSeq} ({durable.Kind}) was produced from {durable.OfferedRows} rows";
            Fault("MissingInput: " + detail);
            _store.AppendRecoveryLog(_token, new RecoveryLogEntry(_snapshot.RecoveryLog.Count + 2, _token.Epoch, _snapshot.Steps.Count, _snapshot.Outputs.Count, _snapshot.Controls.Count, "Halted:MissingInput", detail));
            throw new RecoveryHaltException(HaltKind.MissingInput, "The source ended before every durable step was reproduced (missing input).", new[] { $"durable steps {_snapshot.Steps.Count}, reproduced {step.StepSeq}: {detail}" });
        }

        var problems = new List<string>();
        var sourceProblem = false;
        if (durable.OfferedRows != step.OfferedRows || durable.LastSourceId != step.LastSourceId || durable.LastGroupAt != step.LastGroupAt || durable.Digest("quotes") != step.Digest("quotes"))
        {
            sourceProblem = true;
            problems.Add($"step {step.StepSeq}: source input differs (offered {durable.OfferedRows} vs {step.OfferedRows}, last source id {durable.LastSourceId} vs {step.LastSourceId}, quotes digest {(durable.Digest("quotes") == step.Digest("quotes") ? "equal" : "different")})");
        }

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

            if (durable.Kind != step.Kind || durable.EngineTime != step.EngineTime || !durable.AppliedControls.SequenceEqual(step.AppliedControls) || durable.OutputFrom != step.OutputFrom || durable.OutputToExclusive != step.OutputToExclusive)
            {
                problems.Add($"step {step.StepSeq}: shape differs (kind {durable.Kind}/{step.Kind}, time {durable.EngineTime:O}/{step.EngineTime:O}, outputs [{durable.OutputFrom},{durable.OutputToExclusive})/[{step.OutputFrom},{step.OutputToExclusive}), controls [{string.Join(",", durable.AppliedControls)}]/[{string.Join(",", step.AppliedControls)}])");
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

        if (_durableCheckpoints.TryGetValue(step.StepSeq, out var checkpoint))
        {
            var state = _run.CaptureState();
            if (state.Digest != checkpoint.StateDigest)
            {
                problems.Add($"step {step.StepSeq}: canonical state digest differs{FirstStateDifference(checkpoint, state.Text)}");
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
            // A source that ends before the durable cursor releases its buffered groups early (at the finish); that is missing input, not a changed record.
            var kind = _finishing && step.OfferedRows < durable.OfferedRows ? HaltKind.MissingInput : sourceProblem ? HaltKind.SourceMismatch : HaltKind.ReplayDivergence;
            Fault($"{kind}: {problems[0]}");
            _store.AppendRecoveryLog(_token, new RecoveryLogEntry(_snapshot.RecoveryLog.Count + 2, _token.Epoch, _snapshot.Steps.Count, _snapshot.Outputs.Count, _snapshot.Controls.Count, "Halted:" + kind, problems[0]));
            throw new RecoveryHaltException(kind, kind == HaltKind.MissingInput ? "The source ended before every durable step was reproduced (missing input)." : sourceProblem ? "The source differs from the one the durable run was built from (changed, missing or reordered record)." : "Replaying the durable inputs did not reproduce the durable state.", problems);
        }
    }

    private static string FirstStateDifference(CheckpointRecord checkpoint, string replayedText)
    {
        if (checkpoint.StateText is null)
        {
            return " (state text not kept)";
        }

        var a = checkpoint.StateText.Split('\n');
        var b = replayedText.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<end>";
            var y = i < b.Length ? b[i] : "<end>";
            if (x != y)
            {
                return $"; first differing line {i}: durable '{x}' vs replayed '{y}'";
            }
        }

        return string.Empty;
    }

    private void CommitStep(StepRecord step, List<OutputRecord> outputs, FinalRecord? final, bool checkpointDue, UnitInfo unit)
    {
        CheckpointRecord? checkpoint = null;
        if (checkpointDue)
        {
            var state = _run.CaptureState();
            checkpoint = new CheckpointRecord(step.StepSeq, unit.EngineNow, state.Digest, _options.KeepStateText ? state.Text : null);
        }

        var batch = new CommitBatch(step, outputs, ProjectionBuilder.Build(outputs), checkpoint, final);
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
        OutputBytesCommitted += outputs.Sum(o => (long)o.Payload.Length);
        _crash?.Reached(CrashPoint.AfterCommit, step.StepSeq, step.Kind.ToString());
    }

    /// <summary>
    /// The commit's acknowledgement was lost. Re-read the store and decide by deterministic identity: an identical step at this sequence means the
    /// commit happened; an absent one means it did not and the same batch is applied once more (idempotent); anything else is a conflict. If the
    /// outcome cannot be established, the instance is discarded.
    /// </summary>
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

    // ------------------------------------------------------------------ plumbing

    private void Guard()
    {
        if (_faulted)
        {
            throw new InstanceFaultedException($"This instance was discarded ({_faultReason}); reconstruct it from durable data.");
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DurableRun));
        }

        if (_result is not null)
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
