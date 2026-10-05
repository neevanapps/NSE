using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Domain;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;

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
    CoreScoreCrossoverTradingEngine crossoverEngine,
    DashboardPushClient dashboardPush,
    IValidatedOptions<ScoreWeights> scoreWeightsOptions,
    IOptionsMonitor<PricingOptions> pricingOptions,
    AdaptiveFuturesObservationEngine adaptiveFuturesObserver,
    ILogger<MarketDataIngestionWorker> logger) : BackgroundService
{
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);
    const int FlushBatchSize = 200;
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    // Sensex/Bank Nifty tick-collection monitoring (2026-09-22): ticks/sec per underlying, flush
    // duration, and Ticks-table disk growth, logged at this cadence -- not spammed per-flush (which
    // happens every ~1s/200 ticks). A plain log line, per the task spec's own "not a new dashboard
    // panel or database table" instruction.
    static readonly TimeSpan MonitoringInterval = TimeSpan.FromMinutes(1);

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
    /// Token -> Underlying for today's whole resolved universe (NIFTY + SENSEX + BANKNIFTY),
    /// rebuilt fresh each trading session in <see cref="RunTradingSessionAsync"/> -- used only by
    /// <see cref="RunMonitoringLoopAsync"/> to attribute ticks/sec per underlying; nothing
    /// decision-making reads this (see docs/SENSEX_BANKNIFTY_TICK_COLLECTION.md's no-downstream-
    /// leakage proof for why monitoring is the only new thing that looks at all three underlyings
    /// together). A token subscribed later via <see cref="RunPendingSubscriptionLoopAsync"/> (an
    /// on-demand Dashboard watch request) won't be in this snapshot -- its ticks are counted under
    /// "UNKNOWN" in the monitoring log rather than causing an error; acceptable since this is a
    /// diagnostic aid, not a correctness-sensitive path.
    /// </summary>
    IReadOnlyDictionary<string, string> _tokenToUnderlying = new Dictionary<string, string>();

    /// <summary>Ticks received since the last monitoring report, per underlying -- reset to empty at the start of each trading session and drained (not just read) by <see cref="RunMonitoringLoopAsync"/> every <see cref="MonitoringInterval"/>.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _tickCountByUnderlyingSinceReport = new();

    /// <summary>Flush-duration accumulator since the last monitoring report -- (count, totalMs), read/reset together under <see cref="_flushStatsSync"/> since <see cref="FlushAsync"/> (single writer, the tick loop) and <see cref="RunMonitoringLoopAsync"/> (reader/resetter) run on different loops.</summary>
    readonly Lock _flushStatsSync = new();
    int _flushCountSinceReport;
    double _flushDurationMsTotalSinceReport;

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

    /// <summary>
    /// Edge-triggered guard, same shape as <see cref="_warmUpBlockedLogged"/>: logs once when the
    /// cadence loop's own market-open gate (see <see cref="RunScoreCadenceLoopAsync"/>) first lets
    /// a cadence through for the day, not on every pre-open tick it skips before that.
    /// </summary>
    bool _marketOpenCadenceStartLogged;

    /// <summary>
    /// Audit finding F49 (2026-09-10): this used to run exactly one trading day's session and
    /// return -- <see cref="BackgroundService"/> treats a returned <c>ExecuteAsync</c> as "this
    /// service is permanently done," so without a manual service restart, ingestion/scoring
    /// silently never resumed on day 2. Now loops: wait for market hours, run one day's session,
    /// repeat.
    ///
    /// Two things are started here, once, outside the day loop -- neither belongs inside
    /// <see cref="RunTradingSessionAsync"/>, and re-starting either one per day would itself be
    /// a bug, not a fix:
    /// <list type="bullet">
    /// <item><see cref="dashboardPush"/> -- its SignalR connection auto-reconnects on its own
    /// (<c>WithAutomaticReconnect</c>) and is designed to persist across the overnight
    /// market-closed gap; calling <c>StartAsync</c> again on an already-connected
    /// <c>HubConnection</c> throws.</item>
    /// <item><see cref="RunPaperTradeSummaryLoopAsync"/> -- deliberately runs on this method's
    /// full-lifetime <paramref name="stoppingToken"/>, not a day-scoped one (see that method's
    /// own comment: it keeps sending summaries after a day's market-hours loops have already
    /// wound down), so it never completes during normal operation. Awaiting it inside the day
    /// loop would therefore block forever on day 1 and defeat the loop entirely -- it's started
    /// once here and only awaited once, after the day loop itself exits.</item>
    /// </list>
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await dashboardPush.StartAsync(stoppingToken);
        var paperTradeSummaryLoop = RunPaperTradeSummaryLoopAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await WaitForMarketHoursAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (!await RunTradingSessionAsync(stoppingToken))
            {
                // A hard-stop condition (today: only a missing/invalid FlatTrade session --
                // see RunTradingSessionAsync's own doc comment for why that deliberately still
                // ends the service rather than retrying next day automatically). Everything
                // else that can go wrong during a session is already caught and logged inside
                // the per-loop try/catches (audit finding F34) without ending the session early.
                break;
            }
        }

        await paperTradeSummaryLoop;
    }

    /// <summary>
    /// One trading day's ingestion/scoring/trading session, from FlatTrade session validation
    /// through market close. Returns <c>false</c> when <see cref="ExecuteAsync"/>'s day loop
    /// should stop entirely rather than wait for tomorrow -- currently only when there's no
    /// valid FlatTrade session, which needs a human to fix via the Dashboard (plan 4.3: "do not
    /// silently retry"), not an automatic next-day retry that could mask the same problem
    /// recurring silently every morning. Everything else (feed drops, a bad score cadence, a
    /// failed DB write) is already resilient at the per-loop level and returns <c>true</c>.
    /// </summary>
    async Task<bool> RunTradingSessionAsync(CancellationToken stoppingToken)
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
            return false;
        }

        // Reset for this session's own warm-up stretch -- a prior day's leftover true here
        // would wrongly suppress today's first genuine warm-up-blocked log (audit finding F49:
        // this field was never revisited when the day loop was added, since previously the
        // whole process only ever ran one day and this was set at most once, ever).
        _warmUpBlockedLogged = false;
        _marketOpenCadenceStartLogged = false;

        var asOfDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IstOffset).Date);
        var instruments = await ResolveInstrumentsAsync(session.Token, asOfDate, stoppingToken);
        var subscriptions = instruments.Select(i => (i.Exchange, i.Token)).ToList();

        // Isolated research observer: initialize from today's NIFTY future and replay any
        // already-persisted market-open ticks before the socket starts. This makes a service
        // restart after 09:30 reproduce the same opening-volume forecast/bar state rather than
        // silently starting from the restart time.
        var observationFuture = instruments
            .Where(i => i.InstrumentType == InstrumentType.Future && i.Underlying == "NIFTY")
            .OrderBy(i => i.ExpiryDate)
            .FirstOrDefault();
        if (observationFuture is not null)
        {
            adaptiveFuturesObserver.StartSession(asOfDate, observationFuture);
            await SeedAdaptiveFuturesObserverAsync(observationFuture.Token, asOfDate, stoppingToken);
        }

        // Monitoring-only snapshot (see _tokenToUnderlying's own doc comment) -- built from the
        // SAME `instruments` list passed to LiveFeatureEngine below, which is safe to include
        // SENSEX/BANKNIFTY rows because LiveFeatureEngine's own constructor (audit finding F61)
        // filters to NIFTY internally; this dictionary is purely descriptive and drives no
        // decision. Fresh per session -- a restart mid-day rebuilds it rather than carrying over
        // a prior session's stale counts.
        _tokenToUnderlying = instruments.ToDictionary(i => i.Token, i => i.Underlying);
        _tickCountByUnderlyingSinceReport.Clear();
        lock (_flushStatsSync)
        {
            _flushCountSinceReport = 0;
            _flushDurationMsTotalSinceReport = 0;
        }

        _engine = new LiveFeatureEngine(instruments, scoreWeightsOptions, pricingOptions, logger);
        await SeedEngineHistoryAsync(_engine, asOfDate, stoppingToken);
        await SeedPriorSessionIvHistoryAsync(_engine, asOfDate, stoppingToken);

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

        // A separate, linked token for THIS DAY's feed/cadence/sample/subscription loops only --
        // StopAtMarketCloseAsync cancels *this* once MarketHardClose is reached, so those loops
        // wind down (they already handle OperationCanceledException) without needing the whole
        // service stopped. Deliberately NOT used for the paper-trade summary loop, which lives
        // in ExecuteAsync on the full-lifetime token instead -- see that method's own comment.
        using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var closeWatchdog = StopAtMarketCloseAsync(feedCts, stoppingToken);

        var cadenceLoop = RunScoreCadenceLoopAsync(feedCts.Token);
        var sampleLoop = RunSampleLoopAsync(feedCts.Token);
        var pendingSubscriptionLoop = RunPendingSubscriptionLoopAsync(tickSource, asOfDate, feedCts.Token);
        var monitoringLoop = RunMonitoringLoopAsync(asOfDate, feedCts.Token);
        await RunTickLoopAsync(tickSource, feedCts.Token);
        await cadenceLoop;
        await sampleLoop;
        await pendingSubscriptionLoop;
        await monitoringLoop;
        await closeWatchdog;
        adaptiveFuturesObserver.EndSession();
        return true;
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

    async Task SeedAdaptiveFuturesObserverAsync(string futureToken, DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var dayStartUtc = new DateTimeOffset(asOfDate.ToDateTime(new TimeOnly(9, 15)), IstTime.Offset).ToUniversalTime();
        var cutoffUtc = DateTimeOffset.UtcNow;

        var ticks = db.Ticks
            .AsNoTracking()
            .Where(t => t.Token == futureToken
                && t.ExchangeTimestamp >= dayStartUtc
                && t.ExchangeTimestamp <= cutoffUtc)
            .OrderBy(t => t.ExchangeTimestamp)
            .ThenBy(t => t.Id)
            .AsAsyncEnumerable();

        var count = 0;
        await foreach (var tick in ticks.WithCancellation(ct))
        {
            adaptiveFuturesObserver.OnTick(tick);
            count++;
        }

        if (count > 0)
        {
            logger.LogInformation(
                "Adaptive futures observer seeded with {Count} persisted future ticks for {AsOfDate}",
                count, asOfDate);
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

                    adaptiveFuturesObserver.OnTick(tick);

                    await dashboardPush.PushTickAsync(tick, stoppingToken);

                    // Monitoring only (see _tokenToUnderlying's own doc comment) -- never gates or
                    // filters anything; a token this session's universe doesn't recognize (e.g. a
                    // just-requested Dashboard watch not yet in the snapshot) still gets buffered/
                    // flushed normally below, just counted under "UNKNOWN" here.
                    var underlying = _tokenToUnderlying.GetValueOrDefault(tick.Token, "UNKNOWN");
                    _tickCountByUnderlyingSinceReport.AddOrUpdate(underlying, 1, static (_, count) => count + 1);

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
            catch (OperationCanceledException)
            {
                // Audit finding F45 fixed (2026-09-09): stoppingToken is already cancelled by
                // the time a genuine shutdown reaches here -- FlushAsync's own SaveChangesAsync
                // throws the same OperationCanceledException the tick-reading loop above already
                // caught, but this second occurrence had no catch of its own, so it propagated
                // out of RunTickLoopAsync into ExecuteAsync uncaught, triggering .NET's default
                // BackgroundServiceExceptionBehavior (StopHost) to FTL-log a *normal* stop as if
                // it were an unhandled crash -- live-observed as indistinguishable in the logs
                // from F34's actual incident. A handful of buffered-but-unflushed ticks lost on
                // shutdown is an acceptable, already-established tradeoff (see the per-tick
                // catch above) -- there's no "next successful flush" to retry them on once the
                // service is actually stopping.
                logger.LogWarning("Final flush of {Count} buffered ticks skipped -- shutdown already in progress", buffer.Count);
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

                    // Live-caught 2026-09-15: MarketPreOpen (08:45) is a deliberate head start for
                    // feed connection/subscription/tick warm-up (see that constant's own doc
                    // comment) -- it was never meant to gate cadence SCORING too, but with no
                    // separate check here, this loop had been computing (and PersistSnapshotAsync
                    // writing) real ScoreSnapshot/CoreScoreSnapshot rows for the whole 08:45-09:15
                    // pre-open window since the cadence loop's own inception. CadencePopulator's
                    // backtest dataset -- what every Core-score band/weight was actually tuned and
                    // validated against -- anchors its own cadence grid to dayStart = MarketOpen
                    // exactly and has never included a pre-open cadence. SessionRankTracker never
                    // expires a value once added, so those ~120 pre-open cadences/day were
                    // permanently skewing each ranked term's percentile baseline for the entire
                    // session (diluted as real volume accumulates, never fully removed) -- against
                    // pre-open conditions (thin book, pre-open-auction dynamics, indicative pricing)
                    // that don't resemble continuous trading at all. Gated HERE, not inside
                    // LiveFeatureEngine.ComputeCadence itself, so the engine's own tested contract
                    // (and LiveFeatureEngineTests' many non-9:15 synthetic timestamps) stays
                    // untouched -- this is a Host-side scheduling decision, not a scoring rule.
                    if (TimeOnly.FromDateTime(now.ToIst().DateTime) < EntryRuleEvaluator.MarketOpen)
                    {
                        continue;
                    }

                    if (!_marketOpenCadenceStartLogged)
                    {
                        _marketOpenCadenceStartLogged = true;
                        logger.LogInformation("Market open reached -- score cadence computation/persistence starting");
                    }

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

                        var coreScoreSnapshot = _engine.LastCoreScoreSnapshot;
                        await PersistSnapshotAsync(snapshot, coreScoreSnapshot, _engine.BuildStrikeSnapshots(now), stoppingToken);

                        // Cutover (2026-09-21): CoreScoreHysteresisTradingEngine and
                        // CoreScoreCrossoverTradingEngine are retired from live trading now that the
                        // volume-bar OptionsScoreThreeWaySwitchMaxPainConfirmed pipeline (Phase A-D,
                        // docs/LIVE_PARITY_PLAN.md) has replaced them as the live strategy. Both
                        // engines verified FLAT (zero open positions) on the VM dashboard immediately
                        // before this cutover -- see docs/LIVE_PARITY_PLAN.md's dated note. Same
                        // pattern as the A10 cutover above LiveTradingEngine: both engines stay fully
                        // registered/callable (Program.cs) in case ever wanted again, just no longer
                        // called from this cadence loop. coreScoreSnapshot itself is still computed
                        // and persisted above (PersistSnapshotAsync) since other things may still
                        // observe/consume it even though nothing trades off it now.
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
    /// Delegates to CompositeScoreCalculator.DescribeMissingRequiredComponents -- the live
    /// weights (<see cref="scoreWeightsOptions"/>) decide required-vs-optional there, so this
    /// reads the exact same rule the composite itself gates on rather than a second,
    /// independently-maintained list. Fixed 2026-09-10 (found while validating this same day's
    /// F47 deploy): the old hardcoded "the original six" list kept naming PriceMomentumZ as a
    /// blocker in this log long after F47 made it optional, actively misleading anyone reading
    /// it about why warm-up was actually stalled.
    /// </summary>
    List<string> DescribeWarmUpBlockers(ScoreSnapshot snapshot)
    {
        var inputs = new ScoreComponentInputs(
            OiBuildupNetZ: snapshot.OiBuildupNetZ,
            PcrZ: snapshot.PcrZ,
            FuturesBasisZ: snapshot.FuturesBasisZ,
            IvSkewZ: snapshot.IvSkewZ,
            PriceMomentumZ: snapshot.PriceMomentumZ,
            DepthImbalanceZ: snapshot.DepthImbalanceZ,
            VixChangeZ: snapshot.VixChangeZ,
            GammaExposureZ: snapshot.GammaExposureZ,
            VolumePcrZ: snapshot.VolumePcrZ,
            SpreadRatioZ: snapshot.SpreadRatioZ,
            VannaExposureZ: snapshot.VannaExposureZ,
            CharmExposureZ: snapshot.CharmExposureZ,
            CvdProxyZ: snapshot.CvdProxyZ,
            StraddleRichnessZ: snapshot.StraddleRichnessZ);
        return [.. CompositeScoreCalculator.DescribeMissingRequiredComponents(inputs, scoreWeightsOptions.Current)];
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
                try
                {
                    await SendPaperTradeSummaryAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Audit finding F49 (2026-09-10): this loop now runs unattended for the
                    // whole process lifetime (see ExecuteAsync's own comment on why it's started
                    // once, outside the day loop, and only awaited at final shutdown) rather
                    // than being awaited to completion within a few minutes of starting --
                    // previously an uncaught exception here would surface (and crash the whole
                    // host, same F34 risk as every other loop) shortly after the process
                    // started; now it could otherwise sit silent for hours before the final
                    // await at shutdown ever observed it. Same "one bad tick must not take down
                    // the rest of the day" resilience as every other loop in this class.
                    logger.LogError(ex, "Paper trade summary failed -- continuing with the next one");
                }
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

    async Task PersistSnapshotAsync(ScoreSnapshot snapshot, CoreScoreSnapshot? coreScoreSnapshot, List<StrikeSnapshot> strikeSnapshots, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.ScoreSnapshots.Add(snapshot);

        // Same transaction as the score row -- they describe the same cadence instant, so a
        // partial write would leave analysis joining against a cadence that only half exists.
        // Core score (2026-09-13, live-wiring plan A2): its own table, same cadence, same
        // transaction -- coreScoreSnapshot is only null if LiveFeatureEngine's own combine step
        // threw (logged there already) or ComputeCadence itself returned null, neither of which
        // should ever happen here since snapshot is already known non-null at the call site.
        if (coreScoreSnapshot is not null)
        {
            db.CoreScoreSnapshots.Add(coreScoreSnapshot);
        }

        if (strikeSnapshots.Count > 0)
        {
            db.StrikeSnapshots.AddRange(strikeSnapshots);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Score cadence: composite={Score} coreScore={CoreScore} warmedUp={WarmedUp} strikeRows={StrikeRows}",
            snapshot.CompositeScore, coreScoreSnapshot?.CoreScore, snapshot.IsWarmedUp, strikeSnapshots.Count);
    }

    async Task<FlatTradeSession?> LoadSessionAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        return await db.FlatTradeSessions.FindAsync([FlatTradeSession.SingletonId], ct);
    }

    /// <summary>
    /// Reuses today's instruments if the 08:45-equivalent job already ran (e.g. a service restart)
    /// instead of re-hitting FlatTrade's API -- now checked and resolved PER underlying-group
    /// (NIFTY vs. SENSEX/BANKNIFTY) rather than as one all-or-nothing set (2026-09-22, Sensex/Bank
    /// Nifty tick-collection task), so a restart that finds NIFTY's own rows already resolved but
    /// not yet SENSEX/BANKNIFTY's (or vice versa) still fills in whichever is missing rather than
    /// silently reusing an incomplete day. NIFTY's own resolution (<see cref="InstrumentUniverseResolver"/>)
    /// runs first and is entirely unchanged from before this task -- for a day where NIFTY rows
    /// already exist, this method calls the resolver exactly as often as it did before (zero
    /// times), returns exactly the same NIFTY rows, in the same way.
    /// </summary>
    async Task<IReadOnlyList<Instrument>> ResolveInstrumentsAsync(string sessionToken, DateOnly asOfDate, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();

        var existing = await db.Instruments.Where(i => i.AsOfDate == asOfDate).ToListAsync(ct);
        var resolved = new List<Instrument>(existing);
        var wroteAnything = false;

        if (existing.Any(i => i.Underlying == LiveFeatureEngine.NiftyUnderlying))
        {
            logger.LogInformation(
                "Reusing {Count} previously-resolved NIFTY instruments for {AsOfDate}",
                existing.Count(i => i.Underlying == LiveFeatureEngine.NiftyUnderlying), asOfDate);
        }
        else
        {
            var resolver = scope.ServiceProvider.GetRequiredService<InstrumentUniverseResolver>();
            var niftyInstruments = await resolver.ResolveAsync(sessionToken, asOfDate, ct);
            db.Instruments.AddRange(niftyInstruments);
            resolved.AddRange(niftyInstruments);
            wroteAnything = true;
        }

        // Write-only/archive, future-backtesting-only addition (2026-09-22) -- deliberately
        // best-effort and fully independent of NIFTY's own resolution above: a failure here must
        // never stop or delay NIFTY's own live (paper) trading from starting. See
        // docs/SENSEX_BANKNIFTY_TICK_COLLECTION.md for the full design/verification.
        if (existing.Any(i => i.Underlying is SensexBankNiftyInstrumentMasterProvider.SensexUnderlying or SensexBankNiftyInstrumentMasterProvider.BankNiftyUnderlying))
        {
            logger.LogInformation(
                "Reusing {Count} previously-resolved SENSEX/BANKNIFTY instruments for {AsOfDate}",
                existing.Count(i => i.Underlying is SensexBankNiftyInstrumentMasterProvider.SensexUnderlying or SensexBankNiftyInstrumentMasterProvider.BankNiftyUnderlying),
                asOfDate);
        }
        else
        {
            try
            {
                var otherResolver = scope.ServiceProvider.GetRequiredService<SensexBankNiftyInstrumentUniverseResolver>();
                var otherInstruments = await otherResolver.ResolveAsync(sessionToken, asOfDate, ct);
                db.Instruments.AddRange(otherInstruments);
                resolved.AddRange(otherInstruments);
                wroteAnything = wroteAnything || otherInstruments.Count > 0;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SENSEX/BANKNIFTY instrument resolution failed for {AsOfDate} -- continuing without them for today; NIFTY's own resolution/subscription/trading is unaffected.",
                    asOfDate);
            }
        }

        if (wroteAnything)
        {
            await db.SaveChangesAsync(ct);
        }

        return resolved;
    }

    /// <summary>Restart-safe warm-up (see LiveFeatureEngine.SeedHistory) -- replays today's already-persisted cadence snapshots instead of starting every rolling window from zero.</summary>
    async Task SeedEngineHistoryAsync(LiveFeatureEngine engine, DateOnly asOfDate, CancellationToken ct)
    {
        // Anchored at today's MarketOpen (09:15), not midnight (2026-09-15 fix, see the cadence
        // loop's own doc comment on the same-day gate this pairs with) -- a Host restart before
        // this fix shipped could still have pre-open ScoreSnapshot/CoreScoreSnapshot rows sitting
        // in the DB from earlier in the day (or, before the loop-side gate above existed, from any
        // prior day); replaying those into SessionRankTracker on restart would silently reintroduce
        // the exact contamination the loop-side gate now prevents from being written in the first
        // place. Npgsql only accepts UTC (Offset=0) DateTimeOffset values for timestamptz
        // parameters -- the same rule that bit ReceivedAt/UpdatedAt earlier this session.
        var todayMarketOpenUtc = new DateTimeOffset(asOfDate.ToDateTime(EntryRuleEvaluator.MarketOpen), IstOffset).ToUniversalTime();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var history = await db.ScoreSnapshots
            .Where(s => s.ComputedAt >= todayMarketOpenUtc)
            .OrderBy(s => s.ComputedAt)
            .ToListAsync(ct);

        engine.SeedHistory(history);
        logger.LogInformation("Replayed {Count} historical score snapshots into the rolling windows", history.Count);

        // Core score (2026-09-13, live-wiring plan A1/A5) -- separate table, separate replay call;
        // see LiveFeatureEngine.SeedCoreScoreHistory's own doc comment for why this one is
        // required (not an accepted gap) for the Crossover strategy's fast/slow windows.
        var coreHistory = await db.CoreScoreSnapshots
            .Where(s => s.ComputedAt >= todayMarketOpenUtc)
            .OrderBy(s => s.ComputedAt)
            .ToListAsync(ct);

        engine.SeedCoreScoreHistory(coreHistory);
        logger.LogInformation("Replayed {Count} historical Core-score snapshots into the rolling windows", coreHistory.Count);

        // Batch 5 (2026-09-13, plan A7's own correction): the Crossover strategy's own
        // previousDiffSign, from this SAME persisted history -- required, not an accepted gap,
        // since an already-open crossover position depends on it to ever detect its next flip.
        // See CoreScoreCrossoverTradingEngine.SeedFromHistory's own doc comment.
        crossoverEngine.SeedFromHistory(coreHistory);
        logger.LogInformation("Replayed {Count} historical Core-score snapshots into the Crossover strategy's own previousDiffSign", coreHistory.Count);
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
    //
    // Audit finding F45 fixed (2026-09-09, found while validating a routine deploy): a
    // *normal*, intentional service stop was logging as if F34 had recurred -- not from this
    // method's own per-tick guards (correctly excluded above), but from RunTickLoopAsync's
    // *final flush* after the tick loop's own OperationCanceledException was already caught.
    // That flush reused the already-cancelled stoppingToken, so FlushAsync's SaveChangesAsync
    // threw the identical exception a second time, uncaught this time, propagating out of
    // RunTickLoopAsync into ExecuteAsync and triggering .NET's default
    // BackgroundServiceExceptionBehavior (StopHost) to FTL-log a normal stop as an unhandled
    // crash. Live-observed 2026-09-09: a routine Stop-Service ahead of a VM shutdown produced
    // the exact same FTL stack trace and log shape as F34's actual crash, indistinguishable
    // without checking whether a stop was actually requested. See RunTickLoopAsync's own
    // catch (OperationCanceledException) around the final flush for the fix.
    async Task FlushAsync(List<Tick> buffer, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.Ticks.AddRange(buffer);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await db.SaveChangesAsync(ct);
        sw.Stop();

        lock (_flushStatsSync)
        {
            _flushCountSinceReport++;
            _flushDurationMsTotalSinceReport += sw.Elapsed.TotalMilliseconds;
        }

        logger.LogDebug("Flushed {Count} ticks in {ElapsedMs}ms", buffer.Count, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Sensex/Bank Nifty tick-collection monitoring (2026-09-22 task): ticks/sec per underlying,
    /// average flush duration, and the Ticks table's current disk size (a proxy for "disk growth
    /// per day" -- delta since this session started, logged alongside the running total), all as
    /// a single INFO line every <see cref="MonitoringInterval"/>. Read-only, best-effort -- a
    /// failure here (e.g. the disk-size query) must never affect ingestion itself, same "one bad
    /// loop must not take down the rest of the day" discipline every other loop in this class
    /// already follows (audit finding F34).
    /// </summary>
    async Task RunMonitoringLoopAsync(DateOnly asOfDate, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(MonitoringInterval);
        long? tableSizeAtSessionStartBytes = null;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var perUnderlyingRates = new Dictionary<string, double>();
                    foreach (var key in _tickCountByUnderlyingSinceReport.Keys.ToList())
                    {
                        if (_tickCountByUnderlyingSinceReport.TryRemove(key, out var count))
                        {
                            perUnderlyingRates[key] = count / MonitoringInterval.TotalSeconds;
                        }
                    }

                    int flushCount;
                    double flushDurationTotalMs;
                    lock (_flushStatsSync)
                    {
                        flushCount = _flushCountSinceReport;
                        flushDurationTotalMs = _flushDurationMsTotalSinceReport;
                        _flushCountSinceReport = 0;
                        _flushDurationMsTotalSinceReport = 0;
                    }
                    var avgFlushMs = flushCount > 0 ? flushDurationTotalMs / flushCount : 0;

                    long? tableSizeBytes = null;
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
                        // 2026-09-22, live-caught: this database's actual physical table is
                        // lowercase `ticks`, not `"Ticks"` -- EF's own LINQ-generated SQL for
                        // `db.Ticks` resolves correctly because Npgsql emits the identifier
                        // UNQUOTED, which Postgres then case-folds to lowercase, matching the
                        // real table. A quoted `"Ticks"` (capital T) preserves case instead and
                        // fails with "relation does not exist" -- confirmed live, this query was
                        // erroring every monitoring cycle (harmless to ingestion, but noisy [ERR]
                        // spam). Fixed to the real lowercase name.
                        tableSizeBytes = await db.Database
                            .SqlQuery<long>($"""SELECT pg_total_relation_size('ticks') AS "Value" """)
                            .SingleAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Monitoring: could not read Ticks table size this cycle");
                    }
                    tableSizeAtSessionStartBytes ??= tableSizeBytes;

                    var ticksPerSecDescription = perUnderlyingRates.Count == 0
                        ? "no ticks"
                        : string.Join(", ", perUnderlyingRates.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value:0.0}/s"));
                    var growthSinceSessionStartMb = tableSizeBytes is { } sizeNow && tableSizeAtSessionStartBytes is { } sizeStart
                        ? (sizeNow - sizeStart) / (1024.0 * 1024.0)
                        : (double?)null;

                    logger.LogInformation(
                        "Ingestion monitoring ({AsOfDate}): ticks/sec [{TicksPerSec}], flushes={FlushCount} avgFlushMs={AvgFlushMs:0.0}, Ticks table size={TableSizeMb:0.0}MB (+{GrowthMb:0.00}MB since session start)",
                        asOfDate, ticksPerSecDescription, flushCount, avgFlushMs,
                        tableSizeBytes.HasValue ? tableSizeBytes.Value / (1024.0 * 1024.0) : (double?)null,
                        growthSinceSessionStartMb);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Audit finding F34 discipline -- same as every other loop in this class.
                    logger.LogError(ex, "Ingestion monitoring cycle failed -- continuing with the next one");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
