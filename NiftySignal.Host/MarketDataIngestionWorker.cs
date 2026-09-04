using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Notifications;
using NiftySignal.Persistence;

namespace NiftySignal.Host;

/// <summary>
/// Phase 1: resolves the day's instrument universe (plan 3.1), opens the live FlatTrade
/// feed, and persists raw ticks (plan 3.2 -- capped retention, replay/audit value only;
/// see <see cref="MarketDataIngestionWorker"/>'s sibling, the feature/score worker not yet
/// built, for what actually drives decisions). Deliberately does not consult
/// <c>KillSwitchState</c> -- the kill switch disables new trade *entries* only (plan
/// 1.6/11); ingestion keeps running regardless.
/// </summary>
public sealed class MarketDataIngestionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FlatTradeOptions> flatTradeOptions,
    ITelegramNotifier telegram,
    LiveTradingEngine tradingEngine,
    ILogger<MarketDataIngestionWorker> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    const int FlushBatchSize = 200;
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    // Plan section 6: "Cadence: every 15 seconds."
    static readonly TimeSpan ScoreCadence = TimeSpan.FromSeconds(15);

    // SemaphoreSlim, not a plain lock: the cadence loop needs to await DB calls (score
    // persistence, then the trading engine's own DB-backed entry/exit evaluation) while
    // holding this, and `lock` can't wrap an await. Ticks queue in the channel underneath
    // (unbounded) rather than being lost while a cadence tick briefly holds it.
    readonly SemaphoreSlim _engineSync = new(1, 1);
    LiveFeatureEngine? _engine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var session = await LoadSessionAsync(stoppingToken);
        if (session?.Token is null || session.ClientId is null || !session.IsValidAt(DateTimeOffset.UtcNow))
        {
            var loginUrl = flatTradeOptions.Value.AuthorizeUrl;
            logger.LogCritical(
                "No valid FlatTrade session token -- ingestion cannot start. Log in and save a token via the Dashboard: {LoginUrl}",
                loginUrl);
            // Do not silently retry (plan 4.3) -- a stale token means no data, and that
            // needs to be known immediately, not masked by a retry loop.
            await telegram.SendAsync(
                NotificationCategory.TokenExpiry,
                $"NiftySignal: no valid FlatTrade session. Log in and save a token via the Dashboard: {loginUrl}",
                stoppingToken);
            return;
        }

        var asOfDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).Date);
        var instruments = await ResolveInstrumentsAsync(session.Token, asOfDate, stoppingToken);
        var subscriptions = instruments.Select(i => (i.Exchange, i.Token)).ToList();

        _engine = new LiveFeatureEngine(instruments);
        await SeedEngineHistoryAsync(_engine, asOfDate, stoppingToken);

        logger.LogInformation("Starting FlatTrade feed with {Count} subscriptions for {AsOfDate}", subscriptions.Count, asOfDate);
        await telegram.SendAsync(
            NotificationCategory.ConnectionFailure,
            $"NiftySignal ingestion starting: {subscriptions.Count} instruments subscribed for {asOfDate:dd-MMM-yyyy}.",
            stoppingToken);

        await using var tickScope = scopeFactory.CreateAsyncScope();
        var gapRecorder = tickScope.ServiceProvider.GetRequiredService<IDataGapRecorder>();
        var tickSourceLogger = tickScope.ServiceProvider.GetRequiredService<ILogger<FlatTradeTickSource>>();
        var tickSource = new FlatTradeTickSource(
            flatTradeOptions.Value, session.ClientId, session.Token, subscriptions, gapRecorder, tickSourceLogger);

        var cadenceLoop = RunScoreCadenceLoopAsync(stoppingToken);
        var pendingSubscriptionLoop = RunPendingSubscriptionLoopAsync(tickSource, asOfDate, stoppingToken);
        await RunTickLoopAsync(tickSource, stoppingToken);
        await cadenceLoop;
        await pendingSubscriptionLoop;
    }

    async Task RunTickLoopAsync(FlatTradeTickSource tickSource, CancellationToken stoppingToken)
    {
        var buffer = new List<Tick>(FlushBatchSize);
        var lastFlush = DateTimeOffset.UtcNow;

        await foreach (var tick in tickSource.ReadTicksAsync(stoppingToken))
        {
            await _engineSync.WaitAsync(stoppingToken);
            try
            {
                _engine!.OnTick(tick);
            }
            finally
            {
                _engineSync.Release();
            }

            buffer.Add(tick);
            if (buffer.Count >= FlushBatchSize || DateTimeOffset.UtcNow - lastFlush >= FlushInterval)
            {
                await FlushAsync(buffer, stoppingToken);
                buffer.Clear();
                lastFlush = DateTimeOffset.UtcNow;
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(buffer, stoppingToken);
        }
    }

    async Task RunScoreCadenceLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScoreCadence);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _engineSync.WaitAsync(stoppingToken);
                try
                {
                    var snapshot = _engine!.ComputeCadence(DateTimeOffset.UtcNow);
                    if (snapshot is not null)
                    {
                        await PersistSnapshotAsync(snapshot, stoppingToken);
                        await tradingEngine.EvaluateCadenceAsync(snapshot, _engine, stoppingToken);
                    }
                }
                finally
                {
                    _engineSync.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Picks up on-demand watch requests (Dashboard: a strike outside the originally-
    /// resolved ATM band) and adds them to the live WebSocket feed. Polls rather than
    /// pushes -- there's no Dashboard-to-Host channel other than the database, matching
    /// how FlatTradeSession/KillSwitchState are already shared between the two processes.
    /// </summary>
    async Task RunPendingSubscriptionLoopAsync(FlatTradeTickSource tickSource, DateOnly asOfDate, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
                var pending = await db.Instruments.Where(i => i.AsOfDate == asOfDate && !i.Subscribed).ToListAsync(stoppingToken);

                foreach (var instrument in pending)
                {
                    tickSource.RequestSubscribe(instrument.Exchange, instrument.Token);
                    instrument.Subscribed = true;
                    logger.LogInformation("Requested on-demand subscribe: {Symbol} ({Token})", instrument.TradingSymbol, instrument.Token);
                }

                if (pending.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task PersistSnapshotAsync(ScoreSnapshot snapshot, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.ScoreSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Score cadence: composite={Score} warmedUp={WarmedUp}",
            snapshot.CompositeScore, snapshot.IsWarmedUp);
    }

    async Task<FlatTradeSession?> LoadSessionAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        return await db.FlatTradeSessions.FindAsync([FlatTradeSession.SingletonId], ct);
    }

    /// <summary>Reuses today's instruments if the 08:45-equivalent job already ran (e.g. a service restart) instead of re-hitting FlatTrade's API.</summary>
    async Task<IReadOnlyList<Instrument>> ResolveInstrumentsAsync(string sessionToken, DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        var existing = await db.Instruments.Where(i => i.AsOfDate == asOfDate).ToListAsync(ct);
        if (existing.Count > 0)
        {
            logger.LogInformation("Reusing {Count} previously-resolved instruments for {AsOfDate}", existing.Count, asOfDate);
            return existing;
        }

        var resolver = scope.ServiceProvider.GetRequiredService<InstrumentUniverseResolver>();
        var resolved = await resolver.ResolveAsync(sessionToken, asOfDate, ct);
        db.Instruments.AddRange(resolved);
        await db.SaveChangesAsync(ct);
        return resolved;
    }

    /// <summary>Restart-safe warm-up (see LiveFeatureEngine.SeedHistory) -- replays today's already-persisted cadence snapshots instead of starting every rolling window from zero.</summary>
    async Task SeedEngineHistoryAsync(LiveFeatureEngine engine, DateOnly asOfDate, CancellationToken ct)
    {
        // Npgsql only accepts UTC (Offset=0) DateTimeOffset values for timestamptz
        // parameters -- the same rule that bit ReceivedAt/UpdatedAt earlier this session.
        var todayIstMidnightUtc = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var history = await db.ScoreSnapshots
            .Where(s => s.ComputedAt >= todayIstMidnightUtc)
            .OrderBy(s => s.ComputedAt)
            .ToListAsync(ct);

        engine.SeedHistory(history);
        logger.LogInformation("Replayed {Count} historical score snapshots into the rolling windows", history.Count);
    }

    async Task FlushAsync(List<Tick> buffer, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Ticks.AddRange(buffer);
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Flushed {Count} ticks", buffer.Count);
    }
}
