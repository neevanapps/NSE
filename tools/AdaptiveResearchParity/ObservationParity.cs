using System.Globalization;
using System.Text.Json;
using NiftyResearcher.Bars;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;

static class ObservationParity
{
    public static Task CheckAsync(string root,DateOnly day,JsonElement instruments,IReadOnlyList<AdaptiveFlowState> flow,long barVolume,double? threshold)
    { Check(root,day,instruments,flow,barVolume,threshold);return Task.CompletedTask; }
    static void Check(string root,DateOnly day,JsonElement instruments,IReadOnlyList<AdaptiveFlowState> flow,long barVolume,double? threshold)
    {
        var golden=ResidualParity.ReadCsv(Path.Combine(root,"adaptive-observation-reference.csv"))
            .Where(x=>x["TradeDate"]==day.ToString("yyyy-MM-dd")).ToArray();
        var entries=flow.Where(x=>x.State==AdaptiveStateKind.Weak2 && x.Bar.EndAvailableAtUtc>=new DateTimeOffset(day.ToDateTime(new TimeOnly(9,30)),TimeSpan.FromHours(5.5))).ToArray();
        if(entries.Length==0) { if(golden.Length!=0)throw new InvalidOperationException("Reference observations missing live identities"); return; }
        var options=instruments.EnumerateArray().Where(x=>x.GetProperty("InstrumentType").GetString()=="Option").ToArray();
        var expiry=options.Min(x=>DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!));
        var chain=options.Where(x=>DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!)==expiry)
            .Select(x=>new ObserverOptionInstrument(x.GetProperty("Token").GetString()!,x.GetProperty("TradingSymbol").GetString()!,Enum.Parse<OptionType>(x.GetProperty("OptionType").GetString()!),x.GetProperty("StrikePrice").GetDouble(),expiry,x.GetProperty("LotSize").GetInt32())).ToArray();
        var at=new DateTimeOffset(day.ToDateTime(new TimeOnly(9,30)),TimeSpan.FromHours(5.5)).ToUniversalTime();
        var session=new AdaptiveSessionStateRow { Id=1,TradeDate=day,ModelVersion=OpeningVolumeProjectionV1.ModelVersion,
            SourceBranch="parity",SourceCommitSha="parity",BuildUtc=at,FutureToken="FUT",FutureSymbol="FUT",FutureExpiry=day,
            OpeningWindowStartUtc=at.AddMinutes(-15),OpeningWindowEndUtc=at,EstimatorName=nameof(OpeningVolumeProjectionV1),CreatedAtUtc=at,StrongThreshold=threshold };
        var context=new AdaptiveObserverSessionContext(session,new(day,"FUT","FUT",day,65,barVolume),expiry,chain,null,at);
        var folder=Path.Combine(root,"NiftyResearcher/research-ticks",day.ToString("yyyy-MM-dd"));
        var ticks=new Dictionary<string,List<CleanObserverTick>>(StringComparer.Ordinal);
        foreach(var instrument in chain)
        {
            var path=Path.Combine(folder,instrument.Token+".ndjson");if(!File.Exists(path))continue;
            ticks[instrument.Token]=AdaptiveTickCleaner.Clean(NdjsonTickReader.Read(path).Select(q=>
                new ObserverRawTick(q.Id,q.ExchangeTimestamp,q.ReceivedAt,q.Last,q.Bid,q.Ask,q.BidQty,q.AskQty,q.Volume,q.OpenInterest))).Ticks.ToList();
        }
        var actual=new List<AdaptiveWeak2ObservationRow>();
        foreach(var entry in entries)
        {
            var seq=entry.Bar.BarSeq;
            var baseBar=flow.Single(x=>x.Bar.BarSeq==entry.StrongBaseBarSeq).Bar;
            var weak1=flow.Single(x=>x.Bar.BarSeq==entry.Weak1BarSeq).Bar;
            var direction=entry.Rolling!.PriceDirection;
            var row=new AdaptiveWeak2ObservationRow { SessionId=1,StrongBaseBarSeq=baseBar.BarSeq,Weak1BarSeq=weak1.BarSeq,
                TriggerBarSeq=seq,TriggerTimestampUtc=entry.Bar.EndAvailableAtUtc,OldTrendDirection=direction,ReversalDirection=-direction,
                OptionSide=direction>0?OptionType.Put:OptionType.Call,H5TargetBarSeq=seq+5,
                StrictNoTurnDiagnostic=direction*(weak1.Close-baseBar.Close)>=0 && direction*(entry.Bar.Close-weak1.Close)>=0,
                SelectionPolicy=AdaptiveWeak2ObservationService.SelectionPolicyV1,Status=AdaptiveObservationStatus.PendingH5 };
            var target=flow.SingleOrDefault(x=>x.Bar.BarSeq==seq+5)?.Bar;
            if(target is null) { row.Status=AdaptiveObservationStatus.Unavailable;row.UnavailableReason="H5 did not complete"; }
            else AdaptiveWeak2ObservationService.FinalizeFromCleanTicks(context,row,baseBar.EndAvailableAtUtc,target.EndAvailableAtUtc,target.Close-entry.Bar.Close,ticks,sessionClosing:true);
            actual.Add(row);
        }
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
                if(expected is null||value is null||!double.IsFinite(value.Value)||Math.Abs(value.Value-expected.Value)>(field is "EntryLatencySec" or "ExitLatencySec" or "HoldingSeconds" ? 1e-6 : 1e-9))
                    throw new InvalidOperationException($"H5 {day} #{seq} {field}: research={expected:R}, live={value:R}");
            }
            CheckText("OptionType",x.OptionSide.ToString());CheckText("Symbol",x.TradingSymbol);
            CheckText("OIGatePass",x.OiGatePassed?.ToString()??"");CheckText("StrictNoTurn",x.StrictNoTurnDiagnostic.ToString());
            CheckTime("EntryUtc",x.EntryQuoteTimestampUtc);CheckTime("ExitUtc",x.ExitTimestampUtc);
            void CheckText(string key,string? value) { checks++;if(row[key]!=value)throw new InvalidOperationException($"H5 {day} #{seq} {key}: research={row[key]}, live={value}"); }
            void CheckTime(string key,DateTimeOffset? value) { checks++;if(value is null || Math.Abs((DateTimeOffset.Parse(row[key],CultureInfo.InvariantCulture)-value.Value).TotalSeconds)>1e-6)throw new InvalidOperationException($"H5 {day} #{seq} {key}: time mismatch"); }
        }
        var complete=actual.Count(x=>x.Status==AdaptiveObservationStatus.Completed);
        if(complete!=golden.Length)throw new InvalidOperationException($"H5 {day}: completed research={golden.Length}, live={complete}");
        Console.WriteLine($"H5 {day}: PASS {golden.Length} independent overlapping observations, {checks} fields; unavailable={actual.Count-complete}");
    }
    static double Number(Dictionary<string,string> row,string key)=>double.Parse(row[key],CultureInfo.InvariantCulture);
}
