using NiftySignal.PaperEngineV2.Core.Frames;

namespace NiftySignal.PaperEngineV2.Core.Pipeline;

public enum GuardVerdict { Accept, AcceptAfterGap, SessionStart, Duplicate, RejectConflict, RejectOutOfOrder, RejectCausality }

public sealed record GuardDecision(GuardVerdict Verdict, IntegrityFlags Flags, int? GapFrom, int? GapTo, bool PreviousUsable)
{
    public bool Accepted => Verdict is GuardVerdict.Accept or GuardVerdict.AcceptAfterGap or GuardVerdict.SessionStart;
}

/// <summary>
/// Process-local sequence and causality checks for one consumer: duplicates (same hash ignored, different hash = conflict), out-of-order arrivals, missing bar sequences (a detected GAP), backwards decision keys and
/// inconsistent same-instant ordinals (split bars of one market update). It only reads frames; nothing it records survives the process (durable gap recording is Phase 6d).
/// </summary>
public sealed class FrameSequenceGuard
{
    public const int MaxTrackedFrames = 100_000;      // structural bound: a session never has this many fixed-13,650 bars

    readonly Dictionary<int, string> _hashes = new();
    long? _sessionId;
    MarketStateFrame? _last;

    public int? LastBarSeq => _last?.Identity.BarSeq;

    public MarketStateFrame? LastFrame => _last;

    public GuardDecision Evaluate(MarketStateFrame frame)
    {
        var id = frame.Identity;
        if (_sessionId != id.SessionId)
        {
            _sessionId = id.SessionId; _hashes.Clear(); _last = null;
            return Accept(frame, GuardVerdict.SessionStart, IntegrityFlags.None, null, null, false);
        }

        if (_hashes.TryGetValue(id.BarSeq, out var known))
            return string.Equals(known, frame.Hash, StringComparison.Ordinal)
                ? new GuardDecision(GuardVerdict.Duplicate, IntegrityFlags.None, null, null, false)
                : new GuardDecision(GuardVerdict.RejectConflict, IntegrityFlags.DuplicateConflict, null, null, false);

        var last = _last!;
        if (id.BarSeq <= last.Identity.BarSeq) return new GuardDecision(GuardVerdict.RejectOutOfOrder, IntegrityFlags.OutOfOrder, null, null, false);
        if (id.DecisionKey.CompareTo(last.Identity.DecisionKey) < 0) return new GuardDecision(GuardVerdict.RejectCausality, IntegrityFlags.CausalityViolation, null, null, false);

        if (id.BarSeq == last.Identity.BarSeq + 1)
        {
            // a bar of the same market update (same availability instant) must be the next sibling; a later instant restarts the ordinal at 0
            var expectedOrdinal = id.BarEndAvailableAt == last.Identity.BarEndAvailableAt ? last.Identity.SameInstantOrdinal + 1 : 0;
            if (id.SameInstantOrdinal != expectedOrdinal) return new GuardDecision(GuardVerdict.RejectCausality, IntegrityFlags.CausalityViolation, null, null, false);
            return Accept(frame, GuardVerdict.Accept, IntegrityFlags.None, null, null, last.Identity.Verified);
        }

        return Accept(frame, GuardVerdict.AcceptAfterGap, IntegrityFlags.SequenceGap, last.Identity.BarSeq + 1, id.BarSeq - 1, false);
    }

    /// <summary>Rebuilds the guard from journaled frames after a restart (so duplicates / ordering keep being detected across the restart).</summary>
    public void Restore(IEnumerable<MarketStateFrame> frames)
    {
        _hashes.Clear(); _last = null; _sessionId = null;
        foreach (var f in frames.OrderBy(f => f.Identity.BarSeq))
        {
            _sessionId = f.Identity.SessionId;
            _hashes[f.Identity.BarSeq] = f.Hash;
            _last = f;
        }
    }

    GuardDecision Accept(MarketStateFrame frame, GuardVerdict verdict, IntegrityFlags flags, int? from, int? to, bool previousUsable)
    {
        if (_hashes.Count >= MaxTrackedFrames) throw new InvalidOperationException("frame tracking bound exceeded");
        _hashes[frame.Identity.BarSeq] = frame.Hash;
        _last = frame;
        return new GuardDecision(verdict, flags, from, to, previousUsable);
    }
}

