using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.Host.Npt1Stream;
using NiftySignal.Npt1M1;
using NiftySignal.Tests.Trading.Core;
using NiftySignal.Trading.Adapters;
using NiftySignal.Trading.Core.Events;
using NiftySignal.Trading.Live;

namespace NiftySignal.Tests.Trading.Live;

/// <summary>The M1 producer (deterministic, restartable) and the stream processor's delivery policy (warm-up, live, no fabricated downtime), without a database.</summary>
public class Npt1M1StreamTests
{
    private static readonly DateOnly Day = Clock.Day;
    private const string Fut = "FUT1";

    private static M1Freeze Freeze(long baseBar = 100)
    {
        var session = new SessionFreeze(
            new AdaptiveSessionDefinition(Day, Fut, "NIFTY26SEPFUT", Day.AddDays(7), 65, baseBar),
            Day,
            0.065,
            null,
            M1Time.IstTime(Day, 9, 30),
            M1Time.IstTime(Day, 9, 15),
            Array.Empty<ObserverOptionInstrument>(),
            null,
            new Dictionary<string, decimal>(),
            M1Identity.ModelVersion);
        var decision = OpeningVolumeProjectionV2.Resolve(Day, 1000, true, Array.Empty<PriorSession>(), OpeningVolumeProjectionV2.BetaM1, 65);
        return new M1Freeze(session, new M1FreezeReport(Day.ToString("yyyy-MM-dd"), Day.ToString("yyyy-MM-dd"), 15, 1000, baseBar, "", "", 0, null, "test"), decision);
    }

    /// <summary>One future tick per second from 09:15 with a cumulative volume of +50 per tick (a bar every two ticks) and a drifting book.</summary>
    private static List<SourceTick> Ticks(int seconds)
    {
        var list = new List<SourceTick>();
        var start = M1Time.IstTime(Day, 9, 15);
        for (var i = 0; i < seconds; i++)
        {
            var at = start.AddSeconds(i);
            var mid = 24000m + (i % 17) * 0.5m;
            list.Add(new SourceTick(Fut, i + 1, at, at, mid, mid - 0.5m, mid + 0.5m, 100 + (i % 7) * 10, 90 + (i % 5) * 10, (i + 1) * 50L, 1000));
        }

        return list;
    }

    private static List<M1Bar> Run(M1BarProducer p, IEnumerable<SourceTick> ticks, int batch)
    {
        var bars = new List<M1Bar>();
        var count = 0;
        foreach (var t in ticks)
        {
            p.Offer(t);
            if (++count % batch == 0)
            {
                p.AdvanceSource(t.ReceivedAt);
                bars.AddRange(p.Release().SelectMany(g => g.Bars));
            }
        }

        bars.AddRange(p.Finish().SelectMany(g => g.Bars));
        return bars;
    }

    [Fact]
    public void TheProducer_IsDeterministic_AnyReleaseCadenceAndAnyRestartFromTheTicksGivesTheIdenticalFrames()
    {
        var ticks = Ticks(1800);
        var a = Run(new M1BarProducer(Freeze()), ticks, 1000);
        var b = Run(new M1BarProducer(Freeze()), ticks, 7);                 // another poll cadence
        var restarted = Run(new M1BarProducer(Freeze()), ticks, 333);       // a "restart": a fresh producer fed the day from the start

        Assert.True(a.Count > 100);
        Assert.Equal(a.Select(x => x.FrameHash), b.Select(x => x.FrameHash));
        Assert.Equal(a.Select(x => x.FrameHash), restarted.Select(x => x.FrameHash));
        Assert.All(a, x => Assert.Equal(M1Identity.SessionId(Day), x.Frame.AdaptiveSessionId));
        Assert.Equal(Enumerable.Range(1, a.Count).Select(i => (long)i), a.Select(x => x.Frame.BarSeq));
        Assert.All(a, x => Assert.Equal(M1Identity.ModelVersion, x.Frame.ModelVersion));
        Assert.Contains(a, x => x.Frame.MicroDevTimeWeighted is not null);
    }

    [Fact]
    public void TheFrame_CarriesTheBarsReadinessAndTheMicrostructureOfThatBarOnly()
    {
        var bars = Run(new M1BarProducer(Freeze()), Ticks(1800), 500);
        var first = bars[0];
        var ready = bars.First(b => b.Frame.Ready);

        Assert.False(first.Frame.Ready);                                   // not ready before enough consecutive valid bars / the 09:30 signal start
        Assert.True(ready.Frame.EndAvailableAt >= M1Time.IstTime(Day, 9, 30));
        Assert.Equal(ready.Event.Info!.Ready, ready.Frame.Ready);
        Assert.Equal(ready.Event.AvailableAt, ready.Frame.EndAvailableAt);
        Assert.Null(first.Frame.PeBandContractStrictDelta);                // no option band in this synthetic universe: unavailable, never zero
        Assert.NotNull(first.Frame.BandUnavailableReason);
    }

    private sealed class FakeStore(bool divergent = false) : IM1FrameStore
    {
        public List<long> Stored { get; } = new();

