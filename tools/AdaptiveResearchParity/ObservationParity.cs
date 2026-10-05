using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftyResearcher.Bars;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;

static class ObservationParity
{
    public static async Task CheckAsync(string root,DateOnly day,JsonElement instruments,IReadOnlyList<AdaptiveFlowState> flow,long barVolume,double? threshold)
    {
        var golden=ResidualParity.ReadCsv(Path.Combine(root,"adaptive-observation-reference.csv"))
            .Where(x=>x["TradeDate"]==day.ToString("yyyy-MM-dd")).ToArray();
        var entries=flow.Where(x=>x.State==AdaptiveStateKind.Weak2 && x.Bar.EndAvailableAtUtc>=new DateTimeOffset(day.ToDateTime(new TimeOnly(9,30)),TimeSpan.FromHours(5.5))).ToArray();
        if(entries.Length==0) { if(golden.Length!=0)throw new InvalidOperationException("Reference observations missing live identities"); return; }
        var options=instruments.EnumerateArray().Where(x=>x.GetProperty("InstrumentType").GetString()=="Option").ToArray();
        var expiry=options.Min(x=>DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!));
        var chain=options.Where(x=>DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!)==expiry)
            .Select(x=>new ObserverOptionInstrument(x.GetProperty("Token").GetString()!,x.GetProperty("TradingSymbol").GetString()!,Enum.Parse<OptionType>(x.GetProperty("OptionType").GetString()!),x.GetProperty("StrikePrice").GetDouble(),expiry,x.GetProperty("LotSize").GetInt32())).ToArray();
        await using var source=new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var db=new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var at=new DateTimeOffset(day.ToDateTime(new TimeOnly(9,30)),TimeSpan.FromHours(5.5)).ToUniversalTime();
        var session=new AdaptiveSessionStateRow { Id=1,TradeDate=day,ModelVersion=OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch="parity",SourceCommitSha="parity",BuildUtc=at,FutureToken="FUT",FutureSymbol="FUT",FutureExpiry=day,
            OpeningWindowStartUtc=at.AddMinutes(-15),OpeningWindowEndUtc=at,EstimatorName=nameof(OpeningVolumeProjectionV1),CreatedAtUtc=at,StrongThreshold=threshold };
        var context=new AdaptiveObserverSessionContext(session,new(day,"FUT","FUT",day,65,barVolume),expiry,chain,null,at);
        db.Sessions.Add(session);
        db.FutureBars.AddRange(flow.Select(x=>new AdaptiveFutureBarRow { SessionId=1,BarSeq=x.Bar.BarSeq,StartAvailableAtUtc=x.Bar.StartAvailableAtUtc,EndAvailableAtUtc=x.Bar.EndAvailableAtUtc,Close=x.Bar.Close }));
        await db.SaveChangesAsync();
        var folder=Path.Combine(root,"NiftyResearcher/research-ticks",day.ToString("yyyy-MM-dd"));
        foreach(var instrument in chain)
        {
            var path=Path.Combine(folder,instrument.Token+".ndjson");
            if(!File.Exists(path))continue;
            // Preserve the independent reader's raw Id order: service owns cleaning/classification.
            var batch=new List<Tick>();
            foreach(var q in NdjsonTickReader.Read(path))
            {
                batch.Add(new Tick { Id=q.Id,Token=instrument.Token,Exchange=Exchange.Nfo,ExchangeTimestamp=q.ExchangeTimestamp,
                    ReceivedAt=q.ReceivedAt,LastPrice=(decimal)q.Last,Volume=q.Volume,OpenInterest=q.OpenInterest,
                    Depth=new MarketDepth((decimal)q.Bid,q.BidQty,0,0,0,0,0,0,0,0,(decimal)q.Ask,q.AskQty,0,0,0,0,0,0,0,0) });
                if(batch.Count<10000)continue;
                source.Ticks.AddRange(batch);await source.SaveChangesAsync();source.ChangeTracker.Clear();batch.Clear();
            }
            source.Ticks.AddRange(batch);await source.SaveChangesAsync();source.ChangeTracker.Clear();
        }
        var service=new AdaptiveWeak2ObservationService(NullLogger<AdaptiveWeak2ObservationService>.Instance);
        foreach(var x in entries)
            await service.ProcessPackageAsync(source,db,context,new(x.Bar,x,new(null,false,null,null,null,null,"unavailable"),[],true),default);
        await service.FinalizeSessionAsync(source,db,context,default);
        var actual=await db.Weak2Observations.AsNoTracking().ToListAsync();
        long checks=0;
        foreach(var row in golden)
        {
            var seq=(int)Number(row,"TriggerBarSeq");
            var x=actual.Single(z=>z.TriggerBarSeq==seq);
            if(x.Status!=AdaptiveObservationStatus.Completed)throw new InvalidOperationException($"H5 {day} #{seq}: {x.Status} {x.UnavailableReason}");
            var mappings=new (string Field,double? Actual)[] { ("Strike",x.Strike),("EntryAsk",x.EntryAsk),("EntryBidAtQuote",x.EntryBid),
                ("ExitBid",x.ExitBid),("ExitAskAtQuote",x.ExitAsk),("PairOIBase",x.PairOiStrongBase),("PairOITrigger",x.PairOiTrigger),
                ("PairOIChangeRatio",x.PairOiChangePct),("PnLPoints",x.PnlPoints),("ReturnPct",x.ReturnPct),
                ("MFEPointsExecutableBid",x.MfePointsExecutableBid),("MAEPointsExecutableBid",x.MaePointsExecutableBid),
                ("EntryLatencySec",x.EntryQuoteLatencySeconds),("ExitLatencySec",x.ExitLatencySeconds),("HoldingSeconds",x.HoldingSeconds) };
            foreach(var (field,value) in mappings)
            {
                checks++;var expected=row[field]==""?(double?)null:Number(row,field);
                if(expected is null&&value is null)continue;
                if(expected is null||value is null||!double.IsFinite(value.Value)||Math.Abs(value.Value-expected.Value)>1e-9)
                    throw new InvalidOperationException($"H5 {day} #{seq} {field}: research={expected:R}, live={value:R}");
            }
            CheckText("OptionType",x.OptionSide.ToString());CheckText("Symbol",x.TradingSymbol);
            CheckText("OIGatePass",x.OiGatePassed?.ToString()??"");CheckText("StrictNoTurn",x.StrictNoTurnDiagnostic.ToString());
            CheckTime("EntryUtc",x.EntryQuoteTimestampUtc);CheckTime("ExitUtc",x.ExitTimestampUtc);
            void CheckText(string key,string? value) { checks++;if(row[key]!=value)throw new InvalidOperationException($"H5 {day} #{seq} {key}: research={row[key]}, live={value}"); }
            void CheckTime(string key,DateTimeOffset? value) { checks++;if(DateTimeOffset.Parse(row[key],CultureInfo.InvariantCulture)!=value)throw new InvalidOperationException($"H5 {day} #{seq} {key}: time mismatch"); }
        }
        var complete=actual.Count(x=>x.Status==AdaptiveObservationStatus.Completed);
        if(complete!=golden.Length)throw new InvalidOperationException($"H5 {day}: completed research={golden.Length}, live={complete}");
        Console.WriteLine($"H5 {day}: PASS {golden.Length} independent overlapping observations, {checks} fields; unavailable={actual.Count-complete}");
    }
    static double Number(Dictionary<string,string> row,string key)=>double.Parse(row[key],CultureInfo.InvariantCulture);
}
