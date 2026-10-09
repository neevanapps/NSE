using NiftySignal.AdaptiveObserver;
using System.Diagnostics;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>
/// Watch-only adaptive market observer. Authoritative calculations consume persisted raw ticks,
/// making full-day replay the recovery mechanism after every process restart.
/// </summary>
public sealed class AdaptiveObserverWorker(
    IServiceScopeFactory scopeFactory,
    AdaptiveHistoricalBootstrapService historicalBootstrap,
    AdaptiveSessionCoordinator coordinator,
    AdaptiveSourceTickReader tickReader,
    AdaptiveStateRecoveryService recovery,
    AdaptiveObserverPersistence persistence,
    AdaptiveWeak2ObservationService observations,
    AdaptiveEndedSessionRecoveryService endedSessionRecovery,
    AdaptiveSupplementalPersistence supplemental,
    AdaptiveCommentaryService commentary,
    DashboardPushClient dashboardPush,
    AdaptiveCommentaryNotificationGate notificationGate,
    ILogger<AdaptiveObserverWorker> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan OutsideMarketPoll = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StableOrderingLag = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Cadence of the operational runtime row (heartbeat, last processed source position, partial-bar progress). The row is not market data and
    /// restart correctness never depends on it (recovery replays the raw ticks), so ordinary live polling persists it about once per second
    /// instead of every 250 ms poll. Completed bars, status transitions and session close flush it immediately.
    /// </summary>
    internal static readonly TimeSpan RuntimePersistInterval = TimeSpan.FromSeconds(1);

    LiveState? _live;
    DateOnly? _historyBootstrappedFor;

    internal sealed class LiveState(
        AdaptiveObserverSessionContext context,
        AdaptiveRecoveryResult recovered)
    {
        public AdaptiveObserverSessionContext Context { get; } = context;
        public AdaptiveObserverEngine Engine { get; } = recovered.Engine;
        public AdaptiveIncrementalTickNormalizer Normalizer { get; } = recovered.Normalizer;
        public IReadOnlyList<string> Tokens { get; } = recovered.Tokens;
        public long LastFetchedRawId { get; set; } = recovered.LastFetchedRawId;
        public DateTimeOffset? LastProcessedAvailableAt { get; set; } = recovered.LastProcessedAvailableAt;
        public long? LastProcessedTickId { get; set; } = recovered.LastProcessedTickId;
        public List<ObserverTokenTick> Pending { get; } = recovered.Pending.ToList();
        /// <summary>Recovery hands back raw-Id-ordered pending rows; sort once, then only after new ticks are appended.</summary>
        public bool PendingNeedsSort { get; set; } = recovered.Pending.Count > 1;
        public DateTimeOffset? LastRuntimeFlushUtc { get; set; }
        /// <summary>Completed bars whose sidecar integrity is unknown (a write/verify failed). Commentary never evaluates at or past the first of them.</summary>
        public SortedDictionary<int, AdaptiveCompletedBarPackage> UnverifiedSidecars { get; } = new();
        /// <summary>Bars found unverified during recovery (no package retained to retry); commentary stays capped before them until the next recovery.</summary>
        public SortedSet<int> RecoveryUnverifiedBars { get; } = new(recovered.UnverifiedSidecarBars);

        public int? CommentaryCapBeforeBar
        {
            get
            {
                int? cap = RecoveryUnverifiedBars.Count > 0 ? RecoveryUnverifiedBars.Min : null;
                if (UnverifiedSidecars.Count > 0) cap = cap is { } c ? Math.Min(c, UnverifiedSidecars.Keys.First()) : UnverifiedSidecars.Keys.First();
                return cap;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var nowIst = nowUtc.ToOffset(IstOffset);
                var day = DateOnly.FromDateTime(nowIst.Date);
                var time = TimeOnly.FromDateTime(nowIst.DateTime);
                var weekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

                if (!IsLiveMarketWindow(weekday, time))
                {
                    if (_live is not null && time >= MarketHardClose)
                    {
                        await CloseCurrentSessionAsync(_live, stoppingToken);
                    }

                    _live = null;
                    // Not preemptible: once started a slow reconstruction cannot notice 09:15, so it never starts on weekday mornings.
                    if (ShouldRunEndedSessionRecovery(weekday, time))
                    {
                        await FinalizeEndedSessionsAsync(day, time >= MarketHardClose, stoppingToken);
                    }

                    await DelayAsync(OutsideMarketPoll, stoppingToken);
                    continue;
                }

                if (_live is null || _live.Context.Session.TradeDate != day)
                {
                    // Live priority: no ended-session reconstruction in this branch (it can be very slow and would delay today's start).
                    // Unfinished older sessions are retried only outside the live window (see IsLiveMarketWindow).
                    _live = await TryStartOrRecoverAsync(day, nowUtc, stoppingToken);
                    if (_live is null)
                    {
                        await DelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }
                }

                await PollLiveAsync(_live, nowUtc, stoppingToken);
                await DelayAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Adaptive observer live processing failed; discarding in-memory state and forcing deterministic recovery.");
                await MarkCurrentDegradedBestEffortAsync(ex, stoppingToken);
                _live = null;
                await DelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    /// <summary>
    /// True on a weekday in [09:15, 15:35) IST. Ended-session recovery runs only when this is false (pre-market, at/after the hard close,
    /// weekends), so old unfinished sessions never compete with today's live start/recovery.
    /// </summary>
    internal static bool IsLiveMarketWindow(bool weekday, TimeOnly time) => weekday && time >= MarketOpen && time < MarketHardClose;

    /// <summary>Old ended-session repair runs only on weekends or on a weekday at/after the 15:35 IST hard close; never on a weekday morning or during market hours.</summary>
    internal static bool ShouldRunEndedSessionRecovery(bool weekday, TimeOnly time) => !weekday || time >= MarketHardClose;

    async Task FinalizeEndedSessionsAsync(DateOnly todayIst, bool includeToday, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await endedSessionRecovery.FinalizeEndedAsync(
            scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>(),
            scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>(),todayIst,includeToday,ct);
    }

    /// <summary>Validation tooling only: treats historical bootstrap as already done for <paramref name="day"/>.</summary>
    internal void MarkHistoricalBootstrapDone(DateOnly day) => _historyBootstrappedFor = day;

    internal async Task<LiveState?> TryStartOrRecoverAsync(DateOnly day, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

        if (_historyBootstrappedFor != day)
        {
            await historicalBootstrap.EnsurePriorSessionsAsync(source, observer, day, ct);
            _historyBootstrappedFor = day;
        }

        var context = await coordinator.TryGetOrCreateAsync(source, observer, day, nowUtc, ct);
        if (context is null)
        {
            return null;
        }

        // Hold back a small causal-ordering window; the existing raw persistence can itself lag
        // by up to ~1s, and exchange timestamps are second-granularity.
        var through = nowUtc - StableOrderingLag;
        if (through < context.Session.OpeningWindowStartUtc)
        {
            through = context.Session.OpeningWindowStartUtc;
        }

        var recovered = await recovery.RecoverAsync(source, observer, context, through, ct);
        // Catch the commentary checkpoint up to every persisted completed bar (restart or mid-session deployment); idempotent by event identity.
        // Always a non-notifying backfill, and never past a bar whose sidecar integrity could not be established.
        await notificationGate.SyncAsync(observer, ct);
        await commentary.ProcessAllCompletedAsync(observer, context.Session,
            recovered.UnverifiedSidecarBars.Count > 0 ? recovered.UnverifiedSidecarBars.Min() : null, ct);
        return new LiveState(context, recovered);
    }

    /// <summary>Runtime-row cadence decision (see <see cref="RuntimePersistInterval"/>): forced, a completed bar, the first poll, or the interval elapsed.</summary>
    internal static bool ShouldFlushRuntime(bool force, int completedBars, DateTimeOffset? lastFlushUtc, DateTimeOffset nowUtc) =>
        force || completedBars > 0 || lastFlushUtc is not { } last || nowUtc - last >= RuntimePersistInterval;

    internal async Task PollLiveAsync(LiveState live, DateTimeOffset nowUtc, CancellationToken ct, bool forceRuntimeFlush = false)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

        using var pollActivity = AdaptiveLiveTelemetry.Source.StartActivity("poll");
        IReadOnlyList<(string Token, ObserverRawTick Tick)> raw;
        using (AdaptiveLiveTelemetry.Source.StartActivity("poll.read"))
            raw = await tickReader.ReadRawAfterIdAsync(source, live.Tokens, live.LastFetchedRawId, ct, day: live.Context.Session.TradeDate);
        var appended = 0;
        using (AdaptiveLiveTelemetry.Source.StartActivity("poll.normalize"))
        foreach (var item in raw)
        {
            live.LastFetchedRawId = Math.Max(live.LastFetchedRawId, item.Tick.Id);
            var clean = live.Normalizer.Process(item.Token, item.Tick);
            if (clean is { } tick && tick.AvailableAt >= (live.Context.Session.ObservationStartUtc ?? live.Context.Session.OpeningWindowStartUtc))
            {
                live.Pending.Add(new ObserverTokenTick(item.Token, tick));
                appended++;
            }
        }

        // Once sorted, removing a prefix preserves order, so an unchanged buffer is never re-sorted.
        if (appended > 0) live.PendingNeedsSort = true;
        if (live.PendingNeedsSort)
        {
            if (live.Pending.Count > 1)
            {
                live.Pending.Sort(static (a, b) =>
                {
                    var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
                    return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
                });
            }

            live.PendingNeedsSort = false;
        }

        var stableCutoff = nowUtc - StableOrderingLag;
        var processed = 0;
        var completedBars = 0;
        while (processed < live.Pending.Count && live.Pending[processed].Tick.AvailableAt <= stableCutoff)
        {
            var groupTime=live.Pending[processed].Tick.AvailableAt;
            var groupEnd=processed+1;
            while(groupEnd<live.Pending.Count && live.Pending[groupEnd].Tick.AvailableAt==groupTime)groupEnd++;
            var group=live.Pending.GetRange(processed,groupEnd-processed).Select(x=>(x.Token,x.Tick)).ToArray();
            IReadOnlyList<AdaptiveCompletedBarPackage> packages;
            using (AdaptiveLiveTelemetry.Source.StartActivity("poll.engine"))
                packages = live.Engine.ProcessAvailabilityGroup(group);
            live.LastProcessedAvailableAt = groupTime;
            live.LastProcessedTickId = group.Max(x=>x.Tick.Id);

            foreach (var package in packages)
            {
                completedBars++;
                using var barActivity = AdaptiveLiveTelemetry.Source.StartActivity("bar");
                barActivity?.SetTag("barSeq", package.FutureBar.BarSeq);
                AdaptivePersistResult saved;
                using (AdaptiveLiveTelemetry.Source.StartActivity("bar.core"))
                    saved = await persistence.PersistOrVerifyAsync(observer, live.Context.Session, package, nowUtc, ct);
                // Supplemental sidecar rows are inserted/verified before the Dashboard is told, so a push never races ahead of them.
                // The call never throws into the core observer (sidecar problems are logged and counted). A sidecar whose integrity could not be
                // established (write/verify failed) is never read by commentary: commentary stops before it and the sidecar is retried each bar.
                using (AdaptiveLiveTelemetry.Source.StartActivity("bar.sidecars"))
                {
                    if (!await supplemental.PersistAndVerifyAsync(observer, live.Context.Session, package, ct))
                        live.UnverifiedSidecars[package.FutureBar.BarSeq] = package;
                    foreach (var earlier in live.UnverifiedSidecars.Where(x => x.Key != package.FutureBar.BarSeq).ToArray())
                        if (await supplemental.PersistAndVerifyAsync(observer, live.Context.Session, earlier.Value, ct))
                            live.UnverifiedSidecars.Remove(earlier.Key);
                }
                // Commentary reads the PERSISTED core + sidecar rows of this bar (identical inputs to a later replay) and never throws into the core observer.
                using (AdaptiveLiveTelemetry.Source.StartActivity("bar.commentary"))
                {
                    var mode = await notificationGate.SyncAsync(observer, ct);
                    await commentary.ProcessThroughAsync(observer, live.Context.Session, package.FutureBar.BarSeq, mode, ct, live.CommentaryCapBeforeBar);
                }
                if (saved.Inserted)
                {
                    // Signal only after the DB transaction committed. Failure to notify must not
                    // change authoritative observer state; Dashboard can recover by reading DB.
                    using (AdaptiveLiveTelemetry.Source.StartActivity("bar.push"))
                    try
                    {
                        await dashboardPush.PushAdaptiveStateChangedAsync(
                            live.Context.Session.Id, package.FutureBar.BarSeq, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Adaptive state committed at BarSeq {BarSeq} but Dashboard notification failed.", package.FutureBar.BarSeq);
                    }
                }
                using (AdaptiveLiveTelemetry.Source.StartActivity("bar.observations"))
                    await observations.ProcessPackageAsync(source, observer, live.Context, package, ct);
            }

            processed=groupEnd;
        }

        if (processed > 0)
        {
            live.Pending.RemoveRange(0, processed);
        }

        // Operational runtime row: throttled (see RuntimePersistInterval). Completed bars and explicit flushes write immediately.
        if (ShouldFlushRuntime(forceRuntimeFlush, completedBars, live.LastRuntimeFlushUtc, nowUtc))
        {
            using (AdaptiveLiveTelemetry.Source.StartActivity("poll.runtime"))
                await persistence.UpdatePartialRuntimeAsync(
                    observer,
                    live.Context.Session.Id,
                    live.Engine.PartialBar,
                    nowUtc,
                    live.LastProcessedAvailableAt,
                    live.LastProcessedTickId,
                    ct);
            live.LastRuntimeFlushUtc = nowUtc;
        }
    }

    internal async Task CloseCurrentSessionAsync(LiveState live, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

            // Drain the persisted source and ordering buffer before resolving H5 at close.
            await PollLiveAsync(live, DateTimeOffset.UtcNow + StableOrderingLag, ct, forceRuntimeFlush: true);
            await observations.FinalizeSessionAsync(source, observer, live.Context, ct);
            await persistence.SetRuntimeStatusAsync(
                observer,
                live.Context.Session.Id,
                AdaptiveRuntimeStatus.Closed,
                DateTimeOffset.UtcNow,
                error: null,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adaptive observer session-close finalization failed.");
        }
    }

    async Task MarkCurrentDegradedBestEffortAsync(Exception ex, CancellationToken ct)
    {
        if (_live is null)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();
            await persistence.SetRuntimeStatusAsync(
                db,
                _live.Context.Session.Id,
                AdaptiveRuntimeStatus.Degraded,
                DateTimeOffset.UtcNow,
                ex.Message,
                ct);
        }
        catch (Exception markEx)
        {
            logger.LogError(markEx, "Could not persist adaptive observer degraded status.");
        }
    }

    static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