public enum ConsumerExit { Completed, Faulted, Cancelled }

public sealed record ConsumerStatus(
    long Processed, long Duplicates, long Rejected, long GapsDetected, long EntriesAllowedFrames, long EntriesBlockedFrames, int? LastBarSeq, int RunLength, bool Faulted,
    string? FaultMessage, bool JournalFaulted, bool ClaimsDurableRecording);

/// <summary>
/// The independent V2 consumer: reads the bounded handoff on its OWN task, applies the sequence guard, journals every accepted frame and its disposition BEFORE processing, and hands the frame to the processor.
/// Entries are blocked (journaled reason flags) while integrity is uncertain: unacknowledged overflow / build-failure signals, a gap, an unverified frame, a processing lag beyond the configured limit, a journal fault,
/// a restart, or fewer than <see cref="HandoffOptions.FramesToClearGap"/> consecutive clean frames since the last such event. A missed entry signal is never executed later: after a gap the previous-frame link is dropped
/// (crossovers become Unknown) and the missed frames are simply absent. Any processor or journal exception is contained here - it can never reach the observer.
/// </summary>
public sealed class FrameConsumer
{
    readonly FrameHandoff? _handoff;
    readonly IFrameJournal _journal;
    readonly IFrameProcessor _processor;
    readonly TimeProvider _clock;
    readonly HandoffOptions _options;
    readonly FrameSequenceGuard _guard = new();
    readonly List<GapRecord> _gaps = [];
    long _processed, _duplicates, _rejected, _gapsDetected, _allowed, _blocked;
    int _run;
    bool _faulted, _journalFaulted;
    string? _faultMessage;
    MarketStateFrame? _previous;
    bool _previousUsable;

    public FrameConsumer(FrameHandoff? handoff, HandoffOptions options, IFrameJournal journal, IFrameProcessor processor, TimeProvider clock)
    {
        _handoff = handoff; _options = options; _journal = journal; _processor = processor; _clock = clock;
    }

    public IReadOnlyList<GapRecord> GapLog => _gaps;

    public ConsumerStatus Status => new(_processed, _duplicates, _rejected, _gapsDetected, _allowed, _blocked, _guard.LastBarSeq, _run, _faulted, _faultMessage, _journalFaulted, _journal.IsDurable && !_journalFaulted);

    /// <summary>
    /// Restart after a fault: replays the session's journal through the FRESH processor with the journaled dispositions, restores the guard, and starts with a clean-run length of 0 so entries stay blocked until
    /// <see cref="HandoffOptions.FramesToClearGap"/> new clean frames have been accepted. Not crash-safe recovery: the in-memory journal does not survive a process crash (Phase 6d).
    /// </summary>
    public async Task RecoverFromJournalAsync(long sessionId, CancellationToken ct)
    {
        var replay = await FrameReplayer.ReplayAsync(_journal, sessionId, _processor, ct);
        _guard.Restore(replay.Frames);
        _previous = replay.LastFrame;
        _previousUsable = replay.LastFrame?.Identity.Verified ?? false;
        _run = 0;
        _processed += replay.FramesReplayed;
    }

    public async Task<ConsumerExit> RunAsync(CancellationToken ct)
    {
        if (_handoff is null) throw new InvalidOperationException("this consumer has no handoff");
        try
        {
            await foreach (var frame in _handoff.Reader.ReadAllAsync(ct))
            {
                await HandleAsync(frame, ct);
                if (_faulted) return ConsumerExit.Faulted;
            }

            return ConsumerExit.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConsumerExit.Cancelled;
        }
    }

