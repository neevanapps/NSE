using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Abstractions;
using NiftySignal.Domain.Entities;
using NiftySignal.Host;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Ingestion;

/// <summary>MarketData:CollectSensexBankNifty - the pause switch must cover fresh resolution AND reuse of today's stored rows after a restart, never touch stored data or NIFTY, and leave behaviour unchanged when on.</summary>
public sealed class SensexBankNiftyCollectionSwitchTests
{
    static readonly DateOnly Day = new(2026, 11, 20);

    const string NfoCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        NFO,1,65,NIFTY,NIFTY26NOVFUT,FUTIDX,24-NOV-2026,0.00,
        NFO,3,65,NIFTY,NIFTY26NOV25000CE,OPTIDX,26-NOV-2026,25000.00,CE
        NFO,800001,30,BANKNIFTY,BANKNIFTY29NOV26F,FUTIDX,29-NOV-2026,-0.01,
        NFO,800002,30,BANKNIFTY,BANKNIFTY29NOV26C61200,OPTIDX,29-NOV-2026,61200.00,CE
        """;

    const string BfoCsv = """
        Exchange,Token,Lotsize,Symbol,Tradingsymbol,Instrument,Expiry,Strike,Optiontype
        BFO,900001,20,SENSEX,SENSEX26NOVFUT,FUTIDX,26-NOV-2026,0.00,
        BFO,900002,20,SENSEX,SENSEX26N1580800CE,OPTIDX,26-NOV-2026,80800.00,CE
        """;

    sealed class Fake : HttpMessageHandler
    {
        static int _n;
        public int BfoRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == FlatTradeInstrumentMasterProvider.NfoIndexDerivativesUrl) return Task.FromResult(Text(NfoCsv));
            if (url == SensexBankNiftyInstrumentMasterProvider.BfoIndexDerivativesUrl) { BfoRequests++; return Task.FromResult(Text(BfoCsv)); }
            if (url.EndsWith("GetQuotes", StringComparison.Ordinal)) return Task.FromResult(Json(new { stat = "Ok", lp = "25100.00", c = "25050.00" }));
            if (url.EndsWith("GetOptionChain", StringComparison.Ordinal))
            {
                var token = Interlocked.Increment(ref _n) + 5_000;
                return Task.FromResult(Json(new { stat = "Ok", values = new[] { new { exch = "NFO", tsym = "CHAIN" + token, token = token.ToString(), optt = "CE", strprc = "25050.00", ls = "65", ti = "0.05" } } }));
            }

            throw new InvalidOperationException("Unexpected request " + url);
        }

        static HttpResponseMessage Text(string b) => new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "text/csv") };
        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    sealed class Harness(string dbName, Fake http)
    {
        public Fake Http { get; } = http;

        public NiftySignalDbContext NewDb() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(dbName).Options);

        /// <summary>A worker over the SAME database and fake network, i.e. a restart of the process, with the switch set as given.</summary>
        public MarketDataIngestionWorker Worker(bool collect)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => NewDb());
            services.AddSingleton(new HttpClient(Http));
            services.AddSingleton(Options.Create(new FlatTradeOptions { UserId = "U", ApiKey = "k", ApiSecret = "s" }));
            services.AddScoped(sp => new FlatTradeAuthClient(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IOptions<FlatTradeOptions>>()));
            services.AddScoped(sp => new FlatTradeInstrumentMasterProvider(sp.GetRequiredService<HttpClient>(), NullLogger<FlatTradeInstrumentMasterProvider>.Instance));
            services.AddScoped<IInstrumentMasterProvider>(sp => sp.GetRequiredService<FlatTradeInstrumentMasterProvider>());
            services.AddScoped(sp => new SensexBankNiftyInstrumentMasterProvider(sp.GetRequiredService<HttpClient>(), NullLogger<SensexBankNiftyInstrumentMasterProvider>.Instance));
            services.AddScoped(sp => new InstrumentUniverseResolver(sp.GetRequiredService<IInstrumentMasterProvider>(), sp.GetRequiredService<FlatTradeAuthClient>(), NullLogger<InstrumentUniverseResolver>.Instance));
            services.AddScoped(sp => new SensexBankNiftyInstrumentUniverseResolver(sp.GetRequiredService<SensexBankNiftyInstrumentMasterProvider>(), sp.GetRequiredService<FlatTradeAuthClient>(), NullLogger<SensexBankNiftyInstrumentUniverseResolver>.Instance));
            return new MarketDataIngestionWorker(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), null!, null!, null!, null!, null!, null!,
                Options.Create(new MarketDataCollectionOptions { CollectSensexBankNifty = collect }), NullLogger<MarketDataIngestionWorker>.Instance);
        }

        public async Task<int> StoredOtherAsync()
        {
            await using var db = NewDb();
            return (await db.Instruments.ToListAsync()).Count(IsOther);
        }
    }

    static Harness NewHarness() => new(Guid.NewGuid().ToString(), new Fake());

    static bool IsOther(Instrument i) => i.Underlying is "SENSEX" or "BANKNIFTY";

    [Fact]
    public void ShippedDefault_IsPaused_InCodeAndInHostAppsettings()
    {
        Assert.False(new MarketDataCollectionOptions().CollectSensexBankNifty);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NiftySignal.Host", "appsettings.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "NiftySignal.Host", "appsettings.json")));
        Assert.False(doc.RootElement.GetProperty("MarketData").GetProperty("CollectSensexBankNifty").GetBoolean());
    }

    [Fact]
    public async Task SwitchOff_FreshResolve_SubscribesNoSensexOrBankNifty_AndNeverCallsTheirResolver()
    {
        var h = NewHarness();
        var resolved = await h.Worker(collect: false).ResolveInstrumentsAsync("tok", Day, default);
        Assert.Contains(resolved, i => i.Underlying == "NIFTY");
        Assert.DoesNotContain(resolved, IsOther);
        Assert.Equal(0, h.Http.BfoRequests);                                       // the SENSEX resolver was not even attempted
        Assert.Equal(0, await h.StoredOtherAsync());                               // and nothing was written
    }

    [Fact]
    public async Task SwitchOff_ReuseOfTodaysStoredRows_SubscribesNoSensexOrBankNifty_AndLeavesThemStored()
    {
        var h = NewHarness();
        await h.Worker(collect: true).ResolveInstrumentsAsync("tok", Day, default);     // earlier today, collection was ON: NIFTY + SENSEX + BANKNIFTY are stored
        var stored = await h.StoredOtherAsync();
        Assert.True(stored > 0);

        var resolved = await h.Worker(collect: false).ResolveInstrumentsAsync("tok", Day, default);     // restart with the switch OFF
        Assert.Contains(resolved, i => i.Underlying == "NIFTY");
        Assert.DoesNotContain(resolved, IsOther);
        Assert.Equal(stored, await h.StoredOtherAsync());                          // already-stored instruments untouched
    }

    [Fact]
    public async Task SwitchOn_BehaviourIsUnchanged_FreshAndReuse()
    {
        var h = NewHarness();
        var worker = h.Worker(collect: true);
        var fresh = await worker.ResolveInstrumentsAsync("tok", Day, default);
        Assert.Contains(fresh, i => i.Underlying == "SENSEX");
        Assert.Contains(fresh, i => i.Underlying == "BANKNIFTY");
        Assert.Contains(fresh, i => i.Underlying == "NIFTY");
        var calls = h.Http.BfoRequests;

        var again = await h.Worker(collect: true).ResolveInstrumentsAsync("tok", Day, default);   // restart: reused from the database, no new resolution
        Assert.Equal(calls, h.Http.BfoRequests);
        Assert.Equal(fresh.Select(i => i.Token).OrderBy(x => x), again.Select(i => i.Token).OrderBy(x => x));
    }
}
