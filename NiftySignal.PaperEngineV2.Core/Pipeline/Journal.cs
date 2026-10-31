using System.Security.Cryptography;
using System.Text;
using NiftySignal.PaperEngineV2.Core.Frames;

namespace NiftySignal.PaperEngineV2.Core.Pipeline;

public enum AppendOutcome { Appended, DuplicateSame, ConflictDifferent }

public enum GapCause { Overflow, BuildFailure, MissingFrame }

/// <summary>
/// One immutable frame as journaled (<c>pev2-journal/1</c>). <see cref="PayloadJson"/> is the canonical frame payload, <see cref="FrameHash"/> its SHA-256; uniqueness key = (<see cref="SessionId"/>, <see cref="BarSeq"/>).
/// <see cref="JournalSeq"/> is assigned by the journal (append order) and is not part of the frame identity.
/// </summary>
public sealed record FrameRecord(string Schema, long SessionId, int BarSeq, string FrameHash, string PayloadJson, DateTimeOffset BarEndAvailableAt, long LastSourceTickId, bool Verified, long JournalSeq = 0)
{
    public const string CurrentSchema = "pev2-journal/1";

    public static FrameRecord From(MarketStateFrame f) => new(CurrentSchema, f.Identity.SessionId, f.Identity.BarSeq, f.Hash, f.PayloadJson, f.Identity.BarEndAvailableAt, f.Identity.LastSourceTickId, f.Identity.Verified);
}

/// <summary>
/// The consumer's decision context for one frame, journaled BEFORE the frame is processed: processing lag, integrity flags, the clean-run length and whether the previous frame is a usable crossover predecessor.
/// Replay feeds these recorded values to the processor instead of recomputing them from the wall clock, which is what makes replay deterministic. Uniqueness key = (<see cref="SessionId"/>, <see cref="BarSeq"/>).
/// </summary>
public sealed record DispositionRecord(string Schema, long SessionId, int BarSeq, bool EntriesAllowed, IntegrityFlags BlockReasons, int ProcessingLagMs, int RunLength, bool PreviousFrameUsable, bool ProcessedWithoutJournal, long JournalSeq = 0);

/// <summary>A detected discontinuity (<see cref="FromBarSeq"/>..<see cref="ToBarSeq"/> never reached the consumer). <see cref="Durable"/> is true only when a durable journal recorded it (never in the 6b in-memory journal).</summary>
public sealed record GapRecord(string Schema, long SessionId, int FromBarSeq, int ToBarSeq, GapCause Cause, IntegrityFlags ProducerSignals, bool Durable, long JournalSeq = 0);

public sealed record JournalSession(IReadOnlyList<FrameRecord> Frames, IReadOnlyList<DispositionRecord> Dispositions, IReadOnlyList<GapRecord> Gaps);

/// <summary>
/// The journal contract Phase 6d will implement over PostgreSQL. <see cref="IsDurable"/> tells the truth about the implementation: the 6b in-memory journal is NOT durable and nothing may claim crash-safe recording on it.
/// Appends are idempotent per key (a repeated identical append is <see cref="AppendOutcome.DuplicateSame"/>; a different payload under the same key is <see cref="AppendOutcome.ConflictDifferent"/> and is never overwritten).
/// </summary>
public interface IFrameJournal
{
    bool IsDurable { get; }

    ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord record, CancellationToken ct);

    ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord record, CancellationToken ct);

    ValueTask AppendGapAsync(GapRecord record, CancellationToken ct);

    ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct);
}

/// <summary>An in-memory journal for tests and Phase 6b only: ordered, idempotent, key-unique - and NOT durable. <see cref="BeforeAppend"/> lets a test inject storage faults.</summary>
public sealed class InMemoryFrameJournal : IFrameJournal
{
    readonly object _gate = new();
    readonly List<FrameRecord> _frames = [];
    readonly List<DispositionRecord> _dispositions = [];
    readonly List<GapRecord> _gaps = [];
    long _seq;

    public bool IsDurable => false;

    /// <summary>Test hook invoked before every append with "frame" / "disposition" / "gap"; throwing simulates an unavailable store.</summary>
    public Action<string>? BeforeAppend { get; set; }

    public ValueTask<AppendOutcome> AppendFrameAsync(FrameRecord record, CancellationToken ct)
    {
        BeforeAppend?.Invoke("frame");
        lock (_gate)
        {
            var existing = _frames.FirstOrDefault(f => f.SessionId == record.SessionId && f.BarSeq == record.BarSeq);
            if (existing is not null) return ValueTask.FromResult(existing.FrameHash == record.FrameHash ? AppendOutcome.DuplicateSame : AppendOutcome.ConflictDifferent);
            _frames.Add(record with { JournalSeq = ++_seq });
            return ValueTask.FromResult(AppendOutcome.Appended);
        }
    }

