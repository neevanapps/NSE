using System.Globalization;
using NiftySignal.AdaptiveObserverData;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// The quantity processed by the CURRENT incomplete fixed-volume bar, for the sticky top panel. <see cref="Quantity"/> is the builder's own accumulated volume
/// (<c>ExactAdaptiveFuturesBarBuilder.PartialVolume</c>, persisted by the Host in <c>adaptive_observer_runtime.CurrentPartialBarVolume</c> about once a second): never an estimate, and the builder resets it
/// when a bar completes. Unavailable (with a reason) when the runtime row is missing or inconsistent - it is never clamped or guessed.
/// </summary>
public sealed record BarProgress(long? Quantity, long Target, double? Percent, int? LastCompletedBarSeq, string? RuntimeStatus, DateTimeOffset? HeartbeatUtc, string? UnavailableReason)
{
    public static BarProgress Unavailable(string reason) => new(null, 0, null, null, null, null, reason);

    public string QuantityText => Quantity is { } q && Percent is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"{q:N0} / {Target:N0} – {p:0.0}%")
        : "unavailable";

    /// <summary>Quantity must be within [0, target]; anything else is an inconsistent source row and is reported, not clamped.</summary>
    public static BarProgress From(AdaptiveSessionStateRow session, AdaptiveObserverRuntimeRow runtime)
    {
        var target = session.BaseBarVolume;
        if (target <= 0) return Unavailable("session bar volume target is not positive");
        if (runtime.CurrentPartialBarVolume < 0 || runtime.CurrentPartialBarVolume > target)
            return new(null, target, null, runtime.LastCompletedBarSeq, runtime.RuntimeStatus.ToString(), runtime.LastHeartbeatUtc, "partial-bar quantity is outside [0, target]");
        return new(runtime.CurrentPartialBarVolume, target, runtime.CurrentPartialBarVolume * 100d / target, runtime.LastCompletedBarSeq, runtime.RuntimeStatus.ToString(), runtime.LastHeartbeatUtc, null);
    }
}

/// <summary>
/// One shared, bounded-cadence reader of the authoritative runtime row (a single indexed read per second for the WHOLE Dashboard, however many browser circuits are open, and none while nobody is
/// subscribed). No second tick pipeline, no per-tick work and no database WRITE: the Host already persists the builder's partial volume at most once a second. Subscribers are told only when the value
/// changed. The displayed quantity can therefore trail the live builder by the Host's 1 s flush plus its 2 s stable-ordering lag.
/// </summary>
public sealed class AdaptiveBarProgressService(AdaptiveObserverDataService data, ILogger<AdaptiveBarProgressService> logger) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    const int SessionRefreshEvery = 30;               // polls between full header reads (session identity / trade date roll-over)

    readonly object _gate = new();
    int _subscribers;
    CancellationTokenSource? _cts;
    long? _sessionId;
    int _sinceHeader = SessionRefreshEvery;
    AdaptiveSessionStateRow? _session;

    public BarProgress Current { get; private set; } = BarProgress.Unavailable("not loaded");

    public event Action? Changed;

    public void Subscribe()
    {
        lock (_gate)
        {
            if (_subscribers++ > 0) return;
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }
    }

    public void Unsubscribe()
    {
        lock (_gate)
        {
            if (_subscribers == 0 || --_subscribers > 0) return;
            _cts?.Cancel();
            _cts = null;
        }
    }

    async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do { await RefreshAsync(ct); }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One read: the full header every <see cref="SessionRefreshEvery"/> polls (or until a session is known), otherwise a single indexed runtime read.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        BarProgress next;
        try
        {
            if (_session is null || _sinceHeader >= SessionRefreshEvery)
            {
                var header = await data.LoadHeaderAsync(ct);
                _sinceHeader = 0;
                _session = header?.Session;
                _sessionId = header?.Session.Id;
                next = header is null ? BarProgress.Unavailable("no adaptive session today") : BarProgress.From(header.Session, header.Runtime);
            }
            else
            {
                _sinceHeader++;
                var runtime = await data.LoadRuntimeAsync(_sessionId!.Value, ct);
                next = runtime is null ? BarProgress.Unavailable("runtime row missing") : BarProgress.From(_session, runtime);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bar progress read failed");
            next = BarProgress.Unavailable($"read failed ({ex.GetType().Name})");
            _session = null;
        }

        // The heartbeat moves every flush; only a change of what is DISPLAYED wakes the subscribers.
        var changed = next.Quantity != Current.Quantity || next.Target != Current.Target || next.LastCompletedBarSeq != Current.LastCompletedBarSeq
            || next.UnavailableReason != Current.UnavailableReason || next.RuntimeStatus != Current.RuntimeStatus;
        Current = next;
        if (changed) Changed?.Invoke();
    }

    public void Dispose() { lock (_gate) { _cts?.Cancel(); _cts = null; } }
}
