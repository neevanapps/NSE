using NiftySignal.AdaptiveObserverData;
using NiftySignal.PaperEngineV2.Execution;

namespace NiftySignal.ExecutionService;

/// <summary>
/// What the service knows about the Observer. The last successfully read values are kept when a read fails, so an unreadable state ages into a stale one.
/// <see cref="ProcessedThroughUtc"/> is the availability time of the newest source tick group the Observer has finished; <see cref="LastCompletedBarSeq"/> its newest completed bar.
/// </summary>
public sealed record ObserverState(
    bool SessionExists, AdaptiveRuntimeStatus? Status, DateTimeOffset? HeartbeatUtc, bool ReadFailed = false, DateTimeOffset? ProcessedThroughUtc = null, int? LastCompletedBarSeq = null);

public sealed record ProtectionContext(
    DateTimeOffset Now, TimeOnly IstTime, ObserverState Observer, bool HasOpenPositions, TimeSpan NotLiveFor, DateTimeOffset? LatestMarketDataAt, bool Reconciled, string? FrameProblem,
    DateTimeOffset? OldestUnprocessedTickAt = null, DateTimeOffset? TicksReadFrom = null, bool BarEvaluationOverdue = false);

/// <summary>
/// The independent fail-closed gate (decision A6). Pure. <see cref="SafetyState.EntryBlock"/> forbids entries; <see cref="SafetyState.Invalidate"/> marks the blocks that also consume pending intents
/// (invalid or stale Observer health, unreconciled execution state); stale market data, a bar problem and Observer processing lag are temporary. <see cref="SafetyState.Flatten"/> additionally requests the exit of every open position.
/// It depends on no completed bar and no Dashboard.
/// </summary>
public static class ProtectionPolicy
{
    static readonly TimeOnly ObserverClose = new(15, 30);

    /// <summary>Every processing-freshness entry block starts with this.</summary>
    public const string ProcessingPrefix = "observer processing";

    public static SafetyState Evaluate(ProtectionContext c, ExecutionServiceOptions o)
    {
        var observer = c.Observer;
        var age = observer.HeartbeatUtc is { } beat ? c.Now - beat : (TimeSpan?)null;
        var flattenAfter = o.ObserverHeartbeatStale + o.FlattenGrace;

        string? flatten = null;
        if (observer.ReadFailed)
        {
            // an Observer state that cannot be read ages into a stale heartbeat; with positions and nothing ever read there is nothing to trust
            if (c.HasOpenPositions && (age is not { } known || known > flattenAfter)) flatten = "observer state is unreadable";
        }
        else if (!observer.SessionExists) { if (c.HasOpenPositions) flatten = "observer session is missing"; }
        else if (age is { } a && a > flattenAfter) flatten = "observer heartbeat is stale";
        else if (observer.Status is AdaptiveRuntimeStatus.RecoveryFailed or AdaptiveRuntimeStatus.Degraded) flatten = $"observer state is {observer.Status}";
        else if (observer.Status == AdaptiveRuntimeStatus.Closed && c.IstTime < ObserverClose) flatten = "observer closed the session early";
        else if (o.FlattenWhenNotLiveAfter > TimeSpan.Zero && c.NotLiveFor > o.FlattenWhenNotLiveAfter) flatten = "observer has not been live for longer than the configured limit";

        var block = observer.ReadFailed ? "observer state is unreadable"
            : !observer.SessionExists ? "observer session is missing"
            : observer.Status != AdaptiveRuntimeStatus.Live ? $"observer is not live ({observer.Status?.ToString() ?? "no runtime state"})"
            : age is not { } fresh || fresh > o.ObserverHeartbeatStale ? "observer heartbeat is stale"
            : c.LatestMarketDataAt is not { } data || c.Now - data > o.MarketDataStale ? "market data is stale"
            : !c.Reconciled ? "execution state is not reconciled with the ledger"
            : c.FrameProblem ?? ProcessingBlock(c, o);
        // a single failed read with a still fresh heartbeat is temporary; the same state ages into a stale heartbeat
        var heartbeatStale = age is not { } beatAge || beatAge > o.ObserverHeartbeatStale;
        var invalid = !observer.ReadFailed && !observer.SessionExists ? "observer session is missing"
            : !observer.ReadFailed && observer.Status != AdaptiveRuntimeStatus.Live ? $"observer is not live ({observer.Status?.ToString() ?? "no runtime state"})"
            : heartbeatStale ? "observer heartbeat is stale"
            : !c.Reconciled ? "execution state is not reconciled with the ledger"
            : null;
        return new SafetyState(block ?? flatten, flatten, invalid ?? flatten);
    }

    /// <summary>
    /// A fresh heartbeat shows the Observer is running, not that it is current. Entries are blocked unless its processed position is known and nothing older than the processing-lag limit is waiting for it.
    /// A quiet market has nothing waiting, so the age of the watermark or of the last bar alone never blocks. The texts carry no figures: they are journaled and must not change every cycle.
    /// </summary>
    static string? ProcessingBlock(ProtectionContext c, ExecutionServiceOptions o) =>
        c.Observer.ProcessedThroughUtc is not { } through || c.Observer.LastCompletedBarSeq is not { } bar ? ProcessingPrefix + " position is unavailable"
        : through > c.Now || bar < 0 ? ProcessingPrefix + " position is invalid"
        : c.TicksReadFrom is { } from && through < from ? ProcessingPrefix + " position predates the ticks execution has read"
        : c.OldestUnprocessedTickAt is { } waiting && c.Now - waiting > o.MaxProcessingLag ? ProcessingPrefix + " is behind the market data"
        : c.BarEvaluationOverdue ? ProcessingPrefix + ": a completed bar has not been evaluated"
        : null;
}
