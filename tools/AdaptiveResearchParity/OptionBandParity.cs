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
                    double V(string suffix)=>Convert.ToDouble(metric.GetType().GetProperty(prefix+suffix)!.GetValue(metric));
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
        Console.WriteLine($"BAND {day}: PASS independent selection, CE/PE native quantity, premium notional, price/OI and roll flags; fields={checkedFields}");
        void CheckField(object actual,string field,double? expected)
        {
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
