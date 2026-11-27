using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Pricing;

/// <summary>Today's synthetic session, persisted by the Observer's own recovery from synthetic ticks (so every sidecar the Dashboard reads is written by the production code path).</summary>
sealed class SyntheticSession
{
    readonly DbContextOptions<AdaptiveObserverDbContext> _observer;
    readonly string _source;
    public long SessionId { get; private init; }
    public int LastBar { get; private init; }

    SyntheticSession(DbContextOptions<AdaptiveObserverDbContext> observer, string source) => (_observer, _source) = (observer, source);

    public static async Task<SyntheticSession> CreateAsync(DbContextOptions<AdaptiveObserverDbContext> observerOptions, string sourceConnection, string sha, DateOnly day)
    {
        var at = new DateTimeOffset(day.ToDateTime(new TimeOnly(4, 0)), TimeSpan.Zero);          // 09:30 IST
        var expiry = day.AddDays(4);
        var chain = Enumerable.Range(0, 15).SelectMany(k => new[] { OptionType.Call, OptionType.Put }
            .Select(side => new ObserverOptionInstrument($"S-{(side == OptionType.Call ? "C" : "P")}{k}", $"SYN{22650 + 50 * k}{(side == OptionType.Call ? "CE" : "PE")}", side, 22650 + 50 * k, expiry, 65))).ToList();

        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(sourceConnection).Options);
        // The session freezes THE one NIFTY index instrument of its date: the quote instrument added by the live quote check is removed first.
        await source.Instruments.Where(x => x.AsOfDate == day && x.Underlying == "NIFTY" && x.InstrumentType == InstrumentType.Index).ExecuteDeleteAsync();
        source.Instruments.Add(new Instrument { Token = "S-SPOT", Exchange = Exchange.Nse, TradingSymbol = "Nifty 50", InstrumentType = InstrumentType.Index, Underlying = "NIFTY", AsOfDate = day });
        var random = new Random(5);
        var volume = new Dictionary<string, long>();
        for (var second = 0; second < 300; second++)
        {
            var spot = 22960 + second * 0.25 + random.Next(-4, 5);
            void Add(string token, decimal price, long traded, bool depth)
            {
                volume[token] = volume.GetValueOrDefault(token, 1000) + traded;
                long Q() => 10 + random.Next(200);
                source.Ticks.Add(new Tick
                {
                    Token = token, Exchange = depth ? Exchange.Nfo : Exchange.Nse, ExchangeTimestamp = at.AddSeconds(second), ReceivedAt = at.AddSeconds(second).AddMilliseconds(random.Next(0, 900)),
                    LastPrice = price, Volume = volume[token], OpenInterest = 100000 + random.Next(500),
                    Depth = depth ? new MarketDepth(price - .5m, Q(), price - .55m, Q(), price - .6m, Q(), price - .65m, Q(), price - .7m, Q(), price + .5m, Q(), price + .55m, Q(), price + .6m, Q(), price + .65m, Q(), price + .7m, Q()) : null,
                });
            }

            Add("S-SPOT", (decimal)spot, 0, false);
            foreach (var option in chain)
            {
                var model = AdaptiveResearchPricing.Price(option.OptionType, spot + 10, option.Strike, TimeToExpiry.YearsUntilExpiry(expiry, at.AddSeconds(second)), .065, .18);
                Add(option.Token, Math.Max(1m, Math.Round((decimal)model * 20) / 20 + random.Next(-2, 3) * .05m), random.Next(0, 40), true);
            }

            Add("S-FUT", Math.Round((decimal)spot + 10 + random.Next(-3, 4)), random.Next(0, 60), true);
        }

        await source.SaveChangesAsync();
        source.ChangeTracker.Clear();

