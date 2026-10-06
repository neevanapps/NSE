using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;

namespace NiftySignal.Tests.AdaptiveObserver;

public sealed class AdaptiveWeak2ObservationServiceTests
{
    [Fact]
    public async Task TriggerAndReplay_FreezePolicyAndIdentityWithoutDuplicates()
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var context = Context();
        var states = new[]
        {
            AdaptiveCoreParityTests.MakeFlowState(10, .30, 1, 1, null),
            AdaptiveCoreParityTests.MakeFlowState(11, .26, -1, 1, "Weakening"),
            AdaptiveCoreParityTests.MakeFlowState(12, .22, 1, 1, "Weakening"),
        };
        AdaptiveWeak2Classifier.Apply(states, .20);
        db.FutureBars.AddRange(states.Select(s => new AdaptiveFutureBarRow
        {
            SessionId = context.Session.Id,
            BarSeq = s.Bar.BarSeq,
            StartAvailableAtUtc = s.Bar.StartAvailableAtUtc,
            EndAvailableAtUtc = s.Bar.EndAvailableAtUtc,
            Close = s.Bar.Close,
        }));
        await db.SaveChangesAsync();
        var package = new AdaptiveCompletedBarPackage(states[2].Bar, states[2],
            new OptionBandBarResult(null, false, null, null, null, null, "unavailable"), [], true);
        var service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);

        await service.ProcessPackageAsync(source, db, context, package, default);
        db.ChangeTracker.Clear(); // Fresh tracked state, as after process restart.
        await service.ProcessPackageAsync(source, db, context, package, default);

        var row = Assert.Single(await db.Weak2Observations.ToListAsync());
        Assert.Equal(AdaptiveWeak2ObservationService.SelectionPolicyV1, row.SelectionPolicy);
        Assert.Equal(10, row.StrongBaseBarSeq);
        Assert.Equal(11, row.Weak1BarSeq);
        Assert.Equal(12, row.TriggerBarSeq);
        Assert.Equal(17, row.H5TargetBarSeq);
        Assert.Equal(AdaptiveObservationStatus.PendingH5, row.Status);

        await service.FinalizeSessionAsync(source, db, context, default);
        Assert.Equal(AdaptiveObservationStatus.Unavailable, row.Status);
        Assert.Contains("H5 did not complete", row.UnavailableReason);
        Assert.Equal(AdaptiveWeak2ObservationService.SelectionPolicyV1, row.SelectionPolicy);
        row.StrongBaseBarSeq = 999;
        await db.SaveChangesAsync();
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessPackageAsync(source, db, context, package, default));
        Assert.Contains("StrongBaseBarSeq", mismatch.Message);
        Assert.Equal(999, (await db.Weak2Observations.AsNoTracking().SingleAsync()).StrongBaseBarSeq);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(0, false)]
    [InlineData(-50, false)]
    public async Task PendingRestart_CompletesLockedQuoteFixedStrikeAndStrictOiExpansion(int oiChange, bool accepted)
    {
        await using var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var original = Context();
        var options = new[] {
            new ObserverOptionInstrument("C", "CALL", OptionType.Call, 23000, original.WeeklyOptionExpiry, 65),
            new ObserverOptionInstrument("P", "PUT", OptionType.Put, 23000, original.WeeklyOptionExpiry, 65) };
        var context = original with { Options = options };
        var flow = new[] { AdaptiveCoreParityTests.MakeFlowState(10,.30,1,1,null),
            AdaptiveCoreParityTests.MakeFlowState(11,.26,-1,1,"Weakening"),
            AdaptiveCoreParityTests.MakeFlowState(12,.22,1,1,"Weakening") };
        AdaptiveWeak2Classifier.Apply(flow,.20);
        db.FutureBars.AddRange(flow.Select(x=>new AdaptiveFutureBarRow { SessionId=1, BarSeq=x.Bar.BarSeq,
            StartAvailableAtUtc=x.Bar.StartAvailableAtUtc,EndAvailableAtUtc=x.Bar.EndAvailableAtUtc,Close=x.Bar.Close }));
        var h5 = AdaptiveCoreParityTests.MakeFlowState(17,.10,1,1,null);
        db.FutureBars.Add(new AdaptiveFutureBarRow { SessionId=1,BarSeq=17,StartAvailableAtUtc=h5.Bar.StartAvailableAtUtc,
            EndAvailableAtUtc=h5.Bar.EndAvailableAtUtc,Close=h5.Bar.Close });
        await db.SaveChangesAsync();
        var at=context.SignalStartUtc;
        // OI older than five minutes remains authoritative, even on a quote-less tick.
        source.Ticks.AddRange(Quote(1,"C",at.AddMinutes(-14),0,0,500),Quote(2,"P",at.AddMinutes(-14),20,21,500),
            Quote(3,"P",at.AddSeconds(12),125,125,500+oiChange), // Locked execution quote is valid.
            Quote(4,"P",at.AddSeconds(14),110,111,500+oiChange),
            Quote(5,"P",at.AddSeconds(17),140,141,500+oiChange));
        await source.SaveChangesAsync();
        var band = new OptionBandBarResult(null,false,null,null,null,null,"unavailable");
        var service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        await service.ProcessPackageAsync(source,db,context,new(flow[2].Bar,flow[2],band,[],true),default);
        Assert.Equal(AdaptiveObservationStatus.PendingH5,(await db.Weak2Observations.SingleAsync()).Status);
        db.ChangeTracker.Clear();
        service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        await service.ProcessPackageAsync(source,db,context,new(h5.Bar,h5,band,[],false),default);
        var row=await db.Weak2Observations.SingleAsync();
        Assert.Equal(AdaptiveObservationStatus.Completed,row.Status);
        Assert.Equal(23000d,row.Strike); Assert.Equal("P",row.Token);
        Assert.Equal(accepted,row.OiGatePassed); Assert.Equal(1000L,row.PairOiStrongBase);
        Assert.Equal(1000L+oiChange,row.PairOiTrigger); Assert.Equal(oiChange,row.PairOiChange);
        Assert.Equal(125d,row.EntryAsk); Assert.Equal(140d,row.ExitBid); Assert.Equal(15d,row.PnlPoints);
        Assert.Equal(15d,row.MfePointsExecutableBid); Assert.Equal(-15d,row.MaePointsExecutableBid);
        Assert.Equal(5d,row.HoldingSeconds);
        await service.ProcessPackageAsync(source,db,context,new(h5.Bar,h5,band,[],false),default);
        Assert.Single(await db.Weak2Observations.ToListAsync());
        db.ChangeTracker.Clear();
        service = new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        await service.VerifyCompletedAsync(source, db, context, default);
        row = await db.Weak2Observations.SingleAsync();
        row.PnlPoints = 999d;
        await db.SaveChangesAsync();
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyCompletedAsync(source, db, context, default));
        Assert.Contains("PnlPoints", mismatch.Message);
        Assert.Equal(999d, (await db.Weak2Observations.AsNoTracking().SingleAsync()).PnlPoints);

        static Tick Quote(long id,string token,DateTimeOffset time,decimal bid,decimal ask,long oi)=>new()
        { Id=id,Token=token,Exchange=Exchange.Nfo,ExchangeTimestamp=time,ReceivedAt=time,LastPrice=125,Volume=id,OpenInterest=oi,
          Depth=new MarketDepth(bid,1,0,0,0,0,0,0,0,0,ask,1,0,0,0,0,0,0,0,0) };
    }

    internal static AdaptiveObserverSessionContext Context()
    {
        var day = new DateOnly(2026, 9, 25);
        var at = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);
        var session = new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = day, ModelVersion = OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch = "test", SourceCommitSha = "test", BuildUtc = at,
            FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = day.AddDays(4),
            OpeningWindowStartUtc = at.AddMinutes(-15), OpeningWindowEndUtc = at,
            OpeningVolume = 1000,
            EstimatorName = nameof(OpeningVolumeProjectionV1), CreatedAtUtc = at,
        };
        return new AdaptiveObserverSessionContext(session,
            new AdaptiveSessionDefinition(day, "FUT", "NIFTYFUT", day.AddDays(4), 65, 100),
            day.AddDays(4), [], null, at);
    }
}
