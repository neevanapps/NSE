using NiftyResearcher.Bars;
using NiftyResearcher.Bars.Experiments.FuturesMarketState;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;

// Independent boundary oracle: original research cleaner/classifier, separate prefix aggregation.
// ATM±2 grid is a new rule, so this verifies the specified grid policy on research ticks;
// it does not claim an original research grid implementation exists.
static class OptionBandParity
{
    sealed record Prefix(DateTimeOffset? At,double? Mid,double? ExecutableMid,long? Oi,double[] Quantity,double[] Notional,long Updates);
    public static void Check(string folder,DateOnly day,ObserverOptionInstrument[] chain,
        IReadOnlyList<ExactAdaptiveBar> bars,IReadOnlyDictionary<int,AdaptiveCompletedBarPackage> packages,
        IReadOnlyList<CleanObserverTick> future)
    {
        var boundaries=bars.SelectMany(x=>new[] { x.StartAvailableAtUtc,x.EndAvailableAtUtc }).Distinct().Order().ToArray();
        var data=new Dictionary<string,Dictionary<DateTimeOffset,Prefix>>();
        foreach(var instrument in chain)
        {
            var path=Path.Combine(folder,instrument.Token+".ndjson");
            var ticks=File.Exists(path)?TickCleaner.Clean(NdjsonTickReader.Read(path).ToArray()).Ticks.ToArray():[];
            var enricher=new Phase1TickEnricher();var offset=0;var q=new double[6];var n=new double[6];long updates=0;
            DateTimeOffset? lastAt=null;double? mid=null;double? executableMid=null;long? oi=null;
            var points=new Dictionary<DateTimeOffset,Prefix>();
            foreach(var boundary in boundaries)
            {
                while(offset<ticks.Length && ticks[offset].AvailableAt<=boundary)
                {
                    var tick=ticks[offset++];var e=enricher.Process(tick);
                    lastAt=tick.AvailableAt;oi=tick.OpenInterest;
                    mid=tick.HasTwoSidedQuote?(tick.Bid+tick.Ask)/2:tick.Last;
                    executableMid=tick.Bid>0 && tick.Ask>=tick.Bid?(tick.Bid+tick.Ask)/2:null;
                    if(e.TradeVolume<=0)continue;
                    updates++;
                    var strict=e.StrictSide.ToString()=="Buy"?0:e.StrictSide.ToString()=="Sell"?1:2;
                    var enriched=e.EnrichedSide.ToString()=="Buy"?3:e.EnrichedSide.ToString()=="Sell"?4:5;
                    q[strict]+=e.TradeVolume;q[enriched]+=e.TradeVolume;
                    n[strict]+=tick.Last*e.TradeVolume;n[enriched]+=tick.Last*e.TradeVolume;
                }
                points[boundary]=new(lastAt,mid,executableMid,oi,(double[])q.Clone(),(double[])n.Clone(),updates);
            }
            data[instrument.Token]=points;
        }
        var checkedFields=0L;double[]? previousBand=null;
        var expectedRows=new Dictionary<(int,OptionType),Dictionary<string,double?>>();
        Dictionary<string,double?>? currentRow=null;
        foreach(var bar in bars)
        {
            var actual=packages[bar.BarSeq].OptionBand;
            var strikes=chain.Select(x=>x.Strike).Distinct().Order().ToArray();
            var referenceFuture=future.Last(x=>x.AvailableAt<=bar.StartAvailableAtUtc).Last;
            var expiry=chain[0].ExpiryDate;
            var years=Math.Max(120,(new DateTimeOffset(expiry.ToDateTime(new TimeOnly(15,30)),TimeSpan.FromHours(5.5))-bar.StartAvailableAtUtc).TotalSeconds)/(365*86400d);
            var estimates=new List<double>();
            foreach(var strike in strikes.OrderBy(x=>Math.Abs(x-referenceFuture)).ThenBy(x=>x).Take(5))
            {
                var c=chain.SingleOrDefault(x=>x.Strike==strike && x.OptionType==OptionType.Call);
                var p=chain.SingleOrDefault(x=>x.Strike==strike && x.OptionType==OptionType.Put);
                if(c is null || p is null)continue;
                var cs=data[c.Token][bar.StartAvailableAtUtc];var ps=data[p.Token][bar.StartAvailableAtUtc];
                // Selection requires executable marks, independently read from raw source as-of.
                var cm=cs.ExecutableMid;var pm=ps.ExecutableMid;
                if(cs.At is null || ps.At is null || (bar.StartAvailableAtUtc-cs.At.Value).TotalSeconds>5
                    || (bar.StartAvailableAtUtc-ps.At.Value).TotalSeconds>5 || cm is null || pm is null)continue;
                estimates.Add(strike*Math.Exp(-.065*years)+cm.Value-pm.Value);
            }
            double[]? expectedBand=null;
            if(estimates.Count>=3)
            {
                estimates.Sort();var index=estimates.Count/2;
                var synthetic=estimates.Count%2==1?estimates[index]:(estimates[index-1]+estimates[index])/2;
                var center=strikes.OrderBy(x=>Math.Abs(x-synthetic)).ThenBy(x=>x).First();
                var position=Array.IndexOf(strikes,center);
                if(position>=2 && position+2<strikes.Length)
                    expectedBand=strikes[(position-2)..(position+3)];
                if(expectedBand is not null && expectedBand.Any(k=>chain.Count(x=>x.Strike==k)!=2))expectedBand=null;
            }
            if(expectedBand is null)
            {
                if(actual.Selection is not null || actual.Call is not null || actual.Put is not null)throw new Exception($"BAND {day} #{bar.BarSeq}: expected unavailable");
                continue;
            }
            if(actual.Selection is null || !expectedBand.SequenceEqual(actual.Selection.Strikes))throw new Exception($"BAND {day} #{bar.BarSeq}: selection mismatch");
            var rolled=previousBand is not null && !previousBand.SequenceEqual(expectedBand);
            if(rolled!=actual.BandRolled)throw new Exception("Band roll flag mismatch");
            previousBand=expectedBand;
            foreach(var side in new[] { OptionType.Call,OptionType.Put })
            {
                var members=expectedBand.Select(k=>chain.Single(x=>x.Strike==k && x.OptionType==side)).ToArray();
                var start=members.Select(x=>data[x.Token][bar.StartAvailableAtUtc]).ToArray();
                var end=members.Select(x=>data[x.Token][bar.EndAvailableAtUtc]).ToArray();
                var metric=side==OptionType.Call?actual.Call!:actual.Put!;
                currentRow=new();expectedRows[(bar.BarSeq,side)]=currentRow;
                CheckField(metric,"CenterStrike",expectedBand[2]);
                CheckField(metric,"MaxQuoteAgeSeconds",end.All(x=>x.At.HasValue)?end.Max(x=>Math.Max(0,(bar.EndAvailableAtUtc-x.At!.Value).TotalSeconds)):null);
                for(var category=0;category<6;category++)
                {
                    var suffix=new[] { "StrictBuy","StrictSell","StrictUnknown","EnrichedBuy","EnrichedSell","EnrichedUnknown" }[category];
                    CheckField(metric,"Contract"+suffix,end.Sum(x=>x.Quantity[category])-start.Sum(x=>x.Quantity[category]));
                    CheckField(metric,"Notional"+suffix,end.Sum(x=>x.Notional[category])-start.Sum(x=>x.Notional[category]));
                }
                CheckField(metric,"TradeUpdates",end.Sum(x=>x.Updates)-start.Sum(x=>x.Updates));
                var open=SumNullable(start.Select(x=>x.Mid));var close=SumNullable(end.Select(x=>x.Mid));
                CheckField(metric,"PremiumIndexOpen",open);CheckField(metric,"PremiumIndexClose",close);CheckField(metric,"BarPriceChange",close-open);
                var oiOpen=SumNullable(start.Select(x=>(double?)x.Oi));var oiClose=SumNullable(end.Select(x=>(double?)x.Oi));
                CheckField(metric,"OiOpen",oiOpen);CheckField(metric,"OiClose",oiClose);CheckField(metric,"OiChange",oiClose-oiOpen);
                CheckField(metric,"OiChangePct",oiOpen>0?(oiClose-oiOpen)/oiOpen:null);
                var noiStart=SumNullable(start.Select((x,i)=>x.Oi*members[i].LotSize*x.Mid));
                var noiEnd=SumNullable(end.Select((x,i)=>x.Oi*members[i].LotSize*x.Mid));
                CheckField(metric,"PremiumNotionalOiOpen",noiStart);CheckField(metric,"PremiumNotionalOiClose",noiEnd);
                CheckField(metric,"PremiumNotionalOiChange",noiEnd-noiStart);
                foreach(var prefix in new[] { "Contract","Notional" })
                {
                    double V(string suffix)=>currentRow![prefix+suffix]!.Value;
                    var total=V("StrictBuy")+V("StrictSell")+V("StrictUnknown");
                    var delta=V("StrictBuy")-V("StrictSell");var etotal=V("EnrichedBuy")+V("EnrichedSell")+V("EnrichedUnknown");
                    CheckField(metric,prefix+(prefix=="Contract"?"TotalQuantity":"Total"),total);
                    CheckField(metric,prefix+"StrictDelta",delta);CheckField(metric,prefix+"StrictDeltaRatioTotal",total>0?delta/total:0);
                    CheckField(metric,prefix+"StrictCoverage",total>0?(V("StrictBuy")+V("StrictSell"))/total:0);
                    CheckField(metric,prefix+"EnrichedDelta",V("EnrichedBuy")-V("EnrichedSell"));
                    CheckField(metric,prefix+"EnrichedDeltaRatio",etotal>0?(V("EnrichedBuy")-V("EnrichedSell"))/etotal:0);
                }
            }
        }
        currentRow=null;
        foreach(var bar in bars)
        foreach(var side in new[] { OptionType.Call,OptionType.Put })
        {
            var package=packages[bar.BarSeq].OptionBand;
            var actual=side==OptionType.Call?package.CallRolling:package.PutRolling;
            var sequences=Enumerable.Range(Math.Max(1,bar.BarSeq-9),Math.Min(10,bar.BarSeq)).ToArray();
            if(sequences.Length<10 || sequences.Any(seq=>!expectedRows.ContainsKey((seq,side))))
            { if(actual is not null)throw new Exception("Rolling option availability mismatch");continue; }
            if(actual is null)throw new Exception("Expected option rolling window missing");
            var rows=sequences.Select(seq=>expectedRows[(seq,side)]).ToArray();
            double? S(string key)=>SumNullable(rows.Select(x=>x[key]));
            var elapsed=sequences.Sum(seq=>bars.Single(x=>x.BarSeq==seq).DurationSeconds);
            var price=S("BarPriceChange");var path=rows.Sum(x=>Math.Abs(x["BarPriceChange"]??0));
            CheckField(actual,"WindowBars",10);CheckField(actual,"RollingBandPriceChange",price);
            CheckField(actual,"RollingEfficiency",price.HasValue && path>0?Math.Min(1,Math.Abs(price.Value)/path):0);
            CheckField(actual,"RollingReturnPct",rows.All(x=>x["PremiumIndexOpen"]>0 && x["BarPriceChange"].HasValue)
                ?rows.Sum(x=>100*x["BarPriceChange"]!.Value/x["PremiumIndexOpen"]!.Value):null);
            CheckField(actual,"TradeUpdates",S("TradeUpdates"));CheckField(actual,"OiChange",S("OiChange"));CheckField(actual,"OiChangePct",S("OiChangePct"));
            foreach(var prefix in new[] { "Contract","Notional" })
            {
                var total=S(prefix+(prefix=="Contract"?"TotalQuantity":"Total"))!.Value;
                var strict=S(prefix+"StrictDelta")!.Value;var enriched=S(prefix+"EnrichedDelta")!.Value;
                var etotal=S(prefix+"EnrichedBuy")+S(prefix+"EnrichedSell")+S(prefix+"EnrichedUnknown");
                CheckField(actual,prefix+"StrictDelta",strict);CheckField(actual,prefix+"EnrichedDelta",enriched);
                CheckField(actual,prefix+"StrictDeltaRatioTotal",total>0?strict/total:0);
                CheckField(actual,prefix+"StrictCoverage",total>0?(S(prefix+"StrictBuy")+S(prefix+"StrictSell"))/total:0);
                CheckField(actual,prefix+"EnrichedDeltaRatio",etotal>0?enriched/etotal:0);
                CheckField(actual,prefix+"ActivityPerSecond",elapsed>0?total/elapsed:0);
                var previousSequences=Enumerable.Range(Math.Max(1,bar.BarSeq-10),Math.Min(10,bar.BarSeq-1)).ToArray();
                var previous=previousSequences.Length==10 && previousSequences.All(seq=>expectedRows.ContainsKey((seq,side)))
                    ?previousSequences.Select(seq=>expectedRows[(seq,side)]).ToArray():null;
                CheckField(actual,prefix+"StrictAbsDeltaChange",previous is null?null:Math.Abs(strict)-Math.Abs(previous.Sum(x=>x[prefix+"StrictDelta"]!.Value)));
                CheckField(actual,prefix+"EnrichedDeltaChange",previous is null?null:enriched-previous.Sum(x=>x[prefix+"EnrichedDelta"]!.Value));
            }
        }
        Console.WriteLine($"BAND {day}: PASS independent selection, CE/PE native quantity, premium notional, price/OI, band roll flags and every ten-bar rolling metric; fields={checkedFields}");
        void CheckField(object actual,string field,double? expected)
        {
            if(currentRow is not null)currentRow[field]=expected;
            checkedFields++;var value=actual.GetType().GetProperty(field)!.GetValue(actual);
            if(value is null && expected is null)return;
            if(value is not null && expected.HasValue && double.IsFinite(Convert.ToDouble(value))
                && Math.Abs(Convert.ToDouble(value)-expected.Value)<=1e-9)return;
            throw new Exception($"BAND {day} {field}: reference={expected:R} actual={value}");
        }
    }
    static double? SumNullable(IEnumerable<double?> values)
    { var xs=values.ToArray();return xs.All(x=>x.HasValue)?xs.Sum(x=>x!.Value):null; }
}
