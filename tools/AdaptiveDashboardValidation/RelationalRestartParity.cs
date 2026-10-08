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

static class RelationalRestartParity
{
    public static async Task CheckAsync(DbContextOptions<AdaptiveObserverDbContext> options,string sourceConnection,string sha,string evidence)
    {
        var day=new DateOnly(2026,9,25);var expiry=day.AddDays(4);
        var at=new DateTimeOffset(2026,9,25,4,0,0,TimeSpan.Zero);
        var chain=Enumerable.Range(-6,13).SelectMany(i=>new[] { OptionType.Call,OptionType.Put }.Select(side=>
            new ObserverOptionInstrument($"PG-{side}-{i}",$"PG-{side}-{i}",side,23000+i*50,expiry,65))).ToArray();
        await using var source=new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(sourceConnection).Options);
        for(var index=0;index<=60;index++)
        {
            var time=at.AddSeconds(index);var future=23000d+index+(index>=45?150:0);
            foreach(var option in chain)
            {
                var price=Math.Max(5,125+(option.OptionType==OptionType.Call?1:-1)*.4*(future-option.Strike));
                source.Ticks.Add(Quote(option.Token,time,price,price-.1,price+.1,1000+65*index,1000+10*index));
            }
            var forceSell=index is >=25 and <=33;
            source.Ticks.Add(Quote("PG-FUT",time,future,forceSell?24000:future-.1,forceSell?24001:future+.1,10000+1300*index,100000+index));
        }
        await source.SaveChangesAsync();source.ChangeTracker.Clear();
        await using var db=new AdaptiveObserverDbContext(options);
        var row=new AdaptiveSessionStateRow { TradeDate=day,ModelVersion=OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch="isolated-postgres-restart",SourceCommitSha=sha,BuildUtc=at,FutureToken="PG-FUT",FutureSymbol="PG-NIFTYFUT",
            FutureExpiry=expiry,WeeklyOptionExpiry=expiry,LotSize=65,RiskFreeRate=.065,OptionUniverseJson=JsonSerializer.Serialize(chain),
            OpeningWindowStartUtc=at.AddMinutes(-15),OpeningWindowEndUtc=at,EstimatorName=nameof(OpeningVolumeProjectionV1),
            CreatedAtUtc=at,BaseBarVolume=3250,RollingWindowBars=10,RollingWindowVolume=32500,StrongThreshold=.2 };
        db.Sessions.Add(row);await db.SaveChangesAsync();
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId=row.Id,LastHeartbeatUtc=at });await db.SaveChangesAsync();
        var context=new AdaptiveObserverSessionContext(row,new(day,row.FutureToken,row.FutureSymbol,expiry,65,3250),expiry,chain,null,at);
        var reader=new AdaptiveSourceTickReader();
        var input=await reader.ReadCleanSessionAsync(source,day,chain.Select(x=>x.Token).Append(row.FutureToken).ToArray(),at.AddMinutes(2),default);
        var groups=input.GroupBy(x=>x.Tick.AvailableAt).Select(x=>x.Select(t=>(t.Token,t.Tick)).ToArray()).ToArray();
        AdaptiveObserverEngine NewEngine()=>new(context.Definition,expiry,.065,.2,at,chain,null);
        var baseline=NewEngine();var expected=new List<AdaptiveCompletedBarPackage>();
        foreach(var group in groups)expected.AddRange(baseline.ProcessAvailabilityGroup(group));
        var trigger=expected.FirstOrDefault(x=>x.IsActionableWeak2) ?? throw new Exception("PostgreSQL restart fixture did not produce Weak2.");
        var rolled=expected.FirstOrDefault(x=>x.OptionBand.BandRolled) ?? throw new Exception("PostgreSQL restart fixture did not roll a band.");
        var h5=expected.Single(x=>x.FutureBar.BarSeq==trigger.FutureBar.BarSeq+5);
        var checkpoints=new HashSet<DateTimeOffset> { at.AddSeconds(8),
            expected.First(x=>x.FlowState.State==AdaptiveStateKind.Strong).FutureBar.EndAvailableAtUtc,
            expected.First(x=>x.FlowState.State==AdaptiveStateKind.Weak1).FutureBar.EndAvailableAtUtc,
            trigger.FutureBar.EndAvailableAtUtc,h5.FutureBar.EndAvailableAtUtc.AddSeconds(-1),
            h5.FutureBar.EndAvailableAtUtc,rolled.FutureBar.EndAvailableAtUtc };
        var golden=expected.ToDictionary(x=>x.FutureBar.BarSeq,x=>JsonSerializer.Serialize(x));
        var persistence=new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);
        var observations=new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        var supplemental=new AdaptiveSupplementalPersistence(NullLogger<AdaptiveSupplementalPersistence>.Instance);
        var engine=NewEngine();var restarts=0;
        foreach(var group in groups)
        {
            foreach(var package in engine.ProcessAvailabilityGroup(group))
            {
                if(golden[package.FutureBar.BarSeq]!=JsonSerializer.Serialize(package))throw new Exception("PostgreSQL restart changed an emitted package.");
                await persistence.PersistOrVerifyAsync(db,row,package,group[0].Tick.AvailableAt,default);
                await supplemental.PersistOrVerifyFuturesAsync(db,row.Id,package,default);
                await observations.ProcessPackageAsync(source,db,context,package,default);
            }
            var time=group[0].Tick.AvailableAt;
            if(!checkpoints.Contains(time))continue;
            var partial=engine.PartialBar;db.ChangeTracker.Clear();
            observations=new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
            var recovery=new AdaptiveStateRecoveryService(new(),persistence,observations,NullLogger<AdaptiveStateRecoveryService>.Instance,supplemental);
            var rebuilt=await recovery.RecoverAsync(source,db,context,time,default);
            if(rebuilt.Engine.PartialBar!=partial)throw new Exception("PostgreSQL recovery changed partial volume/start.");
            engine=rebuilt.Engine;restarts++;
        }
        await observations.FinalizeSessionAsync(source,db,context,default);
        await observations.VerifyCompletedAsync(source,db,context,default);
        if(await db.FutureBars.CountAsync(x=>x.SessionId==row.Id)!=expected.Count)throw new Exception("Relational restart duplicated/dropped bars.");
        // Sidecar (08-Oct plan section 71): one versioned row per completed bar, verified across every restart, never a mismatch.
        if(await db.FuturesSupplemental.CountAsync(x=>x.SessionId==row.Id && x.MetricsVersion==FuturesMicrostructureBar.MetricsVersion)!=expected.Count
            || supplemental.MismatchCount!=0 || supplemental.FailureCount!=0)
            throw new Exception($"Sidecar restart parity failed: mismatches={supplemental.MismatchCount}, failures={supplemental.FailureCount}.");
        // Mid-session deployment: delete the earliest sidecar rows (as if the Host were deployed after those bars) and replay.
        db.ChangeTracker.Clear();
        var removed=await db.FuturesSupplemental.Where(x=>x.SessionId==row.Id && x.BarSeq<=5).ExecuteDeleteAsync();
        var backfillRecovery=new AdaptiveStateRecoveryService(new(),persistence,new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance),
            NullLogger<AdaptiveStateRecoveryService>.Instance,supplemental);
        await backfillRecovery.RecoverAsync(source,db,context,groups[^1][0].Tick.AvailableAt,default);
        if(removed!=5 || await db.FuturesSupplemental.CountAsync(x=>x.SessionId==row.Id)!=expected.Count || supplemental.MismatchCount!=0)
            throw new Exception("Sidecar backfill after a mid-session deployment failed.");
        db.ChangeTracker.Clear();
        var completed=await db.Weak2Observations.FirstAsync(x=>x.SessionId==row.Id && x.Status==AdaptiveObservationStatus.Completed);
        completed.PnlPoints=999;await db.SaveChangesAsync();db.ChangeTracker.Clear();
        try { await observations.VerifyCompletedAsync(source,db,context,default);throw new Exception("Relational corruption went undetected."); }
        catch(InvalidOperationException ex) when(ex.Message.Contains("PnlPoints")) { }
        if((await db.Weak2Observations.AsNoTracking().SingleAsync(x=>x.Id==completed.Id)).PnlPoints!=999)
            throw new Exception("Restart overwrote corrupted persisted history.");
        await File.WriteAllTextAsync(Path.Combine(evidence,"postgres-restart.json"),JsonSerializer.Serialize(new {
            sourceSha=sha,restarts,completedBars=expected.Count,trigger=trigger.FutureBar.BarSeq,
            bandRoll=rolled.FutureBar.BarSeq,corruptionRejected=true,sidecarRows=expected.Count,sidecarBackfilledBars=removed,scope="PostgreSQL source, package persistence, Weak2/OI/H5 reconciliation and partial-state restarts" },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"POSTGRES RESTART PASS: {restarts} process-state resets, {expected.Count} completed bars, Weak2/H5 and band roll, corruption rejected without overwrite.");
    }
    static Tick Quote(string token,DateTimeOffset time,double last,double bid,double ask,long volume,long oi)=>new() {
        Token=token,Exchange=Exchange.Nfo,ExchangeTimestamp=time,ReceivedAt=time,LastPrice=(decimal)last,Volume=volume,OpenInterest=oi,
        Depth=new MarketDepth((decimal)bid,100,0,0,0,0,0,0,0,0,(decimal)ask,100,0,0,0,0,0,0,0,0) };
}
