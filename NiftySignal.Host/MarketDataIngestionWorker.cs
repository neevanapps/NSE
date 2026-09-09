using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Domain;
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
    DashboardPushClient dashboardPush,
    ILogger<MarketDataIngestionWorker> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    const int FlushBatchSize = 200;
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    // Live feed/cadence activity is only meaningful inside this window (2026-09-07, live-caught:
    // with no gating at all, the cadence loop kept computing on frozen post-close prices until
    // scores went to NULL around 16:10). MarketPreOpen matches the "08:45-equivalent job"
    // ResolveInstrumentsAsync's own comment already assumes; MarketHardClose is SquareOffTime
    // (15:15) plus a buffer past NSE's 15:30 close, not the square-off time itself -- square-off
    // still needs the cadence loop running to actually fire the exit.
    static readonly TimeOnly MarketPreOpen = new(8, 45);
    static readonly TimeOnly MarketHardClose = new(15, 35);
    static readonly TimeSpan MarketHoursPollInterval = TimeSpan.FromMinutes(1);

    // Plan section 6: "Cadence: every 15 seconds."
    static readonly TimeSpan ScoreCadence = TimeSpan.FromSeconds(15);

    // Within-cadence smoothing (2026-09-04, see LiveFeatureEngine's class doc comment): 5
    // samples per 15s cadence window, folded into a running average instead of trusting a
    // single instantaneous read right at the cadence boundary.
    static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    // Not in plan section 12's original send-on list (2026-09-04, user-requested) -- not
    // clock-aligned to :00/:30, same as every other periodic loop in this worker.
    static readonly TimeSpan PaperTradeSummaryInterval = TimeSpan.FromMinutes(30);

    // SemaphoreSlim, not a plain lock: the cadence loop needs to await DB calls (score
    // persistence, then the trading engine's own DB-backed entry/exit evaluation) while
    // holding this, and `lock` can't wrap an await. Ticks queue in the channel underneath
    // (unbounded) rather than being lost while a cadence tick briefly holds it.
    readonly SemaphoreSlim _engineSync = new(1, 1);
    LiveFeatureEngine? _engine;

    /// <summary>
    /// Edge-triggered guard for the F10 warm-up-blocker log below -- true while the current
    /// not-warmed-up stretch has already logged once. 2026-09-09: the original F10 fix logged
    /// unconditionally on every cadence while blocked, which the plan's own throttling
    /// requirement ("once per gap, not every 15s") explicitly ruled out -- a multi-minute
    /// warm-up window would otherwise produce a duplicate line every 15s for its entire
    /// duration. Reset to false the moment warm-up succeeds, so a later, genuinely new
    /// not-warmed-up stretch logs again.
    /// </summary>
    bool _warmUpBlockedLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForMarketHoursAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

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
        await SeedPriorSessionIvHistoryAsync(_engine, asOfDate, stoppingToken);
        await dashboardPush.StartAsync(stoppingToken);

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

        // The only consumer of ConnectionUnstable -- turns a FATAL log line no one was
        // watching (live-caught 2026-09-07: the feed dropped six times in one morning with
        // zero notification) into an actual Telegram message. ConnectionFailure already has a
        // 5-minute cooldown in RateLimitedTelegramNotifier, so a flapping reconnect can't spam.
        tickSource.ConnectionUnstable += failures => telegram.SendAsync(
            NotificationCategory.ConnectionFailure,
            $"NiftySignal: FlatTrade feed has failed {failures} times consecutively and may be down.",
            stoppingToken);

        // A separate, linked token for the feed/cadence/sample/subscription loops only --
        // StopAtMarketCloseAsync cancels *this* once MarketHardClose is reached, so those loops
        // wind down (they already handle OperationCanceledException) without needing the whole
        // service stopped. The paper-trade summary loop deliberately stays on the full-lifetime
        // stoppingToken -- it's cheap (once/30min) and still useful to send after close.
        using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var closeWatchdog = StopAtMarketCloseAsync(feedCts, stoppingToken);

        var cadenceLoop = RunScoreCadenceLoopAsync(feedCts.Token);
        var sampleLoop = RunSampleLoopAsync(feedCts.Token);
        var pendingSubscriptionLoop = RunPendingSubscriptionLoopAsync(tickSource, asOfDate, feedCts.Token);
        var paperTradeSummaryLoop = RunPaperTradeSummaryLoopAsync(stoppingToken);
        await RunTickLoopAsync(tickSource, feedCts.Token);
        await cadenceLoop;
        await sampleLoop;
        await pendingSubscriptionLoop;
        await closeWatchdog;
        await paperTradeSummaryLoop;
    }

    /// <summary>Waits (checking once a minute) until the current IST time is within [MarketPreOpen, MarketHardClose) on a weekday -- keeps the worker from attempting a live feed connection at all outside that window.</summary>
    async Task WaitForMarketHoursAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var nowIst = DateTimeOffset.UtcNow.ToIst();
            var nowTime = TimeOnly.FromDateTime(nowIst.DateTime);
            var isWeekday = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (isWeekday && nowTime >= MarketPreOpen && nowTime < MarketHardClose)
            {
                return;
            }

            logger.LogInformation("Outside market hours ({NowIst:HH:mm} IST, {DayOfWeek}) -- waiting for {MarketPreOpen}", nowIst, nowIst.DayOfWeek, MarketPreOpen);
            try
            {
                await Task.Delay(MarketHoursPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Cancels <paramref name="feedCts"/> once IST time reaches MarketHardClose, so the live feed/cadence/sample/subscription loops stop rather than running on frozen post-close data until the service is manually stopped.</summary>
    async Task StopAtMarketCloseAsync(CancellationTokenSource feedCts, CancellationToken lifetimeToken)
    {
        try
        {
            while (!lifetimeToken.IsCancellationRequested)
            {
                var nowIst = DateTimeOffset.UtcNow.ToIst();
                if (TimeOnly.FromDateTime(nowIst.DateTime) >= MarketHardClose)
                {
                    logger.LogInformation("Market hard-close reached ({NowIst:HH:mm} IST) -- stopping the live feed and cadence loops", nowIst);
                    feedCts.Cancel();
                    return;
                }

                await Task.Delay(MarketHoursPollInterval, lifetimeToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task RunTickLoopAsync(FlatTradeTickSource tickSource, CancellationToken stoppingToken)
    {
        var buffer = new List<Tick>(FlushBatchSize);
        var lastFlush = DateTimeOffset.UtcNow;

        try
        {
            await foreach (var tick in tickSource.ReadTicksAsync(stoppingToken))
            {
                try
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

                    await dashboardPush.PushTickAsync(tick, stoppingToken);

                    buffer.Add(tick);
                    if (buffer.Count >= FlushBatchSize || DateTimeOffset.UtcNow - lastFlush >= FlushInterval)
                    {
                        await FlushAsync(buffer, stoppingToken);
                        buffer.Clear();
                        lastFlush = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Audit finding F34 (2026-09-09, live-caught) -- see FlushAsync's own doc
                    // comment for the incident this fixes. A tick not yet added to `buffer` when
                    // this fires is simply skipped (same "log and move on" tolerance as a single
                    // bad score cadence); ticks already in `buffer` from earlier in this batch are
                    // deliberately left there, not cleared, so they're retried on the next
                    // successful flush instead of lost.
                    logger.LogError(ex, "Tick processing failed for {Token} -- continuing with the next tick", tick.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        if (buffer.Count > 0)
        {
            try
            {
                await FlushAsync(buffer, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Final flush of {Count} buffered ticks failed during shutdown", buffer.Count);
            }
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
                    var now = DateTimeOffset.UtcNow;
                    var snapshot = _engine!.ComputeCadence(now);
                    if (snapshot is not null)
                    {
                        if (!snapshot.IsWarmedUp)
                        {
                            // Audit finding F10 (2026-09-08): previously, one dead required
                            // component silently withheld the composite score with no way to
                            // tell which one -- diagnosed once today only by adding a temporary
                            // throw. Named directly against ScoreSnapshot's own properties
                            // (compiler-checked: a renamed/removed property breaks the build)
                            // rather than a second, independently-maintained list of the six
                            // required names. Throttled to once per not-warmed-up stretch, not
                            // every 15s cadence (see _warmUpBlockedLogged's own doc comment) --
                            // fixed 2026-09-09, the original version logged unconditionally.
                            if (!_warmUpBlockedLogged)
                            {
                                var blocking = DescribeWarmUpBlockers(snapshot);
                                if (blocking.Count > 0)
                                {
                                    logger.LogWarning("Composite score not warmed up -- blocked by: {Blocking}", string.Join(", ", blocking));
                                    _warmUpBlockedLogged = true;
                                }
                            }
                        }
                        else
                        {
                            _warmUpBlockedLogged = false;
                        }

                        await PersistSnapshotAsync(snapshot, _engine.BuildStrikeSnapshots(now), stoppingToken);
                        await tradingEngine.EvaluateCadenceAsync(snapshot, _engine, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Live-caught 2026-09-08: an uncaught DbUpdateException here (ScoreWeightsVersion
                    // too long for its column) escaped the while loop and silently ended scoring/trading
                    // for the rest of the session, while ticks kept flowing on their own separate loop --
                    // nothing else in the process noticed. One bad cadence must never take down the rest
                    // of the day; log it and let the next tick try again.
                    logger.LogError(ex, "Score cadence tick failed -- continuing with the next cadence");
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
    /// The six components CompositeScoreCalculator treats as required (everything except
    /// VixChange and the diagnostic-only Gamma/Vanna/Charm/VolumePcr/SpreadRatio/CvdProxy/
    /// StraddleRichness set) -- named here explicitly rather than reflected, since this list
    /// changes only when the plan's original six change, which is rare enough that an explicit
    /// list reads better than reflection magic.
    /// </summary>
    static List<string> DescribeWarmUpBlockers(ScoreSnapshot snapshot)
    {
        var blocking = new List<string>();
        if (snapshot.OiBuildupNetZ is null) blocking.Add(nameof(ScoreSnapshot.OiBuildupNetZ));
        if (snapshot.PcrZ is null) blocking.Add(nameof(ScoreSnapshot.PcrZ));
        if (snapshot.FuturesBasisZ is null) blocking.Add(nameof(ScoreSnapshot.FuturesBasisZ));
        if (snapshot.IvSkewZ is null) blocking.Add(nameof(ScoreSnapshot.IvSkewZ));
        if (snapshot.PriceMomentumZ is null) blocking.Add(nameof(ScoreSnapshot.PriceMomentumZ));
        if (snapshot.DepthImbalanceZ is null) blocking.Add(nameof(ScoreSnapshot.DepthImbalanceZ));
        return blocking;
    }

    /// <summary>Feeds LiveFeatureEngine.Sample every few seconds so ComputeCadence has more than one instant to average over -- see LiveFeatureEngine's class doc comment.</summary>
    async Task RunSampleLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _engineSync.WaitAsync(stoppingToken);
                try
                {
                    _engine!.Sample(DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Audit finding F34 (2026-09-09) -- same "one bad tick must not take down the
                    // rest of the day" resilience as RunTickLoopAsync/FlushAsync; see FlushAsync's
                    // own doc comment for the incident this fixes.
                    logger.LogError(ex, "Sample tick failed -- continuing with the next one");
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
                try
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Audit finding F34 (2026-09-09) -- same resilience as the other three loops;
                    // this one does real DB I/O (Instruments query + SaveChangesAsync), so it
                    // shares the exact transient-Postgres-failure risk that crashed
                    // RunTickLoopAsync -- see FlushAsync's own doc comment for that incident.
                    logger.LogError(ex, "Pending-subscription poll failed -- continuing with the next one");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Open positions + today's closed-trade stats, sent every 30 minutes -- but only when there's something to report (2026-09-07: previously sent unconditionally, including a bare "0 trades" every 30 minutes all day).</summary>
    async Task RunPaperTradeSummaryLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PaperTradeSummaryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SendPaperTradeSummaryAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task SendPaperTradeSummaryAsync(CancellationToken ct)
    {
        // Computed fresh from UtcNow on every call (2026-09-07), not from the asOfDate
        // captured once at ExecuteAsync startup -- a worker still running past IST midnight
        // (the exact "no market-hours gating" issue fixed alongside this) would otherwise
        // keep comparing against the day it started, silently including a prior day's trades
        // under "today" once the calendar actually rolled over.
        var todayIstMidnightUtc = new DateTimeOffset(DateTimeOffset.UtcNow.ToIst().Date, IstOffset).ToUniversalTime();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        var open = await db.PaperTrades.Where(t => t.ExitTime == null).ToListAsync(ct);
        var closedToday = await db.PaperTrades
            .Where(t => t.ExitTime != null && t.ExitTime >= todayIstMidnightUtc)
            .OrderByDescending(t => t.ExitTime)
            .ToListAsync(ct);

        if (open.Count == 0 && closedToday.Count == 0)
        {
            return;
        }

        var message = BuildPaperTradeSummaryMessage(open, closedToday);
        await telegram.SendAsync(NotificationCategory.PaperTradeSummary, message, ct);
    }

    static string BuildPaperTradeSummaryMessage(List<PaperTrade> open, List<PaperTrade> closedToday)
    {
        var wins = closedToday.Count(t => t.NetPnl > 0);
        var netPnl = closedToday.Sum(t => t.NetPnl ?? 0);
        var grossWin = closedToday.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl ?? 0);
        var grossLoss = Math.Abs(closedToday.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl ?? 0));
        var winRate = closedToday.Count == 0 ? 0 : 100.0 * wins / closedToday.Count;

        var sb = new StringBuilder();
        sb.AppendLine($"NiftySignal paper trade summary ({DateTimeOffset.UtcNow.ToOffset(IstOffset):HH:mm} IST)");
        sb.AppendLine($"Open positions: {open.Count}");
        sb.AppendLine(closedToday.Count == 0
            ? "Closed today: 0"
            : $"Closed today: {closedToday.Count} ({wins}W/{closedToday.Count - wins}L, {winRate:0}% win rate)");
        sb.AppendLine($"Net P&L: {netPnl:+0.00;-0.00}");
        if (grossLoss > 0)
        {
            sb.AppendLine($"Profit factor: {(double)(grossWin / grossLoss):0.00}");
        }

        if (closedToday.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent closes:");
            foreach (var t in closedToday.Take(5))
            {
                sb.AppendLine($"- {t.TradingSymbol} ({t.Direction}): {t.NetPnl:+0.00;-0.00} ({t.ExitReason})");
            }
        }

        return sb.ToString().TrimEnd();
    }

    async Task PersistSnapshotAsync(ScoreSnapshot snapshot, List<StrikeSnapshot> strikeSnapshots, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.ScoreSnapshots.Add(snapshot);

        // Same transaction as the score row -- they describe the same cadence instant, so a
        // partial write would leave analysis joining against a cadence that only half exists.
        if (strikeSnapshots.Count > 0)
        {
            db.StrikeSnapshots.AddRange(strikeSnapshots);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Score cadence: composite={Score} warmedUp={WarmedUp} strikeRows={StrikeRows}",
            snapshot.CompositeScore, snapshot.IsWarmedUp, strikeSnapshots.Count);
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

    /// <summary>
    /// Seeds the prior-session ATM IV distribution the F3 IV-rank gate ranks against once
    /// mature (2026-09-09 external review amendment -- see
    /// LiveFeatureEngine.SeedPriorSessionIvHistory). One representative value per prior session
    /// (that session's mean of its own persisted AtmIv), most recent first, capped at the same
    /// 20-session window LiveFeatureEngine itself caps at. Best-effort: a query failure here
    /// must not stop ingestion from starting -- it just leaves the IV-rank gate in cold-start
    /// (same-day-only) mode for this run, same as any other day with fewer than 5 prior
    /// sessions of history.
    /// </summary>
    async Task SeedPriorSessionIvHistoryAsync(LiveFeatureEngine engine, DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        try
        {
            // Must match LiveFeatureEngine.MaxPriorSessionsForIvRank's own limit of 20.
            var sessionMeans = await db.Database.SqlQuery<double>(
                $"""
                SELECT AVG("AtmIv") AS "Value"
                FROM score_snapshots
                WHERE "AtmIv" IS NOT NULL
                  AND ("ComputedAt" AT TIME ZONE 'Asia/Kolkata')::date < {asOfDate}
                GROUP BY ("ComputedAt" AT TIME ZONE 'Asia/Kolkata')::date
                ORDER BY ("ComputedAt" AT TIME ZONE 'Asia/Kolkata')::date DESC
                LIMIT 20
                """).ToListAsync(ct);

            // Oldest-first, matching SeedHistory's chronological-replay convention elsewhere --
            // harmless either way, since ComputeIvRank only ever reads min/max over the whole
            // set, but consistent ordering keeps this easier to reason about if that changes.
            sessionMeans.Reverse();
            engine.SeedPriorSessionIvHistory(sessionMeans);
            logger.LogInformation("Seeded IV-rank gate with {Count} prior sessions' ATM IV", sessionMeans.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to seed prior-session IV-rank history -- gate stays in cold-start (same-day-only) mode for this run");
        }
    }

    // Audit finding F34 (2026-09-09, live-caught): a transient DB failure here used to throw
    // straight out of this method, out of the await foreach in RunTickLoopAsync, and crash the
    // whole BackgroundService -- live-caught for real from a routine Postgres service restart,
    // mid-session. NiftySignalHost went to Stopped and needed a manual Start-Service to recover;
    // had a position been open at the time, it would have gone unmonitored (no stop-loss/exit-
    // rule evaluation) until someone noticed. Same failure class RunScoreCadenceLoopAsync was
    // already fixed against on 2026-09-08 (see its own catch block's doc comment: "One bad
    // cadence must never take down the rest of the day") -- that fix just hadn't been extended to
    // this loop yet. RunTickLoopAsync now catches around its own per-tick body (see there);
    // RunSampleLoopAsync and RunPendingSubscriptionLoopAsync got the same treatment alongside it,
    // since both had the identical gap.
    async Task FlushAsync(List<Tick> buffer, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Ticks.AddRange(buffer);
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Flushed {Count} ticks", buffer.Count);
    }
}
