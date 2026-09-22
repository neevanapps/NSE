using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;

namespace NiftySignal.Tests.Ingestion;

/// <summary>
/// Proves the "current expiry only, per index" behavior <see cref="SensexBankNiftyInstrumentUniverseResolver"/>
/// exists for -- both indices' canned master data below carry TWO distinct expiries (mirroring
/// what real FlatTrade master data looks like, confirmed live 2026-09-22: Sensex has weekly rows,
/// Bank Nifty has monthly-spaced rows), and every assertion checks that only the EARLIER one is
/// ever resolved or requested. Network calls (scrip master CSV downloads, GetQuotes,
/// GetOptionChain) are faked via <see cref="FakeHandler"/> rather than hitting FlatTrade for
/// real -- this repo has no live session token available in CI/local test runs.
/// </summary>
public sealed class SensexBankNiftyInstrumentUniverseResolverTests
{
    static readonly DateOnly AsOfDate = new(2026, 9, 22);
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Two SENSEX weekly expiries + two BANKNIFTY monthly-spaced expiries, matching the real shape
    // found live 2026-09-22 (weekly for Sensex, large gaps for Bank Nifty). CURRENT = the earlier
    // of each pair; NEXT must never appear in a resolved instrument or a GetOptionChain request.
    const string BfoCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        BFO,900001,20,SENSEX,SENSEX26SEPFUT,FUTIDX,24-SEP-2026,0.00,
        BFO,900002,20,SENSEX,SENSEX26O1580800CE,OPTIDX,24-SEP-2026,80800.00,CE
        BFO,900003,20,SENSEX,SENSEX26O1580800PE,OPTIDX,24-SEP-2026,80800.00,PE
        BFO,900004,20,SENSEX,SENSEX26OCT80800CE,OPTIDX,01-OCT-2026,80800.00,CE
        """;

    const string NfoCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        NFO,800001,30,BANKNIFTY,BANKNIFTY29SEP26F,FUTIDX,29-SEP-2026,-0.01,
        NFO,800002,30,BANKNIFTY,BANKNIFTY29SEP26C61200,OPTIDX,29-SEP-2026,61200.00,CE
        NFO,800003,30,BANKNIFTY,BANKNIFTY29SEP26P61200,OPTIDX,29-SEP-2026,61200.00,PE
        NFO,800004,30,BANKNIFTY,BANKNIFTY27OCT26C61200,OPTIDX,27-OCT-2026,61200.00,CE
        NFO,26000,65,NIFTY,NIFTY25SEP26C25000,OPTIDX,25-SEP-2026,25000.00,CE
        """;

