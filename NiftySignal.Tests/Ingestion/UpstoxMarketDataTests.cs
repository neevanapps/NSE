using System.Net;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Ingestion.Upstox;
using NiftySignal.Ingestion.Upstox.Protocol;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Ingestion;

public sealed class UpstoxMarketDataTests
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateTimeOffset Now = new(2026, 10, 7, 4, 0, 0, TimeSpan.Zero);

    static FeedResponse Frame(long volume = 650, long time = 1791345600000) => new()
    {
        Type = NiftySignal.Ingestion.Upstox.Protocol.Type.LiveFeed,
        CurrentTs = time,
        Feeds = { ["NSE_FO|12345"] = new Feed
        {
            FullFeed = new FullFeed { MarketFF = new MarketFullFeed
            {
                Ltpc = new LTPC { Ltp = 25000, Ltt = time - 10000 }, Vtt = volume, Oi = 1300,
                MarketLevel = new MarketLevel { BidAskQuote = { Enumerable.Range(0, 5).Select(i => new Quote { BidP = 24999 - i, AskP = 25001 + i, BidQ = 65 * (i + 1), AskQ = 130 * (i + 1) }) } },
            } },
        } },
    };

    [Fact]
    public void Mapper_ProtobufRoundTrip_UsesMessageClockAndCumulativeVolume()
    {
        var raw = Frame();
        var parsed = FeedResponse.Parser.ParseFrom(raw.ToByteArray());
        var mapper = new UpstoxFeedMapper();
        var tick = Assert.Single(mapper.Map(parsed, Now));
        Assert.Equal(MarketDataProvider.Upstox, tick.Provider);
        Assert.Equal("UP:NSE_FO|12345", tick.Token);
        Assert.Equal(650, tick.Volume); // contracts; never multiply by lot size
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(raw.CurrentTs), tick.ExchangeTimestamp);
        Assert.Equal(tick.ExchangeTimestamp.AddSeconds(-10), tick.LastTradeTimestamp);
        Assert.Equal(Now, tick.ReceivedAt);
        Assert.True(tick.IsSnapshot);
        Assert.Equal(24995m, tick.Depth!.Bid5Price);
        Assert.Equal(650, tick.Depth.Ask5Qty);
        var next = Assert.Single(mapper.Map(Frame(715, raw.CurrentTs + 100), Now.AddMilliseconds(100)));
        Assert.False(next.IsSnapshot);
        Assert.True(Assert.Single(new UpstoxFeedMapper().Map(Frame(715), Now)).IsSnapshot);
    }

    [Fact]
    public void Mapper_DepthOnlyUpdate_DoesNotUseOldTradeClock()
    {
        var mapper = new UpstoxFeedMapper();
        var first = Assert.Single(mapper.Map(Frame(), Now));
        var quote = Frame(650, Frame().CurrentTs + 500);
        quote.Feeds["NSE_FO|12345"].FullFeed.MarketFF.Ltpc.Ltt = first.LastTradeTimestamp!.Value.ToUnixTimeMilliseconds();
        quote.Feeds["NSE_FO|12345"].FullFeed.MarketFF.MarketLevel.BidAskQuote[0].BidQ = 999;
        var next = Assert.Single(mapper.Map(quote, Now.AddMilliseconds(500)));
        Assert.Equal(first.LastTradeTimestamp, next.LastTradeTimestamp);
        Assert.Equal(first.ExchangeTimestamp.AddMilliseconds(500), next.ExchangeTimestamp);
        Assert.Equal(first.Volume, next.Volume);
        Assert.Equal(999, next.Depth!.Bid1Qty);
    }

    [Fact]
    public void Mapper_IndexHasNoSyntheticVolumeOiOrDepth()
    {
        var response = new FeedResponse { CurrentTs = Frame().CurrentTs, Type = NiftySignal.Ingestion.Upstox.Protocol.Type.InitialFeed };
        response.Feeds["NSE_INDEX|India VIX"] = new Feed { FullFeed = new FullFeed { IndexFF = new IndexFullFeed { Ltpc = new LTPC { Ltp = 14.5 } } } };
        var tick = Assert.Single(new UpstoxFeedMapper().Map(response, Now));
        Assert.Equal(0, tick.Volume); Assert.Null(tick.OpenInterest); Assert.Null(tick.Depth); Assert.Null(tick.LastTradeTimestamp);
    }

    [Fact]
    public void Mapper_MarketStatusIsNotATick_AndWrongModeRejected()
    {
        Assert.Empty(new UpstoxFeedMapper().Map(new FeedResponse { Type = NiftySignal.Ingestion.Upstox.Protocol.Type.MarketInfo }, Now));
        var frame = Frame(); frame.Feeds["NSE_FO|12345"] = new Feed { Ltpc = new LTPC { Ltp = 25000 } };
        Assert.Throws<InvalidOperationException>(() => new UpstoxFeedMapper().Map(frame, Now));
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    [InlineData(100, 65.5)]
    public void Mapper_InvalidVolumeOrOiRejected(long volume, double oi)
    {
        var frame = Frame(volume); frame.Feeds["NSE_FO|12345"].FullFeed.MarketFF.Oi = oi;
        Assert.Throws<InvalidOperationException>(() => new UpstoxFeedMapper().Map(frame, Now));
    }

    [Fact]
    public void Mapper_IncompleteDepthRemainsUnavailable()
    {
        var frame = Frame(); frame.Feeds["NSE_FO|12345"].FullFeed.MarketFF.MarketLevel.BidAskQuote.RemoveAt(4);
        Assert.Null(Assert.Single(new UpstoxFeedMapper().Map(frame, Now)).Depth);
    }

    [Theory]
    [InlineData("2026-10-06T20:00:00Z", "2026-10-06T22:00:00Z")]
    [InlineData("2026-10-06T22:00:00Z", "2026-10-07T22:00:00Z")]
    [InlineData("2026-10-07T04:00:00Z", "2026-10-07T22:00:00Z")]
    public void Auth_ExpiryUsesActual0330IstCutoff(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), UpstoxAuthClient.RegularExpiry(DateTimeOffset.Parse(now)));

    [Fact]
    public async Task Auth_UsesFormExchange_AndDoesNotExposeFailureBody()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync();
            Assert.Contains("client_secret=secret", body); Assert.Contains("grant_type=authorization_code", body);
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("secret authorization-code token") };
        });
        var auth = new UpstoxAuthClient(new HttpClient(handler), Options.Create(new UpstoxOptions { ApiKey = "key", ApiSecret = "secret", RedirectUri = "https://example.test" }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ExchangeAsync("code", CancellationToken.None));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData(" plain-code ", "plain-code")]
    [InlineData("https://example.test/callback?state=abc&code=ab%2Bcd%3D", "ab+cd=")]
    public void Auth_ExtractsAndDecodesRedirectCode(string pasted, string expected)
        => Assert.Equal(expected, UpstoxAuthClient.ExtractCode(pasted));

    [Fact]
    public void Master_ParsesNativeKeyExpiryTickUnitsAndContractIdentity()
    {
        var expiry = new DateTimeOffset(2026, 10, 13, 18, 29, 59, TimeSpan.Zero).ToUnixTimeMilliseconds();
        using var json = JsonDocument.Parse($$"""[{"instrument_key":"NSE_FO|12345","name":"NIFTY","underlying_key":"NSE_INDEX|Nifty 50","instrument_type":"CE","expiry":{{expiry}},"lot_size":65,"tick_size":5,"trading_symbol":"NIFTY 25000 CE 13 OCT 26","strike_price":25000}]""");
        // Upstox expiry epochs are end-of-date IST, so use 18:29:59 UTC for 23:59:59 IST.
        var option = Assert.Single(UpstoxInstrumentMasterProvider.Parse(json.RootElement, Day));
        Assert.Equal(new DateOnly(2026, 10, 13), option.ExpiryDate);
        Assert.Equal(0.05m, option.TickSize); Assert.Equal(65, option.LotSize);
        Assert.Equal("NSE_FO|12345", option.NativeInstrumentKey);
        Assert.Equal("UP:NSE_FO|12345", option.Token);
        Assert.Equal(OptionType.Call, option.OptionType); Assert.Equal("NIFTY", option.Underlying);
    }

    [Fact]
    public void Band_SelectsTwoExpiriesAndPairedFixedContracts()
    {
        var all = new List<Instrument>();
        foreach (var expiry in new[] { Day.AddDays(6), Day.AddDays(13) })
        foreach (var strike in Enumerable.Range(0, 31).Select(i => 24250 + i * 50))
        foreach (var side in new[] { OptionType.Call, OptionType.Put })
            all.Add(new Instrument { Token = $"{expiry}-{strike}-{side}", TradingSymbol = "test", Exchange = Exchange.Nfo,
                Underlying = "NIFTY", InstrumentType = InstrumentType.Option, ExpiryDate = expiry, StrikePrice = strike, OptionType = side, AsOfDate = Day });
        var band = UpstoxMarketDataProvider.SelectOptionBand(all, [Day.AddDays(6), Day.AddDays(13)], 25000);
        Assert.Equal(84, band.Count); Assert.All(band, x => Assert.InRange(x.StrikePrice!.Value, 24500, 25500));
        var original = band.First(); Assert.Same(original, all.Single(x => x.Token == original.Token));
        all.Remove(all.First(x => x.StrikePrice == 25000 && x.OptionType == OptionType.Call));
        Assert.Throws<InvalidOperationException>(() => UpstoxMarketDataProvider.SelectOptionBand(all, [Day.AddDays(6)], 25000));
    }

    static NiftySignalDbContext Database() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task DayLock_RejectsSwitchAndPersistsOriginalSource()
    {
        await using var db = Database();
        await MarketDataSessionStore.EnsureDayAsync(db, Day, MarketDataProvider.Upstox, Now, CancellationToken.None);
        await MarketDataSessionStore.EnsureDayAsync(db, Day, MarketDataProvider.Upstox, Now, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketDataSessionStore.EnsureDayAsync(db, Day, MarketDataProvider.FlatTrade, Now, CancellationToken.None));
        Assert.Equal(MarketDataProvider.Upstox, Assert.Single(db.MarketDataDays).Provider);
        await MarketDataSessionStore.EnsureDayAsync(db, Day.AddDays(1), MarketDataProvider.FlatTrade, Now, CancellationToken.None);
    }

    [Fact]
    public async Task DayLock_AdoptsLegacyFlatTradeData_RejectsUpstox()
    {
        await using var db = Database();
        db.Ticks.Add(new Tick { Token = "12345", Exchange = Exchange.Nfo, ExchangeTimestamp = Now, ReceivedAt = Now, LastPrice = 25000 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => MarketDataSessionStore.EnsureDayAsync(db, Day, MarketDataProvider.Upstox, Now, CancellationToken.None));
        Assert.Empty(db.MarketDataDays);
        await MarketDataSessionStore.EnsureDayAsync(db, Day, MarketDataProvider.FlatTrade, Now, CancellationToken.None);
    }

    [Fact]
    public async Task Credentials_AreIndependentAndUpstoxUsesExplicitExpiry()
    {
        await using var db = Database();
        db.UpstoxSessions.Add(new UpstoxSession { Token = "upstox", ExpiresAtUtc = Now.AddMinutes(1), IsAnalyticsToken = true });
        db.FlatTradeSessions.Add(new FlatTradeSession { Token = "flattrade", ClientId = "client", IssuedAt = Now });
        await db.SaveChangesAsync();
        Assert.Equal("upstox", (await MarketDataSessionStore.LoadAsync(db, MarketDataProvider.Upstox, Now, CancellationToken.None))!.Token);
        Assert.Equal("flattrade", (await MarketDataSessionStore.LoadAsync(db, MarketDataProvider.FlatTrade, Now, CancellationToken.None))!.Token);
        Assert.Null(await MarketDataSessionStore.LoadAsync(db, MarketDataProvider.Upstox, Now.AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task Continuity_RejectsIntradayRestartSnapshotButPreservesLegacyReplay()
    {
        await using var db = Database();
        var first = Assert.Single(new UpstoxFeedMapper().Map(Frame(), Now)); first.IsSnapshot = true;
        db.Ticks.Add(first); await db.SaveChangesAsync();
        await UpstoxContinuityGuard.EnsureAsync(db, Day, [first.Token], Now, CancellationToken.None);
        var restarted = Assert.Single(new UpstoxFeedMapper().Map(Frame(1300), Now.AddMinutes(2)));
        db.Ticks.Add(restarted); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpstoxContinuityGuard.EnsureAsync(db, Day, [first.Token], Now.AddMinutes(2), CancellationToken.None));
        await UpstoxContinuityGuard.EnsureAsync(db, Day, ["12345"], Now.AddMinutes(2), CancellationToken.None);
    }

    [Fact]
    public async Task Continuity_RejectsRecordedOutageEvenAfterReconnect()
    {
        await using var db = Database();
        db.DataGaps.Add(new DataGap { StartedAt = Now, EndedAt = Now.AddSeconds(10), Reason = "Upstox feed failure: WebSocketException" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpstoxContinuityGuard.EnsureAsync(db, Day, ["UP:NSE_FO|12345"], Now.AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public void Schema_ModelMatchesMigrationSnapshot_AndIncludesProviderProvenance()
    {
        using var db = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>()
            .UseNpgsql("Host=localhost;Database=nifty_provider_test;Username=test;Password=test").Options);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.Database.GenerateCreateScript();
        Assert.Contains("market_data_days", sql);
        Assert.Contains("upstox_session", sql);
        Assert.Contains("\"NativeInstrumentKey\"", sql);
        Assert.Contains("\"LastTradeTimestamp\"", sql);
        Assert.Contains("\"IsSnapshot\"", sql);
        Assert.Contains("IX_ticks_snapshot_token_received_at", sql);
    }

    [Fact]
    public async Task Continuity_AllowsCompletedPreOpenReconnects_BlocksNewInMarketSnapshot()
    {
        await using var db = Database();
        var open = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
        foreach (var time in new[] { open.AddMinutes(-20), open.AddMinutes(-10) })
            db.Ticks.Add(new Tick { Token = "UP:NSE_FO|12345", Exchange = Exchange.Nfo, ExchangeTimestamp = time, ReceivedAt = time, IsSnapshot = true });
        await db.SaveChangesAsync();
        await UpstoxContinuityGuard.EnsureAsync(db, Day, ["UP:NSE_FO|12345"], Now, CancellationToken.None);
        db.Ticks.Add(new Tick { Token = "UP:NSE_FO|12345", Exchange = Exchange.Nfo, ExchangeTimestamp = Now, ReceivedAt = Now, IsSnapshot = true });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpstoxContinuityGuard.EnsureAsync(db, Day, ["UP:NSE_FO|12345"], Now, CancellationToken.None));
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
