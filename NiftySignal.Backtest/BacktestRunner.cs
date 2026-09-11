using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NiftySignal.Domain.Entities;
using NiftySignal.Host;
using NiftySignal.Persistence;
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
/// </summary>
public sealed class BacktestRunner(
    NiftySignalDbContext realTicksDb,
    IServiceScopeFactory backtestScopeFactory,
    LiveTradingEngine tradingEngine,
    IValidatedOptions<ScoreWeights>? scoreWeightsOptions = null,
    ILogger? logger = null)
{
    static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);
    static readonly TimeSpan CadenceInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan IstOffset = TimeSpan.FromHours(5.5);

    public async Task<BacktestResult> RunAsync(DateOnly fromDate, DateOnly toDate, CancellationToken ct)
    {
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

        return new BacktestResult(trades, PerformanceReportBuilder.Build(trades), totalCadences, totalScoredCadences);
    }

    async Task<(int Cadences, int Scored)> RunDayAsync(DateOnly day, CancellationToken ct)
    {
        var dayStartUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), IstOffset).ToUniversalTime();
        var dayEndUtc = dayStartUtc.AddDays(1);

        var instruments = await realTicksDb.Instruments.Where(i => i.AsOfDate == day).ToListAsync(ct);
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
                    if (snapshot.CompositeScore is not null)
                    {
                        scoredCount++;
                    }

                    await PersistSnapshotAsync(snapshot, ct);

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

    async Task PersistSnapshotAsync(ScoreSnapshot snapshot, CancellationToken ct)
    {
        await using var scope = backtestScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
        db.ScoreSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Everything a backtest run over the InMemory trade store produced. <see cref="Performance"/>
/// is the same PerformanceReportBuilder.Build output live paper trades would produce -- no
/// backtest-specific reporting logic exists (see PerformanceReportBuilder's own doc comment).
/// </summary>
public sealed record BacktestResult(
    IReadOnlyList<PaperTrade> Trades,
    PerformanceReport Performance,
    int TotalCadences,
    int ScoredCadences);
