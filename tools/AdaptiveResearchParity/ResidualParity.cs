using System.Globalization;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using NiftyResearcher.Bars;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Domain.Enums;

static class ResidualParity
{
    public static void Check(string root, DateOnly day, JsonElement instruments, IReadOnlyList<ExactAdaptiveBar> bars)
    {
        const double rate = .065;
        var options = instruments.EnumerateArray().Where(x => x.GetProperty("InstrumentType").GetString() == "Option").ToArray();
        var expiry = options.Min(x => DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!));
        var chain = options.Where(x => DateOnly.Parse(x.GetProperty("ExpiryDate").GetString()!) == expiry)
            .Select(x => new ObserverOptionInstrument(x.GetProperty("Token").GetString()!, x.GetProperty("TradingSymbol").GetString()!,
                Enum.Parse<OptionType>(x.GetProperty("OptionType").GetString()!), x.GetProperty("StrikePrice").GetDouble(), expiry, x.GetProperty("LotSize").GetInt32())).ToArray();
        var folder = Path.Combine(root, "NiftyResearcher/research-ticks", day.ToString("yyyy-MM-dd"));
        var series = new Dictionary<string, CleanObserverTick[]>();
        foreach (var i in chain)
        {
            var path = Path.Combine(folder, i.Token + ".ndjson");
            if (!File.Exists(path)) continue;
            var raw = NdjsonTickReader.Read(path).Select(x => new ObserverRawTick(x.Id,x.ExchangeTimestamp,x.ReceivedAt,x.Last,x.Bid,x.Ask,x.BidQty,x.AskQty,x.Volume,x.OpenInterest));
            series[i.Token] = AdaptiveTickCleaner.Clean(raw).Ticks.ToArray();
        }
        Dictionary<string,OptionQuoteSnapshot> At(DateTimeOffset at)
        {
            var result = new Dictionary<string,OptionQuoteSnapshot>();
            foreach (var (token, ticks) in series)
            {
                var lo=0; var hi=ticks.Length;
                while (lo<hi) { var mid=(lo+hi)/2; if(ticks[mid].AvailableAt<=at) lo=mid+1; else hi=mid; }
                if(lo==0)continue;
                var q=ticks[lo-1]; result[token]=new(token,q.AvailableAt,q.Last,q.Bid,q.Ask,q.OpenInterest);
            }
            return result;
        }
        var at0 = new DateTimeOffset(day.ToDateTime(new TimeOnly(9,30)),TimeSpan.FromHours(5.5)).ToUniversalTime();
        var future = instruments.EnumerateArray().Single(x=>x.GetProperty("InstrumentType").GetString()=="Future");
        var futureToken=future.GetProperty("Token").GetString()!;
        var futureTicks=AdaptiveTickCleaner.Clean(NdjsonTickReader.Read(Path.Combine(folder,futureToken+".ndjson"))
            .Select(q=>new ObserverRawTick(q.Id,q.ExchangeTimestamp,q.ReceivedAt,q.Last,q.Bid,q.Ask,q.BidQty,q.AskQty,q.Volume,q.OpenInterest))).Ticks;
        var anchorFuture=futureTicks.LastOrDefault(x=>x.AvailableAt<=at0);
        var sessionAnchor=anchorFuture.Id!=0 && (at0-anchorFuture.AvailableAt).TotalSeconds<=5
            ? OptionResidualModel.BuildAnchor(at0,expiry,anchorFuture.Last,chain,At(at0),rate) : null;
        var engine=new AdaptiveObserverEngine(new(day,futureToken,future.GetProperty("TradingSymbol").GetString()!,
            DateOnly.Parse(future.GetProperty("ExpiryDate").GetString()!),future.GetProperty("LotSize").GetInt32(),
            OpeningVolumeProjectionV1.DiscoveryOutOfFold[day].AdaptiveBarVolume),expiry,rate,null,at0,chain,sessionAnchor);
        var input=series.SelectMany(x=>x.Value.Select(t=>(Token:x.Key,Tick:t)))
            .Concat(futureTicks.Select(t=>(Token:futureToken,Tick:t))).OrderBy(x=>x.Tick.AvailableAt).ThenBy(x=>x.Tick.Id).ToList();
        var integrated=new Dictionary<int,AdaptiveCompletedBarPackage>();
        for(var offset=0;offset<input.Count;)
        {
            var end=offset+1;while(end<input.Count && input[end].Tick.AvailableAt==input[offset].Tick.AvailableAt)end++;
            foreach(var package in engine.ProcessAvailabilityGroup(input.GetRange(offset,end-offset)))integrated.Add(package.FutureBar.BarSeq,package);
            offset=end;
        }
        long checks=0; var maximumError=0d;
        foreach (var variant in new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 })
        {
            var stem = variant == ResidualVariant.Atm ? "option_price_residual" : "option_price_residual_band";
            var path = Path.Combine(root,$"data/experiments/futures_market_state/{stem}/adaptive_{stem}_detail.csv");
            if (!File.Exists(path)) throw new FileNotFoundException("Generate pinned Python residual evidence first",path);
            var rows=ReadCsv(path).Where(x=>x["TradeDate"]==day.ToString("yyyy-MM-dd")).ToArray();
            if(rows.Length==0) {
                if(integrated.Values.Any(x=>x.Residuals.Any(r=>r.Variant==variant && r.Reading is not null)))throw new InvalidOperationException("Live diagnostic available on reference-unavailable day");
                Console.WriteLine($"RESIDUAL {day} {variant}: reference unavailable"); continue; }
            if(sessionAnchor is null)throw new InvalidOperationException($"RESIDUAL {day} {variant}: missing live anchor for {rows.Length} reference rows");
            var actualAvailable=integrated.Values.Where(x=>x.Residuals.Any(r=>r.Variant==variant && r.Reading is not null)).Select(x=>x.FutureBar.BarSeq).OrderBy(x=>x);
            var expectedAvailable=rows.Select(x=>(int)N(x,"BarSeq")).OrderBy(x=>x);
            if(!actualAvailable.SequenceEqual(expectedAvailable))throw new InvalidOperationException($"RESIDUAL {day} {variant}: integrated availability differs from research");
            foreach(var row in rows)
            {
                var seq=(int)N(row,"BarSeq"); var bar=bars.Single(x=>x.BarSeq==seq);
                // The frozen Python reference consumes millisecond-formatted flow CSV. Compare
                // pricing on that SAME input clock; direct exact-bar precision is checked separately.
                var asOf = DateTimeOffset.Parse(row["EndIst"] + "+05:30",CultureInfo.InvariantCulture);
                if (bar.EndAvailableAtUtc < asOf || (bar.EndAvailableAtUtc-asOf).TotalMilliseconds>=1)
                    throw new InvalidOperationException("Reference CSV timestamp differs beyond serialization precision");
                var actual=integrated[seq].Residuals.Single(x=>x.Variant==variant).Reading
                    ?? throw new InvalidOperationException($"RESIDUAL {day} {variant} #{seq}: integrated live unavailable, reference available");
                if(actual.AsOfUtc!=asOf)throw new InvalidOperationException("Integrated residual diagnostic clock mismatch");
                var band=variant==ResidualVariant.AtmPlusMinus2;
                var comparisons = new (string Field, double Actual)[] {
                    (band ? "CenterStrike":"Strike",actual.CenterStrike),
                    (band ? "CEBasketActual":"CEActual",actual.CEActual),
                    (band ? "PEBasketActual":"PEActual",actual.PEActual),
                    (band ? "CEBasketExpected":"CEExpectedConstantIV",actual.CEExpected),
                    (band ? "PEBasketExpected":"PEExpectedConstantIV",actual.PEExpected),
                    (band ? "CEBasketResidual":"CEResidualPoints",actual.CEResidual),
                    (band ? "PEBasketResidual":"PEResidualPoints",actual.PEResidual),
                    (band ? "CEBasketResidualPct":"CEResidualPctOf0930",actual.CEResidualPct),
                    (band ? "PEBasketResidualPct":"PEResidualPctOf0930",actual.PEResidualPct),
                    ("DirectionalResidualPct",actual.DirectionalResidualPct),("CommonResidualPct",actual.CommonResidualPct) };
                foreach(var (field,value) in comparisons)
                {
                    var expected=N(row,field); var error=Math.Abs(value-expected); checks++; maximumError=Math.Max(maximumError,error);
                    if(!double.IsFinite(value)||error>1e-6)throw new InvalidOperationException($"RESIDUAL {day} {variant} #{seq} {field}: research={expected:R}, live={value:R}, error={error:R}, limit=1e-6");
                }
            }
            Console.WriteLine($"RESIDUAL {day} {variant}: PASS {rows.Length} rows");
        }
        Console.WriteLine($"RESIDUAL {day}: fields={checks}, maxAbsoluteError={maximumError:R}");
    }
    static double N(Dictionary<string,string> row,string key)=>double.Parse(row[key],CultureInfo.InvariantCulture);
    public static IEnumerable<Dictionary<string,string>> ReadCsv(string path)
    {
        using var parser=new TextFieldParser(path); parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes=true;
        var header=parser.ReadFields()!;
        while(!parser.EndOfData)
        {
            var values=parser.ReadFields()!;
            if(values.Length!=header.Length)throw new InvalidOperationException("Malformed reference CSV");
            yield return header.Select((x,i)=>(x,values[i])).ToDictionary(x=>x.x,x=>x.Item2);
        }
    }
}
