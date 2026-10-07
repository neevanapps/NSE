using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Dashboard;

public sealed class LiveQuoteTests
{
    static readonly DateOnly Day = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
    static readonly DateTimeOffset At = new(Day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(5.5));
    static Instrument Instrument(string token, string underlying, InstrumentType type, DateOnly? expiry = null, decimal? strike = null, OptionType side = OptionType.None) =>
        new() { Token = token, TradingSymbol = token, Underlying = underlying, InstrumentType = type,
            Exchange = Exchange.Nfo, AsOfDate = Day, ExpiryDate = expiry, StrikePrice = strike, OptionType = side };
    static Tick Tick(string token, decimal price, DateTimeOffset? at = null, bool depth = false) =>
        new() { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = (at ?? At).ToUniversalTime(),
            ReceivedAt = (at ?? At).ToUniversalTime(), LastPrice = price,
            Depth = depth ? new MarketDepth(price - 1, 65, 0, 0, 0, 0, 0, 0, 0, 0, price + 1, 65, 0, 0, 0, 0, 0, 0, 0, 0) : null };
    static List<Instrument> Universe() =>
    [
        Instrument("spot", "NIFTY", InstrumentType.Index), Instrument("future", "NIFTY", InstrumentType.Future, Day.AddDays(20)),
        Instrument("vix", "NIFTY", InstrumentType.Vix),
        Instrument("call", "NIFTY", InstrumentType.Option, Day.AddDays(6), 25000, OptionType.Call),
        Instrument("put", "NIFTY", InstrumentType.Option, Day.AddDays(6), 25000, OptionType.Put),
        Instrument("next-call", "NIFTY", InstrumentType.Option, Day.AddDays(13), 25000, OptionType.Call),
        Instrument("bank-spot", "BANKNIFTY", InstrumentType.Index), Instrument("bank-future", "BANKNIFTY", InstrumentType.Future, Day.AddDays(20)),
        Instrument("bank-call", "BANKNIFTY", InstrumentType.Option, Day.AddDays(3), 56000, OptionType.Call),
        Instrument("sensex-spot", "SENSEX", InstrumentType.Index), Instrument("sensex-future", "SENSEX", InstrumentType.Future, Day.AddDays(20)),
        Instrument("sensex-call", "SENSEX", InstrumentType.Option, Day.AddDays(1), 82000, OptionType.Call),
    ];

    [Fact]
    public void MixedUniverse_PersistedRestartAndPushesKeepNiftyPricesAndNearestNiftyExpiry()
    {
        using var fixture = new Fixture();
        var live = fixture.Live;
        var rows = Universe();
        live.RefreshQuoteUniverse(rows, [Tick("spot", 24900), Tick("call", 90)],
            [Tick("spot", 25000), Tick("future", 25040), Tick("vix", 14), Tick("call", 100, depth: true), Tick("put", 110, depth: true), Tick("sensex-call", 800)], Day);
        Assert.Equal(25000, live.SpotLtp); Assert.Equal(25040, live.FutureLtp);
        Assert.Equal(100, live.QuickQuotes[(25000, OptionType.Call)].Ltp);
        Assert.Equal(99, live.QuickQuotes[(25000, OptionType.Call)].Bid);
        Assert.Equal(101, live.QuickQuotes[(25000, OptionType.Call)].Ask);
        Assert.Equal(10, live.QuickQuotes[(25000, OptionType.Call)].Change);
        foreach (var token in new[] { "bank-spot", "bank-future", "sensex-spot", "sensex-future", "bank-call", "sensex-call", "next-call" })
            live.ApplyPushedTick(Tick(token, 85000, At.AddSeconds(1), true));
        Assert.Equal(25000, live.SpotLtp); Assert.Equal(25040, live.FutureLtp);
        Assert.Equal(2, live.QuickQuotes.Count);
        Assert.Equal(100, live.QuickQuotes[(25000, OptionType.Call)].Ltp);
        live.ApplyPushedTick(Tick("call", 102, At.AddSeconds(2), true));
        Assert.Equal(102, live.QuickQuotes[(25000, OptionType.Call)].Ltp);
    }

    [Fact]
    public void SlowPollCannotOverwriteNewerPush_AndDayRolloverCannotSeedYesterday()
    {
        using var fixture = new Fixture(); var live = fixture.Live; var rows = Universe();
        live.RefreshQuoteUniverse(rows, [], [Tick("spot", 25000)], Day);
        live.ApplyPushedTick(Tick("spot", 25050, At.AddSeconds(5)));
        live.RefreshQuoteUniverse(rows, [], [Tick("spot", 25000)], Day);
        Assert.Equal(25050, live.SpotLtp);
        foreach (var row in rows) row.AsOfDate = Day.AddDays(1);
        live.RefreshQuoteUniverse(rows, [], [Tick("spot", 25050), Tick("call", 100)], Day.AddDays(1));
        Assert.Null(live.SpotLtp); Assert.Empty(live.QuickQuotes);
    }

    [Fact]
    public void SelectorKeepsUserPicksInRangeAfterSpotMoves_AndNeverInventsZeroStrikes()
    {
        Assert.Empty(LiveQuotePanel.BuildStrikes(null, [], null, null));
        var strikes = LiveQuotePanel.BuildStrikes(27000, [25000, 25050], 25000, 25100);
        Assert.Contains(25000, strikes); Assert.Contains(25100, strikes);
        Assert.All(strikes, x => Assert.True(x > 0));
        Assert.DoesNotContain(56000, strikes); Assert.DoesNotContain(82000, strikes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PanelIsVisibleWithOrWithoutSessionMetadata_AndRendersNiftyBidAsk(bool validSession)
    {
        using var fixture = new Fixture();
        if (validSession)
        {
            await using var db = fixture.Factory.CreateDbContext();
            db.FlatTradeSessions.Add(new FlatTradeSession { Token = "test-token", IssuedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        fixture.Live.RefreshQuoteUniverse(Universe(), [], [Tick("spot", 25000), Tick("call", 100, depth: true), Tick("put", 110, depth: true)], Day);
        var collection = new ServiceCollection();
        collection.AddLogging(); collection.AddSingleton(fixture.Live);
        collection.AddSingleton<IDbContextFactory<NiftySignalDbContext>>(fixture.Factory);
        await using var services = collection.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LiveQuotePanel>()).ToHtmlString());
        Assert.Contains("NIFTY Live Quote", html); Assert.Contains("99.00 / 101.00", html); Assert.Contains("109.00 / 111.00", html);
        Assert.Contains("Nifty call strike", html); Assert.Contains("Nifty put strike", html);
        Assert.DoesNotContain("56000", html); Assert.DoesNotContain("82000", html);
        Assert.Equal(!validSession, html.Contains("Session metadata stale"));
    }

    sealed class Fixture : IDisposable
    {
        public Factory Factory { get; } = new();
        public LiveDataService Live { get; }
        public Fixture() => Live = new(Factory, new FlatTradeAuthClient(new HttpClient(), Options.Create(new FlatTradeOptions { UserId = "test", ApiKey = "test", ApiSecret = "test" })),
            new Monitor(), NullLogger<LiveDataService>.Instance, startPolling: false);
        public void Dispose() => Live.Dispose();
    }
    sealed class Factory : IDbContextFactory<NiftySignalDbContext>
    {
        readonly DbContextOptions<NiftySignalDbContext> _options = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public NiftySignalDbContext CreateDbContext() => new(_options);
        public Task<NiftySignalDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new();
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }
}