    public ValueTask<AppendOutcome> AppendDispositionAsync(DispositionRecord record, CancellationToken ct)
    {
        BeforeAppend?.Invoke("disposition");
        lock (_gate)
        {
            var existing = _dispositions.FirstOrDefault(d => d.SessionId == record.SessionId && d.BarSeq == record.BarSeq);
            if (existing is not null) return ValueTask.FromResult(existing with { JournalSeq = 0 } == record with { JournalSeq = 0 } ? AppendOutcome.DuplicateSame : AppendOutcome.ConflictDifferent);
            _dispositions.Add(record with { JournalSeq = ++_seq });
            return ValueTask.FromResult(AppendOutcome.Appended);
        }
    }

    public ValueTask AppendGapAsync(GapRecord record, CancellationToken ct)
    {
        BeforeAppend?.Invoke("gap");
        lock (_gate) { _gaps.Add(record with { JournalSeq = ++_seq, Durable = false }); }
        return ValueTask.CompletedTask;
    }

    public ValueTask<JournalSession> ReadSessionAsync(long sessionId, CancellationToken ct)
    {
        lock (_gate)
            return ValueTask.FromResult(new JournalSession(
                _frames.Where(f => f.SessionId == sessionId).OrderBy(f => f.JournalSeq).ToArray(),
                _dispositions.Where(d => d.SessionId == sessionId).OrderBy(d => d.JournalSeq).ToArray(),
                _gaps.Where(g => g.SessionId == sessionId).OrderBy(g => g.JournalSeq).ToArray()));
    }
}

public sealed class ReplayIntegrityException(string message) : Exception(message);

/// <summary>The consumer's view of what to do with a frame, as journaled.</summary>
public sealed record FrameDisposition(bool EntriesAllowed, IntegrityFlags BlockReasons, int ProcessingLagMs, int RunLength, bool PreviousFrameUsable, bool IsReplay, bool ProcessedWithoutJournal);

/// <summary>The V2 strategy side of a frame (Phase 6c plugs the evaluator and the execution simulation in here). It runs on the CONSUMER's task only, never on the observer's thread.</summary>
public interface IFrameProcessor
{
    ValueTask ProcessAsync(MarketStateFrame frame, MarketStateFrame? previous, FrameDisposition disposition, CancellationToken ct);
}

public sealed record ReplayResult(int FramesReplayed, int FramesWithoutDisposition, MarketStateFrame? LastFrame, IReadOnlyList<MarketStateFrame> Frames);

public static class FrameReplayer
{
    /// <summary>
    /// Replays a journaled session through a (fresh) processor in journal order using the JOURNALED dispositions - never the wall clock. Every payload is re-hashed and re-validated (a tampered or corrupt payload throws
    /// <see cref="ReplayIntegrityException"/>); a frame without a disposition (a crash between the two appends) is replayed with entries blocked. Replay can never look ahead: frames are fed strictly in journal order.
    /// </summary>
    public static async Task<ReplayResult> ReplayAsync(IFrameJournal journal, long sessionId, IFrameProcessor processor, CancellationToken ct)
    {
        var session = await journal.ReadSessionAsync(sessionId, ct);
        var byBar = session.Dispositions.ToDictionary(d => d.BarSeq);
        MarketStateFrame? previous = null;
        var frames = new List<MarketStateFrame>();
        var without = 0;
        foreach (var rec in session.Frames.OrderBy(f => f.JournalSeq))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rec.PayloadJson))).ToLowerInvariant();
            if (!string.Equals(hash, rec.FrameHash, StringComparison.Ordinal)) throw new ReplayIntegrityException($"journaled payload of bar {rec.BarSeq} does not match its hash");
            MarketStateFrame frame;
            try { frame = FrameSerializer.Read(rec.PayloadJson); }
            catch (FrameValidationException ex) { throw new ReplayIntegrityException($"journaled payload of bar {rec.BarSeq} is invalid: {ex.Message}"); }
            if (!string.Equals(frame.Hash, rec.FrameHash, StringComparison.Ordinal)) throw new ReplayIntegrityException($"journaled payload of bar {rec.BarSeq} is not canonical");

            FrameDisposition d;
            if (byBar.TryGetValue(rec.BarSeq, out var disp))
                d = new FrameDisposition(disp.EntriesAllowed, disp.BlockReasons, disp.ProcessingLagMs, disp.RunLength, disp.PreviousFrameUsable, IsReplay: true, disp.ProcessedWithoutJournal);
            else { without++; d = new FrameDisposition(false, IntegrityFlags.ConsumerFault, 0, 0, false, IsReplay: true, false); }

            await processor.ProcessAsync(frame, d.PreviousFrameUsable ? previous : null, d, ct);
            previous = frame;
            frames.Add(frame);
        }

        return new ReplayResult(frames.Count, without, previous, frames);
    }
}
