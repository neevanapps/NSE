using NiftySignal.AdaptiveObserver;
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
    DashboardPushClient dashboardPush,
    ILogger<AdaptiveObserverWorker> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    static readonly TimeOnly MarketOpen = new(9, 15);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan OutsideMarketPoll = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StableOrderingLag = TimeSpan.FromSeconds(2);

    LiveState? _live;
    DateOnly? _historyBootstrappedFor;

    sealed class LiveState(
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

                if (!weekday || time < MarketOpen || time >= MarketHardClose)
                {
                    if (_live is not null && time >= MarketHardClose)
                    {
                        await CloseCurrentSessionAsync(_live, stoppingToken);
                    }

                    _live = null;
                    await FinalizeEndedSessionsAsync(day, time >= MarketHardClose, stoppingToken);
                    await DelayAsync(OutsideMarketPoll, stoppingToken);
                    continue;
                }

                if (_live is null || _live.Context.Session.TradeDate != day)
                {
                    await FinalizeEndedSessionsAsync(day, includeToday: false, stoppingToken);
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

    async Task FinalizeEndedSessionsAsync(DateOnly todayIst, bool includeToday, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await endedSessionRecovery.FinalizeEndedAsync(
            scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>(),
            scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>(),todayIst,includeToday,ct);
    }

    async Task<LiveState?> TryStartOrRecoverAsync(DateOnly day, DateTimeOffset nowUtc, CancellationToken ct)
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
        return new LiveState(context, recovered);
    }

    async Task PollLiveAsync(LiveState live, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

        var raw = await tickReader.ReadRawAfterIdAsync(source, live.Tokens, live.LastFetchedRawId, ct);
        foreach (var item in raw)
        {
            live.LastFetchedRawId = Math.Max(live.LastFetchedRawId, item.Tick.Id);
            var clean = live.Normalizer.Process(item.Token, item.Tick);
            if (clean is { } tick)
            {
                live.Pending.Add(new ObserverTokenTick(item.Token, tick));
            }
        }

        if (live.Pending.Count > 1)
        {
            live.Pending.Sort(static (a, b) =>
            {
                var byTime = a.Tick.AvailableAt.CompareTo(b.Tick.AvailableAt);
                return byTime != 0 ? byTime : a.Tick.Id.CompareTo(b.Tick.Id);
            });
        }

        var stableCutoff = nowUtc - StableOrderingLag;
        var processed = 0;
        while (processed < live.Pending.Count && live.Pending[processed].Tick.AvailableAt <= stableCutoff)
        {
            var item = live.Pending[processed];
            var packages = live.Engine.Process(item.Token, item.Tick);
            live.LastProcessedAvailableAt = item.Tick.AvailableAt;
            live.LastProcessedTickId = item.Tick.Id;

            foreach (var package in packages)
            {
                var saved = await persistence.PersistOrVerifyAsync(
                    observer, live.Context.Session, package, nowUtc, ct);
                await observations.ProcessPackageAsync(source, observer, live.Context, package, ct);

                if (saved.Inserted)
                {
                    // Signal only after the DB transaction committed. Failure to notify must not
                    // change authoritative observer state; Dashboard can recover by reading DB.
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
            }

            processed++;
        }

        if (processed > 0)
        {
            live.Pending.RemoveRange(0, processed);
        }

        await persistence.UpdatePartialRuntimeAsync(
            observer,
            live.Context.Session.Id,
            live.Engine.PartialBar,
            nowUtc,
            live.LastProcessedAvailableAt,
            live.LastProcessedTickId,
            ct);
    }

    async Task CloseCurrentSessionAsync(LiveState live, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            var observer = scope.ServiceProvider.GetRequiredService<AdaptiveObserverDbContext>();

            // Drain the persisted source and ordering buffer before resolving H5 at close.
            await PollLiveAsync(live, DateTimeOffset.UtcNow + StableOrderingLag, ct);
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
