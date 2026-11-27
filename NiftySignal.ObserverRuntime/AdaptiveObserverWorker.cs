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
    DashboardPushClient dashboardPush,
    ILogger<AdaptiveObserverWorker> logger,
    IEnumerable<IAdaptiveLiveSink>? sinks = null,
    FuturesDepth5Gateway? depth5 = null,
    Microsoft.Extensions.Options.IOptions<AdaptiveRecoveryOptions>? recoveryOptions = null,
    TimeProvider? clock = null,
    AdaptiveCheckpointStore? checkpoints = null,
    IObserverOwnership? ownership = null) : BackgroundService
{
    readonly IAdaptiveLiveSink[] _sinks = sinks?.ToArray() ?? [];
    readonly AdaptiveRecoveryOptions _recoveryOptions = recoveryOptions?.Value ?? new AdaptiveRecoveryOptions();
    /// <summary>Wall clock for heartbeats: the instant a value is written, never the instant the poll started.</summary>
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    DateTimeOffset? _recoveryNotBeforeUtc;
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
    DateOnly? _fixedHistoryFor;

    public sealed class LiveState(
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
        /// <summary>Largest number of rows awaiting processing at once in any live poll (bounded-memory diagnostic).</summary>
        public int PeakPendingRows { get; set; }
        public DateTimeOffset? LastRuntimeFlushUtc { get; set; }
        /// <summary>Futures Top-5 depth feed (null when the flag is off). Shares the recovery feed's cursor and pending events.</summary>
        public FuturesDepth5Feed? Depth5Feed { get; } = recovered.Depth5Feed;
        /// <summary>False once a depth read failed in this live session: later bars then carry NO depth sidecar (never a partial one) until the next recovery replays and backfills them.</summary>
        public bool Depth5Healthy { get; set; } = true;
        /// <summary>Options Top-5 depth feed (null when the S5 flag is off) and the frozen option universe's tokens it covers.</summary>
        public FuturesDepth5Feed? OptionsDepth5Feed { get; } = recovered.OptionsDepth5Feed;
        public IReadOnlyList<string> OptionDepthTokens { get; } = recovered.OptionDepthTokens ?? [];
        /// <summary>False once an options depth read failed in this live session: later bars carry NO options depth rows until the next recovery replays and backfills them (the futures sidecar and the core are unaffected).</summary>
        public bool OptionsDepth5Healthy { get; set; } = true;
        /// <summary>Fixed-clock sessions store their 09:30 residual anchor once, when the engine freezes it (see AdaptiveObserverPersistence.PersistResidualAnchorOnceAsync).</summary>
        public bool AnchorStored { get; set; } = !AdaptiveFixedClockV1.IsFixedClock(context.Session.ModelVersion) || context.ResidualAnchor is not null || recovered.Engine.SelfAnchorAttempted;
        /// <summary>Completed bars whose sidecar integrity is unknown (a write/verify failed). The paper path never receives a verified bar at or past the first of them (see <see cref="PaperVerifiedCapBeforeBar"/>).</summary>
        public SortedDictionary<int, AdaptiveCompletedBarPackage> UnverifiedSidecars { get; } = new();
        /// <summary>Bars found unverified during recovery (no package retained to retry); the paper path stays capped before them until the next recovery.</summary>
        public SortedSet<int> RecoveryUnverifiedBars { get; } = new(recovered.UnverifiedSidecarBars);
        /// <summary>Completed bars whose publication to the execution service has not been committed yet (bar -> verified verdict at completion).</summary>
        public SortedDictionary<int, bool> UnpublishedBars { get; } = new();

        /// <summary>The publication of the bars persisted by this start or recovery has not succeeded yet.</summary>
        public bool RecoveredBarsUnpublished { get; set; }
        /// <summary>Bars that triggered a Weak2 observation so far (part of the checkpoint, so a restore can still verify the persisted trigger set).</summary>
        public SortedSet<int> TriggerBars { get; } = new(recovered.TriggerBars ?? []);
        /// <summary>Identity under which this session's state may be checkpointed; null when checkpoints are off.</summary>
        public string? CheckpointIdentity { get; } = recovered.CheckpointIdentity;

        /// <summary>
        /// The fail-closed gate of the paper path, UNCHANGED by the removal of commentary (it was always derived only from the two sidecar-integrity sets above, never from commentary state): the first bar whose
        /// sidecar integrity is unknown, live or found in recovery. A bar at or after it is handed to the paper engine as unverified.
        /// </summary>
        public int? PaperVerifiedCapBeforeBar
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
                // One writer: without the ownership nothing is processed, and state built while it was held is discarded.
                if (ownership is not null && !await ownership.EnsureAsync(stoppingToken))
                {
                    _live = null;
                    await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

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
                    // Pre-market on a trading weekday: rebuild the prior days on the fixed 13,650 clock now, so the live start at 09:15 never waits for it.
                    if (weekday && time < MarketOpen && _fixedHistoryFor != day)
                    {
                        await EnsureFixedHistoryAsync(day, stoppingToken);
                    }

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
                    if (_recoveryNotBeforeUtc is { } notBefore && nowUtc < notBefore)
                    {
                        await DelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }

                    // Live priority: no ended-session reconstruction in this branch (it can be very slow and would delay today's start).
                    // Unfinished older sessions are retried only outside the live window (see IsLiveMarketWindow).
                    _live = await TryStartOrRecoverAsync(day, nowUtc, stoppingToken);
                    if (_live is null)
                    {
                        await DelayAsync(TimeSpan.FromSeconds(1), stoppingToken);
                        continue;
                    }

                    // In-process consumers (the Host's optional paper bridges) are told that a session starts or was recovered; they never throw into this loop.
                    foreach (var sink in _sinks) sink.OnSessionStarted(_live);
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

    async Task EnsureFixedHistoryAsync(DateOnly day, CancellationToken ct)
    {
        _fixedHistoryFor = day;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var seeded = await historicalBootstrap.EnsurePriorFixedSessionsAsync(
                scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>(), scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>(), day, ct);
            if (seeded > 0)
            {
                logger.LogInformation("Adaptive fixed-clock history ready for {TradeDay}: {Seeded} prior day(s) rebuilt.", day, seeded);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never blocks the observer: the Strong threshold simply stays uncalibrated when too few prior fixed-clock days exist.
            logger.LogError(ex, "Adaptive fixed-clock history bootstrap failed; Strong/Weak calibration may be unavailable today.");
        }
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

        if (_fixedHistoryFor != day)
        {
            await EnsureFixedHistoryAsync(day, ct);
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

        AdaptiveRecoveryResult recovered;
        try
        {
            recovered = await recovery.RecoverAsync(source, observer, context, through, ct, TimeSpan.FromSeconds(_recoveryOptions.TimeLimitSeconds));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ex is AdaptiveRecoveryTimeoutException)
            {
                _recoveryNotBeforeUtc = _clock.GetUtcNow() + TimeSpan.FromSeconds(_recoveryOptions.TimeoutRetryBackoffSeconds);
            }

            // There is no live state yet, so the failure is recorded here; otherwise the row would stay "Rebuilding" with no error.
            await MarkStatusBestEffortAsync(context.Session.Id, AdaptiveRuntimeStatus.RecoveryFailed, ex.Message, ct);
            throw;
        }

        _recoveryNotBeforeUtc = null;
        var live = new LiveState(context, recovered);
        // Part of every start and recovery (not of the service loop), so no caller can go live with the recovered bars unpublished.
        await PublishRecoveredBarsAsync(live, ct);
        return live;
    }

    /// <summary>The paper hand-off condition: the bar's own sidecars are verified AND it lies before the first bar of unknown integrity (fail closed until the next recovery).</summary>
    internal static bool IsBarVerifiedForPaper(int barSeq, bool ownSidecarUnverified, int? cap) => !ownSidecarUnverified && !(cap is { } c && barSeq >= c);

    /// <summary>Runtime-row cadence decision (see <see cref="RuntimePersistInterval"/>): forced, a completed bar, the first poll, or the interval elapsed.</summary>
    internal static bool ShouldFlushRuntime(bool force, int completedBars, DateTimeOffset? lastFlushUtc, DateTimeOffset nowUtc) =>
        force || completedBars > 0 || lastFlushUtc is not { } last || nowUtc - last >= RuntimePersistInterval;

    internal async Task PollLiveAsync(LiveState live, DateTimeOffset nowUtc, CancellationToken ct, bool forceRuntimeFlush = false)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

        using var pollActivity = AdaptiveLiveTelemetry.Source.StartActivity("poll");
        var stableCutoff = nowUtc - StableOrderingLag;
        var window = TimeSpan.FromSeconds(_recoveryOptions.ReadWindowSeconds);
        var batch = _recoveryOptions.LiveReadBatchSize;
        var completedBars = 0;
        var catchUpMark = Stopwatch.GetTimestamp();
        var newestReceived = DateTimeOffset.MinValue;

        // Bounded memory: one batch is read, then everything whose order is already final is processed and released before the next batch is read. While a backlog remains, only groups available more than one read
        // window before the newest row seen are final (a later Id is never received more than that earlier: the same assumption recovery enforces), so at most one window of rows plus one batch is held.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = live.LastFetchedRawId;
            IReadOnlyList<(string Token, ObserverRawTick Tick)> raw = [];
            using (AdaptiveLiveTelemetry.Source.StartActivity("poll.read"))
            {
                long? after = cursor > 0 ? cursor : await AdaptiveSourceTickReader.FindStartCursorAsync(source, live.Tokens, live.Context.Session.TradeDate, window, ct);
                if (after is { } from)
                    raw = await tickReader.ReadRawAfterIdAsync(source, live.Tokens, from, ct, day: live.Context.Session.TradeDate, batchSize: batch);
            }

            using (AdaptiveLiveTelemetry.Source.StartActivity("poll.normalize"))
            foreach (var item in raw)
            {
                live.LastFetchedRawId = Math.Max(live.LastFetchedRawId, item.Tick.Id);
                if (item.Tick.ReceivedAt > newestReceived) newestReceived = item.Tick.ReceivedAt;
                var clean = live.Normalizer.Process(item.Token, item.Tick);
                if (clean is { } tick && tick.AvailableAt >= (live.Context.Session.ObservationStartUtc ?? live.Context.Session.OpeningWindowStartUtc))
                {
                    live.Pending.Add(new ObserverTokenTick(item.Token, tick));
                    live.PendingNeedsSort = true;
                }
            }

            var backlog = raw.Count >= batch && live.LastFetchedRawId > cursor;
            var through = stableCutoff;
            if (backlog && newestReceived.AddTicks(-1) - window < through) through = newestReceived.AddTicks(-1) - window;
            live.PeakPendingRows = Math.Max(live.PeakPendingRows, live.Pending.Count);

            await PollDepthAsync(source, live, backlog ? newestReceived : null, ct);

            // Once sorted, removing a prefix preserves order, so an unchanged buffer is never re-sorted.
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

            await ProcessThroughAsync(through);
            await TryCheckpointAsync(observer, live, ct);
            if (!backlog) break;
        }

        // A publication that failed is retried by every poll, not only when another bar completes.
        if (live.RecoveredBarsUnpublished || live.UnpublishedBars.Count > 0) await PublishBarsAsync(observer, live, [], ct);

        // Every group at or before the stable cutoff has been processed: the paper path may advance its clock to it (deadlines, max hold, the 15:10 liquidation).
        foreach (var sink in _sinks) sink.OnPollCompleted(stableCutoff);

        // Operational runtime row: throttled (see RuntimePersistInterval). Completed bars and explicit flushes write immediately.
        if (ShouldFlushRuntime(forceRuntimeFlush, completedBars, live.LastRuntimeFlushUtc, nowUtc))
        {
            using (AdaptiveLiveTelemetry.Source.StartActivity("poll.runtime"))
                await persistence.UpdatePartialRuntimeAsync(
                    observer,
                    live.Context.Session.Id,
                    live.Engine.PartialBar,
                    _clock.GetUtcNow(),
                    live.LastProcessedAvailableAt,
                    live.LastProcessedTickId,
                    ct);
            live.LastRuntimeFlushUtc = nowUtc;
        }

        async Task ProcessThroughAsync(DateTimeOffset through)
        {
            var processed = 0;
            while (processed < live.Pending.Count && live.Pending[processed].Tick.AvailableAt <= through)
            {
                var groupTime=live.Pending[processed].Tick.AvailableAt;
                var groupEnd=processed+1;
                while(groupEnd<live.Pending.Count && live.Pending[groupEnd].Tick.AvailableAt==groupTime)groupEnd++;
                var group=live.Pending.GetRange(processed,groupEnd-processed).Select(x=>(x.Token,x.Tick)).ToArray();
                IReadOnlyList<AdaptiveCompletedBarPackage> packages;
                // Every depth event up to and including this boundary is delivered before the group (identical to recovery); with the flag off the feed is null and nothing happens.
                if (live.Depth5Feed is { } feed && live.Depth5Healthy)
                    foreach (var depthEvent in feed.DrainThrough(groupTime)) live.Engine.ObserveDepth(depthEvent);
                if (live.OptionsDepth5Feed is { } optFeed && live.OptionsDepth5Healthy)
                    foreach (var depthEvent in optFeed.DrainThrough(groupTime)) live.Engine.ObserveDepth(depthEvent);
                using (AdaptiveLiveTelemetry.Source.StartActivity("poll.engine"))
                    packages = live.Engine.ProcessAvailabilityGroup(group);
                if (live.OptionsDepth5Feed is not null && !live.OptionsDepth5Healthy)
                    packages = packages.Select(p => p.OptionsDepth5 is null ? p : p with { OptionsDepth5 = null }).ToList();
                if (live.Depth5Feed is not null && !live.Depth5Healthy)
                    packages = packages.Select(p => p.Depth5 is null ? p : p with { Depth5 = null }).ToList();
                live.LastProcessedAvailableAt = groupTime;
                live.LastProcessedTickId = group.Max(x=>x.Tick.Id);
                if (!live.AnchorStored && live.Engine.SelfAnchorAttempted)
                {
                    await persistence.PersistResidualAnchorOnceAsync(observer, live.Context.Session.Id, live.Engine.ResidualAnchor, nowUtc, ct);
                    live.AnchorStored = true;
                }

                var groupBars = new List<(AdaptiveCompletedBarPackage Package, bool Verified)>();
                foreach (var package in packages)
                {
                    completedBars++;
                    if (package.IsActionableWeak2) live.TriggerBars.Add(package.FutureBar.BarSeq);
                    using var barActivity = AdaptiveLiveTelemetry.Source.StartActivity("bar");
                    barActivity?.SetTag("barSeq", package.FutureBar.BarSeq);
                    AdaptivePersistResult saved;
                    using (AdaptiveLiveTelemetry.Source.StartActivity("bar.core"))
                        saved = await persistence.PersistOrVerifyAsync(observer, live.Context.Session, package, _clock.GetUtcNow(), ct);
                    // Supplemental sidecar rows are inserted/verified before the Dashboard is told, so a push never races ahead of them.
                    // The call never throws into the core observer (sidecar problems are logged and counted). A sidecar whose integrity could not be
                    // established (write/verify failed) is never handed to the paper path as verified (see PaperVerifiedCapBeforeBar) and the sidecar is retried each bar.
                    using (AdaptiveLiveTelemetry.Source.StartActivity("bar.sidecars"))
                    {
                        if (!await supplemental.PersistAndVerifyAsync(observer, live.Context.Session, package, ct))
                            live.UnverifiedSidecars[package.FutureBar.BarSeq] = package;
                        foreach (var earlier in live.UnverifiedSidecars.Where(x => x.Key != package.FutureBar.BarSeq).ToArray())
                            if (await supplemental.PersistAndVerifyAsync(observer, live.Context.Session, earlier.Value, ct))
                                live.UnverifiedSidecars.Remove(earlier.Key);
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
                    // A bar is handed to the paper path only when its persisted projection and sidecars are verified.
                    groupBars.Add((package, IsBarVerifiedForPaper(package.FutureBar.BarSeq, live.UnverifiedSidecars.ContainsKey(package.FutureBar.BarSeq), live.PaperVerifiedCapBeforeBar)));
                }

                // Durable handoff to the execution service (its own process): one insert into the observer database per bar, never waiting for the service. A failure is contained and retried on the next bar.
                if (groupBars.Count > 0)
                {
                    using (AdaptiveLiveTelemetry.Source.StartActivity("bar.publish"))
                        await PublishBarsAsync(observer, live, groupBars, ct);
                }

                foreach (var sink in _sinks) sink.OnGroup(groupTime, group, groupBars, live);

                processed=groupEnd;

                // A long catch-up inside one poll still reports its own progress (from this loop, never from a timer).
                if (processed < live.Pending.Count && Stopwatch.GetElapsedTime(catchUpMark) >= RuntimePersistInterval)
                {
                    await persistence.UpdatePartialRuntimeAsync(observer, live.Context.Session.Id, live.Engine.PartialBar, _clock.GetUtcNow(), live.LastProcessedAvailableAt, live.LastProcessedTickId, ct);
                    catchUpMark = Stopwatch.GetTimestamp();
                }
            }

            if (processed > 0)
            {
                live.Pending.RemoveRange(0, processed);
            }
        }
    }

    /// <summary>
    /// A checkpoint of the live state between two batches, when due. Skipped while anything about a processed bar is unsettled (sidecar unverified or awaiting retry, publication outstanding, depth withheld),
    /// because a restore does not replay those bars.
    /// </summary>
    async Task TryCheckpointAsync(AdaptiveObserverDbContext observer, LiveState live, CancellationToken ct)
    {
        if (checkpoints is null || live.CheckpointIdentity is not { } identity || !checkpoints.IsDue()) return;
        if (live.PaperVerifiedCapBeforeBar is not null || live.RecoveredBarsUnpublished || live.UnpublishedBars.Count > 0 || !live.Depth5Healthy || !live.OptionsDepth5Healthy || !live.AnchorStored && live.Engine.SelfAnchorAttempted) return;
        if (supplemental.PendingStrikeRetryCount + supplemental.PendingCloseAtmRetryCount + supplemental.PendingDepth5RetryCount + supplemental.PendingOptionsDepth5RetryCount > 0) return;
        static AdaptiveSourcePosition At(long cursor) => new(cursor, new Dictionary<string, long>(StringComparer.Ordinal));
        await checkpoints.TrySaveAsync(observer, identity, new AdaptiveCheckpointState
        {
            SessionId = live.Context.Session.Id, Engine = live.Engine, Normalizer = live.Normalizer, Position = At(live.LastFetchedRawId), Unprocessed = live.Pending.ToList(),
            LastProcessedAvailableAt = live.LastProcessedAvailableAt, LastProcessedTickId = live.LastProcessedTickId, TriggerBars = live.TriggerBars.ToList(),
            Depth5Feed = live.Depth5Feed, Depth5Position = live.Depth5Feed is { } depth ? At(depth.LastFetchedId) : null,
            OptionsDepth5Feed = live.OptionsDepth5Feed, OptionsDepth5Position = live.OptionsDepth5Feed is { } options ? At(options.LastFetchedId) : null,
        }, ct);
    }

    async Task PollDepthAsync(NiftySignalDbContext source, LiveState live, DateTimeOffset? coverReceivedAt, CancellationToken ct)
    {
        if (live.Depth5Feed is { } depthFeed && depth5 is not null && live.Depth5Healthy)
        {
            // A depth failure must never disturb the core observer: depth is simply withheld for the rest of this live session (see LiveState.Depth5Healthy).
            try
            {
                using (AdaptiveLiveTelemetry.Source.StartActivity("poll.depth5"))
                    await depth5.PollAsync(source, depthFeed, live.Context.Definition.FutureToken, live.Context.Session.TradeDate,
                        live.Context.Session.ObservationStartUtc ?? live.Context.Session.OpeningWindowStartUtc, ct, coverReceivedAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                live.Depth5Healthy = false;
                logger.LogError(ex, "Futures depth5 read failed; depth sidecar withheld for the rest of this session until the next recovery (core observer unaffected).");
            }
        }

        if (live.OptionsDepth5Feed is { } optionsFeed && depth5 is not null && live.OptionsDepth5Healthy)
        {
            try
            {
                using (AdaptiveLiveTelemetry.Source.StartActivity("poll.options-depth5"))
                    await depth5.PollOptionsAsync(source, optionsFeed, live.OptionDepthTokens, live.Context.Session.TradeDate,
                        live.Context.Session.ObservationStartUtc ?? live.Context.Session.OpeningWindowStartUtc, ct, coverReceivedAt);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                live.OptionsDepth5Healthy = false;
                logger.LogError(ex, "Options depth5 read failed; the options depth sidecar is withheld for the rest of this session until the next recovery (core observer and futures depth unaffected).");
            }
        }
    }

    async Task PublishBarsAsync(AdaptiveObserverDbContext observer, LiveState live, IReadOnlyList<(AdaptiveCompletedBarPackage Package, bool Verified)> bars, CancellationToken ct)
    {
        try
        {
            foreach (var (package, verified) in bars) live.UnpublishedBars[package.FutureBar.BarSeq] = verified;
            // In bar order only: the execution service never looks back, so a later bar published ahead of an earlier one hides the earlier one for good.
            if (live.RecoveredBarsUnpublished && !await PublishRecoveredBarsAsync(live, ct)) return;
            foreach (var (barSeq, verified) in live.UnpublishedBars.ToArray())
            {
                await AdaptiveBarPublisher.PublishAsync(observer, live.Context.Session.Id, barSeq, verified, _clock.GetUtcNow(), ct);
                live.UnpublishedBars.Remove(barSeq);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Adaptive bar publication failed ({Pending} bar(s) pending); the execution service sees no new bar until it succeeds. The observer is unaffected.", live.UnpublishedBars.Count);
        }
    }

    /// <summary>
    /// Publishes the bars a start or recovery persisted (those before the first bar still awaiting its own live publication). Contained: a failure only delays the execution service, and no later bar is
    /// published until this succeeds.
    /// </summary>
    async Task<bool> PublishRecoveredBarsAsync(LiveState live, CancellationToken ct)
    {
        live.RecoveredBarsUnpublished = true;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();
            int? before = live.UnpublishedBars.Count > 0 ? live.UnpublishedBars.Keys.First() : null;
            await AdaptiveBarPublisher.PublishMissingAsync(observer, live.Context.Session.Id, live.PaperVerifiedCapBeforeBar, _clock.GetUtcNow(), ct, before);
            live.RecoveredBarsUnpublished = false;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing the recovered adaptive bars failed; it is retried, and no later bar is published until it succeeds. The observer is unaffected.");
            return false;
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
            foreach (var sink in _sinks) sink.OnSessionClosing();
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

        await MarkStatusBestEffortAsync(_live.Context.Session.Id, AdaptiveRuntimeStatus.Degraded, ex.Message, ct);
    }

    async Task MarkStatusBestEffortAsync(long sessionId, AdaptiveRuntimeStatus status, string error, CancellationToken ct)
    {
        try
        {
            // A fresh scope: the failed operation's context may hold half-built changes.
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();
            await persistence.SetRuntimeStatusAsync(db, sessionId, status, _clock.GetUtcNow(), error, ct);
        }
        catch (Exception markEx)
        {
            logger.LogError(markEx, "Could not persist adaptive observer {Status} status.", status);
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
