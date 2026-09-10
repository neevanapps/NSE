using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public class LiveTradingEngineTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 4);
    static readonly DateOnly NearestExpiry = new(2026, 9, 8);
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    const string SpotToken = "26000";
    const string FutureToken = "68407";
    const string AtmCallToken = "42000"; // strike 24000, priced ~124.74 -- see WarmedFeatureEngineWithAtmCall

    /// <summary>
    /// A recording fake, not a mock library -- this project has no mocking dependency and
    /// these tests only need "did a notification of category X go out," which a list check
    /// answers fine.
    /// </summary>
    sealed class FakeTelegramNotifier : ITelegramNotifier
    {
        public List<(NotificationCategory Category, string Message)> Sent { get; } = [];

        public Task SendAsync(NotificationCategory category, string message, CancellationToken cancellationToken)
        {
            Sent.Add((category, message));
            return Task.CompletedTask;
        }
    }

    /// <summary>Fixed, never-reloading stand-in for the real hot-reloaded config -- these tests assert against TestRulesetConfigs' known values, not whatever's live.</summary>
    sealed class FixedOptions<T>(T value) : IValidatedOptions<T>
    {
        public T Current { get; } = value;
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly ServiceProvider _provider;
        readonly DashboardPushClient _dashboardPush;
        public LiveTradingEngine Engine { get; }
        public FakeTelegramNotifier Telegram { get; } = new();

        public Fixture()
        {
            // The Guid has to be generated once and captured, not called inline inside the
            // configure lambda -- AddDbContext re-invokes that lambda on every new scoped
            // DbContext instantiation, so an inline Guid.NewGuid() would hand every scope
            // (i.e. every WithDbAsync call, and every scope LiveTradingEngine creates
            // internally) a *different* random database, silently making writes in one
            // scope invisible to reads in another. Caught by every "a trade was persisted"
            // assertion below coming back with an empty PaperTrades table.
            var dbName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<NiftySignalDbContext>(o => o.UseInMemoryDatabase(dbName));
            _provider = services.BuildServiceProvider();

            // Never started (StartAsync is never called), so it never actually attempts a
            // connection -- PushTradesChangedAsync's own state check makes every call from
            // the engine under test a harmless no-op, same as a real Dashboard being down.
            _dashboardPush = new DashboardPushClient(Options.Create(new DashboardPushOptions()), NullLogger<DashboardPushClient>.Instance);

            Engine = new LiveTradingEngine(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Telegram,
                _dashboardPush,
                new FixedOptions<RulesetConfig>(TestRulesetConfigs.Default()),
                NullLogger<LiveTradingEngine>.Instance);
        }

        // Resolving the (scoped) DbContext directly from the root provider would hand back
        // the same cached root-scope instance on every call -- disposing it once, as a test
        // naturally does after each assertion block, would leave it unusable for the next
        // one. A fresh scope per call avoids that, and matches how the production code
        // itself resolves NiftySignalDbContext (LiveTradingEngine.EvaluateCadenceAsync via
        // IServiceScopeFactory), so this fixture exercises the same DI shape.
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
    ];

    static MarketDepth Depth(decimal bid, decimal ask) => new(
        Bid1Price: bid, Bid1Qty: 1000, Bid2Price: 0, Bid2Qty: 0, Bid3Price: 0, Bid3Qty: 0, Bid4Price: 0, Bid4Qty: 0, Bid5Price: 0, Bid5Qty: 0,
        Ask1Price: ask, Ask1Qty: 1000, Ask2Price: 0, Ask2Qty: 0, Ask3Price: 0, Ask3Qty: 0, Ask4Price: 0, Ask4Qty: 0, Ask5Price: 0, Ask5Qty: 0);

    static Tick MakeTick(string token, decimal lastPrice, DateTimeOffset at, long? oi = null, MarketDepth? depth = null) => new()
    {
        Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = lastPrice, OpenInterest = oi, Depth = depth,
    };

    static LiveFeatureEngine WarmedFeatureEngineWithAtmCall(DateTimeOffset now)
    {
        var engine = new LiveFeatureEngine(BaseUniverse());
        engine.OnTick(MakeTick(SpotToken, 24000m, now));
        engine.OnTick(MakeTick(FutureToken, 24000m, now));
        // A premium that lands inside the live ruleset's [100,150] band (2026-09-07, narrowed
        // from [150,200]) with room either side -- not tied to a specific BlackScholes solve,
        // just a plausible near-ATM premium for these cadence-evaluation tests.
        engine.OnTick(MakeTick(AtmCallToken, 124.74m, now, oi: 150_000, depth: Depth(bid: 124.24m, ask: 125.24m)));
        return engine;
    }

    static ScoreSnapshot WarmedSnapshot(double score, DateTimeOffset at) => new()
    {
        ComputedAt = at,
        CompositeScore = score,
        IsWarmedUp = true,
        WeightSetVersion = "test-weights-1",
    };

    [Fact]
    public async Task EvaluateCadenceAsync_DoesNothing_WhenCompositeScoreIsNull()
    {
        await using var fixture = new Fixture();
        var now = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngineWithAtmCall(now);
        var snapshot = new ScoreSnapshot { ComputedAt = now, CompositeScore = null, IsWarmedUp = false, WeightSetVersion = "v1" };

        await fixture.Engine.EvaluateCadenceAsync(snapshot, featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));
        Assert.Empty(fixture.Telegram.Sent);
    }

    [Fact]
    public async Task EvaluateCadenceAsync_NoEntry_WhenKillSwitchDisabled()
    {
        await using var fixture = new Fixture();
        await fixture.WithDbAsync(async db =>
        {
            db.KillSwitchStates.Add(new KillSwitchState { EntriesEnabled = false, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test" });
            await db.SaveChangesAsync();
        });

        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngineWithAtmCall(t0);

        // Two cadence ticks 50s apart so the score-sustain requirement (45s) is met --
        // otherwise the sustain check alone would explain a "no entry" result and this
        // test wouldn't actually prove the kill switch is what's blocking it.
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(50)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));
    }

    [Fact]
    public async Task EvaluateCadenceAsync_EntersTrade_WhenScoreSustainedAndStrikeCandidateValid()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngineWithAtmCall(t0);

        // Realistic 15s-spaced cadences (audit finding F12: sustain now also requires a
        // minimum cadence count, not just elapsed time -- two widely-spaced ticks, as this
        // test used before, is exactly the feed-stall scenario that gate exists to reject).
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(30)), featureEngine, CancellationToken.None);

        // Three cadences in, 30s sustained: cadence count already cleared but duration hasn't
        // reached MinScoreSustainedSeconds (45) yet -- must not enter.
        await fixture.WithDbAsync(async db => Assert.Empty(await db.PaperTrades.ToListAsync()));

        // Fourth tick, 45s in: both MinScoreSustainedSeconds (45) and MinScoreSustainedCadences
        // (3) are satisfied -- should enter.
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(45)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.Equal(AtmCallToken, trade.InstrumentToken);
            Assert.Equal(EntryDirection.Bullish, trade.Direction);
            Assert.Equal(70, trade.EntryScore);
            // FillEntry: ask (125.24) + tickSize(0.05) * SlippageTicks(2) = 125.34.
            Assert.Equal(125.34m, trade.EntryPrice);
        });
        Assert.Contains(fixture.Telegram.Sent, s => s.Category == NotificationCategory.TradeEntry);
    }

    static ScoreSnapshot WarmedSnapshotWithOpposingRatioScore(double score, DateTimeOffset at) => new()
    {
        ComputedAt = at,
        CompositeScore = score,
        IsWarmedUp = true,
        WeightSetVersion = "test-weights-1",
        // Strongly bearish and warmed up -- the opposite direction and sign from `score`
        // above, and clearly past any threshold that would matter if this were ever read.
        RatioCompositeScore = -99,
        RatioIsWarmedUp = true,
        RatioWeightSetVersion = "ratio-test-1",
    };

    [Fact]
    public async Task EvaluateCadenceAsync_IgnoresRatioCompositeScore_EvenWhenStronglyOppositeToCompositeScore()
    {
        // Structural-safety proof (weekend build, 2026-09-09): LiveTradingEngine reads only
        // ScoreSnapshot.CompositeScore to decide entries -- a RatioCompositeScore strongly
        // opposite in sign must have zero effect. Same four-cadence sustain sequence as
        // EvaluateCadenceAsync_EntersTrade_WhenScoreSustainedAndStrikeCandidateValid above,
        // and must produce the identical outcome despite the added opposing ratio score.
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var featureEngine = WarmedFeatureEngineWithAtmCall(t0);

        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshotWithOpposingRatioScore(70, t0), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshotWithOpposingRatioScore(70, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshotWithOpposingRatioScore(70, t0.AddSeconds(30)), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshotWithOpposingRatioScore(70, t0.AddSeconds(45)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.Equal(AtmCallToken, trade.InstrumentToken);
            Assert.Equal(EntryDirection.Bullish, trade.Direction);
            Assert.Equal(70, trade.EntryScore);
            Assert.Equal(125.34m, trade.EntryPrice);
        });
    }

    [Fact]
    public async Task EvaluateCadenceAsync_SquareOff_ClosesOpenPosition()
    {
        await using var fixture = new Fixture();
        var entryTime = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);
        var squareOffNow = new DateTimeOffset(2026, 9, 4, 15, 15, 0, Ist);

        await fixture.WithDbAsync(async db =>
        {
            db.PaperTrades.Add(new PaperTrade
            {
                InstrumentToken = AtmCallToken, TradingSymbol = "NIFTY08SEP26C24000", Direction = EntryDirection.Bullish,
                EntryTime = entryTime, EntryPrice = 164.34m, Quantity = 130, EntryScore = 70,
                RulesetVersion = "live-v1-2026-09-04", ScoreWeightsVersion = "test-weights-1",
            });
            await db.SaveChangesAsync();
        });

        var featureEngine = WarmedFeatureEngineWithAtmCall(squareOffNow);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(20, squareOffNow), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.NotNull(trade.ExitTime);
            Assert.Equal(ExitReason.SquareOff, trade.ExitReason);
        });
        Assert.Contains(fixture.Telegram.Sent, s => s.Category == NotificationCategory.Exit);
    }

    [Fact]
    public async Task EvaluateCadenceAsync_PartialBookThenFinalExit_ComputesCorrectBlendedPnl()
    {
        await using var fixture = new Fixture();
        var entryTime = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);

        await fixture.WithDbAsync(async db =>
        {
            db.PaperTrades.Add(new PaperTrade
            {
                InstrumentToken = AtmCallToken, TradingSymbol = "NIFTY08SEP26C24000", Direction = EntryDirection.Bullish,
                EntryTime = entryTime, EntryPrice = 100m, Quantity = 65, EntryScore = 70,
                RulesetVersion = "live-v1-2026-09-04", ScoreWeightsVersion = "test-weights-1",
            });
            await db.SaveChangesAsync();
        });

        // Partial book: +35% profit (>= the 15% PartialBookAtProfitPct threshold).
        var partialNow = entryTime.AddMinutes(10);
        var partialEngine = new LiveFeatureEngine(BaseUniverse());
        partialEngine.OnTick(MakeTick(FutureToken, 24000m, partialNow));
        partialEngine.OnTick(MakeTick(AtmCallToken, 135m, partialNow, depth: Depth(bid: 135m, ask: 135.5m)));
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, partialNow), partialEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var afterPartial = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.True(afterPartial.HasPartiallyBooked);
            Assert.Null(afterPartial.ExitTime); // still open -- only the fraction was booked
            Assert.Equal(134.9m, afterPartial.PartialExitPrice); // 135 - tickSize(0.05)*SlippageTicks(2)
        });

        // Final exit: price drops to 90 -- after a partial book, TrailAfterPartialBook
        // moves the stop to breakeven, so any non-positive profit% now triggers StopLoss.
        var finalNow = entryTime.AddMinutes(20);
        var finalEngine = new LiveFeatureEngine(BaseUniverse());
        finalEngine.OnTick(MakeTick(FutureToken, 24000m, finalNow));
        finalEngine.OnTick(MakeTick(AtmCallToken, 90m, finalNow, depth: Depth(bid: 90m, ask: 90.5m)));
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, finalNow), finalEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            var closed = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.NotNull(closed.ExitTime);
            Assert.Equal(ExitReason.StopLoss, closed.ExitReason);
            Assert.Equal(89.9m, closed.ExitPrice); // 90 - tickSize(0.05)*SlippageTicks(2)

            // Hand-computed expectation (lot size 65, PartialBookFraction 0.5 -> 32/33
            // split, brokerage 20/order, 3 orders total: entry + partial exit + final exit):
            //   partialGross   = 134.9 * 32 = 4316.8
            //   finalGross     = 89.9 * 33  = 2966.7
            //   grossPnl       = (4316.8 + 2966.7) - (100 * 65) = 783.5
            //   entryNetValue  = (100*65) + 20 = 6520
            //   partialNetVal  = 4316.8 - 20 = 4296.8
            //   finalNetVal    = 2966.7 - 20 = 2946.7
            //   netPnl         = (4296.8 + 2946.7) - 6520 = 723.5
            Assert.Equal(783.5m, closed.GrossPnl);
            Assert.Equal(723.5m, closed.NetPnl);
        });
    }

    [Fact]
    public async Task EvaluateCadenceAsync_TracksExcursions_AtTheirExtremes_NotTheLatestMark()
    {
        await using var fixture = new Fixture();
        var entryTime = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);

        await fixture.WithDbAsync(async db =>
        {
            db.PaperTrades.Add(new PaperTrade
            {
                InstrumentToken = AtmCallToken, TradingSymbol = "NIFTY08SEP26C24000", Direction = EntryDirection.Bullish,
                EntryTime = entryTime, EntryPrice = 100m, Quantity = 130, EntryScore = 70,
                RulesetVersion = "live-v1-2026-09-04", ScoreWeightsVersion = "test-weights-1",
            });
            await db.SaveChangesAsync();
        });

        // Marks are the bid (that's what an exit would actually fill at): 110 -> 92 -> 104.
        // Against a 100 entry that's +10%, -8%, +4%. None of the three is both extremes, so a
        // "last value wins" bug would fail this: MFE must hold +10 and MAE must hold -8 even
        // though the final observation is +4.
        foreach (var (offsetSeconds, bid, ask) in new[] { (15, 110m, 110.5m), (30, 92m, 92.5m), (45, 104m, 104.5m) })
        {
            var at = entryTime.AddSeconds(offsetSeconds);
            var engine = new LiveFeatureEngine(BaseUniverse());
            engine.OnTick(MakeTick(SpotToken, 24000m, at));
            engine.OnTick(MakeTick(FutureToken, 24000m, at));
            engine.OnTick(MakeTick(AtmCallToken, bid, at, depth: Depth(bid: bid, ask: ask)));

            // Score held low enough to avoid tripping an exit -- but above ExitOnScoreBelowAbs
            // (30), or ScoreDecay would close the position before the third mark lands.
            await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(35, at), engine, CancellationToken.None);
        }

        await fixture.WithDbAsync(async db =>
        {
            var trade = Assert.Single(await db.PaperTrades.ToListAsync());
            Assert.Null(trade.ExitTime); // still open -- this test is about excursions, not exits
            Assert.Equal(10.0, trade.MaxFavourableExcursionPct!.Value, precision: 4);
            Assert.Equal(-8.0, trade.MaxAdverseExcursionPct!.Value, precision: 4);
        });
    }

    [Fact]
    public async Task EvaluateCadenceAsync_NoEntry_WhenDailyProfitTargetAlreadyReached()
    {
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);

        // Target is 30% of 50,000 capital = 15,000. One closed trade at 15,100 clears it, so
        // the day is done taking new risk even though every other entry condition is satisfied.
        await fixture.WithDbAsync(async db =>
        {
            db.PaperTrades.Add(new PaperTrade
            {
                InstrumentToken = AtmCallToken, TradingSymbol = "NIFTY08SEP26C24000", Direction = EntryDirection.Bullish,
                EntryTime = t0.AddHours(-1), EntryPrice = 100m, Quantity = 130, EntryScore = 70,
                ExitTime = t0.AddMinutes(-30), ExitPrice = 150m, ExitReason = ExitReason.PartialBook,
                GrossPnl = 15_250m, NetPnl = 15_100m,
                RulesetVersion = "live-v1-2026-09-04", ScoreWeightsVersion = "test-weights-1",
            });
            await db.SaveChangesAsync();
        });

        var featureEngine = WarmedFeatureEngineWithAtmCall(t0);

        // Two ticks 50s apart so the 45s sustain requirement is genuinely met -- otherwise
        // sustain alone would explain the absence of a new trade.
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(50)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            // Still just the pre-existing closed trade -- no new entry was opened.
            var trades = await db.PaperTrades.ToListAsync();
            Assert.Single(trades);
            Assert.NotNull(trades[0].ExitTime);
        });
        Assert.DoesNotContain(fixture.Telegram.Sent, s => s.Category == NotificationCategory.TradeEntry);
    }

    [Fact]
    public async Task EvaluateCadenceAsync_NoEntry_WhenCommittedCapitalWouldExceedTotal_EvenUnderMaxConcurrentPositions()
    {
        // Audit finding F48 (2026-09-10): two open positions at a high entry price (150 each,
        // qty 130 -> Rs 19,500 apiece, Rs 39,000 committed) leave MaxConcurrentPositions (3)
        // unbroken -- only 2 of 3 slots used, so the position-COUNT gate alone would let a
        // third entry through. But a third position at this test's own ~125.34 fill x 130 qty
        // (~Rs 16,294) would bring total committed capital to ~Rs 55,294, over Capital.Total
        // (Rs 50,000) -- proving the capital-SUM gate is the one actually doing the rejecting
        // here, not MaxConcurrentPositions (which this scenario deliberately never trips).
        await using var fixture = new Fixture();
        var t0 = new DateTimeOffset(2026, 9, 4, 10, 0, 0, Ist);

        await fixture.WithDbAsync(async db =>
        {
            for (var i = 0; i < 2; i++)
            {
                db.PaperTrades.Add(new PaperTrade
                {
                    InstrumentToken = $"OPEN-{i}", TradingSymbol = $"NIFTY08SEP26C2{i}000", Direction = EntryDirection.Bullish,
                    EntryTime = t0.AddHours(-1), EntryPrice = 150m, Quantity = 130, EntryScore = 70,
                    RulesetVersion = "live-v1-2026-09-04", ScoreWeightsVersion = "test-weights-1",
                });
            }

            await db.SaveChangesAsync();
        });

        var featureEngine = WarmedFeatureEngineWithAtmCall(t0);

        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(15)), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(30)), featureEngine, CancellationToken.None);
        await fixture.Engine.EvaluateCadenceAsync(WarmedSnapshot(70, t0.AddSeconds(45)), featureEngine, CancellationToken.None);

        await fixture.WithDbAsync(async db =>
        {
            // Still just the two pre-existing open positions -- the third was rejected.
            var trades = await db.PaperTrades.ToListAsync();
            Assert.Equal(2, trades.Count);
            Assert.All(trades, t => Assert.Null(t.ExitTime));
        });
        Assert.DoesNotContain(fixture.Telegram.Sent, s => s.Category == NotificationCategory.TradeEntry);
    }
}
