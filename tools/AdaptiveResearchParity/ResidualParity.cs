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
        long checks=0; var maximumError=0d;
        foreach (var variant in new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 })
        {
            var stem = variant == ResidualVariant.Atm ? "option_price_residual" : "option_price_residual_band";
            var path = Path.Combine(root,$"data/experiments/futures_market_state/{stem}/adaptive_{stem}_detail.csv");
            if (!File.Exists(path)) throw new FileNotFoundException("Generate pinned Python residual evidence first",path);
            var rows=ReadCsv(path).Where(x=>x["TradeDate"]==day.ToString("yyyy-MM-dd")).ToArray();
            if(rows.Length==0) { Console.WriteLine($"RESIDUAL {day} {variant}: reference unavailable"); continue; }
            var anchor=OptionResidualModel.BuildAnchor(at0,expiry,N(rows[0],"Future0930"),chain,At(at0),rate)
                ?? throw new InvalidOperationException($"RESIDUAL {day} {variant}: missing live anchor for {rows.Length} reference rows");
            foreach(var row in rows)
            {
                var seq=(int)N(row,"BarSeq"); var bar=bars.Single(x=>x.BarSeq==seq);
                // The frozen Python reference consumes millisecond-formatted flow CSV. Compare
                // pricing on that SAME input clock; direct exact-bar precision is checked separately.
                var asOf = DateTimeOffset.Parse(row["EndIst"] + "+05:30",CultureInfo.InvariantCulture);
                if (bar.EndAvailableAtUtc < asOf || (bar.EndAvailableAtUtc-asOf).TotalMilliseconds>=1)
                    throw new InvalidOperationException("Reference CSV timestamp differs beyond serialization precision");
                var actual=OptionResidualModel.Evaluate(anchor,asOf,N(row,"FutureNow"),At(asOf),rate).SingleOrDefault(x=>x.Variant==variant)
                    ?? throw new InvalidOperationException($"RESIDUAL {day} {variant} #{seq}: live unavailable, reference available");
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