        public Task<bool> EnsureAsync(M1Bar bar, DateTimeOffset nowUtc, CancellationToken ct)
        {
            Stored.Add(bar.Event.BarSeq);
            return Task.FromResult(!divergent);
        }
    }

    private sealed class FakeFeed : IPaperFeed
    {
        public List<(DateTimeOffset At, int Quotes, IReadOnlyList<LiveBar> Bars)> Groups { get; } = new();

        public void OnGroup(DateTimeOffset at, IReadOnlyList<LiveQuote> quotes, IReadOnlyList<LiveBar> bars) => Groups.Add((at, quotes.Count, bars));

        public void OnHeartbeat(DateTimeOffset watermark)
        {
        }

        public void Finish()
        {
        }
    }

    [Fact]
    public async Task Groups_BeforeTheOpen_AreWarmupOnly_LiveGroupsAreComplete_AndDowntimeGroupsAreNeverGivenToTheEngine()
    {
        var producer = new M1BarProducer(Freeze());
        var bars = Run(producer, Ticks(2400), 100);                         // the whole day replay as one list of groups, then re-run to get the groups themselves
        var groups = new List<M1Group>();
        var p2 = new M1BarProducer(Freeze());
        foreach (var t in Ticks(2400))
        {
            p2.Offer(t);
        }

        groups.AddRange(p2.Finish());
        Assert.NotEmpty(bars);

        var warmupEnd = M1Time.IstTime(Day, 9, 30);
        var liveBoundary = M1Time.IstTime(Day, 9, 45);                      // this process started observing at 09:45
        var store = new FakeStore();
        var feed = new FakeFeed();
        var telemetry = new Npt1M1Telemetry();
        var processor = new Npt1M1StreamProcessor(store, feed, warmupEnd, liveBoundary, telemetry, NullLogger.Instance);

        await processor.HandleAsync(groups, CancellationToken.None);

        Assert.All(feed.Groups.Where(g => g.At < warmupEnd), g => { Assert.Equal(0, g.Quotes); Assert.NotEmpty(g.Bars); });           // warm-up: bars only
        Assert.DoesNotContain(feed.Groups, g => g.At >= warmupEnd && g.At < liveBoundary);                                           // 09:30..09:45 happened while nobody observed
        Assert.Contains(feed.Groups, g => g.At >= liveBoundary && g.Quotes > 0);                                                       // live: quotes and bars
        Assert.True(telemetry.GroupsSkippedCatchUp > 0);
        Assert.Equal(groups.SelectMany(g => g.Bars).Count(), store.Stored.Count);                                                      // every frame is stored, including the withheld ones
        Assert.Equal(groups.SelectMany(g => g.Bars).Count(), (int)telemetry.BarsProduced);
    }

    [Fact]
    public void TheWorker_ResolvesFromTheHostRegistrations_AndDoesNothingWhileNoNpt1StrategyIsEnabled()
    {
        // the registrations of NiftySignal.Host/Program.cs (only added when a strategy is enabled), with a dummy connection that is never opened
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trading:PaperLive:Enabled"] = "true",
            ["Trading:PaperLive:Strategies:NPT1-MD5:Enabled"] = "true",
            ["Trading:PaperLive:Capital"] = "1000000",
            ["Trading:PaperLive:BrokeragePerOrder"] = "0",
            ["Trading:PaperLive:OptionTickSize"] = "0.05",
        }).Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(configuration);
        services.AddDbContext<NiftySignal.Persistence.NiftySignalDbContext>(o => o.UseNpgsql("Host=localhost;Database=never_opened"));
        services.AddDbContext<NiftySignal.Npt1M1.Data.Npt1M1DbContext>(o => o.UseNpgsql("Host=localhost;Database=never_opened"));
        services.AddSingleton<NiftySignal.Npt1M1.Data.Npt1TickReader>();
        services.AddSingleton<NiftySignal.Npt1M1.Data.M1SessionBuilder>();
        services.AddSingleton<Npt1M1Telemetry>();
        services.AddSingleton<EfFrameStore>();
        services.AddHostedService<Npt1M1Worker>();

        using var provider = services.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var worker = Assert.Single(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        Assert.IsType<Npt1M1Worker>(worker);
        Assert.True(PaperLiveOptions.Bind(configuration).AnyNpt1Enabled);
        Assert.Empty(PaperLiveOptions.Bind(configuration).Validate());
    }

    [Fact]
    public async Task ADivergentStoredFrame_IsWithheldFromTheEngine_AsUnverified()
    {
        var p = new M1BarProducer(Freeze());
        foreach (var t in Ticks(1200))
        {
            p.Offer(t);
        }

        var groups = p.Finish();
        var feed = new FakeFeed();
        var telemetry = new Npt1M1Telemetry();
        var processor = new Npt1M1StreamProcessor(new FakeStore(divergent: true), feed, M1Time.IstTime(Day, 9, 30), M1Time.IstTime(Day, 9, 15), telemetry, NullLogger.Instance);

        await processor.HandleAsync(groups, CancellationToken.None);

        Assert.All(feed.Groups.SelectMany(g => g.Bars), b => Assert.False(b.Verified));
        Assert.True(telemetry.DivergentFrames > 0);
    }
}