    /// <summary>Handles one frame. Never throws (faults are recorded and stop the consumer).</summary>
    public async Task HandleAsync(MarketStateFrame frame, CancellationToken ct)
    {
        if (_faulted) return;
        try
        {
            var decision = _guard.Evaluate(frame);
            if (decision.Verdict == GuardVerdict.Duplicate) { _duplicates++; return; }
            if (!decision.Accepted) { _rejected++; BreakRun(); return; }

            var flags = IntegrityFlags.None;
            var causeSignals = IntegrityFlags.None;
            if (decision.Verdict == GuardVerdict.AcceptAfterGap)
            {
                _gapsDetected++;
                causeSignals = _handoff?.TakeSignals() ?? IntegrityFlags.None;       // the producer signals are accounted for by the gap they caused
                var cause = (causeSignals & IntegrityFlags.Overflow) != 0 ? GapCause.Overflow : (causeSignals & IntegrityFlags.BuildFailure) != 0 ? GapCause.BuildFailure : GapCause.MissingFrame;
                var gap = new GapRecord(JournalSchema, frame.Identity.SessionId, decision.GapFrom!.Value, decision.GapTo!.Value, cause, causeSignals, Durable: false);
                _gaps.Add(gap);
                await TryJournalVoidAsync(() => _journal.AppendGapAsync(gap, ct).AsTask());
                _run = 0; flags |= IntegrityFlags.SequenceGap;
            }

            flags |= _handoff?.PeekSignals() ?? IntegrityFlags.None;                   // still-unacknowledged overflow / build failure: entries blocked
            if (!frame.Identity.Verified) { flags |= IntegrityFlags.UnverifiedFrame; _run = 0; }
            else if (decision.Verdict == GuardVerdict.SessionStart) _run = frame.Identity.BarSeq == 1 ? _options.FramesToClearGap : 1;    // a clean start at bar 1 needs no warm-up; a mid-session start counts as a post-gap run
            else if (decision.Verdict == GuardVerdict.AcceptAfterGap) _run = 1;
            else _run++;
            if (_journalFaulted) { flags |= IntegrityFlags.JournalFault; _run = 0; }

            var lag = _clock.GetUtcNow() - frame.Identity.BarEndAvailableAt;
            var lagMs = (int)Math.Clamp(lag.TotalMilliseconds, 0, int.MaxValue);
            if (lag > _options.MaxProcessingLag) flags |= IntegrityFlags.ProcessingLag;
            if (_run < _options.FramesToClearGap) flags |= IntegrityFlags.RunTooShort;
            var allowed = flags == IntegrityFlags.None;

            var framePreviousUsable = decision.PreviousUsable && _previousUsable;
            var withoutJournal = false;
            var append = await TryJournalAsync(() => _journal.AppendFrameAsync(FrameRecord.From(frame), ct).AsTask());
            if (append is AppendOutcome.ConflictDifferent) { _rejected++; return; }
            if (append is null) { withoutJournal = true; flags |= IntegrityFlags.JournalFault; allowed = false; _run = 0; }

            var disposition = new DispositionRecord(JournalSchema, frame.Identity.SessionId, frame.Identity.BarSeq, allowed, flags, lagMs, _run, framePreviousUsable, withoutJournal);
            if (append is not null) await TryJournalAsync(() => _journal.AppendDispositionAsync(disposition, ct).AsTask());

            if (allowed) _allowed++; else _blocked++;
            await _processor.ProcessAsync(frame, framePreviousUsable ? _previous : null,
                new FrameDisposition(allowed, flags, lagMs, _run, framePreviousUsable, IsReplay: false, withoutJournal), ct);
            _processed++;
            _previous = frame;
            _previousUsable = frame.Identity.Verified;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _faulted = true; _faultMessage = $"{ex.GetType().Name}: {ex.Message}";           // contained: the loop stops, frames queue up (and overflow visibly if the queue fills)
        }
    }

    const string JournalSchema = "pev2-journal/1";

    void BreakRun() => _run = 0;

    /// <summary>Runs a journal append; a thrown exception marks the journal faulted (entries blocked, nothing claimed durable) and returns null.</summary>
    async Task<AppendOutcome?> TryJournalAsync(Func<Task<AppendOutcome>> append)
    {
        try { var r = await append(); _journalFaulted = false; return r; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { _journalFaulted = true; return null; }
    }

    async Task TryJournalVoidAsync(Func<Task> append)
    {
        try { await append(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { _journalFaulted = true; }
    }
}
