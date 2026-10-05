using System.Text.Json;
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

public sealed class AdaptiveSessionRecoveryTests
{
    [Fact]
    public async Task ExistingSession_UsesFrozenUniverseAndRateAfterSourceInstrumentMutation()
    {
        await using var source=Source(); await using var db=Observer();
        var context=Context();
        var original=new ObserverOptionInstrument("P","ORIGINAL",OptionType.Put,23000,context.WeeklyOptionExpiry,65);
        context.Session.OptionUniverseJson=JsonSerializer.Serialize(new[] { original });
        db.Sessions.Add(context.Session); await db.SaveChangesAsync();
        source.Instruments.Add(new Instrument { Token="P",TradingSymbol="MUTATED",Exchange=Exchange.Nfo,
            InstrumentType=InstrumentType.Option,OptionType=OptionType.Put,Underlying="NIFTY",AsOfDate=context.Session.TradeDate,
            StrikePrice=24000,ExpiryDate=context.WeeklyOptionExpiry,LotSize=999 });
        await source.SaveChangesAsync(); db.ChangeTracker.Clear();
        var coordinator=Coordinator();
        var recovered=await coordinator.TryGetOrCreateAsync(source,db,context.Session.TradeDate,context.SignalStartUtc.AddMinutes(30),default);
        Assert.NotNull(recovered);Assert.Equal(original,Assert.Single(recovered!.Options));
        Assert.Equal(.065,recovered.Session.RiskFreeRate); // Current configuration is deliberately .2.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterHoursRestart_ReplaysAndFinalizesPendingSessionIdempotently(bool nextDay)
    {
        await using var source=Source(); await using var db=Observer();
        var context=Context();var at=context.SignalStartUtc;
        context.Session.StrongThreshold=.2;
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId=1,LastHeartbeatUtc=at });
        await db.SaveChangesAsync();
        for(var i=0;i<=12;i++)source.Ticks.Add(new Tick { Id=i+1,Token="FUT",Exchange=Exchange.Nfo,
            ExchangeTimestamp=at.AddSeconds(i),ReceivedAt=at.AddSeconds(i),LastPrice=23000+i,Volume=1000+100*i,
            Depth=new NiftySignal.Domain.ValueObjects.MarketDepth(
                i<10?23000+i-1:24000,1,0,0,0,0,0,0,0,0,
                i<10?23000+i+.1m:24001,1,0,0,0,0,0,0,0,0) });
        await source.SaveChangesAsync();
        var persistence=new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var observations=new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        var recovery=new AdaptiveStateRecoveryService(new(),persistence,observations,NullLogger<AdaptiveStateRecoveryService>.Instance);
        await recovery.RecoverAsync(source,db,context,at.AddMinutes(1),default);
        Assert.Equal(12,await db.FutureBars.CountAsync());
        var pending=Assert.Single(await db.Weak2Observations.ToListAsync());
        Assert.Equal(12,pending.TriggerBarSeq);
        Assert.Equal(AdaptiveObservationStatus.PendingH5,pending.Status);
        db.ChangeTracker.Clear();
        var service=new AdaptiveEndedSessionRecoveryService(Coordinator(),recovery,observations,persistence,
            NullLogger<AdaptiveEndedSessionRecoveryService>.Instance);
        var day=nextDay?context.Session.TradeDate.AddDays(1):context.Session.TradeDate;
        await service.FinalizeEndedAsync(source,db,day,includeToday:!nextDay,default);
        var runtime=await db.Runtime.SingleAsync();Assert.Equal(AdaptiveRuntimeStatus.Closed,runtime.RuntimeStatus);
        Assert.Equal(12,runtime.LastRecoveryReconciledBars);
        var row=await db.Weak2Observations.SingleAsync();Assert.Equal(AdaptiveObservationStatus.Unavailable,row.Status);
        Assert.Contains("H5 did not complete",row.UnavailableReason);
        await service.FinalizeEndedAsync(source,db,day,includeToday:!nextDay,default);
        Assert.Equal(12,await db.FutureBars.CountAsync());Assert.Single(await db.Weak2Observations.ToListAsync());
    }

    [Fact]
    public async Task MissingDiscoveryHistory_RefusesToFreezeThresholdFromPartialSource()
    {
        await using var source=Source();await using var db=Observer();
        var bootstrap=new AdaptiveHistoricalBootstrapService(new(),NullLogger<AdaptiveHistoricalBootstrapService>.Instance);
        var ex=await Assert.ThrowsAsync<InvalidOperationException>(()=>bootstrap.EnsurePriorSessionsAsync(source,db,new DateOnly(2026,9,26),default));
        Assert.Contains("required frozen discovery sessions missing",ex.Message);
        Assert.Empty(await db.Sessions.ToListAsync());
    }

    static AdaptiveObserverSessionContext Context()
    {
        var c=AdaptiveWeak2ObservationServiceTests.Context();c.Session.WeeklyOptionExpiry=c.WeeklyOptionExpiry;
        c.Session.BaseBarVolume=100;c.Session.RollingWindowBars=10;c.Session.LotSize=65;c.Session.RiskFreeRate=.065;
        return c;
    }
    static NiftySignalDbContext Source()=>new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    static AdaptiveObserverDbContext Observer()=>new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w=>w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    static AdaptiveSessionCoordinator Coordinator()=>new(new(),new Monitor(),NullLogger<AdaptiveSessionCoordinator>.Instance);
    sealed class Monitor:IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; }=new() { RiskFreeRate=.2 };
        public PricingOptions Get(string? name)=>CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions,string?> listener)=>null;
    }
}
