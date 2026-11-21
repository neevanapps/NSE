using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.FlatTrade;

namespace NiftySignal.Tests.Ingestion;

/// <summary>
/// The NIFTY daily universe: ONLY the nearest weekly expiry on or after the as-of date, 15 strikes each side (2026-11; previously two expiries x 10 strikes). Network calls are faked, but the
/// fake master carries a REAL strike ladder and the fake chain behaves like a broker (a window of strikes, both sides), so coverage validation is exercised, not bypassed.
/// </summary>
public sealed class InstrumentUniverseResolverTests
{
    static readonly DateOnly AsOf = new(2026, 11, 20);
    const decimal PrevClose = 25050m;

    static int Token(int expiryNo, decimal strike, bool ce) => expiryNo * 100_000 + (int)(strike / 50m) * 2 + (ce ? 0 : 1);

    // Three weekly expiries with a full 50-point ladder (24000..26200); one stale expiry BEFORE the as-of date that must never be picked.
    static readonly string NfoCsv = BuildMaster();

    static string BuildMaster()
    {
        var sb = new StringBuilder("Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype\n");
        sb.AppendLine("NFO,1,65,NIFTY,NIFTY26NOVFUT,FUTIDX,24-NOV-2026,0.00,");
        foreach (var (no, label, date) in new[] { (1, "18NOV", "18-NOV-2026"), (2, "26NOV", "26-NOV-2026"), (3, "03DEC", "03-DEC-2026"), (4, "10DEC", "10-DEC-2026") })
        {
            for (var k = 24000m; k <= 26200m; k += 50m)
            {
                sb.AppendLine($"NFO,{Token(no, k, true)},65,NIFTY,NIFTY{label}{k:0}CE,OPTIDX,{date},{k:0.00},CE");
                sb.AppendLine($"NFO,{Token(no, k, false)},65,NIFTY,NIFTY{label}{k:0}PE,OPTIDX,{date},{k:0.00},PE");
            }
        }

        return sb.ToString();
    }

    sealed class Fake : HttpMessageHandler
    {
        public readonly List<(string Anchor, string Count)> Chains = [];
        /// <summary>Builds the broker's response values for (anchor, count). Default: every strike within count steps each side of the reference price, CE and PE.</summary>
        public Func<string, int, IEnumerable<object>> Respond = (anchor, count) => Window(2, count, 0, includeBoth: true);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == FlatTradeInstrumentMasterProvider.NfoIndexDerivativesUrl) return new(HttpStatusCode.OK) { Content = new StringContent(NfoCsv, Encoding.UTF8, "text/csv") };
            if (url.EndsWith("GetQuotes", StringComparison.Ordinal)) return Json(new { stat = "Ok", lp = "25100.00", c = PrevClose.ToString("0.00") });
            if (url.EndsWith("GetOptionChain", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var jData = body[(body.IndexOf("jData=", StringComparison.Ordinal) + 6)..body.IndexOf("&jKey=", StringComparison.Ordinal)];
                using var doc = JsonDocument.Parse(jData);
                var tsym = doc.RootElement.GetProperty("tsym").GetString()!;
                var cnt = doc.RootElement.GetProperty("cnt").GetString()!;
                Chains.Add((tsym, cnt));
                return Json(new { stat = "Ok", values = Respond(tsym, int.Parse(cnt)).ToArray() });
            }

            throw new InvalidOperationException("Unexpected request " + url);
        }

        /// <summary>Strikes centre-count .. centre+count (centre = the strike nearest the reference price) of one expiry.</summary>
        public static IEnumerable<object> Window(int expiryNo, int count, int lowerShrink, bool includeBoth)
        {
            for (var k = PrevClose - count * 50m + lowerShrink * 50m; k <= PrevClose + count * 50m; k += 50m)
            {
                yield return Row(expiryNo, k, true);
                if (includeBoth) yield return Row(expiryNo, k, false);
            }
        }

        public static object Row(int expiryNo, decimal k, bool ce) => new { exch = "NFO", tsym = $"X{k:0}{(ce ? "CE" : "PE")}", token = Token(expiryNo, k, ce).ToString(), optt = ce ? "CE" : "PE", strprc = k.ToString("0.00"), ls = "65", ti = "0.05" };

        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    sealed class CaptureLogger : ILogger<InstrumentUniverseResolver>
    {
        public readonly List<(LogLevel Level, string Message)> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }

    static (InstrumentUniverseResolver R, Fake F, CaptureLogger L) Build()
    {
        var fake = new Fake();
        var http = new HttpClient(fake);
        var master = new FlatTradeInstrumentMasterProvider(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<FlatTradeInstrumentMasterProvider>.Instance);
        var auth = new FlatTradeAuthClient(http, Options.Create(new FlatTradeOptions { UserId = "U", ApiKey = "k", ApiSecret = "s" }));
        var log = new CaptureLogger();
        return (new InstrumentUniverseResolver(master, auth, log), fake, log);
    }

