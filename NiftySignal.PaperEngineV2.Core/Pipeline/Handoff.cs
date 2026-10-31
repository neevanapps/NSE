using System.Collections.Concurrent;
using System.Threading.Channels;
using NiftySignal.PaperEngineV2.Core.Frames;

namespace NiftySignal.PaperEngineV2.Core.Pipeline;

/// <summary>
/// Bounded, validated parameters of the observer-to-V2 handoff. There is deliberately NO default: the production capacity, the maximum processing lag and the gap-clearing run length are to be set from
/// Phase 6g benchmarks, so every caller (tests included) states them explicitly.
/// </summary>
public sealed record HandoffOptions
{
    public const int MaxCapacity = 100_000;

    public HandoffOptions(int capacity, TimeSpan maxProcessingLag, int framesToClearGap, int overflowLedgerCapacity = 256)
    {
        if (capacity is < 1 or > MaxCapacity) throw new ArgumentOutOfRangeException(nameof(capacity), $"capacity must be in [1, {MaxCapacity}]");
        if (maxProcessingLag <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxProcessingLag), "maxProcessingLag must be positive");
        if (framesToClearGap < 1) throw new ArgumentOutOfRangeException(nameof(framesToClearGap), "framesToClearGap must be >= 1");
        if (overflowLedgerCapacity is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(overflowLedgerCapacity), "overflowLedgerCapacity must be in [1, 10000]");
        Capacity = capacity; MaxProcessingLag = maxProcessingLag; FramesToClearGap = framesToClearGap; OverflowLedgerCapacity = overflowLedgerCapacity;
    }

    public int Capacity { get; }

    /// <summary>A frame processed later than this after its availability instant never opens a new entry (journaled, so replay agrees).</summary>
    public TimeSpan MaxProcessingLag { get; }

    /// <summary>After a gap, an unverified frame, a consumer restart or any integrity event, entries stay blocked until this many CONSECUTIVE contiguous verified frames have been accepted.</summary>
    public int FramesToClearGap { get; }

    public int OverflowLedgerCapacity { get; }
}

/// <summary>Why a frame could not be handed over (or why entries are blocked). Flags so several can hold at once.</summary>
[Flags]
public enum IntegrityFlags
{
    None = 0,
    Overflow = 1,
    BuildFailure = 2,
    SequenceGap = 4,
    OutOfOrder = 8,
    DuplicateConflict = 16,
    CausalityViolation = 32,
    UnverifiedFrame = 64,
    ConsumerFault = 128,
    JournalFault = 256,
    ProcessingLag = 512,
    RunTooShort = 1024,
}

public enum OfferOutcome { Accepted, RejectedFull, BuildFailed, Closed }

public readonly record struct OfferResult(OfferOutcome Outcome, long TotalOffered, long TotalRejected)
{
    public bool Accepted => Outcome == OfferOutcome.Accepted;
}

/// <summary>An observable record of a frame that could NOT be queued (its identity and decision key; the payload is never serialised on the observer thread). Process-local: lost with the process (nothing here is durable).</summary>
public sealed record OverflowRecord(long SessionId, int BarSeq, DecisionKey DecisionKey, long OfferSequence);

public sealed record HandoffStats(long Offered, long Accepted, long RejectedFull, long BuildFailures, int QueueDepth, int Capacity, IntegrityFlags ProducerSignals, int? FirstDroppedBarSeq, int? LastDroppedBarSeq);

/// <summary>
/// The observer-to-V2 handoff. <see cref="Offer"/> is O(1), lock-free, never waits, never throws and never touches a database or strategy code: a full queue REJECTS the frame, increments counters and raises the
/// producer-side <see cref="IntegrityFlags.Overflow"/> signal, so a frame is never lost silently - the consumer later sees the sequence gap, acknowledges the signal and blocks entries until the run is clean. The queue
/// holds only immutable frames. (Process-local: gap detection here is NOT durable gap recording, which arrives with persistence in 6d.)
/// </summary>
public sealed class FrameHandoff
{
    readonly Channel<MarketStateFrame> _channel;
    readonly ConcurrentQueue<OverflowRecord> _ledger = new();
    readonly int _ledgerCapacity;
    long _offered, _accepted, _rejected, _buildFailures;
    int _signals;
    int _firstDropped = -1, _lastDropped = -1;
    int _closed;

