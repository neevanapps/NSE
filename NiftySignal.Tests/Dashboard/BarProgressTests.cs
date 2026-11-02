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

    static AdaptiveObserverRuntimeRow Runtime(long partial, int lastBar, DateTimeOffset? heartbeat = null, AdaptiveRuntimeStatus status = AdaptiveRuntimeStatus.Live, DateTimeOffset? started = null, DateTimeOffset? sourceTick = null) =>
        new() { SessionId = 1, RuntimeStatus = status, LastHeartbeatUtc = heartbeat ?? Now, CurrentPartialBarVolume = partial, LastCompletedBarSeq = lastBar, CurrentPartialBarStartedAtUtc = started, LastProcessedSourceAvailableAtUtc = sourceTick };

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

        await svc.RefreshAsync();                                                   // nothing changed at all: no notification
        Assert.Equal(1, notified);
        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.LastHeartbeatUtc = Now.AddSeconds(1); await db.SaveChangesAsync(); }       // heartbeat only: the "as of" time moves, so subscribers re-render
        await svc.RefreshAsync();
        Assert.Equal(2, notified);

        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.CurrentPartialBarVolume = 12_900; await db.SaveChangesAsync(); }
        await svc.RefreshAsync();
        Assert.Equal("12,900 / 13,650 – 94.5%", svc.Current.QuantityText);
        Assert.Equal(3, notified);

        await using (var db = f.CreateDbContext()) { var r = await db.Runtime.SingleAsync(); r.LastCompletedBarSeq = 42; r.CurrentPartialBarVolume = 310; await db.SaveChangesAsync(); }    // bar 42 completed, the builder reset
        await svc.RefreshAsync();
        Assert.Equal("310 / 13,650 – 2.3%", svc.Current.QuantityText);
        Assert.Equal(42, svc.Current.LastCompletedBarSeq);
        Assert.Equal(4, notified);
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
        await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(10_420, 41, DateTimeOffset.UtcNow, started: DateTimeOffset.UtcNow.AddSeconds(-90), sourceTick: DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); }
        var svc = new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance);
        await svc.RefreshAsync();
        var html = await RenderPanelAsync(s => s.AddSingleton(svc), new());
        Assert.Contains("data-testid=\"incomplete-bar\"", html);
        Assert.Contains("Current incomplete adaptive bar — Provisional", html);
        Assert.Contains("10,420 / 13,650 · 76.3%", html);
        Assert.Contains("bar 42", html);                                                // the incomplete bar is the one after the last completed bar
        Assert.Contains("data-state=\"Live\"", html);
        Assert.Contains("role=\"progressbar\"", html);
        Assert.Contains("quote-progress-fill", html);
        Assert.DoesNotContain("Bar qty", html);                                         // the old inline message is gone
        Assert.Contains(" IST", html);                                                  // source timestamps (heartbeat, last tick) and the Dashboard refresh time are shown separately
        Assert.Contains("data-testid=\"incomplete-bar-refreshed\"", html);
        Assert.Contains("Open · High · Low · Last price · Trade-update count — unavailable", html);   // no OHLC / update counter exists in the data contract: said so, never synthesized
        Assert.Contains("quote-panel-sticky", html);                                    // the same sticky panel carries it: quote and incomplete bar stay together
        svc.Dispose();
    }

    [Fact]
    public async Task StickyPanel_WithoutTheService_ShowsUnavailable_NotAnEstimate()
    {
        var html = await RenderPanelAsync(_ => { }, new());
        Assert.Contains("data-testid=\"incomplete-bar\"", html);
        Assert.Contains("data-state=\"Unavailable\"", html);
        Assert.Contains("unavailable", html);
        Assert.DoesNotContain("/ 13,650", html);
        Assert.DoesNotContain("quote-progress-fill", html);
    }

    [Fact]
    public async Task StickyPanel_IncompleteBarAndProgress_AreInsideThePanel_AndAStaleHeartbeatIsNeverShownAsLive()
    {
        var f = new ObserverFactory();
        await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(5_000, 41, DateTimeOffset.UtcNow.AddMinutes(-3), started: DateTimeOffset.UtcNow.AddMinutes(-5))); await db.SaveChangesAsync(); }
        var svc = new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance);
        await svc.RefreshAsync();
        var html = await RenderPanelAsync(s => s.AddSingleton(svc), new());
        Assert.Contains("data-state=\"Stale\"", html);
        Assert.DoesNotContain("data-state=\"Live\"", html);
        Assert.Contains("last known state", html);
        Assert.Contains("quote-progress-stale", html);
        // one panel: the quote row, then the provisional section, then the progress track at the bottom
        Assert.True(html.IndexOf("quote-row", StringComparison.Ordinal) < html.IndexOf("data-testid=\"incomplete-bar\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("data-testid=\"incomplete-bar\"", StringComparison.Ordinal) < html.IndexOf("data-testid=\"incomplete-bar-progress\"", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "quote-panel-sticky"));
        Assert.DoesNotContain("quote-row", html[html.IndexOf("data-testid=\"incomplete-bar-progress\"", StringComparison.Ordinal)..]);
        svc.Dispose();
    }

    [Fact]
    public async Task HistoricalCapture_IsIdenticalWhateverTheLiveRuntimeRowSays_SoNoPostBoundaryValueCanLeak()
    {
        async Task<string> Capture(long partial, int lastBar)
        {
            var f = new ObserverFactory();
            await using (var db = f.CreateDbContext()) { db.Sessions.Add(Session()); db.Runtime.Add(Runtime(partial, lastBar, DateTimeOffset.UtcNow, started: DateTimeOffset.UtcNow)); await db.SaveChangesAsync(); }
            return await RenderPanelAsync(s =>
            {
                s.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(f);
                s.AddSingleton<AdaptiveQuoteAsOfService>();
                s.AddSingleton(new AdaptiveBarProgressService(new AdaptiveObserverDataService(f, NullLogger<AdaptiveObserverDataService>.Instance), NullLogger<AdaptiveBarProgressService>.Instance));
            }, new() { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 41 });
        }

        var early = await Capture(100, 41);
        var later = await Capture(13_000, 90);                      // the live row has moved far past the pinned bar
        Assert.Equal(early, later);
        Assert.Contains("Pinned snapshot · completed bar 41", later);
        Assert.DoesNotContain("13,000", later);
        Assert.DoesNotContain("bar 91", later);
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
        Assert.Contains("Pinned snapshot · completed bar 41", html);
        Assert.DoesNotContain("10,420", html);                                           // the live partial quantity never leaks into a pinned capture
        Assert.DoesNotContain("quote-progress", html);                                   // no live progress bar in a pinned snapshot
        Assert.DoesNotContain("Provisional", html);
        Assert.Contains("quote-panel-capture", html);
        Assert.DoesNotContain("quote-panel-sticky", html);
    }

    [Fact]
    public void AuthoritativeBuilder_PartialVolumeNeverReachesTheTarget_OversizedUpdatesAreSplit_AndEveryCompletedBarIsExact()
    {
        var day = new DateOnly(2026, 10, 7);
        const long target = 13_650;
        var session = new NiftySignal.AdaptiveObserver.AdaptiveSessionDefinition(day, "FUT", "NIFTYFUT", day.AddDays(20), 65, target);
        var builder = new NiftySignal.AdaptiveObserver.ExactAdaptiveFuturesBarBuilder(session);
        var t = DateTimeOffset.Parse("2026-10-07T03:45:00Z");
        var rng = new Random(13);
        long total = 0, cumulative = 1_000;
        long id = 1;
        builder.Add(new NiftySignal.AdaptiveObserver.EnrichedObserverTick(new NiftySignal.AdaptiveObserver.CleanObserverTick(id++, t, t, 100, 99, 101, 10, 10, cumulative, 1000), 0, NiftySignal.AdaptiveObserver.ObserverTradeSide.Unknown, NiftySignal.AdaptiveObserver.ObserverTradeSide.Unknown));
        for (var i = 0; i < 5_000; i++)
        {
            var size = rng.Next(0, 10) == 0 ? rng.Next(20_000, 70_000) : rng.Next(1, 900);       // one update in ten is bigger than a whole bar (up to ~5 bars)
            cumulative += size; total += size;
            var at = t.AddMilliseconds(i * 50 + 50);
            builder.Add(new NiftySignal.AdaptiveObserver.EnrichedObserverTick(new NiftySignal.AdaptiveObserver.CleanObserverTick(id++, at, at, 100, 99, 101, 10, 10, cumulative, 1000), size, NiftySignal.AdaptiveObserver.ObserverTradeSide.Buy, NiftySignal.AdaptiveObserver.ObserverTradeSide.Buy));
            Assert.InRange(builder.PartialVolume, 0, target - 1);                                  // never at or above the target: a bar that reaches it is completed and the partial restarts below it
            var p = BarProgress.From(Session(target), Runtime(builder.PartialVolume, builder.Bars.Count));
            Assert.NotNull(p.Quantity);                                                            // therefore the progress is always representable, never "unavailable"
        }

        Assert.All(builder.Bars.Where(b => b.IsComplete), b => Assert.Equal(target, b.Volume));
        Assert.Equal(total, builder.Bars.Where(b => b.IsComplete).Sum(b => b.Volume) + builder.PartialVolume);     // no tick counted twice, none lost
        Assert.True(builder.SplitUpdates > 100);
    }
}
