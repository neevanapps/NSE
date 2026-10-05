using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveDashboardReadTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public async Task ReadSelectors_AnchorAllGridsToCommittedRuntimeAndSurviveReaderRestart(int count)
    {
        var options=new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var at=DateTimeOffset.UtcNow;
        await using(var db=new AdaptiveObserverDbContext(options))
        {
            var session=AdaptiveWeak2ObservationServiceTests.Context().Session;
            session.TradeDate=DateOnly.FromDateTime(at.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
            db.Sessions.Add(session);
            db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId=1,LastCompletedBarSeq=20,LastHeartbeatUtc=at,
                CurrentPartialBarVolume=50,CurrentPartialBarStartedAtUtc=at });
            for(var seq=1;seq<=21;seq++)
            {
                var end=at.AddMinutes(seq-21);
                db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId=1,BarSeq=seq,StartAvailableAtUtc=end.AddMinutes(-1),EndAvailableAtUtc=end });
                foreach(var side in new[] { OptionType.Call,OptionType.Put })
                    db.OptionBandBars.Add(new AdaptiveOptionBandBarRow { SessionId=1,BarSeq=seq,Side=side,StartAvailableAtUtc=end.AddMinutes(-1),EndAvailableAtUtc=end,BandStrikes="",UnavailableReason="quotes missing" });
                // Leave residuals absent, as before 09:30 or in an unavailable historical row.
            }
            await db.SaveChangesAsync();
        }
        var factory=new Factory(options);
        var reader=new AdaptiveObserverDataService(factory,NullLogger<AdaptiveObserverDataService>.Instance);
        var first=await reader.LoadSnapshotAsync(count);
        Assert.NotNull(first);
        Assert.Equal(count,first!.Futures.Count); Assert.Equal(20,first.Futures[0].Bar.BarSeq);
        var sequences=first.Futures.Select(x=>x.Bar.BarSeq).ToArray();
        Assert.Equal(sequences,first.Options.Select(x=>x.BarSeq));
        foreach(var variant in new[] { ResidualVariant.Atm,ResidualVariant.AtmPlusMinus2 })
        {
            var rows=first.Residuals.Where(x=>x.Variant==variant).ToArray();
            Assert.Equal(sequences,rows.Select(x=>x.BarSeq));
            Assert.All(rows,x=> { Assert.False(x.IsAvailable); Assert.NotEmpty(x.UnavailableReason!); });
        }
        Assert.Equal(50,first.Runtime.CurrentPartialBarVolume);
        Assert.DoesNotContain(first.Futures,x=>x.Bar.BarSeq==21);
        // New reader has no SignalR notification state: database reconstruction remains identical.
        reader=new AdaptiveObserverDataService(factory,NullLogger<AdaptiveObserverDataService>.Instance);
        var restarted=await reader.LoadSnapshotAsync(count);
        Assert.Equal(sequences,restarted!.Futures.Select(x=>x.Bar.BarSeq));
    }
    sealed class Factory(DbContextOptions<AdaptiveObserverDbContext> options):IDbContextFactory<AdaptiveObserverDbContext>
    { public AdaptiveObserverDbContext CreateDbContext()=>new(options); }
}
