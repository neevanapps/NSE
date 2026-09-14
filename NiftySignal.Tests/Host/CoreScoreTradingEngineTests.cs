using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Notifications;
using NiftySignal.Persistence;
using NiftySignal.Rules;
using NiftySignal.Tests.Rules;

namespace NiftySignal.Tests.Host;

/// <summary>
/// Batch 5 (2026-09-13, live-wiring plan) -- CoreScoreHysteresisTradingEngine/
/// CoreScoreCrossoverTradingEngine, mirroring LiveTradingEngineTests' own fixture shape (in-memory
/// DB, FakeTelegramNotifier, FixedOptions). The plan's own explicit test requirements: per-strategy
/// risk-gate isolation (one strategy's daily-loss breach must not block the other), MaxConcurrentPositions=1
/// enforcement, and confirmation these engines act purely on CoreScoreSnapshot -- the last of
/// which is structurally guaranteed here (EvaluateCadenceAsync's own signature takes a
/// CoreScoreSnapshot, never a ScoreSnapshot, so there is no ScoreSnapshot.CompositeScore for
/// either engine to even accidentally read), not just behaviorally tested.
/// </summary>
public class CoreScoreTradingEngineTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 4);
    static readonly DateOnly NearestExpiry = new(2026, 9, 8);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    const string SpotToken = "26000";
    const string FutureToken = "68407";
    const string AtmCallToken = "42000"; // strike 24000, priced ~124.74 -- lands in [100,150]
    const string AtmPutToken = "43000"; // strike 24000, same premium band

    sealed class FakeTelegramNotifier : ITelegramNotifier
    {
        public List<(NotificationCategory Category, string Message)> Sent { get; } = [];

        public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken)
        {
            Sent.Add((category, message));
            return Task.CompletedTask;
        }
    }

    sealed class FixedOptions<T>(T value) : IValidatedOptions<T>
    {
        public T Current { get; } = value;
    }

    /// <summary>Captures formatted log lines so ShadowMode's "[SHADOW] Would ENTER/EXIT" logging (Batch 6) can be asserted on directly, since nothing reaches the DB or Telegram in that mode.</summary>
    sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>Both new engines share ONE in-memory DB (and one Capital.Total) -- required for the cross-strategy capital/risk-isolation tests below to mean anything.</summary>
    sealed class Fixture : IAsyncDisposable
    {
        readonly ServiceProvider _provider;
        readonly DashboardPushClient _dashboardPush;
        public CoreScoreHysteresisTradingEngine Hysteresis { get; }
        public CoreScoreCrossoverTradingEngine Crossover { get; }
        public FakeTelegramNotifier Telegram { get; } = new();
        public CapturingLogger<CoreScoreHysteresisTradingEngine> HysteresisLog { get; } = new();
        public CapturingLogger<CoreScoreCrossoverTradingEngine> CrossoverLog { get; } = new();

        public Fixture(CoreScoreHysteresisConfig? hysteresisConfig = null, CoreScoreCrossoverConfig? crossoverConfig = null)
        {
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<NiftySignalDbContext>(o => o.UseInMemoryDatabase(dbName));
            _provider = services.BuildServiceProvider();

            _dashboardPush = new DashboardPushClient(Options.Create(new DashboardPushOptions()), NullLogger<DashboardPushClient>.Instance);

            var rulesetOptions = new FixedOptions<RulesetConfig>(TestRulesetConfigs.Default());
            Hysteresis = new CoreScoreHysteresisTradingEngine(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Telegram,
                _dashboardPush,
                rulesetOptions,
                new FixedOptions<CoreScoreHysteresisConfig>(hysteresisConfig ?? TestCoreScoreConfigs.Hysteresis()),
                HysteresisLog);
            Crossover = new CoreScoreCrossoverTradingEngine(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Telegram,
                _dashboardPush,
                rulesetOptions,
                new FixedOptions<CoreScoreCrossoverConfig>(crossoverConfig ?? TestCoreScoreConfigs.Crossover()),
                CrossoverLog);
        }

        public async Task WithDbAsync(Func<NiftySignalDbContext, Task> action)
        {
            using var scope = _provider.CreateScope();
            await action(scope.ServiceProvider.GetRequiredService<NiftySignalDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            _provider.Dispose();
            await _dashboardPush.DisposeAsync();
        }
    }

    static List<Instrument> BaseUniverse() =>
    [
        new()
        {
            Token = SpotToken, Exchange = Exchange.Nse, TradingSymbol = "Nifty 50",
            InstrumentType = InstrumentType.Index, Underlying = "NIFTY", LotSize = 1, TickSize = 0.05m, AsOfDate = AsOfDate,
        },
        new()
        {
            Token = FutureToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY08SEP26F",
            InstrumentType = InstrumentType.Future, ExpiryDate = NearestExpiry, Underlying = "NIFTY",
            LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
        },
        new()
        {
            Token = AtmCallToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY08SEP26C24000",
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, StrikePrice = 24000m,
            ExpiryDate = NearestExpiry, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
        },
        new()
        {
            Token = AtmPutToken, Exchange = Exchange.Nfo, TradingSymbol = "NIFTY08SEP26P24000",
            InstrumentType = InstrumentType.Option, OptionType = OptionType.Put, StrikePrice = 24000m,
            ExpiryDate = NearestExpiry, Underlying = "NIFTY", LotSize = 65, TickSize = 0.05m, AsOfDate = AsOfDate,
        },
    ];

    static MarketDepth Depth(decimal bid, decimal ask) => new(
        Bid1Price: bid, Bid1Qty: 1000, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask, Ask1Qty: 1000, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    static Tick MakeTick(string token, decimal lastPrice, DateTimeOffset at, long? oi = null, MarketDepth? depth = null) => new()
    {
        Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = lastPrice, OpenInterest = oi, Depth = depth,
    };

    static LiveFeatureEngine WarmedFeatureEngine(DateTimeOffset now)
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 24000m, now));
        engine.OnTick(MakeTick(FutureToken, 24000m, now));
        engine.OnTick(MakeTick(AtmCallToken, 124.74m, now, oi: 150_000, depth: Depth(bid: 124.24m, ask: 125.24m)));
        engine.OnTick(MakeTick(AtmPutToken, 124.74m, now, oi: 150_000, depth: Depth(bid: 124.24m, ask: 125.24m)));
        return engine;
    }

    static CoreScoreSnapshot HysteresisSnapshot(double score, DateTimeOffset at) => new()
    {
        ComputedAt = at,
        CoreScore = score,
        IsWarmedUp = true,
        WeightSetVersion = "test-core-weights-1",
    };

    static CoreScoreSnapshot CrossoverSnapshot(double fast, double slow, DateTimeOffset at) => new()
    {
        ComputedAt = at,
        CoreScoreFast = fast,
        CoreScoreSlow = slow,
        IsWarmedUp = true,
        WeightSetVersion = "test-core-weights-1",
    };

    // ==================== Hysteresis ====================

    [Fact]
    public async Task Hysteresis_EntersTrade_WhenScoreAtOrAboveThreshold()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.Equal(StrategyId.CoreScoreHysteresis, trade.StrategyId);
            Assert.Equal(AtmCallToken, trade.InstrumentToken);
            Assert.Equal(EntryDirection.Bullish, trade.Direction);
            // FillEntry: ask (125.24) + tickSize(0.05) * SlippageTicks(2) = 125.34.
            Assert.Equal(125.34m, trade.EntryPrice);
        });
        Assert.Contains(fixture.Telegram.Sent, s => s.Category == NotificationCategory.TradeEntry && s.Message.Contains("CoreScoreHysteresis"));
    }

    [Fact]
    public async Task Hysteresis_DoesNotEnter_WhenScoreBelowThreshold()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(29.9, t0), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));
    }

    [Fact]
    public async Task Hysteresis_ExitsOnlyOnce_ScoreCrossesToTheOppositeThreshold_NotOnAPlainZeroCross()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0), featureEngine, CancellationToken.None);

        // Score drops to a mild negative (-10) -- a plain zero-cross, but NOT past -30. The
        // hysteresis band must hold the position open here (this is the whole point of the band
        // over a symmetric ScoreFlip).
        var t1 = t0.AddSeconds(15);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(-10, t1), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db => Assert.Null((await db.PaperTrades.SingleAsync()).ExitTime));

        // Score crosses below -30 -- now it must exit, reason ScoreInvalidated. -31 also clears
        // the entry threshold in the new (Bearish) direction, so -- same as LiveTradingEngine's
        // own "a position closing this tick frees the slot for a fresh entry the same tick"
        // design -- a second, Bearish position opens in the same cadence right after.
        var t2 = t1.AddSeconds(15);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(-31, t2), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db =>
        {
            var trades = await db.PaperTrades.OrderBy(p => p.EntryTime).ToListAsync();
            Assert.Equal(2, trades.Count);
            Assert.NotNull(trades[0].ExitTime);
            Assert.Equal(ExitReason.ScoreInvalidated, trades[0].ExitReason);
            Assert.Equal(EntryDirection.Bearish, trades[1].Direction);
            Assert.Null(trades[1].ExitTime);
        });
    }

    [Fact]
    public async Task Hysteresis_DoesNotOpenASecondPosition_WhileOneIsAlreadyOpen()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0), featureEngine, CancellationToken.None);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0.AddSeconds(30)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db => Assert.Single(await db.PaperTrades.ToListAsync()));
    }

    // ==================== Crossover ====================

    [Fact]
    public async Task Crossover_EntersOnFirstCross_ThenFlipsOnTheNextOppositeCross()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        // First warmed-up reading establishes the baseline (diffSign=+1), not a cross yet --
        // matches CoreScoreCrossoverRules.Observe's own null-previous-sign branch.
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: 5, slow: 2, t0), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));

        // Sign flips to -1 -- a genuine crossover, bearish. First-ever entry (nothing open to close).
        var t1 = t0.AddSeconds(15);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: -3, slow: 1, t1), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.Equal(StrategyId.CoreScoreCrossover, trade.StrategyId);
            Assert.Equal(EntryDirection.Bearish, trade.Direction);
            Assert.Equal(AtmPutToken, trade.InstrumentToken);
            Assert.Null(trade.ExitTime);
        });

        // Sign flips back to +1 -- close the put (CrossoverFlip), open the call, same cadence.
        var t2 = t1.AddSeconds(15);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: 4, slow: 1, t2), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db =>
        {
            var trades = await db.PaperTrades.OrderBy(p => p.EntryTime).ToListAsync();
            Assert.Equal(2, trades.Count);
            Assert.Equal(ExitReason.CrossoverFlip, trades[0].ExitReason);
            Assert.Equal(EntryDirection.Bullish, trades[1].Direction);
            Assert.Equal(AtmCallToken, trades[1].InstrumentToken);
            Assert.Null(trades[1].ExitTime);
        });
    }

    [Fact]
    public async Task Crossover_NeverHoldsMoreThanOnePosition_AcrossAFlip()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(5, 2, t0), featureEngine, CancellationToken.None);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(-3, 1, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(4, 1, t0.AddSeconds(30)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var openCount = await db.PaperTrades.CountAsync(p => p.ExitTime == null && p.StrategyId == StrategyId.CoreScoreCrossover);
            Assert.Equal(1, openCount);
        });
    }

    [Fact]
    public async Task Crossover_SquareOff_ClosesOpenPosition_AndSkipsTheCrossoverCheckThatCadence()
    {
        await using var fixture = new Fixture();
        var entryTime = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var squareOffNow = new DateTimeOffset(2026, 9, 4, 15, 15, 0, Ist);
        var featureEngine = WarmedFeatureEngine(squareOffNow);

        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(5, 2, entryTime), featureEngine, CancellationToken.None);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(-3, 1, entryTime.AddSeconds(15)), featureEngine, CancellationToken.None);

        // At/after SquareOffTime (15:15): closes regardless of what the crossover would say --
        // even though fast/slow here would otherwise register ANOTHER opposite cross.
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(6, 2, squareOffNow), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var trade = await db.PaperTrades.SingleAsync();
            Assert.Equal(ExitReason.SquareOff, trade.ExitReason);
        });
    }

    // ==================== Per-strategy risk-gate isolation ====================

    [Fact]
    public async Task DailyLossLimitBreach_OnOneStrategy_DoesNotBlockTheOtherStrategy()
    {
        // The direct test of the "per-strategy independent risk limits" decision
        // (docs/replication_plan.md) -- Hysteresis hitting its OWN (deliberately tiny)
        // daily-loss cap must have zero effect on Crossover's ability to trade, and vice versa
        // is implied by the same StrategyId-scoped query shape both engines share.
        await using var fixture = new Fixture(hysteresisConfig: TestCoreScoreConfigs.Hysteresis(maxDailyLossPct: 0.01));
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        // Hysteresis enters, then immediately gets invalidated at a loss (mark price unchanged,
        // but a real BrokeragePerOrder cost on both legs guarantees a small realised loss).
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0), featureEngine, CancellationToken.None);
        var t1 = t0.AddSeconds(15);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(-31, t1), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var closed = await db.PaperTrades.Where(p => p.StrategyId == StrategyId.CoreScoreHysteresis).SingleAsync();
            Assert.NotNull(closed.ExitTime);
            Assert.True(closed.NetPnl < 0, "Expected a small realised loss from brokerage alone.");
        });

        // Hysteresis's own daily loss limit (0.01% of 50,000 = 5 rupees) is now breached by the
        // brokerage-only loss above -- confirm it can no longer enter.
        var t2 = t1.AddSeconds(15);
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t2), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db =>
        {
            var hysteresisTrades = await db.PaperTrades.CountAsync(p => p.StrategyId == StrategyId.CoreScoreHysteresis);
            Assert.Equal(1, hysteresisTrades); // still just the one closed trade -- no new entry
        });

        // Crossover, sharing the same DB/capital pool but its own (generous) risk limits, must
        // still be free to trade.
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(5, 2, t0), featureEngine, CancellationToken.None);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(-3, 1, t1), featureEngine, CancellationToken.None);
        await fixture.WithDbAsync(async db =>
        {
            var crossoverOpen = await db.PaperTrades.CountAsync(p => p.StrategyId == StrategyId.CoreScoreCrossover && p.ExitTime == null);
            Assert.Equal(1, crossoverOpen);
        });
    }

    // ==================== ShadowMode (Batch 6, 2026-09-14) ====================

    [Fact]
    public async Task Hysteresis_ShadowMode_EntersAndExits_WithoutWritingAnyDbRowOrNotification()
    {
        await using var fixture = new Fixture(hysteresisConfig: TestCoreScoreConfigs.Hysteresis(shadowMode: true));
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0), featureEngine, CancellationToken.None);
        Assert.Contains(fixture.HysteresisLog.Messages, m => m.Contains("[SHADOW]") && m.Contains("Would ENTER"));

        // A second qualifying cadence must NOT open a second shadow position -- proves
        // MaxConcurrentPositions=1 is still enforced against the in-memory _shadowPosition, not
        // just against (an always-empty, in shadow mode) db.PaperTrades.
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(30, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        Assert.Single(fixture.HysteresisLog.Messages, m => m.Contains("Would ENTER"));

        // Score crosses below -30 -- the open shadow position must exit.
        await fixture.Hysteresis.EvaluateCadenceAsync(HysteresisSnapshot(-31, t0.AddSeconds(30)), featureEngine, CancellationToken.None);
        Assert.Contains(fixture.HysteresisLog.Messages, m => m.Contains("[SHADOW]") && m.Contains("Would EXIT"));

        // A fresh entry should be possible again once the shadow position is cleared (the Bearish
        // reentry the real-mode test also exercises) -- proves _shadowPosition was actually reset
        // to null on exit, not left stuck "open" forever.
        Assert.Equal(2, fixture.HysteresisLog.Messages.Count(m => m.Contains("Would ENTER")));

        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));
        Assert.Empty(fixture.Telegram.Sent);
    }

    [Fact]
    public async Task Crossover_ShadowMode_FlipsAcrossCrossovers_WithoutWritingAnyDbRowOrNotification()
    {
        await using var fixture = new Fixture(crossoverConfig: TestCoreScoreConfigs.Crossover(shadowMode: true));
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngine(t0);

        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: 5, slow: 2, t0), featureEngine, CancellationToken.None);
        Assert.DoesNotContain(fixture.CrossoverLog.Messages, m => m.Contains("Would ENTER"));

        var t1 = t0.AddSeconds(15);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: -3, slow: 1, t1), featureEngine, CancellationToken.None);
        Assert.Contains(fixture.CrossoverLog.Messages, m => m.Contains("[SHADOW]") && m.Contains("Would ENTER") && m.Contains("Bearish"));

        // Flip back -- must close the (in-memory) put and open a call, proving the shadow position's
        // own InstrumentToken (needed by CloseAsync's TryGetLatestQuote) round-trips correctly.
        var t2 = t1.AddSeconds(15);
        await fixture.Crossover.EvaluateCadenceAsync(CrossoverSnapshot(fast: 4, slow: 1, t2), featureEngine, CancellationToken.None);
        Assert.Contains(fixture.CrossoverLog.Messages, m => m.Contains("[SHADOW]") && m.Contains("Would EXIT"));
        Assert.Contains(fixture.CrossoverLog.Messages, m => m.Contains("[SHADOW]") && m.Contains("Would ENTER") && m.Contains("Bullish"));

        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));
        Assert.Empty(fixture.Telegram.Sent);
    }
}