    /// <summary>Routes by request URI/verb to canned responses; records every GetOptionChain anchor symbol requested, for the "only current expiry is ever asked for" assertion.</summary>
    sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<string> OptionChainAnchorsRequested = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url == FlatTradeInstrumentMasterProvider.NfoIndexDerivativesUrl)
            {
                return TextResponse(NfoCsv);
            }
            if (url == SensexBankNiftyInstrumentMasterProvider.BfoIndexDerivativesUrl)
            {
                return TextResponse(BfoCsv);
            }
            if (url.EndsWith("GetQuotes", StringComparison.Ordinal))
            {
                // The future's own quote, used as the ATM-anchor price (see this resolver's own
                // doc comment on why no spot-index quote is fetched).
                var json = JsonSerializer.Serialize(new { stat = "Ok", lp = "61250.00", c = "61200.00" });
                return JsonResponse(json);
            }
            if (url.EndsWith("GetOptionChain", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var anchorTsym = ExtractTsym(body);
                OptionChainAnchorsRequested.Add(anchorTsym);

                var values = new[]
                {
                    new { exch = "BFO", tsym = anchorTsym + "-CHAIN-CE", token = "999001", optt = "CE", strprc = "61200.00", ls = "20", ti = "0.05" },
                    new { exch = "BFO", tsym = anchorTsym + "-CHAIN-PE", token = "999002", optt = "PE", strprc = "61200.00", ls = "20", ti = "0.05" },
                };
                var json = JsonSerializer.Serialize(new { stat = "Ok", values });
                return JsonResponse(json);
            }

            throw new InvalidOperationException($"Unexpected request in test fake: {request.Method} {url}");
        }

        static string ExtractTsym(string formBody)
        {
            // Body shape: jData={...}&jKey=... -- jData is a JSON object with a "tsym" field.
            var jDataStart = formBody.IndexOf("jData=", StringComparison.Ordinal) + "jData=".Length;
            var jDataEnd = formBody.IndexOf("&jKey=", StringComparison.Ordinal);
            var jData = formBody[jDataStart..jDataEnd];
            using var doc = JsonDocument.Parse(jData);
            return doc.RootElement.GetProperty("tsym").GetString()!;
        }

        static HttpResponseMessage TextResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/csv") };
        static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    static (SensexBankNiftyInstrumentUniverseResolver Resolver, FakeHandler Handler) BuildResolver()
    {
        var handler = new FakeHandler();
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new FlatTradeOptions { UserId = "TESTUID", ApiKey = "k", ApiSecret = "s" });

        var masterProvider = new SensexBankNiftyInstrumentMasterProvider(httpClient, NullLogger<SensexBankNiftyInstrumentMasterProvider>.Instance);
        var authClient = new FlatTradeAuthClient(httpClient, options);
        var resolver = new SensexBankNiftyInstrumentUniverseResolver(masterProvider, authClient, NullLogger<SensexBankNiftyInstrumentUniverseResolver>.Instance);
        return (resolver, handler);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesOnlySensexCurrentWeeklyExpiry_NeverNextWeek()
    {
        var (resolver, handler) = BuildResolver();

        var instruments = await resolver.ResolveAsync("session-token", AsOfDate, CancellationToken.None);

        var sensexOptions = instruments.Where(i => i.Underlying == "SENSEX" && i.InstrumentType == InstrumentType.Option).ToList();
        Assert.NotEmpty(sensexOptions);
        Assert.All(sensexOptions, o => Assert.Equal(new DateOnly(2026, 9, 24), o.ExpiryDate));
        Assert.DoesNotContain(sensexOptions, o => o.ExpiryDate == new DateOnly(2026, 10, 1));

        // Only ONE GetOptionChain call for Sensex, anchored on the CURRENT expiry's own row --
        // never a second call for the next expiry (unlike InstrumentUniverseResolver's own
        // Take(2) loop for Nifty, which deliberately DOES call it twice).
        Assert.Contains("SENSEX26O1580800CE", handler.OptionChainAnchorsRequested);
        Assert.DoesNotContain("SENSEX26OCT80800CE", handler.OptionChainAnchorsRequested);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesOnlyBankNiftyCurrentMonthlyExpiry_NeverNextMonth()
    {
        var (resolver, handler) = BuildResolver();

        var instruments = await resolver.ResolveAsync("session-token", AsOfDate, CancellationToken.None);

        var bankNiftyOptions = instruments.Where(i => i.Underlying == "BANKNIFTY" && i.InstrumentType == InstrumentType.Option).ToList();
        Assert.NotEmpty(bankNiftyOptions);
        Assert.All(bankNiftyOptions, o => Assert.Equal(new DateOnly(2026, 9, 29), o.ExpiryDate));
        Assert.DoesNotContain(bankNiftyOptions, o => o.ExpiryDate == new DateOnly(2026, 10, 27));

        Assert.Contains("BANKNIFTY29SEP26C61200", handler.OptionChainAnchorsRequested);
        Assert.DoesNotContain("BANKNIFTY27OCT26C61200", handler.OptionChainAnchorsRequested);
    }

    [Fact]
    public async Task ResolveAsync_IncludesTheNearestFutureForBothIndices()
    {
        var (resolver, _) = BuildResolver();

        var instruments = await resolver.ResolveAsync("session-token", AsOfDate, CancellationToken.None);

        var sensexFuture = Assert.Single(instruments, i => i.Underlying == "SENSEX" && i.InstrumentType == InstrumentType.Future);
        Assert.Equal("900001", sensexFuture.Token);
        Assert.Equal(Exchange.Bfo, sensexFuture.Exchange);

        var bankNiftyFuture = Assert.Single(instruments, i => i.Underlying == "BANKNIFTY" && i.InstrumentType == InstrumentType.Future);
        Assert.Equal("800001", bankNiftyFuture.Token);
        Assert.Equal(Exchange.Nfo, bankNiftyFuture.Exchange);
    }

    [Fact]
    public async Task ResolveAsync_NeverIncludesAnyNiftyInstrument()
    {
        // The NFO CSV in this fixture also carries a NIFTY row (as the real one does) -- proves
        // this resolver's own underlying filter excludes it, same as InstrumentUniverseResolver's
        // own NIFTY-only filter excludes BANKNIFTY (FlatTradeInstrumentMasterProviderTests).
        var (resolver, _) = BuildResolver();

        var instruments = await resolver.ResolveAsync("session-token", AsOfDate, CancellationToken.None);

        Assert.DoesNotContain(instruments, i => i.Underlying == "NIFTY");
    }
}