    public FrameHandoff(HandoffOptions options)
    {
        Options = options;
        _ledgerCapacity = options.OverflowLedgerCapacity;
        _channel = Channel.CreateBounded<MarketStateFrame>(new BoundedChannelOptions(options.Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    }

    public HandoffOptions Options { get; }

    public ChannelReader<MarketStateFrame> Reader => _channel.Reader;

    public OfferResult Offer(MarketStateFrame frame)
    {
        try
        {
            var seq = Interlocked.Increment(ref _offered);
            if (Volatile.Read(ref _closed) == 1) return new OfferResult(OfferOutcome.Closed, seq, Interlocked.Read(ref _rejected));
            if (_channel.Writer.TryWrite(frame))
            {
                Interlocked.Increment(ref _accepted);
                return new OfferResult(OfferOutcome.Accepted, seq, Interlocked.Read(ref _rejected));
            }

            var rejected = Interlocked.Increment(ref _rejected);
            Interlocked.Or(ref _signals, (int)IntegrityFlags.Overflow);
            Interlocked.CompareExchange(ref _firstDropped, frame.Identity.BarSeq, -1);
            Volatile.Write(ref _lastDropped, frame.Identity.BarSeq);
            _ledger.Enqueue(new OverflowRecord(frame.Identity.SessionId, frame.Identity.BarSeq, frame.Identity.DecisionKey, seq));
            while (_ledger.Count > _ledgerCapacity && _ledger.TryDequeue(out _)) { }          // the ring is bounded; the COUNTERS above never lose a drop
            return new OfferResult(OfferOutcome.RejectedFull, seq, rejected);
        }
        catch (Exception)
        {
            return new OfferResult(OfferOutcome.Closed, Interlocked.Read(ref _offered), Interlocked.Read(ref _rejected));      // never throws into the observer
        }
    }

    /// <summary>
    /// Builds the frame and offers it, all without ever throwing: a build failure is a producer-side signal (<see cref="IntegrityFlags.BuildFailure"/>), counted, and surfaces as a sequence gap. The builder delegate
    /// runs on the CALLER's thread (the observer's), so it must be the cheap pure construction only - no database, no strategy work.
    /// </summary>
    public OfferResult OfferBuilt(Func<MarketStateFrame> build)
    {
        MarketStateFrame frame;
        try
        {
            frame = build();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _buildFailures);
            Interlocked.Or(ref _signals, (int)IntegrityFlags.BuildFailure);
            return new OfferResult(OfferOutcome.BuildFailed, Interlocked.Read(ref _offered), Interlocked.Read(ref _rejected));
        }

        return Offer(frame);
    }

    /// <summary>Returns and clears the producer-side signals (the consumer calls this once it has accounted for them through the sequence gap they caused).</summary>
    public IntegrityFlags TakeSignals() => (IntegrityFlags)Interlocked.Exchange(ref _signals, 0);

    public IntegrityFlags PeekSignals() => (IntegrityFlags)Volatile.Read(ref _signals);

    public void Complete()
    {
        Volatile.Write(ref _closed, 1);
        _channel.Writer.TryComplete();
    }

    public IReadOnlyList<OverflowRecord> OverflowLedger() => _ledger.ToArray();

    public HandoffStats Stats() => new(Interlocked.Read(ref _offered), Interlocked.Read(ref _accepted), Interlocked.Read(ref _rejected), Interlocked.Read(ref _buildFailures), _channel.Reader.Count, Options.Capacity,
        PeekSignals(), _firstDropped < 0 ? null : _firstDropped, _lastDropped < 0 ? null : _lastDropped);
}
