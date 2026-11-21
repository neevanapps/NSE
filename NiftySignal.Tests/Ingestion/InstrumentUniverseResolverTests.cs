using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;

namespace NiftySignal.Tests.Ingestion;

/// <summary>The NIFTY daily universe: ONLY the nearest weekly expiry on or after the as-of date, 15 strikes each side (2026-11; previously two expiries x 10 strikes). Network calls are faked.</summary>
public sealed class InstrumentUniverseResolverTests
{
    static readonly DateOnly AsOf = new(2026, 11, 20);

    // Three weekly expiries; one stale row BEFORE the as-of date that must never be picked.
    const string NfoCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        NFO,1,65,NIFTY,NIFTY26NOVFUT,FUTIDX,24-NOV-2026,0.00,
        NFO,2,65,NIFTY,NIFTY18NOV25000CE,OPTIDX,18-NOV-2026,25000.00,CE
        NFO,3,65,NIFTY,NIFTY26NOV25000CE,OPTIDX,26-NOV-2026,25000.00,CE
        NFO,4,65,NIFTY,NIFTY03DEC25000CE,OPTIDX,03-DEC-2026,25000.00,CE
        NFO,5,65,NIFTY,NIFTY10DEC25000CE,OPTIDX,10-DEC-2026,25000.00,CE
        """;

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<(string Anchor, string Count)> Chains = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == FlatTradeInstrumentMasterProvider.NfoIndexDerivativesUrl) return new(HttpStatusCode.OK) { Content = new StringContent(NfoCsv, Encoding.UTF8, "text/csv") };
            if (url.EndsWith("GetQuotes", StringComparison.Ordinal)) return Json(new { stat = "Ok", lp = "25100.00", c = "25050.00" });
            if (url.EndsWith("GetOptionChain", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var jData = body[(body.IndexOf("jData=", StringComparison.Ordinal) + 6)..body.IndexOf("&jKey=", StringComparison.Ordinal)];
                using var doc = JsonDocument.Parse(jData);
                var tsym = doc.RootElement.GetProperty("tsym").GetString()!;
                Chains.Add((tsym, doc.RootElement.GetProperty("cnt").GetString()!));
                return Json(new { stat = "Ok", values = new[] { new { exch = "NFO", tsym = tsym + "-CE", token = "900", optt = "CE", strprc = "25050.00", ls = "65", ti = "0.05" } } });
            }

            throw new InvalidOperationException("Unexpected request " + url);
        }

        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    static (InstrumentUniverseResolver R, Fake F) Build()
    {
        var fake = new Fake();
        var http = new HttpClient(fake);
        var master = new FlatTradeInstrumentMasterProvider(http, NullLogger<FlatTradeInstrumentMasterProvider>.Instance);
        var auth = new FlatTradeAuthClient(http, Options.Create(new FlatTradeOptions { UserId = "U", ApiKey = "k", ApiSecret = "s" }));
        return (new InstrumentUniverseResolver(master, auth, NullLogger<InstrumentUniverseResolver>.Instance), fake);
    }

    [Fact]
    public async Task Resolves_OnlyTheNearestWeeklyExpiryOnOrAfterAsOf_With15StrikesEachSide()
    {
        var (resolver, fake) = Build();
        var instruments = await resolver.ResolveAsync("tok", AsOf, CancellationToken.None);

        var chain = Assert.Single(fake.Chains);                                   // ONE GetOptionChain call, not one per expiry
        Assert.Equal("NIFTY26NOV25000CE", chain.Anchor);                           // anchored on 26-Nov (18-Nov is before the as-of date; 03-Dec / 10-Dec are later weeks)
        Assert.Equal("15", chain.Count);
        Assert.Equal(15, InstrumentUniverseResolver.StrikeCountEachSide);
        var options = instruments.Where(i => i.InstrumentType == InstrumentType.Option).ToList();
        Assert.NotEmpty(options);
        Assert.All(options, o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Index);
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Future);
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Vix);
    }

    [Fact]
    public async Task ExpiryDay_TheExpiryOnTheAsOfDateItselfIsTheNearest()
    {
        var (resolver, fake) = Build();
        var instruments = await resolver.ResolveAsync("tok", new DateOnly(2026, 11, 26), CancellationToken.None);
        Assert.Equal("NIFTY26NOV25000CE", Assert.Single(fake.Chains).Anchor);
        Assert.All(instruments.Where(i => i.InstrumentType == InstrumentType.Option), o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
    }

    [Fact]
    public async Task NoExpiryOnOrAfterAsOf_FailsLoudly()
    {
        var (resolver, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("tok", new DateOnly(2026, 12, 20), CancellationToken.None));
    }
}
