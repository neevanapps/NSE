using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveRecoveryBufferTests
{
    [Fact]
    public async Task Recovery_RetainsPersistedRowsBeyondCutoffBeforeAdvancingIdCursor()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w=>w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        var context = AdaptiveWeak2ObservationServiceTests.Context();
        var cutoff = context.SignalStartUtc;
        db.Sessions.Add(context.Session);
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId=context.Session.Id, LastHeartbeatUtc=cutoff });
        await db.SaveChangesAsync();
        // Id and availability order differ. Both rows must survive in their causal order.
        source.Ticks.AddRange(
            new Tick { Id=1, Token="FUT", Exchange=Exchange.Nfo, ExchangeTimestamp=cutoff.AddSeconds(2), ReceivedAt=cutoff.AddSeconds(2), LastPrice=23001m, Volume=1150 },
            new Tick { Id=2, Token="FUT", Exchange=Exchange.Nfo, ExchangeTimestamp=cutoff.AddSeconds(-1), ReceivedAt=cutoff.AddSeconds(-1), LastPrice=23000m, Volume=1000 });
        await source.SaveChangesAsync();
        var reader=new AdaptiveSourceTickReader();
        var service = new AdaptiveStateRecoveryService(reader,
            new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance),
            new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance),
            NullLogger<AdaptiveStateRecoveryService>.Instance);
        var recovered=await service.RecoverAsync(source,db,context,cutoff,default);
        Assert.Equal(2,recovered.LastFetchedRawId);
        Assert.Equal(new long[] { 1 },recovered.Pending.Select(x=>x.Tick.Id));
        Assert.All(recovered.Pending,x=>Assert.Equal(cutoff.AddSeconds(2),x.Tick.AvailableAt));
        Assert.Equal(2,recovered.LastProcessedTickId);
        Assert.Empty(recovered.Engine.FutureBars);
        Assert.Empty(await reader.ReadRawAfterIdAsync(source,recovered.Tokens,recovered.LastFetchedRawId,default));
        var baseline=new AdaptiveObserverEngine(context.Definition,context.WeeklyOptionExpiry,context.Session.RiskFreeRate,
            context.Session.StrongThreshold,context.SignalStartUtc,context.Options,context.ResidualAnchor);
        var cleanTicks=await reader.ReadCleanSessionAsync(source,context.Session.TradeDate,["FUT"],cutoff.AddSeconds(3),default);
        foreach(var item in cleanTicks) baseline.Process(item.Token,item.Tick);
        foreach(var item in recovered.Pending.OrderBy(x=>x.Tick.AvailableAt).ThenBy(x=>x.Tick.Id)) recovered.Engine.Process(item.Token,item.Tick);
        Assert.Equal(JsonSerializer.Serialize(baseline.FutureBars),JsonSerializer.Serialize(recovered.Engine.FutureBars));
        Assert.Equal(JsonSerializer.Serialize(baseline.PartialBar),JsonSerializer.Serialize(recovered.Engine.PartialBar));
        Assert.Single(recovered.Engine.FutureBars);
    }
}