        await using var db = new AdaptiveObserverDbContext(observerOptions);
        var row = new AdaptiveSessionStateRow
        {
            TradeDate = day, ModelVersion = AdaptiveFixedClockV1.ModelVersion, AtmRuleVersion = AtmCenterRules.SpotV1Version, SourceBranch = "isolated-ui-validation", SourceCommitSha = sha, BuildUtc = at,
            FutureToken = "S-FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = expiry, WeeklyOptionExpiry = expiry, LotSize = 65, RiskFreeRate = .065, OptionUniverseJson = JsonSerializer.Serialize(chain),
            OpeningWindowStartUtc = at.AddMinutes(-15), OpeningWindowEndUtc = at, EstimatorName = "synthetic", CreatedAtUtc = at, BaseBarVolume = 130, RollingWindowBars = 10, RollingWindowVolume = 1300, StrongThreshold = .2,
        };
        db.Sessions.Add(row); await db.SaveChangesAsync();
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = row.Id, LastHeartbeatUtc = at }); await db.SaveChangesAsync();
        var definition = new AdaptiveSessionDefinition(day, row.FutureToken, row.FutureSymbol, expiry, 65, 130);
        var context = new AdaptiveObserverSessionContext(row, definition, expiry, chain, null, at);
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var supplemental = new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var recovery = new AdaptiveStateRecoveryService(new AdaptiveSourceTickReader(), persistence,
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance), NullLogger<AdaptiveStateRecoveryService>.Instance, supplemental,
            new AdaptiveSessionSupplementalService(NullLogger<AdaptiveSessionSupplementalService>.Instance));
        var result = await recovery.RecoverAsync(source, db, context, at.AddMinutes(10), default);
        var last = result.Engine.FutureBars.Count(x => x.IsComplete);
        if (last < 60) throw new Exception($"The synthetic session produced only {last} bars.");
        await AdaptiveBarPublisher.PublishMissingAsync(db, row.Id, null, DateTimeOffset.UtcNow, default);
        var session = new SyntheticSession(observerOptions, sourceConnection) { SessionId = row.Id, LastBar = last };
        await session.SetLiveAsync();
        return session;
    }

    async Task RuntimeAsync(Action<AdaptiveObserverRuntimeRow> change)
    {
        await using var db = new AdaptiveObserverDbContext(_observer);
        var runtime = await db.Runtime.SingleAsync(x => x.SessionId == SessionId);
        change(runtime);
        runtime.LastHeartbeatUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public Task SetLiveAsync() => RuntimeAsync(r => { r.RuntimeStatus = AdaptiveRuntimeStatus.Live; r.LastCompletedBarSeq = LastBar; r.LastError = null; r.RecoveryPhase = null; r.RecoveryFailureCount = 0; });

    public Task SetRebuildingAsync() => RuntimeAsync(r =>
    {
        r.RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding; r.RecoveryPhase = "replaying"; r.LastRecoveryStartedUtc = DateTimeOffset.UtcNow.AddSeconds(-75); r.RecoveryPersistedBarsAtStart = 60;
        r.RecoveryBarsReplayed = 42; r.RecoveryTicksProcessed = 12345; r.LastRecoveryProgressUtc = DateTimeOffset.UtcNow;
    });

    public Task SetRecoveryFailedAsync() => RuntimeAsync(r => { r.RuntimeStatus = AdaptiveRuntimeStatus.RecoveryFailed; r.RecoveryFailureCount = 3; r.LastError = "synthetic failure"; });

    /// <summary>A quote received now for every option, so the live quote columns have a current value.</summary>
    public async Task AddFreshOptionQuotesAsync()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(_source).Options);
        var tokens = await source.Ticks.AsNoTracking().Where(x => x.Token.StartsWith("S-C") || x.Token.StartsWith("S-P")).Select(x => x.Token).Distinct().ToListAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var token in tokens)
            source.Ticks.Add(new Tick { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = now, ReceivedAt = now, LastPrice = 120, Volume = 10_000_000, OpenInterest = 100_000,
                Depth = new MarketDepth(119.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0, 120.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0) });
        await source.SaveChangesAsync();
    }
}
