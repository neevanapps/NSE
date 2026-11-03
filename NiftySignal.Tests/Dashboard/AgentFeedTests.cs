using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Dashboard;

/// <summary>The read-only agent feed ("agent-v1"): completed bars only, raw (not display) values, nulls for missing data, loopback-only access outside the login, and an idempotent JSONL writer.</summary>
public sealed class AgentFeedTests : IDisposable
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly DateOnly Today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
    static readonly DateTimeOffset Open930 = new(Today.ToDateTime(new TimeOnly(9, 30)), Ist);
    static readonly DateOnly Expiry = Today.AddDays(6);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "agent-feed-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- fixtures ----

    sealed class ObserverFactory : IDbContextFactory<AdaptiveObserverDbContext>
    {
        readonly DbContextOptions<AdaptiveObserverDbContext> _o = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public AdaptiveObserverDbContext CreateDbContext() => new(_o);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    sealed class SourceFactory : IDbContextFactory<NiftySignalDbContext>
    {
        readonly DbContextOptions<NiftySignalDbContext> _o = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public NiftySignalDbContext CreateDbContext() => new(_o);
        public Task<NiftySignalDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed record World(ObserverFactory Observer, SourceFactory Source)
    {
        public AgentSnapshotService Service() => new(
            new AdaptiveObserverDataService(Observer, NullLogger<AdaptiveObserverDataService>.Instance),
            new AdaptiveQuoteAsOfService(Source, Observer), new VixSummaryService(Source), Observer);

        public AdaptiveObserverDataService Data() => new(Observer, NullLogger<AdaptiveObserverDataService>.Instance);
    }

    static DateTimeOffset End(int seq) => Open930.AddMinutes(seq);

    static string OptionJson() => JsonSerializer.Serialize(new[]
    {
        new ObserverOptionInstrument("CE25000", "CE25000", OptionType.Call, 25000, Expiry, 65),
        new ObserverOptionInstrument("PE25000", "PE25000", OptionType.Put, 25000, Expiry, 65),
    });

    static Instrument Inst(string token, InstrumentType type) => new()
    { Token = token, TradingSymbol = token, Underlying = "NIFTY", InstrumentType = type, Exchange = Exchange.Nfo, AsOfDate = Today };

    static Tick Px(string token, decimal price, DateTimeOffset at, bool depth = false) => new()
    { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at.ToUniversalTime(), ReceivedAt = at.ToUniversalTime(), LastPrice = price,
        Depth = depth ? new MarketDepth(price - 0.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0, price + 0.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0) : null };

    static AdaptiveOptionStrikeBarRow Strike(int seq, OptionType side) => new()
    {
        SessionId = 1, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = End(seq), Expiry = Expiry, Strike = 25000,
        Side = side, Token = side == OptionType.Call ? "CE25000" : "PE25000", TradingSymbol = side == OptionType.Call ? "CE25000" : "PE25000",
        PriceStart = 100, PriceEnd = side == OptionType.Call ? 105 : 96, PriceChange = side == OptionType.Call ? 5 : -4, TotalQuantity = 500,
        StrictBuy = 300, StrictSell = 100, StrictDelta = side == OptionType.Call ? 200 : -50, EnrichedBuy = 300, EnrichedSell = 100, EnrichedDelta = side == OptionType.Call ? 150 : -40,
        OiStart = 1000, OiEnd = side == OptionType.Call ? 1050 : 990, OiDelta = side == OptionType.Call ? 50 : -10,
        IvStart = 15, IvEnd = side == OptionType.Call ? 16 : 14.5, IvChange = side == OptionType.Call ? 1 : -0.5,
        ActivityPerSecond = 3, MicroDevTimeWeighted = side == OptionType.Call ? 0.05 : -0.03, TobTimeWeighted = side == OptionType.Call ? 0.1 : -0.06, TobUsableSeconds = 50,
        NetOfi = side == OptionType.Call ? 20 : -8, GrossOfi = 40,
        DeltaStart = side == OptionType.Call ? 0.5 : -0.5, DeltaEnd = side == OptionType.Call ? 0.51 : -0.51, DeltaChange = side == OptionType.Call ? 0.01 : -0.01,
        VegaStart = 10, VegaEnd = side == OptionType.Call ? 10.2 : 9.7, VegaChange = side == OptionType.Call ? 0.2 : -0.3,
    };

    /// <summary>A session whose runtime says <paramref name="completed"/> bars are complete. The database ALSO holds a row for the next (incomplete) bar, as a race could leave it.</summary>
    static async Task<World> SeedAsync(int completed, bool withStrikeRows = true, bool withVix = true, bool withSpot = true, string basisStatus = "Usable")
    {
        var observer = new ObserverFactory();
        var source = new SourceFactory();
        await using (var db = observer.CreateDbContext())
        {
            db.Sessions.Add(new()
            {
                Id = 1, TradeDate = Today, ModelVersion = AdaptiveFixedClockV1.ModelVersion, SourceBranch = "seed", SourceCommitSha = "0123456789abcdef", BuildUtc = Open930,
                FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = Today.AddDays(20), OpeningWindowStartUtc = Open930.AddMinutes(-15), OpeningWindowEndUtc = Open930,
                EstimatorName = "V1", CreatedAtUtc = Open930, OptionUniverseJson = OptionJson(), WeeklyOptionExpiry = Expiry, BaseBarVolume = 13_650, RollingWindowBars = 3,
                Future0930 = 25000, ResidualCenterStrike = 25000,
            });
            db.Runtime.Add(new() { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = End(completed), LastCompletedBarSeq = completed, CurrentPartialBarVolume = 7_777, CurrentPartialBarStartedAtUtc = End(completed) });
            for (var seq = 1; seq <= completed + 1; seq++)
            {
                var end = End(seq);
                db.FutureBars.Add(new()
                {
                    SessionId = 1, BarSeq = seq, StartAvailableAtUtc = end.AddMinutes(-1), EndAvailableAtUtc = end, Volume = 13_650, TradeUpdates = 10, DurationSeconds = 60,
                    Open = 25000, High = 25010, Low = 24990, Close = 25000 + seq, BarPriceDisplacement = 1.5, StrictDelta = 123, EnrichedDelta = -456, EnrichedBuyVolume = 6000, EnrichedSellVolume = 6000,
                });
                if (seq >= 3)
                {
                    db.RollingStates.Add(new()
                    {
                        SessionId = 1, StartBarSeq = seq - 2, EndBarSeq = seq, StartAvailableAtUtc = end.AddMinutes(-3), EndAvailableAtUtc = end, PriceDisplacement = -7.25, OiChange = 4242, Efficiency = 0.5,
                        State = AdaptiveStateKind.Weak2, StrictDominanceEvolution = "Weakening",
                    });
                }

                foreach (var side in new[] { OptionType.Call, OptionType.Put })
                {
                    db.OptionBandBars.Add(new()
                    {
                        SessionId = 1, BarSeq = seq, Side = side, BandStrikes = "25000", StartAvailableAtUtc = end.AddMinutes(-1), EndAvailableAtUtc = end, CenterStrike = 25000, BandAvailable = true,
                    });
                    if (withStrikeRows)
                    {
                        db.OptionStrikeBars.Add(Strike(seq, side));
                    }
                }

                db.OptionResidualBars.Add(new()
                {
                    SessionId = 1, BarSeq = seq, Variant = ResidualVariant.AtmPlusMinus2, EndAvailableAtUtc = end, Relationship = "ALIGN", IsAvailable = true, CenterStrike = 25000,
                    CEResidualPct = 1.25, PEResidualPct = -2.5, DirectionalResidualPct = 3.75, ResidualDirection = 1, MaxQuoteAgeSeconds = 0.75, CEResidual = 2, PEResidual = 3,
                });
                db.FuturesSupplemental.Add(new()
                {
                    SessionId = 1, BarSeq = seq, MetricsVersion = FuturesMicrostructureBar.MetricsVersion, MicroDevTimeWeighted = 0.1234, TobTimeWeighted = 0.2, TobUsableSeconds = 50,
                    MicroDevUsableSeconds = 50, Ofi = -400, GrossOfi = 1000,
                });
                db.BasisSupplemental.Add(new() { SessionId = 1, BarSeq = seq, MetricsVersion = FuturesBasisBar.MetricsVersion, BasisStart = 100, BasisEnd = 101.75, DeltaBasis = 1.75, Status = basisStatus });
            }

            foreach (var side in new[] { OptionType.Call, OptionType.Put })
            {
                for (var k = 0; k < 5; k++)
                {
                    db.ResidualAnchorComponents.Add(new() { SessionId = 1, Side = side, Strike = 24900 + 50 * k, Token = $"{side}{k}", TradingSymbol = $"{side}{k}", QuoteTimestampUtc = Open930, Price0930 = 20, IsCenterStrike = k == 2 });
                }
            }

            await db.SaveChangesAsync();
        }

        await using (var s = source.CreateDbContext())
        {
            s.Instruments.AddRange(Inst("SPOT", InstrumentType.Index), Inst("VIX", InstrumentType.Vix), Inst("FUT", InstrumentType.Future));
            for (var seq = 1; seq <= completed + 1; seq++)
            {
                var end = End(seq);
                if (withSpot) s.Ticks.Add(Px("SPOT", 24_000 + seq, end.AddSeconds(-2)));
                if (withVix) s.Ticks.Add(Px("VIX", 14 + seq * 0.1m, end.AddSeconds(-20)));
                s.Ticks.Add(Px("CE25000", 100 + seq, end.AddSeconds(-3), depth: true));
                s.Ticks.Add(Px("PE25000", 90 + seq, end.AddSeconds(-3)));                                  // no depth: bid / ask stay null
            }

            if (withSpot) s.Ticks.Add(Px("SPOT", 99_999, End(completed).AddSeconds(5)));                    // a tick AFTER bar `completed` ended must never reach that bar
            if (withVix) s.Ticks.Add(Px("VIX", 77, End(completed).AddSeconds(5)));
            await s.SaveChangesAsync();
        }

        return new World(observer, source);
    }

    static JsonElement Json(AgentSnapshotResponse response) => JsonDocument.Parse(JsonSerializer.Serialize(response, AgentFeedJson.Options)).RootElement;

    AgentFeedWriter Writer(World w, TimeProvider? clock = null, int settleSeconds = 0) => new(
        w.Service(), w.Data(), Options.Create(new AgentFeedOptions { RootDirectory = _dir, SettleSeconds = settleSeconds }), NullLogger<AgentFeedWriter>.Instance, clock);

    string FeedFile => new AgentFeedOptions { RootDirectory = _dir }.FilePath(Today);

    // ---- completed only, bars range ----

    [Fact]
    public async Task Snapshot_ReturnsOnlyCompletedBars_NeverTheIncompleteBarThatAlreadyHasARow()
    {
        var w = await SeedAsync(completed: 6);                       // the database also holds a row for bar 7
        var json = Json(await w.Service().SnapshotAsync(300));
        var seqs = json.GetProperty("bars").EnumerateArray().Select(b => b.GetProperty("barSeq").GetInt32()).ToList();
        Assert.Equal(Enumerable.Range(1, 6), seqs);
        Assert.All(json.GetProperty("bars").EnumerateArray(), b => Assert.True(b.GetProperty("isCompleted").GetBoolean()));
        Assert.Equal(6, json.GetProperty("meta").GetProperty("latestCompletedBarSeq").GetInt32());
        var text = json.GetRawText();
        Assert.DoesNotContain("\"volume\":7777", text);                        // the live partial-bar volume is not part of the feed
        Assert.DoesNotContain("\"spot\":99999", text);                       // a tick after the boundary is not either
    }

    [Theory]
    [InlineData(null, 20, 6, 25)]
    [InlineData(3, 3, 23, 25)]
    [InlineData(1, 1, 25, 25)]
    [InlineData(300, 25, 1, 25)]
    public async Task Snapshot_BarsParameter_SelectsTheLatestNCompletedBars(int? bars, int expectedCount, int firstSeq, int lastSeq)
    {
        var w = await SeedAsync(completed: 25);
        var json = Json(await w.Service().SnapshotAsync(bars ?? AgentSnapshotService.DefaultBars));
        var seqs = json.GetProperty("bars").EnumerateArray().Select(b => b.GetProperty("barSeq").GetInt32()).ToList();
        Assert.Equal(expectedCount, seqs.Count);
        Assert.Equal(firstSeq, seqs[0]);
        Assert.Equal(lastSeq, seqs[^1]);
        Assert.Equal(seqs.OrderBy(x => x), seqs);                    // oldest first
    }

    [Theory]
    [InlineData(null, true, 20)]
    [InlineData("", true, 20)]
    [InlineData("1", true, 1)]
    [InlineData("300", true, 300)]
    [InlineData("0", false, 0)]
    [InlineData("301", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("1.5", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("99999999999", false, 0)]
    public void BarsParameter_DefaultIs20_AndOnlyOneToThreeHundredIsAccepted(string? raw, bool ok, int expected)
    {
        Assert.Equal(ok, AgentFeedEndpoint.TryParseBars(raw, out var bars));
        if (ok) Assert.Equal(expected, bars);
    }

    [Fact]
    public async Task Snapshot_WithoutASessionOrCompletedBar_IsAnEmptyFeedWithAReason()
    {
        var none = new World(new ObserverFactory(), new SourceFactory());
        var empty = await none.Service().SnapshotAsync(20);
        Assert.Empty(empty.Bars);
        Assert.False(empty.Meta.DataOk);
        Assert.Contains("no adaptive session today", empty.Meta.DataIssues);
        var fresh = await SeedAsync(completed: 0);
        var noBars = await fresh.Service().SnapshotAsync(20);
        Assert.Empty(noBars.Bars);
        Assert.Contains("no completed bar yet", noBars.Meta.DataIssues);
    }

    // ---- payload: raw values, units, nulls ----

    [Fact]
    public async Task Payload_IsRaw_MicroDevNotInverted_DeltaAndVegaNotScaled()
    {
        var w = await SeedAsync(completed: 6);
        var json = Json(await w.Service().SnapshotAsync(1));
        var bar = json.GetProperty("bars")[0];
        Assert.Equal("agent-v1", json.GetProperty("meta").GetProperty("schema").GetString());
        Assert.Equal(0.1234, bar.GetProperty("futures").GetProperty("microDevRaw").GetDouble(), 12);                    // the Dashboard shows -0.1234 (Inv)
        var ce = bar.GetProperty("options").GetProperty("ce");
        var pe = bar.GetProperty("options").GetProperty("pe");
        Assert.Equal(0.05, ce.GetProperty("microDevRaw").GetDouble(), 12);
        Assert.Equal(-0.03, pe.GetProperty("microDevRaw").GetDouble(), 12);
        Assert.Equal(0.01, ce.GetProperty("deltaChange").GetDouble(), 12);                                                // not 1.0 (x100)
        Assert.Equal(0.51, ce.GetProperty("delta").GetDouble(), 12);
        Assert.Equal(0.2, ce.GetProperty("vegaChangePer1Vol").GetDouble(), 12);
        Assert.Equal(10.2, ce.GetProperty("vegaPer1Vol").GetDouble(), 12);
        Assert.Equal(16, ce.GetProperty("ivPoints").GetDouble(), 12);
        var dir = bar.GetProperty("options").GetProperty("dir");
        Assert.Equal(0.08, dir.GetProperty("microDevRaw").GetProperty("dir").GetProperty("raw").GetDouble(), 12);        // CE - PE of the raw values, positive: no inversion
        Assert.Equal(0.02, dir.GetProperty("delta").GetProperty("dir").GetProperty("raw").GetDouble(), 12);              // CE change - PE change, unscaled
    }

    [Fact]
    public async Task Payload_OiDirectionIsPeMinusCe_AllOtherDirectionsAreCeMinusPe()
    {
        var w = await SeedAsync(completed: 4);
        var dir = Json(await w.Service().SnapshotAsync(1)).GetProperty("bars")[0].GetProperty("options").GetProperty("dir");
        Assert.Equal(-60d, dir.GetProperty("oi").GetProperty("dir").GetProperty("raw").GetDouble());                      // ce 50, pe -10: PE - CE = -60
        Assert.Equal(250d, dir.GetProperty("strict").GetProperty("dir").GetProperty("raw").GetDouble());                   // 200 - (-50)
        Assert.Equal(9d, dir.GetProperty("price").GetProperty("dir").GetProperty("raw").GetDouble());                      // 5 - (-4)
    }

    [Fact]
    public async Task Payload_CarriesTheFuturesFieldsAndMeta()
    {
        var w = await SeedAsync(completed: 6);
        var json = Json(await w.Service().SnapshotAsync(1));
        var meta = json.GetProperty("meta");
        Assert.False(string.IsNullOrEmpty(meta.GetProperty("buildSha").GetString()));
        Assert.Equal("seed / 01234567", meta.GetProperty("sessionSource").GetString());
        Assert.Equal(13_650, meta.GetProperty("clock").GetProperty("baseBarVolume").GetInt64());
        Assert.Equal("fixedVolume", meta.GetProperty("clock").GetProperty("kind").GetString());
        Assert.Equal(Expiry.ToString("yyyy-MM-dd"), meta.GetProperty("expiry").GetString());
        Assert.Equal(25000, meta.GetProperty("selectedStrike").GetDouble());
        Assert.Equal(25000, meta.GetProperty("centerStrike").GetDouble());
        var bar = json.GetProperty("bars")[0];
        Assert.Equal(End(6).ToOffset(Ist).ToString("yyyy-MM-dd'T'HH:mm:sszzz"), bar.GetProperty("endIst").GetString());
        Assert.Equal(60, bar.GetProperty("durationSec").GetDouble());
        var f = bar.GetProperty("futures");
        Assert.Equal(25006, f.GetProperty("price").GetDouble());
        Assert.Equal(1.5, f.GetProperty("priceDelta").GetDouble());
        Assert.Equal(-7.25, f.GetProperty("rollDeltaPx").GetDouble());
        Assert.Equal(13_650, f.GetProperty("volume").GetInt64());
        Assert.Equal(123, f.GetProperty("strictDelta").GetInt64());
        Assert.Equal(-456, f.GetProperty("enrichedDelta").GetInt64());
        Assert.Equal(-400, f.GetProperty("ofi").GetInt64());
        Assert.Equal(4242, f.GetProperty("rollOiDelta").GetInt64());
        Assert.Equal(1.75, f.GetProperty("basisDelta").GetDouble());
        Assert.Equal(0.5, f.GetProperty("rollEfficiency").GetDouble());
        Assert.Equal("Weak2", f.GetProperty("state").GetString());
        Assert.Equal("Weakening", f.GetProperty("evolution").GetString());
        var r = bar.GetProperty("residual");
        Assert.True(r.GetProperty("available").GetBoolean());
        Assert.Equal(1.25, r.GetProperty("ceResPct").GetDouble());
        Assert.Equal(-2.5, r.GetProperty("peResPct").GetDouble());
        Assert.Equal(3.75, r.GetProperty("directionalResPct").GetDouble());
        Assert.Equal("UP", r.GetProperty("direction").GetString());
        Assert.Equal("ALIGN", r.GetProperty("relation").GetString());
        Assert.Equal(0.75, r.GetProperty("quoteAgeSec").GetDouble());
        Assert.True(bar.GetProperty("dataOk").GetBoolean());
    }

    [Fact]
    public async Task Payload_SpotVixAndOptionQuotes_AreAsOfTheBarBoundary_NeverLater()
    {
        var w = await SeedAsync(completed: 6);
        var bar = Json(await w.Service().SnapshotAsync(1)).GetProperty("bars")[0];
        var m = bar.GetProperty("market");
        Assert.Equal(24_006d, m.GetProperty("spot").GetDouble());                          // not the 99,999 tick 5 s after the boundary
        Assert.Equal(2d, m.GetProperty("spotAgeSec").GetDouble());
        Assert.Equal(14.6d, m.GetProperty("vix").GetDouble(), 9);                          // not 77
        Assert.Equal(0.1d, m.GetProperty("vixChange").GetDouble(), 9);                    // bar end 14.6 vs bar start 14.5
        var ce = bar.GetProperty("options").GetProperty("ce");
        Assert.Equal(106.5d, ce.GetProperty("ask").GetDouble());                           // depth of the last tick at or before the boundary
        Assert.Equal(105.5d, ce.GetProperty("bid").GetDouble());
        Assert.Equal(JsonValueKind.Null, bar.GetProperty("options").GetProperty("pe").GetProperty("bid").ValueKind);   // that tick had no depth: null, not 0
    }

    [Fact]
    public async Task MissingData_IsNullNeverZero_AndDataOkSaysWhy()
    {
        var w = await SeedAsync(completed: 5, withStrikeRows: false, withVix: false, withSpot: false, basisStatus: FuturesBasisBar.StatusStaleEnd);
        var bar = Json(await w.Service().SnapshotAsync(1)).GetProperty("bars")[0];
        Assert.False(bar.GetProperty("dataOk").GetBoolean());
        var issues = bar.GetProperty("dataIssues").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("fixed-strike CE row missing", issues);
        Assert.Contains("fixed-strike PE row missing", issues);
        Assert.Contains("spot missing", issues);
        Assert.Contains("VIX missing", issues);
        Assert.Contains($"basis {FuturesBasisBar.StatusStaleEnd}", issues);
        var ce = bar.GetProperty("options").GetProperty("ce");
        Assert.False(ce.GetProperty("rowAvailable").GetBoolean());
        foreach (var name in new[] { "price", "delta", "vegaPer1Vol", "ivPoints", "strictDelta", "oiChange", "microDevRaw", "ofi", "activityPerSec" })
        {
            Assert.Equal(JsonValueKind.Null, ce.GetProperty(name).ValueKind);
        }

        var dir = bar.GetProperty("options").GetProperty("dir");
        Assert.Equal(JsonValueKind.Null, dir.GetProperty("strict").GetProperty("dir").GetProperty("raw").ValueKind);
        var market = bar.GetProperty("market");
        Assert.Equal(JsonValueKind.Null, market.GetProperty("spot").ValueKind);
        Assert.Equal(JsonValueKind.Null, market.GetProperty("vix").ValueKind);
        Assert.False(Json(await w.Service().SnapshotAsync(1)).GetProperty("meta").GetProperty("dataOk").GetBoolean());
    }

    // ---- loopback guard ----

    static DefaultHttpContext Request(string? remote, string host = "localhost:5210", params (string Name, string Value)[] headers)
    {
        var c = new DefaultHttpContext();
        c.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        c.Request.Host = new HostString(host);
        foreach (var (name, value) in headers) c.Request.Headers[name] = value;
        return c;
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_DirectCallers_AreAccepted(string remote) => Assert.Null(AgentFeedEndpoint.RejectReason(Request(remote)));

    [Theory]
    [InlineData("100.105.67.79")]            // the VM's Tailscale address
    [InlineData("100.64.0.9")]               // another tailnet peer
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("8.8.8.8")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    public void NonLoopback_Callers_AreRejected(string remote) => Assert.NotNull(AgentFeedEndpoint.RejectReason(Request(remote)));

    [Fact]
    public void UnknownRemoteAddress_IsRejected() => Assert.NotNull(AgentFeedEndpoint.RejectReason(Request(null)));

    [Theory]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-For", "100.64.0.9")]
    [InlineData("Forwarded", "for=100.64.0.9")]
    [InlineData("X-Real-IP", "127.0.0.1")]
    [InlineData("X-Forwarded-Host", "dashboard.example")]
    public void ForwardingHeaders_AreNeverTrusted_ARequestThroughAProxyIsRefused(string header, string value) =>
        Assert.NotNull(AgentFeedEndpoint.RejectReason(Request("127.0.0.1", "localhost:5210", (header, value))));

    [Theory]
    [InlineData("localhost:5210", true)]
    [InlineData("127.0.0.1:5210", true)]
    [InlineData("[::1]:5210", true)]
    [InlineData("localhost", true)]
    [InlineData("evil.example:5210", false)]
    [InlineData("100.105.67.79:5210", false)]
    public void HostHeader_MustNameALoopbackHost_AgainstDnsRebinding(string host, bool accepted) =>
        Assert.Equal(accepted, AgentFeedEndpoint.RejectReason(Request("127.0.0.1", host)) is null);

    // ---- the endpoint in a real pipeline, next to the Dashboard's cookie login ----

    async Task<(WebApplication App, HttpClient Client)> StartAsync(World w)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "agent-feed-tests" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(w.Observer);
        builder.Services.AddSingleton<IDbContextFactory<NiftySignalDbContext>>(w.Source);
        builder.Services.AddSingleton<AdaptiveObserverDataService>();
        builder.Services.AddSingleton<AdaptiveQuoteAsOfService>();
        builder.Services.AddSingleton<VixSummaryService>();
        builder.Services.AddSingleton<AgentSnapshotService>();
        // The same authentication shape as Program.cs: cookie scheme, login enforced per endpoint / page, no fallback policy.
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => o.LoginPath = "/login");
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        AgentFeedEndpoint.Map(app);
        app.MapGet("/protected", () => "dashboard page").RequireAuthorization();
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    [Fact]
    public async Task Endpoint_OnLoopback_ServesTheFeedWithoutTheDashboardLogin_WhileLoginProtectedRoutesStillChallenge()
    {
        var w = await SeedAsync(completed: 6);
        var (app, client) = await StartAsync(w);
        await using var _ = app;
        using var __ = client;

        var ok = await client.GetAsync("/api/agent/snapshot?bars=3");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("application/json", ok.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", ok.Headers.CacheControl?.ToString());
        Assert.False(ok.Headers.Contains("Set-Cookie"));                                  // it neither needs nor creates a Dashboard session
        var json = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, json.GetProperty("bars").GetArrayLength());
        Assert.Equal("agent-v1", json.GetProperty("meta").GetProperty("schema").GetString());

        var protectedRoute = await client.GetAsync("/protected");                          // same pipeline: the login is still enforced where it is configured
        Assert.Equal(HttpStatusCode.Redirect, protectedRoute.StatusCode);
        Assert.Contains("/login", protectedRoute.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Endpoint_RejectsBadBars_NonGetMethods_ProxiedAndWrongHostRequests()
    {
        var w = await SeedAsync(completed: 6);
        var (app, client) = await StartAsync(w);
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/agent/snapshot?bars=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/agent/snapshot?bars=301")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/agent/snapshot?bars=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/agent/snapshot?bars=300")).StatusCode);
        Assert.Equal(6, JsonDocument.Parse(await client.GetStringAsync("/api/agent/snapshot")).RootElement.GetProperty("bars").GetArrayLength());   // default 20 > 6 available

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/agent/snapshot", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.DeleteAsync("/api/agent/snapshot")).StatusCode);

        var proxied = new HttpRequestMessage(HttpMethod.Get, "/api/agent/snapshot");
        proxied.Headers.Add("X-Forwarded-For", "100.64.0.9");
        var refused = await client.SendAsync(proxied);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("100.64.0.9", await refused.Content.ReadAsStringAsync());

        var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/agent/snapshot");
        rebound.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebound)).StatusCode);
    }

    // ---- JSONL ----

    static List<JsonElement> Lines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => { try { return (JsonElement?)JsonDocument.Parse(l).RootElement; } catch (JsonException) { return null; } })
            .Where(e => e is not null).Select(e => e!.Value).ToList();

    [Fact]
    public async Task Jsonl_AppendsOneLinePerCompletedBar_TheSameObjectAsTheApiBar_AndNeverTheProvisionalBar()
    {
        var w = await SeedAsync(completed: 6);
        var written = await Writer(w).WriteNewBarsAsync();
        Assert.Equal(6, written);
        var lines = Lines(FeedFile);
        Assert.Equal(Enumerable.Range(1, 6), lines.Select(l => l.GetProperty("barSeq").GetInt32()));        // bar 7 has a row but is not complete
        var api = Json(await w.Service().SnapshotAsync(300)).GetProperty("bars");
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(api[i].GetRawText(), lines[i].GetRawText());                                         // identical shape and values
        }

        Assert.Equal(Today.ToString("yyyy-MM-dd") + ".jsonl", Path.GetFileName(FeedFile));
        Assert.EndsWith("agent-feed", Path.GetDirectoryName(FeedFile));
    }

    [Fact]
    public async Task Jsonl_IsIdempotentOnBarSeq_AcrossPassesRestartsAndABackfill()
    {
        var w = await SeedAsync(completed: 6);
        Assert.Equal(6, await Writer(w).WriteNewBarsAsync());
        var first = Writer(w);
        Assert.Equal(0, await first.WriteNewBarsAsync());                                                      // nothing new
        Assert.Equal(0, await Writer(w).WriteNewBarsAsync());                                                  // a restarted process re-reads the file: still nothing
        Assert.Equal(6, Lines(FeedFile).Count);

        // Bar 7 completes (the runtime advances); only that bar is appended, by the old and by a restarted writer alike.
        await using (var db = w.Observer.CreateDbContext())
        {
            (await db.Runtime.SingleAsync()).LastCompletedBarSeq = 7;
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await first.WriteNewBarsAsync());
        Assert.Equal(0, await Writer(w).WriteNewBarsAsync());
        var seqs = Lines(FeedFile).Select(l => l.GetProperty("barSeq").GetInt32()).ToList();
        Assert.Equal(Enumerable.Range(1, 7), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
    }

    [Fact]
    public async Task Jsonl_RestartMidSession_BackfillsOnlyTheMissingBars_AndKeepsExistingLinesUntouched()
    {
        var w = await SeedAsync(completed: 6);
        Assert.Equal(6, await Writer(w).WriteNewBarsAsync());
        var all = File.ReadAllLines(FeedFile);
        File.WriteAllLines(FeedFile, all.Where((_, i) => i != 2 && i != 3));                                   // bars 3 and 4 never made it to disk
        var before = File.ReadAllLines(FeedFile);

        Assert.Equal(2, await Writer(w).WriteNewBarsAsync());
        var after = File.ReadAllLines(FeedFile);
        Assert.Equal(before, after.Take(before.Length));                                                       // appended at the end, earlier lines byte-identical
        Assert.Equal(new[] { 1, 2, 5, 6, 3, 4 }, Lines(FeedFile).Select(l => l.GetProperty("barSeq").GetInt32()));
    }

    [Fact]
    public async Task Jsonl_ATornLastLine_IsLeftAlone_AndItsBarIsWrittenAgainInFull()
    {
        var w = await SeedAsync(completed: 3);
        Assert.Equal(3, await Writer(w).WriteNewBarsAsync());
        var lines = File.ReadAllLines(FeedFile);
        File.WriteAllText(FeedFile, string.Join('\n', lines.Take(2)) + "\n" + lines[2][..40]);                  // crash mid-write of bar 3: no newline, invalid JSON

        Assert.Equal(1, await Writer(w).WriteNewBarsAsync());
        var good = Lines(FeedFile);
        Assert.Equal(new[] { 1, 2, 3 }, good.Select(l => l.GetProperty("barSeq").GetInt32()));
        Assert.Equal(lines[2], File.ReadAllLines(FeedFile).Last());
    }

    [Fact]
    public async Task Jsonl_TheNewestBarWaitsForItsSettleTime_OlderBarsDoNot()
    {
        var w = await SeedAsync(completed: 6);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var writer = Writer(w, clock, settleSeconds: 10);
        Assert.Equal(5, await writer.WriteNewBarsAsync());                                                     // bars 1..5 at once; bar 6 is still settling
        Assert.Equal(5, Lines(FeedFile).Count);
        clock.Now = clock.Now.AddSeconds(9);
        Assert.Equal(0, await writer.WriteNewBarsAsync());
        clock.Now = clock.Now.AddSeconds(2);
        Assert.Equal(1, await writer.WriteNewBarsAsync());
        Assert.Equal(Enumerable.Range(1, 6), Lines(FeedFile).Select(l => l.GetProperty("barSeq").GetInt32()));
    }

    [Fact]
    public async Task Jsonl_WithoutASession_WritesNothingAndCreatesNoFile()
    {
        var none = new World(new ObserverFactory(), new SourceFactory());
        Assert.Equal(0, await Writer(none).WriteNewBarsAsync());
        Assert.False(Directory.Exists(Path.GetDirectoryName(FeedFile)));
    }

    [Fact]
    public void Options_FolderIsConfigurable_AndTheFileIsNamedByTheTradeDate()
    {
        var o = new AgentFeedOptions { RootDirectory = @"D:\data\feeds" };
        Assert.Equal(Path.Combine(@"D:\data\feeds", "agent-feed", "2026-11-03.jsonl"), o.FilePath(new DateOnly(2026, 11, 3)));
        var relative = new AgentFeedOptions { RootDirectory = "data" };
        Assert.StartsWith(AppContext.BaseDirectory, relative.FeedDirectory());
        Assert.True(new AgentFeedOptions().JsonlEnabled);
        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AgentFeed:RootDirectory"] = "X", ["AgentFeed:JsonlEnabled"] = "false" }).Build().GetSection(AgentFeedOptions.SectionName).Get<AgentFeedOptions>()!;
        Assert.Equal("X", bound.RootDirectory);
        Assert.False(bound.JsonlEnabled);
    }
}
