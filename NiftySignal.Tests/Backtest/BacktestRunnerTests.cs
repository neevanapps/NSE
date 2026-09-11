using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Backtest;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Scoring;
using NiftySignal.Tests.Rules;

namespace NiftySignal.Tests.Backtest;

/// <summary>
/// Audit finding F32's runner, tested end to end through the same
/// BacktestTickSource -> LiveFeatureEngine -> LiveTradingEngine -> PerformanceReportBuilder
/// pipeline it actually drives -- not by asserting against a hand-built ScoreSnapshot the way
/// LiveTradingEngineTests can, since BacktestRunner constructs and owns LiveFeatureEngine itself.
/// </summary>
public sealed class BacktestRunnerTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    const string SpotToken = "26000";
    const string FutureToken = "68407";
    const string CallToken = "42637";
    const string PutToken = "42638";

    static List<Instrument> Universe(DateOnly asOfDate, DateOnly nearestExpiry) =>
    [
        new()
        {
            Token = SpotToken, Exchange = Exchange.Nse, TradingSymbol = "Nifty 50",
            InstrumentType = InstrumentType.Index, Underlying = "NIFTY", LotSize = 1, TickSize = 0.05m, AsOfDate = asOfDate,
        },
        new()
        {
            Token = FutureToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTYFUT",
            InstrumentType = InstrumentType.Future, ExpiryDate = nearestExpiry, Underlying = "NIFTY",
            LotSize = 65, TickSize = 0.05m, AsOfDate = asOfDate,
        },
        new()
        {
            Token = CallToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY23950CE",
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 23950m,
            ExpiryDate = nearestExpiry, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = asOfDate,
        },
        new()
        {
            Token = PutToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY23950PE",
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Put, StrikePrice = 23950m,
            ExpiryDate = nearestExpiry, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = asOfDate,
        },
    ];

    static MarketDepth Depth(long bidQty, long askQty, decimal bid, decimal ask) => new(
        Bid1Price: bid, Bid1Qty: bidQty, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask, Ask1Qty: askQty, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    static Tick MakeTick(string token, decimal lastPrice, DateTimeOffset at, long? oi = null, MarketDepth? depth = null) => new()
    {
        Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = lastPrice, OpenInterest = oi, Depth = depth,
    };

    static NiftySignalDbContext NewRealTicksDb() => new(
        new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static (IServiceScopeFactory ScopeFactory, LiveTradingEngine TradingEngine, DashboardPushClient DashboardPush) BuildTradingEngine(RulesetConfig config)
    {
        // The name must be captured once, not generated inline inside the lambda -- AddDbContext
        // re-invokes that lambda on every new scoped DbContext instantiation, so an inline
        // Guid.NewGuid() would hand every scope a different random database (same pitfall
        // LiveTradingEngineTests.Fixture's own comment documents).
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<NiftySignalDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        var dashboardPush = new DashboardPushClient(Options.Create(new DashboardPushOptions()), NullLogger<DashboardPushClient>.Instance);
        var tradingEngine = new LiveTradingEngine(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new NoOpTelegramNotifier(),
            dashboardPush,
            new FixedOptions<RulesetConfig>(config),
            NullLogger<LiveTradingEngine>.Instance);

        return (provider.GetRequiredService<IServiceScopeFactory>(), tradingEngine, dashboardPush);
    }

    [Fact]
    public async Task RunAsync_SkipsADayWithNoResolvedInstruments_ButStillProcessesOtherDaysInRange()
    {
        var emptyDay = new DateOnly(2026, 9, 7); // Monday, deliberately given zero instruments -- simulates a missing-data day
        var dataDay = new DateOnly(2026, 9, 8);
        var nearestExpiry = new DateOnly(2026, 9, 11);

        await using var realTicksDb = NewRealTicksDb();
        realTicksDb.Instruments.AddRange(Universe(dataDay, nearestExpiry));

        // A single instant, ticked once, on the data day only -- every instrument ticks at the
        // exact same timestamp, so the tick-driven clock loop produces exactly one cadence
        // (nextSample and nextCadence both start and end at that one instant).
        var at = new DateTimeOffset(dataDay.ToDateTime(new TimeOnly(9, 30)), Ist);
        realTicksDb.Ticks.AddRange(
            MakeTick(SpotToken, 23950m, at),
            MakeTick(FutureToken, 23960m, at));
        await realTicksDb.SaveChangesAsync();

        var (scopeFactory, tradingEngine, dashboardPush) = BuildTradingEngine(TestRulesetConfigs.Default());
        try
        {
            var runner = new BacktestRunner(realTicksDb, scopeFactory, tradingEngine);

            var result = await runner.RunAsync(emptyDay, dataDay, CancellationToken.None);

            Assert.Equal(1, result.TotalCadences);
        }
        finally
        {
            await dashboardPush.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_PersistsScoreSnapshots_ToItsOwnIsolatedStore_NeverTheRealReadOnlyDb()
    {
        var day = new DateOnly(2026, 9, 8);
        var nearestExpiry = new DateOnly(2026, 9, 11);

        await using var realTicksDb = NewRealTicksDb();
        realTicksDb.Instruments.AddRange(Universe(day, nearestExpiry));
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 30)), Ist);
        realTicksDb.Ticks.AddRange(
            MakeTick(SpotToken, 23950m, at),
            MakeTick(FutureToken, 23960m, at));
        await realTicksDb.SaveChangesAsync();

        var (scopeFactory, tradingEngine, dashboardPush) = BuildTradingEngine(TestRulesetConfigs.Default());
        try
        {
            var runner = new BacktestRunner(realTicksDb, scopeFactory, tradingEngine);

            var result = await runner.RunAsync(day, day, CancellationToken.None);

            Assert.Equal(1, result.TotalCadences);

            using var backtestScope = scopeFactory.CreateScope();
            var backtestDb = backtestScope.ServiceProvider.GetRequiredService<NiftySignalDbContext>();
            Assert.Equal(1, await backtestDb.ScoreSnapshots.CountAsync());

            // The read-only "real" side must never see a write from this run.
            Assert.Equal(0, await realTicksDb.ScoreSnapshots.CountAsync());
            Assert.Equal(0, await realTicksDb.PaperTrades.CountAsync());
        }
        finally
        {
            await dashboardPush.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_RecordsAPaperTrade_WhenReplayedTicksProduceAGenuinelyWarmedUpQualifyingScore()
    {
        var day = new DateOnly(2026, 9, 8);
        var nearestExpiry = new DateOnly(2026, 9, 11);

        await using var realTicksDb = NewRealTicksDb();
        realTicksDb.Instruments.AddRange(Universe(day, nearestExpiry));

        // DepthImbalance is the only nonzero-weight component in this test's ScoreWeights (below)
        // -- it needs 5 minutes of span (FeatureWindowLengths.DepthImbalance) before its rolling
        // window reports warmed up, so every strike is ticked every 15s across 10 minutes:
        // comfortably past warm-up, then comfortably past the sustain gate (45s/3 cadences) too.
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 30)), Ist);
        var cadenceCount = 40; // 40 * 15s = 10 minutes
        for (var i = 0; i < cadenceCount; i++)
        {
            var at = start.AddSeconds(i * 15);
            // Monotonically ramping, not a near-constant value: a rolling window fed an almost
            // perfectly flat series has a near-zero stddev, so its z-score is dominated by
            // floating-point noise rather than a real signal (confirmed empirically -- an
            // earlier "constant strong imbalance" version of this test produced a wildly
            // swinging, effectively random-signed score for exactly this reason). Ramping keeps
            // genuine variance in the window while keeping the *current* cadence always at the
            // high end of what the window has seen so far, for a reliably positive z-score.
            var qty = 20_000 + (i * 3_000);

            realTicksDb.Ticks.AddRange(
                MakeTick(SpotToken, 23950m, at),
                MakeTick(FutureToken, 23960m, at),
                // Call: increasingly bid-skewed (aggressive buying ramping up) -- callImbalance -> +1.
                MakeTick(CallToken, 120m, at, oi: 200_000, depth: Depth(bidQty: qty, askQty: 10, bid: 119.5m, ask: 120.5m)),
                // Put: increasingly ask-skewed (aggressive selling ramping up) -- putImbalance ->
                // -1, so ComputeDepthImbalance's callImbalance - putImbalance comes out strongly
                // positive (bullish), per its own doc comment (audit finding F2).
                MakeTick(PutToken, 120m, at, oi: 200_000, depth: Depth(bidQty: 10, askQty: qty, bid: 119.5m, ask: 120.5m)));
        }

        await realTicksDb.SaveChangesAsync();

        // Isolate DepthImbalance as the sole driver of the composite score -- every other
        // component is weight-0 (and therefore optional, per CompositeScoreCalculator.IsOptional),
        // so this test doesn't need to organically warm up all fourteen components (several have
        // 30-minute-plus windows) just to prove BacktestRunner's own wiring is correct.
        var testWeights = new ScoreWeights(
            Version: "test-weights-backtest-depth-only",
            OiBuildupNet: 0, Pcr: 0, FuturesBasis: 0, IvSkew: 0, PriceMomentum: 0, DepthImbalance: 1.0,
            VixChange: 0, GammaExposure: 0, VolumePcr: 0, SpreadRatio: 0, VannaExposure: 0, CharmExposure: 0,
            CvdProxy: 0, StraddleRichness: 0);

        var ruleset = TestRulesetConfigs.Default(); // MinAbsScore 55, sustain 45s/3 cadences, premium [100,150], spread <=2%, OI >= 100k
        var (scopeFactory, tradingEngine, dashboardPush) = BuildTradingEngine(ruleset);
        try
        {
            var runner = new BacktestRunner(realTicksDb, scopeFactory, tradingEngine, new FixedOptions<ScoreWeights>(testWeights));

            var result = await runner.RunAsync(day, day, CancellationToken.None);

            Assert.Equal(cadenceCount, result.TotalCadences);
            Assert.NotEmpty(result.Trades);
            var trade = result.Trades[0];
            Assert.Equal(EntryDirection.Bullish, trade.Direction);
            Assert.Equal("test-ruleset-1", trade.RulesetVersion);
        }
        finally
        {
            await dashboardPush.DisposeAsync();
        }
    }
}
