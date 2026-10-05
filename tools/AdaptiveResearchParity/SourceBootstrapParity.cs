using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Host;
using NiftySignal.Persistence;

sealed class SourceBootstrapParity : IAsyncDisposable
{
    readonly AdaptiveObserverDbContext observer = new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(w=>w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
    public async Task CheckAsync(DateOnly day,string token,string symbol,DateOnly expiry,int lot,
        ObserverRawTick[] raw,IReadOnlyList<AdaptiveFlowState> expected,double? threshold)
    {
        await using var source=new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        source.Instruments.Add(new Instrument { Token=token,TradingSymbol=symbol,Underlying="NIFTY",Exchange=Exchange.Nfo,
            InstrumentType=InstrumentType.Future,ExpiryDate=expiry,LotSize=lot,AsOfDate=day });
        source.Ticks.AddRange(raw.Select(x=>new Tick { Id=x.Id,Token=token,Exchange=Exchange.Nfo,
            ExchangeTimestamp=x.ExchangeTimestamp,ReceivedAt=x.ReceivedAt,LastPrice=(decimal)x.Last,Volume=x.Volume,OpenInterest=x.OpenInterest,
            Depth=new MarketDepth((decimal)x.Bid,x.BidQty,0,0,0,0,0,0,0,0,(decimal)x.Ask,x.AskQty,0,0,0,0,0,0,0,0) }));
        await source.SaveChangesAsync();source.ChangeTracker.Clear();
        var bootstrap=new AdaptiveHistoricalBootstrapService(new(),NullLogger<AdaptiveHistoricalBootstrapService>.Instance);
        await bootstrap.EnsurePriorSessionsAsync(source,observer,day.AddDays(1),default);
        var session=await observer.Sessions.AsNoTracking().SingleOrDefaultAsync(x=>x.TradeDate==day)
            ?? throw new Exception($"SOURCE {day}: production bootstrap excluded complete research source");
        if(session.StrongThreshold!=threshold)throw new Exception($"SOURCE {day}: persisted daily threshold mismatch {session.StrongThreshold:R} vs {threshold:R}");
        var rows=await observer.RollingStates.AsNoTracking().Where(x=>x.SessionId==session.Id).OrderBy(x=>x.EndBarSeq).ToListAsync();
        var states=expected.Where(x=>x.Rolling is not null).ToArray();
        if(rows.Count!=states.Length)throw new Exception("Source bootstrap rolling count differs");
        long checkedFields=0;
        for(var i=0;i<rows.Count;i++)
        {
            var row=rows[i];var state=states[i];
            foreach(var property in typeof(AdaptiveRollingStateRow).GetProperties())
            {
                if(property.Name is "Id" or "SessionId")continue;
                var origin=state.GetType().GetProperty(property.Name);
                object? value=origin is not null ? origin.GetValue(state) : state.Rolling!.GetType().GetProperty(property.Name)!.GetValue(state.Rolling);
                var actual=property.GetValue(row);checkedFields++;
                if(actual is double a && value is double e && double.IsFinite(a) && double.IsFinite(e) && Math.Abs(a-e)<=1e-9)continue;
                if(Equals(actual,value))continue;
                throw new Exception($"SOURCE {day} #{row.EndBarSeq} {property.Name}: source={actual}, research-validated={value}");
            }
        }
        await bootstrap.EnsurePriorSessionsAsync(source,observer,day.AddDays(1),default);
        if(await observer.Sessions.CountAsync(x=>x.TradeDate==day)!=1
            || await observer.RollingStates.CountAsync(x=>x.SessionId==session.Id)!=rows.Count)throw new Exception("Source bootstrap duplicated persisted seed on restart");
        observer.ChangeTracker.Clear();
        Console.WriteLine($"SOURCE {day}: PASS actual source reader/bootstrap, persisted threshold and {checkedFields} rolling fields, idempotent restart");
    }
    public ValueTask DisposeAsync()=>observer.DisposeAsync();
}
