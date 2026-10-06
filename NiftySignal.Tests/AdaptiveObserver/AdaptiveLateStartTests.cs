using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveLateStartTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly DateTimeOffset Open = new(2026, 10, 6, 3, 45, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Late = Open.AddMinutes(45);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrPartialOpening_UsesPriorMedianAndFreezesItAcrossRestart(bool partial)
    {
        await using var source = Source(); await using var db = Observer();
        AddInstruments(source);
        AddHistory(db, Day.AddDays(-2), 1000);
        AddHistory(db, Day.AddDays(-1), 3000);
        AddHistory(db, Day.AddDays(1), 999999); // Future data must never enter the fallback.
        var fallbackHistory = AddHistory(db, Day.AddDays(-3), 777777);
        fallbackHistory.UsesMedianOpeningFallback = true;
        if (partial) AddTick(source, Open.AddMinutes(14), 1000);
        AddTick(source, Late, 100000);
        await source.SaveChangesAsync(); await db.SaveChangesAsync();
        var coordinator = Coordinator();
        var context = await coordinator.TryGetOrCreateAsync(source, db, Day, Late.AddSeconds(1), default);
        Assert.NotNull(context);
        Assert.True(context!.Session.UsesMedianOpeningFallback);
        Assert.Equal(0, context.Session.OpeningVolume);
        Assert.Equal(2000, context.Session.EstimatorInputOpeningVolume);
        Assert.Equal(2, context.Session.MedianOpeningSampleCount);
        Assert.Equal(partial ? 1 : 0, context.Session.OpeningCoverageMinutes);
        Assert.Equal(Late, context.Session.ObservationStartUtc);
        Assert.Null(context.ResidualAnchor);
        Assert.Equal(OpeningVolumeProjectionV1.SelectBaseBarVolume(2000, 65), context.Session.BaseBarVolume);

        foreach (var previous in await db.Sessions.Where(x => x.TradeDate < Day).ToListAsync()) previous.OpeningVolume = 999999;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var resumed = await coordinator.TryGetOrCreateAsync(source, db, Day, Late.AddMinutes(20), default);
        Assert.Equal(context.Session.BaseBarVolume, resumed!.Session.BaseBarVolume);
        Assert.Equal(2000, resumed.Session.EstimatorInputOpeningVolume);
        Assert.Equal(Late, resumed.SignalStartUtc);
    }

    [Fact]
    public async Task LateRestart_WithCompleteStoredOpening_UsesActualVolume()
    {
        await using var source = Source(); await using var db = Observer(); AddInstruments(source);
        for (var minute = 0; minute <= 15; minute++) AddTick(source, Open.AddMinutes(minute), 1000 + minute * 100);
        await source.SaveChangesAsync();
        var context = await Coordinator().TryGetOrCreateAsync(source, db, Day, Late, default);
        Assert.False(context!.Session.UsesMedianOpeningFallback);
        Assert.Equal(1400, context.Session.OpeningVolume);
        Assert.Equal(1400, context.Session.EstimatorInputOpeningVolume);
        Assert.Equal(Open, context.Session.ObservationStartUtc);
    }

    [Fact]
    public async Task NoPriorValidatedOpening_RefusesInventedFallback()
    {
        await using var source = Source(); await using var db = Observer(); AddInstruments(source);
        AddTick(source, Late, 100000); await source.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Coordinator().TryGetOrCreateAsync(source, db, Day, Late, default));
        Assert.Contains("No validated prior opening volume", error.Message);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    [Fact]
    public async Task LateRecovery_DoesNotInventOfflineBars_AndVerifiedTenBarsCountAfterRestart()
    {
        await using var source = Source(); await using var db = Observer(); AddInstruments(source);
        AddHistory(db, Day.AddDays(-1), 2000); await db.SaveChangesAsync();
        AddTick(source, Late, 1000000); await source.SaveChangesAsync();
        var context = (await Coordinator().TryGetOrCreateAsync(source, db, Day, Late, default))!;
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var observations = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        var recovery = new AdaptiveStateRecoveryService(new(), persistence, observations, NullLogger<AdaptiveStateRecoveryService>.Instance);
        var baseline = await recovery.RecoverAsync(source, db, context, Late, default);
        Assert.Empty(baseline.Engine.FutureBars); Assert.Equal(0, baseline.Engine.PartialBar.AccumulatedVolume);
        for (var i = 1; i <= 10; i++) AddTick(source, Late.AddSeconds(i), 1000000 + context.Session.BaseBarVolume * i);
        await source.SaveChangesAsync();
        var nine = await recovery.RecoverAsync(source, db, context, Late.AddSeconds(9), default);
        Assert.Equal(9, AdaptiveReadinessPolicy.ConsecutiveValidBars(nine.Engine.FutureBars, context.Session.BaseBarVolume, 9));
        Assert.All(nine.Engine.FutureBars, x => Assert.True(x.StartAvailableAtUtc >= Late));
        var ten = await recovery.RecoverAsync(source, db, context, Late.AddSeconds(10), default);
        Assert.Equal(10, AdaptiveReadinessPolicy.ConsecutiveValidBars(ten.Engine.FutureBars, context.Session.BaseBarVolume, 10));
        var restarted = await recovery.RecoverAsync(source, db, context, Late.AddSeconds(10), default);
        Assert.Equal(10, restarted.ReconciledBars);
        Assert.Equal(10, AdaptiveReadinessPolicy.ConsecutiveValidBars(restarted.Engine.FutureBars, context.Session.BaseBarVolume, 10));
        Assert.Equal(10, await db.FutureBars.CountAsync(x => x.SessionId == context.Session.Id));
    }

    [Fact]
    public void Readiness_RequiresTenContiguousValidBars_AndIgnoresFutureBars()
    {
        var bars = Enumerable.Range(1, 11).Select(i => AdaptiveCoreParityTests.MakeBar(i, 100, 101, 70, 20, 10)).ToArray();
        Assert.Equal(9, AdaptiveReadinessPolicy.ConsecutiveValidBars(bars, 100, 9));
        Assert.Equal(10, AdaptiveReadinessPolicy.ConsecutiveValidBars(bars, 100, 10));
        Assert.Equal(0, AdaptiveReadinessPolicy.ConsecutiveValidBars(bars, 101, 10));
        Assert.Equal(4, AdaptiveReadinessPolicy.ConsecutiveValidBars(bars.Where(x => x.BarSeq != 6), 100, 10));
        Assert.False(AdaptiveReadinessPolicy.IsValid(100, 100, double.NaN, 101, 100, 101, 1, Open, Open.AddSeconds(1)));
        Assert.False(AdaptiveReadinessPolicy.IsValid(100, 100, 100, 101, 100, 101, 0, Open, Open.AddSeconds(1)));
        Assert.Equal(2000, AdaptiveOpeningCoverage.Median([3000, 1000]));
        Assert.Equal(2000, AdaptiveOpeningCoverage.Median([3000, 2000, 1000]));
    }

    [Fact]
    public async Task ReplayInsertion_DoesNotDeclareLiveBeforeReconciliationCompletes()
    {
        await using var db = Observer();
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = context.Session.Id,
            RuntimeStatus = AdaptiveRuntimeStatus.Rebuilding, LastHeartbeatUtc = Open });
        await db.SaveChangesAsync();
        var bar = AdaptiveCoreParityTests.MakeBar(1, 100, 101, 70, 20, 10);
        var package = new AdaptiveCompletedBarPackage(bar, AdaptiveFlowEvolutionTracker.Build([bar]).Single(),
            new OptionBandBarResult(null, false, null, null, null, null, "Unavailable"), [], false);
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        await persistence.PersistOrVerifyAsync(db, context.Session, package, Open, default);
        Assert.Equal(AdaptiveRuntimeStatus.Rebuilding, (await db.Runtime.SingleAsync()).RuntimeStatus);
    }

    static AdaptiveSessionStateRow AddHistory(AdaptiveObserverDbContext db, DateOnly day, long opening)
    {
        var row = AdaptiveWeak2ObservationServiceTests.Context().Session;
        row.Id = 0; row.TradeDate = day; row.IsHistoricalSeed = true; row.OpeningVolume = opening;
        db.Sessions.Add(row); return row;
    }
    static void AddInstruments(NiftySignalDbContext source)
    {
        source.Instruments.Add(new Instrument { Token = "FUT", TradingSymbol = "NIFTYFUT", Exchange = Exchange.Nfo,
            Underlying = "NIFTY", InstrumentType = InstrumentType.Future, AsOfDate = Day, ExpiryDate = Day.AddDays(20), LotSize = 65 });
        source.Instruments.Add(new Instrument { Token = "CE", TradingSymbol = "NIFTYCE", Exchange = Exchange.Nfo,
            Underlying = "NIFTY", InstrumentType = InstrumentType.Option, OptionType = OptionType.Call,
            AsOfDate = Day, ExpiryDate = Day, StrikePrice = 23000, LotSize = 65 });
    }
    static void AddTick(NiftySignalDbContext source, DateTimeOffset at, long volume) => source.Ticks.Add(new Tick {
        Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000, Volume = volume });
    static NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    static AdaptiveSessionCoordinator Coordinator() => new(new(), new Monitor(), NullLogger<AdaptiveSessionCoordinator>.Instance);
    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }
}
