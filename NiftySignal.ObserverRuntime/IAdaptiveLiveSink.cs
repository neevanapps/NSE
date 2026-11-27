using NiftySignal.AdaptiveObserver;

namespace NiftySignal.Host;

/// <summary>
/// An in-process consumer of the live Observer: the Host's optional paper bridges, which exist only where the Observer runs inside the Host. The NiftySignalObserver service registers none; its consumers
/// read the published bars. An implementation must contain its own failures.
/// </summary>
public interface IAdaptiveLiveSink
{
    /// <summary>A live session started or was recovered.</summary>
    void OnSessionStarted(AdaptiveObserverWorker.LiveState live);

    /// <summary>One availability group was processed and its completed bars persisted.</summary>
    void OnGroup(DateTimeOffset at, IReadOnlyList<(string Token, CleanObserverTick Tick)> group, IReadOnlyList<(AdaptiveCompletedBarPackage Package, bool Verified)> bars, AdaptiveObserverWorker.LiveState live);

    /// <summary>Every group at or before the stable cutoff has been processed.</summary>
    void OnPollCompleted(DateTimeOffset stableCutoff) { }

    /// <summary>The session is closing.</summary>
    void OnSessionClosing() { }
}
