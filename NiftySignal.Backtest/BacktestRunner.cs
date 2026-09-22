using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Entities;
using NiftySignal.Features;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;

namespace NiftySignal.Backtest;

/// <summary>
/// Audit finding F32's actual runner: replays real historical ticks through the same
/// LiveFeatureEngine/LiveTradingEngine pipeline that decides real (paper) trades live, one
/// trading day at a time. See BacktestTickSource's doc comment for why this is safe -- both
/// engines were already confirmed clean of any DateTimeOffset.UtcNow dependency before this
/// class was written.
///
/// Reads real ticks/instruments from <paramref name="realTicksDb"/> (a read-only DbContext
/// against real Postgres) but writes every trial ScoreSnapshot/PaperTrade this run produces to
/// its own isolated store via <paramref name="backtestScopeFactory"/> (an EF InMemoryDatabase in
/// production use, per NiftySignal.Backtest.csproj's own comment) -- the two are never the same
/// DbContext, so a backtest run can never mutate real data.
///
/// A fresh LiveFeatureEngine per trading day, not one spanning the whole range: expiry rolls and
/// resolved instrument sets differ day to day (see Instrument.AsOfDate), and production itself
/// never carries LiveFeatureEngine's warm-up state across a day boundary either -- matching that
/// is correctness, not a shortcut. tradingEngine and its underlying DB persist across the whole
/// range, though: day-scoped risk gates (MaxDailyLossPct, trade counts, consecutive losses)
/// already filter by EntryTime against each cadence's own `now`, so they reset at day boundaries
/// without any special-casing here.
///
/// <paramref name="scoreSource"/> (2026-09-11, backtest-only) lets a run be driven by the ratio
/// composite (level or momentum) instead of the original one, entirely within this class -- see
/// the comment at its one use site below for why this can never leak into the live path.
///
/// <see cref="BacktestScoreSource.DynamicHybrid"/> (2026-09-11, user requirement: no hardcoded
/// entry/exit metric or value) additionally needs <paramref name="mutableRulesetOptions"/> -- the
/// SAME options instance <paramref name="tradingEngine"/> was constructed with, so this class can
/// hand it a freshly-derived RulesetConfig (Entry.MinAbsScore/Exit.ExitOnScoreBelowAbs recomputed
/// from the ratio level's own session-so-far distribution) before every cadence, exploiting
/// LiveTradingEngine's existing hot-reload read (`_config => rulesetOptions.Current`) rather than
/// changing LiveTradingEngine at all. <paramref name="dynamicEntryRankCutoff"/>/<paramref
/// name="dynamicExitRankCutoff"/> are the one remaining parameter this mode has -- percentile
/// cutoffs, not score magnitudes, so the same value stays meaningful regardless of which regime
/// or score is in play. See SessionRankTracker's own doc comment for why that's a materially
/// different kind of constant than the MinAbsScore=64/17 magnitudes this session's diagnosis
/// already found to be miscalibrated.
/// </summary>
public sealed class BacktestRunner(
    NiftySignalDbContext realTicksDb,
    IServiceScopeFactory backtestScopeFactory,
    LiveTradingEngine tradingEngine,
    IValidatedOptions<ScoreWeights>? scoreWeightsOptions = null,
    ILogger? logger = null,
    BacktestScoreSource scoreSource = BacktestScoreSource.OriginalComposite,
    MutableRulesetOptions? mutableRulesetOptions = null,
    double dynamicEntryRankCutoff = 85.0,
    double dynamicExitRankCutoff = 50.0)
{
    static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan CadenceInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public async Task<BacktestResult> RunAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
        if (scoreSource == BacktestScoreSource.DynamicHybrid && mutableRulesetOptions is null)
        {
            throw new InvalidOperationException(
                $"{nameof(BacktestScoreSource.DynamicHybrid)} requires {nameof(mutableRulesetOptions)} -- pass the same MutableRulesetOptions instance tradingEngine was constructed with.");
        }

        var totalCadences = 0;
        var totalScoredCadences = 0;

        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var (cadences, scored) = await RunDayAsync(day, ct);
            totalCadences += cadences;
            totalScoredCadences += scored;
        }

        await using var scope = backtestScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        var trades = await db.PaperTrades.OrderBy(t => t.EntryTime).ToListAsync(ct);
        var snapshots = await db.ScoreSnapshots.OrderBy(s => s.ComputedAt).ToListAsync(ct);

        return new BacktestResult(trades, snapshots, PerformanceReportBuilder.Build(trades), totalCadences, totalScoredCadences);
    }

    async Task<(int Cadences, int Scored)> RunDayAsync(DateOnly day, CancellationToken ct)
    {
        var dayStartUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();
        var dayEndUtc = dayStartUtc.AddDays(1);

        // Audit finding F61 (2026-09-22): explicit NIFTY filter at the intake, in addition to
        // LiveFeatureEngine's own internal filter (belt-and-suspenders -- see that class's
        // NiftyUnderlying doc comment) -- this backtest runner drives the exact live-decision
        // pipeline, so its own instrument set should never silently include another underlying's
        // rows even if the engine's internal guard were ever weakened.
        var instruments = await realTicksDb.Instruments.Where(i => i.AsOfDate == day && i.Underlying == LiveFeatureEngine.NiftyUnderlying).ToListAsync(ct);
        if (instruments.Count == 0)
        {
            logger?.LogInformation("Backtest: no resolved instruments for {Day} -- skipping (weekend/holiday/no data).", day);
            return (0, 0);
        }

        var tickSource = new BacktestTickSource(realTicksDb, dayStartUtc, dayEndUtc);
        var ticks = new List<Tick>();
        await foreach (var tick in tickSource.ReadTicksAsync(ct))
        {
            ticks.Add(tick);
        }

        if (ticks.Count == 0)
        {
            logger?.LogInformation("Backtest: no ticks for {Day} -- skipping.", day);
            return (0, 0);
        }

        LiveFeatureEngine engine;
        try
        {
            engine = new LiveFeatureEngine(instruments, scoreWeightsOptions, logger: logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A day whose resolved instrument set is missing the index/future/nearest-expiry
            // options this constructor requires (bad backfill, partial day) must not take down
            // the rest of the range -- same "one bad thing must not stop everything else"
            // discipline this project already applies to the live cadence loop (F34).
            logger?.LogWarning(ex, "Backtest: could not construct LiveFeatureEngine for {Day} -- skipping.", day);
            return (0, 0);
        }

        var nextSample = ticks[0].ExchangeTimestamp;
        var nextCadence = ticks[0].ExchangeTimestamp;
        var lastTickTime = ticks[^1].ExchangeTimestamp;
        var tickIndex = 0;
        var cadenceCount = 0;
        var scoredCount = 0;

        // Fresh per day, same lifetime as `engine` above -- audit finding F55's dynamic-hybrid
        // mode ranks the ratio level against THIS DAY's own accumulating distribution, not a
        // fixed historical snapshot (see SessionRankTracker's own doc comment).
        var sessionRankTracker = new SessionRankTracker();

        while (nextSample <= lastTickTime || nextCadence <= lastTickTime)
        {
            ct.ThrowIfCancellationRequested();
            var boundary = nextSample < nextCadence ? nextSample : nextCadence;

            // Apply every tick recorded up to this boundary before sampling/computing at it --
            // matches live: by the time a timer fires, OnTick has already applied everything
            // that arrived before that instant.
            while (tickIndex < ticks.Count && ticks[tickIndex].ExchangeTimestamp <= boundary)
            {
                engine.OnTick(ticks[tickIndex]);
                tickIndex++;
            }

            if (nextSample <= boundary)
            {
                engine.Sample(boundary);
                nextSample = nextSample.Add(SampleInterval);
            }

            if (nextCadence <= boundary)
            {
                var snapshot = engine.ComputeCadence(boundary);
                nextCadence = nextCadence.Add(CadenceInterval);

                if (snapshot is not null)
                {
                    cadenceCount++;

                    // Persist the real, unmodified snapshot first -- the ScoreSnapshot row stays
                    // accurate for later analysis regardless of which score below actually drove
                    // the trade decision.
                    await PersistSnapshotAsync(snapshot, ct);

                    // Explicit, deliberate, backtest-only opt-in (2026-09-11, requested by name --
                    // see docs/REVIEW_FINDINGS.md's 2026-09-11 section). This does NOT touch
                    // LiveTradingEngine.cs, so CLAUDE.md's standing guarantee that
                    // LiveTradingEngine only ever reads CompositeScore stays literally true for
                    // the live path -- LiveTradingEngineTests' own parity test still proves it.
                    // Only this in-memory copy, used for entry/exit evaluation below, is mutated,
                    // and only after the DB write above has already completed.
                    switch (scoreSource)
                    {
                        case BacktestScoreSource.RatioLevel:
                            snapshot.CompositeScore = snapshot.RatioCompositeScore;
                            snapshot.IsWarmedUp = snapshot.RatioIsWarmedUp;
                            break;
                        case BacktestScoreSource.RatioMomentum:
                            // Audit finding F55 (2026-09-11): RatioMomentum is null unless BOTH
                            // the fast and slow ratio composites are warmed up (the slow one is
                            // the binding constraint -- see LiveFeatureEngine's own comment at
                            // the field's computation), so IsWarmedUp tracks that directly rather
                            // than reusing RatioIsWarmedUp (which only reflects the slow one).
                            snapshot.CompositeScore = snapshot.RatioMomentum;
                            snapshot.IsWarmedUp = snapshot.RatioMomentum is not null;
                            break;
                        case BacktestScoreSource.DynamicHybrid:
                            ApplyDynamicHybrid(snapshot, sessionRankTracker);
                            break;
                        case BacktestScoreSource.OriginalComposite:
                        default:
                            break;
                    }

                    if (snapshot.CompositeScore is not null)
                    {
                        scoredCount++;
                    }

                    try
                    {
                        await tradingEngine.EvaluateCadenceAsync(snapshot, engine, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Same "one bad cadence must never take down the rest of the day"
                        // discipline MarketDataIngestionWorker's live loop already follows
                        // (audit finding F34).
                        logger?.LogError(ex, "Backtest: cadence at {At} failed -- continuing with the next cadence", boundary);
                    }
                }
            }
        }

        return (cadenceCount, scoredCount);
    }

    /// <summary>
    /// Audit finding F55, redesigned 2026-09-11 per user's own diagnosis: every ratio metric is
    /// built from option chain data that *reacts* to price, so none of it can lead -- confirmed by
    /// the first DynamicHybrid build's own result (ratio-level-primary, 30 trades, +7,343.25, but
    /// selectivity alone, not timing, was doing the work). Price now drives entry; the ratio score
    /// only confirms and exits. Same "no hardcoded metric or value" rule as the first build, same
    /// substitution-not-modification pattern (EntryRuleEvaluator/ExitRuleEvaluator/
    /// ScoreSustainTracker/LiveTradingEngine all remain completely unmodified):
    ///
    /// 1. Magnitude/selectivity: FuturesVwapDeviationZ (the future's own price relative to its own
    ///    volume-weighted average, the most direct zero-lag signal available -- see
    ///    LiveFeatureEngine.ComputeCadence's own comment) ranked against ITS OWN session-so-far
    ///    distribution via SessionRankTracker, same mechanism the first build used for the ratio
    ///    level, just fed a different series. Entry.MinAbsScore/Exit.ExitOnScoreBelowAbs are
    ///    recomputed from that rank every cadence via mutableRulesetOptions, exactly as before.
    /// 2. Direction: sign of FuturesVwapDeviationZ.
    /// 3. Qualifies (nonzero effective score) only when the ratio LEVEL's sign agrees (user's
    ///    explicit confirmation-gate requirement) AND RatioMomentum's sign also agrees (keeps the
    ///    exact exit mechanism already proven to work all session -- the moment either the
    ///    broader ratio level or specifically its momentum turns against the price move, the
    ///    effective score zeros out, tripping ExitOnScoreBelowAbs for an open position the same
    ///    way it already does today).
    /// </summary>
    void ApplyDynamicHybrid(ScoreSnapshot snapshot, SessionRankTracker sessionRankTracker)
    {
        if (snapshot.FuturesVwapDeviationZ is not { } vwapDeviationZ)
        {
            snapshot.CompositeScore = null;
            snapshot.IsWarmedUp = false;
            return;
        }

        // Rank against the session's PRIOR bars only, then add this bar -- external review
        // (2026-09-12) correctly flagged that Add-then-read included this bar's own value in the
        // distribution used to threshold itself. Same-bar inclusion is mild in a single-pass
        // backtest (this bar's ONE value barely moves a growing session distribution), but reading
        // before adding is the honest version of "was this rank clearable BEFORE this bar arrived,"
        // which is what a live/session-rank gate would actually see in real time.
        var current = mutableRulesetOptions!.Current;
        var dynamicMinAbsScore = sessionRankTracker.ValueAtPercentile(dynamicEntryRankCutoff) ?? double.MaxValue;
        var dynamicExitBelowAbs = sessionRankTracker.ValueAtPercentile(dynamicExitRankCutoff) ?? 0.0;
        sessionRankTracker.Add(Math.Abs(vwapDeviationZ));
        mutableRulesetOptions.Current = current with
        {
            Entry = current.Entry with { MinAbsScore = dynamicMinAbsScore },
            Exit = current.Exit with { ExitOnScoreBelowAbs = dynamicExitBelowAbs },
        };

        var priceDirection = Math.Sign(vwapDeviationZ);
        var levelAgrees = snapshot.RatioCompositeScore is { } ratioLevel && Math.Sign(ratioLevel) == priceDirection;
        var momentumAgrees = snapshot.RatioMomentum is { } momentum && Math.Sign(momentum) == priceDirection;
        var qualifies = priceDirection != 0 && levelAgrees && momentumAgrees;

        snapshot.CompositeScore = qualifies ? vwapDeviationZ : 0;
        snapshot.IsWarmedUp = true;
    }

    async Task PersistSnapshotAsync(ScoreSnapshot snapshot, CancellationToken ct)
    {
        await using var scope = backtestScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.ScoreSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Which score a backtest run's entry/exit evaluation is driven by (2026-09-11) -- backtest-only,
/// see <see cref="BacktestRunner"/>'s own doc comment for why this can never leak into the live
/// path. <see cref="OriginalComposite"/> is the default and matches what actually trades live
/// today.
/// </summary>
public enum BacktestScoreSource
{
    OriginalComposite,
    RatioLevel,
    RatioMomentum,

    /// <summary>
    /// Audit finding F55 (2026-09-11): ratio level for direction, gated by a session-rank-relative
    /// (not fixed) strength threshold, filtered by RatioMomentum's sign agreeing with it -- see
    /// BacktestRunner.ApplyDynamicHybrid for the full mechanism. Requires
    /// BacktestRunner's mutableRulesetOptions parameter to be set.
    /// </summary>
    DynamicHybrid,
}

/// <summary>
/// Everything a backtest run over the InMemory trade store produced. <see cref="Performance"/>
/// is the same PerformanceReportBuilder.Build output live paper trades would produce -- no
/// backtest-specific reporting logic exists (see PerformanceReportBuilder's own doc comment).
/// <see cref="Snapshots"/> (2026-09-11, audit finding F55) is every ScoreSnapshot the run
/// produced -- exposed so a caller can derive a threshold from a score's own real distribution
/// (e.g. RatioMomentum's) rather than guessing one, the same way F13's MinAbsScore was originally
/// derived; not something the InMemory store persists past this process exiting, so this is the
/// only place to read it from.
/// </summary>
public sealed record BacktestResult(
    IReadOnlyList<PaperTrade> Trades,
    IReadOnlyList<ScoreSnapshot> Snapshots,
    PerformanceReport Performance,
    int TotalCadences,
    int ScoredCadences);
