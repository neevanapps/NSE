using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Dashboard;

/// <summary>Phase 4: the sticky quote panel shows the Host builder's partial-bar quantity (authoritative runtime row), the target, the percentage and the last completed bar.</summary>
public class BarProgressTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static DateOnly TodayIst => DateOnly.FromDateTime(Now.ToOffset(TimeSpan.FromHours(5.5)).DateTime);

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

    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new();
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }

    static AdaptiveSessionStateRow Session(long target = 13_650) => new()
    {
        Id = 1, TradeDate = TodayIst, ModelVersion = "adaptive-fixed13650-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = Now, FutureToken = "F", FutureSymbol = "F", FutureExpiry = TodayIst.AddDays(20),
        OpeningWindowStartUtc = Now, OpeningWindowEndUtc = Now, EstimatorName = "x", CreatedAtUtc = Now, BaseBarVolume = target,
    };

    static AdaptiveObserverRuntimeRow Runtime(long partial, int lastBar, DateTimeOffset? heartbeat = null, AdaptiveRuntimeStatus status = AdaptiveRuntimeStatus.Live) =>
        new() { SessionId = 1, RuntimeStatus = status, LastHeartbeatUtc = heartbeat ?? Now, CurrentPartialBarVolume = partial, LastCompletedBarSeq = lastBar };

    [Fact]
    public void From_FormatsQuantityTargetAndPercent_AndRejectsInconsistentRows()
    {
        var p = BarProgress.From(Session(), Runtime(10_420, 41));
        Assert.Equal("10,420 / 13,650 – 76.3%", p.QuantityText);
        Assert.Equal(41, p.LastCompletedBarSeq);
        Assert.Equal("0 / 13,650 – 0.0%", BarProgress.From(Session(), Runtime(0, 7)).QuantityText);              // right after a completion
        foreach (var bad in new long[] { 13_651, -1 })
        {
            var u = BarProgress.From(Session(), Runtime(bad, 3));
            Assert.Null(u.Quantity);                                   // never clamped or guessed
            Assert.Equal("unavailable", u.QuantityText);
            Assert.NotNull(u.UnavailableReason);
        }

        Assert.NotNull(BarProgress.From(Session(0), Runtime(5, 1)).UnavailableReason);
    }

    [Fact]
    public async Task Service_ReadsTheRuntimeRow_NotifiesOnlyOnDisplayedChange_AndResetsOnBarCompletion()
    {
        var f = new ObserverFactory();
        await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(10_420, 41)); await db.SaveChangesAsync(); }
        var svc = new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance);
        var notified = 0;
        svc.Changed += () => notified++;

        await svc.RefreshAsync();
        Assert.Equal("10,420 / 13,650 – 76.3%", svc.Current.QuantityText);
        Assert.Equal(1, notified);

        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.LastHeartbeatUtc = Now.AddSeconds(1); await db.SaveChangesAsync(); }       // heartbeat only
        await svc.RefreshAsync();
        Assert.Equal(1, notified);

        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.CurrentPartialBarVolume = 12_900; await db.SaveChangesAsync(); }
        await svc.RefreshAsync();
        Assert.Equal("12,900 / 13,650 – 94.5%", svc.Current.QuantityText);
        Assert.Equal(2, notified);

        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.LastCompletedBarSeq = 42; r.CurrentPartialBarVolume = 310; await db.SaveChangesAsync(); }    // bar 42 completed, the builder reset
        await svc.RefreshAsync();
        Assert.Equal("310 / 13,650 – 2.3%", svc.Current.QuantityText);
        Assert.Equal(42, svc.Current.LastCompletedBarSeq);
        Assert.Equal(3, notified);
    }

    [Fact]
    public async Task Service_NoSessionOrMissingRuntime_IsUnavailableWithReason()
    {
        var empty = new AdaptiveBarProgressService(new AdaptiveObserverDataService(new ObserverFactory(), NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance);
        await empty.RefreshAsync();
        Assert.Equal("no adaptive session today", empty.Current.UnavailableReason);
        Assert.Equal("unavailable", empty.Current.QuantityText);
    }

    static async Task<string> RenderPanelAsync(Action<ServiceCollection> configure, Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var source = new SourceFactory();
        services.AddSingleton<IDbContextFactory<NiftySignalDbContext>>(source);
        configure(services);
        using var live = new LiveDataService(source, new FlatTradeAuthClient(new HttpClient(), Options.Create(new FlatTradeOptions { UserId = "t", ApiKey = "t", ApiSecret = "t" })), new Monitor(), NullLogger<LiveDataService>.Instance, startPolling: false);
        services.AddSingleton(live);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<LiveQuotePanel>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        return System.Net.WebUtility.HtmlDecode(html);
    }

    [Fact]
    public async Task StickyPanel_Live_ShowsQuantityAgainstTarget_Percent_AndLastCompletedBar()
    {
        var f = new ObserverFactory();
        await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(10_420, 41)); await db.SaveChangesAsync(); }
        var svc = new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance);
        await svc.RefreshAsync();
        var html = await RenderPanelAsync(s => s.AddSingleton(svc), new());
        Assert.Contains("data-testid=\"bar-progress\"", html);
        Assert.Contains("10,420 / 13,650 – 76.3%", html);
        Assert.Contains("last completed bar 41", html);
        Assert.Contains("quote-panel-sticky", html);                                    // the same sticky panel carries it: quote and quantity stay together
        svc.Dispose();
    }

    [Fact]
    public async Task StickyPanel_WithoutTheService_ShowsUnavailable_NotAnEstimate()
    {
        var html = await RenderPanelAsync(_ => { }, new());
        Assert.Contains("data-testid=\"bar-progress\"", html);
        Assert.Contains("unavailable", html);
        Assert.DoesNotContain("/ 13,650", html);
    }

    [Fact]
    public async Task StickyPanel_CaptureMode_StaysStaticAndPinned_NoLiveQuantity()
    {
        var f = new ObserverFactory();
        await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(10_420, 41)); await db.SaveChangesAsync(); }
        var html = await RenderPanelAsync(s =>
        {
            s.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(f);
            s.AddSingleton<AdaptiveQuoteAsOfService>();
            s.AddSingleton(new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance));
        }, new() { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 41 });
        Assert.Contains("pinned · completed bar 41", html);
        Assert.DoesNotContain("10,420", html);                                           // the live partial quantity never leaks into a pinned capture
        Assert.Contains("quote-panel-capture", html);
        Assert.DoesNotContain("quote-panel-sticky", html);
    }
}
