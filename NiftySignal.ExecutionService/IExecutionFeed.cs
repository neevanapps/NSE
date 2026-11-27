using NiftySignal.AdaptiveObserver;
using NiftySignal.PaperEngineV2.Core.Frames;
using NiftySignal.PaperEngineV2.Execution;
using NiftySignal.PaperEngineV2.Runtime;

namespace NiftySignal.ExecutionService;

public sealed record SessionRead(V2SessionInfo? Session, ObserverState Observer);

/// <summary><see cref="ProgressOnly"/> marks a tick (the session future) that only measures how far the Observer has processed: never a quote, never evidence of fresh option data.</summary>
public sealed record FeedTick(string Token, CleanObserverTick Tick, bool IsVix, bool ProgressOnly = false);

public sealed record PublishedBar(int BarSeq, bool Verified, string? UnverifiedReason);

/// <summary>Everything the execution service reads from other processes' data. Read-only; nothing here is written by the service.</summary>
public interface IExecutionFeed
{
    /// <summary>Today's fixed-clock Observer session (frozen option universe) and the Observer's runtime state.</summary>
    Task<SessionRead> ReadSessionAsync(DateOnly tradeDate, CancellationToken ct);

    /// <summary>Clean ticks of the session's options, future and India VIX stored since the previous call (the first call reads only a short look-back window).</summary>
    Task<IReadOnlyList<FeedTick>> ReadTicksAsync(V2SessionInfo session, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<PublishedBar>> ReadPublishedAsync(long sessionId, int afterBarSeq, int limit, CancellationToken ct);

    /// <summary>The frame of a published bar (without quotes), or the reason it cannot be assembled.</summary>
    Task<(MarketStateFrame? Frame, string? Problem)> BuildFrameAsync(V2SessionInfo session, PublishedBar bar, CancellationToken ct);

    Task<IReadOnlyList<ContractAverage>> ReadAveragesAsync(V2SessionInfo session, IReadOnlyCollection<string> tokens, int barSeq, CancellationToken ct);
}