    [Fact]
    public async Task Resolves_OnlyTheNearestWeeklyExpiryOnOrAfterAsOf_With15StrikesEachSide()
    {
        var (resolver, fake, log) = Build();
        var instruments = await resolver.ResolveAsync("tok", AsOf, CancellationToken.None);

        var chain = Assert.Single(fake.Chains);                                   // ONE GetOptionChain call, not one per expiry
        Assert.Equal("NIFTY26NOV24000CE", chain.Anchor);                           // anchored on 26-Nov (18-Nov is before the as-of date; 03-Dec / 10-Dec are later weeks)
        Assert.Equal("15", chain.Count);
        Assert.Equal(15, InstrumentUniverseResolver.StrikeCountEachSide);
        var options = instruments.Where(i => i.InstrumentType == InstrumentType.Option).ToList();
        Assert.All(options, o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
        Assert.Equal(options.Count, options.Select(o => o.Token).Distinct().Count());
        var strikes = options.Select(o => o.StrikePrice!.Value).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(24300m, strikes.First());                                     // ATM 25050 - 15 steps
        Assert.Equal(25800m, strikes.Last());                                      // ATM 25050 + 15 steps
        Assert.All(strikes, k => { Assert.Contains(options, o => o.StrikePrice == k && o.OptionType == OptionType.Call); Assert.Contains(options, o => o.StrikePrice == k && o.OptionType == OptionType.Put); });
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Index);
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Future);
        Assert.Contains(instruments, i => i.InstrumentType == InstrumentType.Vix);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning);        // a complete chain is not a warning
    }

    [Fact]
    public async Task AnAlternativeCountSemantics_FewerStrikesThanExpected_IsLoggedAsIncomplete_NotPadded_AndNeverClaims62()
    {
        var (resolver, fake, log) = Build();
        fake.Respond = (_, count) => Fake.Window(2, count, lowerShrink: 1, includeBoth: true);      // broker returned one strike fewer on the lower side
        var instruments = await resolver.ResolveAsync("tok", AsOf, CancellationToken.None);
        var options = instruments.Where(i => i.InstrumentType == InstrumentType.Option).ToList();
        Assert.Equal(60, options.Count);                                                           // exactly what the broker returned: 30 strikes x 2, nothing added
        Assert.All(options, o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
        var warn = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("INCOMPLETE"));
        Assert.Contains("missing 2", warn.Message);                                                // 24300 CE + PE
        Assert.Contains("24300CE", warn.Message);
        Assert.Contains("next expiry is NOT substituted", warn.Message);
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("62"));
    }

    [Fact]
    public async Task AMissingSide_OneCeOnly_IsReportedAsIncompleteCoverage()
    {
        var (resolver, fake, log) = Build();
        fake.Respond = (_, _) => [Fake.Row(2, 25050m, true)];                                      // the old fake's shape: a single CE
        var instruments = await resolver.ResolveAsync("tok", AsOf, CancellationToken.None);
        Assert.Single(instruments, i => i.InstrumentType == InstrumentType.Option);
        var warn = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("INCOMPLETE"));
        Assert.Contains("strikes without both sides 1", warn.Message);
        Assert.Contains("missing 61", warn.Message);                                               // 31 expected strikes x 2 sides - 1 received
    }

    [Fact]
    public async Task DuplicatesAndAnotherExpiry_AreRejected_NeverAcceptedIntoTheNearestExpiryUniverse()
    {
        var (resolver, fake, log) = Build();
        fake.Respond = (a, c) => Fake.Window(2, c, 0, true)
            .Concat(Fake.Window(2, 1, 0, true))                                                    // every near-ATM token and strike/side repeated
            .Concat(Fake.Window(3, 2, 0, true));                                                   // the 03-DEC contracts leaked in
        var instruments = await resolver.ResolveAsync("tok", AsOf, CancellationToken.None);
        var options = instruments.Where(i => i.InstrumentType == InstrumentType.Option).ToList();
        Assert.Equal(62, options.Count);                                                           // the validated complete set, once each
        Assert.Equal(options.Count, options.Select(o => o.Token).Distinct().Count());
        Assert.Equal(options.Count, options.Select(o => (o.StrikePrice, o.OptionType)).Distinct().Count());
        Assert.All(options, o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
        var warn = Assert.Single(log.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("INCOMPLETE"));
        Assert.Contains("wrong-expiry 10", warn.Message);
        Assert.Contains("duplicate-token 6", warn.Message);
    }

    [Fact]
    public async Task EmptyChain_FailsLoudly_InsteadOfStoringAnEmptyUniverse()
    {
        var (resolver, fake, _) = Build();
        fake.Respond = (_, _) => [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("tok", AsOf, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiryDay_TheExpiryOnTheAsOfDateItselfIsTheNearest()
    {
        var (resolver, fake, _) = Build();
        var instruments = await resolver.ResolveAsync("tok", new DateOnly(2026, 11, 26), CancellationToken.None);
        Assert.Equal("NIFTY26NOV24000CE", Assert.Single(fake.Chains).Anchor);
        Assert.All(instruments.Where(i => i.InstrumentType == InstrumentType.Option), o => Assert.Equal(new DateOnly(2026, 11, 26), o.ExpiryDate));
    }

    [Fact]
    public async Task NoExpiryOnOrAfterAsOf_FailsLoudly()
    {
        var (resolver, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync("tok", new DateOnly(2026, 12, 20), CancellationToken.None));
    }
}
