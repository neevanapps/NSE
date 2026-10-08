using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Ingestion.FlatTrade;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.Dashboard;

/// <summary>Slice 1 of the 08-Oct plan: compact grids, derived Urgency and the pinned (as-of) Live Quote.</summary>
public sealed class AdaptiveCompactGridTests
{
    static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateTimeOffset Open930 = new(Day.ToDateTime(new TimeOnly(9, 30)), Ist);

    // ---- Urgency (derived, never persisted) ----

    [Theory]
    [InlineData(1000, 50.0, 20.0)]
    [InlineData(0, 10.0, 0.0)]
    [InlineData(19500, 109.0, 19500 / 109.0)]
    public void Urgency_PositiveDuration_IsVolumePerSecond(long volume, double seconds, double expected) =>
        Assert.Equal(expected, AdaptiveGridProjection.Urgency(volume, seconds)!.Value, 9);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Urgency_ZeroOrInvalidDuration_IsUnavailableNotInfinite(double seconds) =>
        Assert.Null(AdaptiveGridProjection.Urgency(1000, seconds));

    // ---- As-of tick selection ----

    static Tick T(long id, decimal price, DateTimeOffset exchange, DateTimeOffset? received = null) =>
        new() { Id = id, Token = "X", Exchange = Exchange.Nfo, ExchangeTimestamp = exchange.ToUniversalTime(),
            ReceivedAt = (received ?? exchange).ToUniversalTime(), LastPrice = price };

    [Fact]
    public void AsOfSelector_NeverReturnsATickAvailableAfterTheBoundary()
    {
        var b = Open930.AddSeconds(30);
        var ticks = new[]
        {
            T(1, 100, b.AddSeconds(-10)),
            T(2, 101, b.AddSeconds(-1)),
            T(3, 999, b.AddSeconds(1)),                                   // exchange time after the boundary
            T(4, 998, b.AddSeconds(-5), received: b.AddSeconds(2)),       // exchanged before, but not available until after
            T(5, 0, b.AddSeconds(-1)),                                    // invalid price is skipped
        };
        Assert.Equal(2, AsOfTickSelector.Latest(ticks, b)!.Id);
        Assert.Null(AsOfTickSelector.Latest(ticks, b.AddSeconds(-20)));
        Assert.Equal(4, AsOfTickSelector.Latest(ticks, b.AddSeconds(5))!.Id); // once everything is available, the latest AVAILABILITY wins (tick 4 became available at +2s, after tick 3 at +1s)
    }

    [Fact]
    public void AsOfSelector_AvailabilityIsTheLaterOfExchangeAndReceiveTime_TiesBreakBySourceId()
    {
        var b = Open930;
        var a = T(10, 100, b.AddSeconds(-3), received: b.AddSeconds(-2));
        var c = T(11, 101, b.AddSeconds(-2));
        Assert.Equal(11, AsOfTickSelector.Latest([a, c], b)!.Id);
        Assert.Equal(AsOfTickSelector.AvailableAt(a), b.AddSeconds(-2));
    }

    // ---- As-of service against the databases ----

    sealed class SourceFactory : IDbContextFactory<NiftySignalDbContext>
    {
        readonly DbContextOptions<NiftySignalDbContext> _o = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public NiftySignalDbContext CreateDbContext() => new(_o);
        public Task<NiftySignalDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    sealed class ObserverFactory : IDbContextFactory<AdaptiveObserverDbContext>
    {
        readonly DbContextOptions<AdaptiveObserverDbContext> _o = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public AdaptiveObserverDbContext CreateDbContext() => new(_o);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    static Instrument Inst(string token, InstrumentType type) => new()
    { Token = token, TradingSymbol = token, Underlying = "NIFTY", InstrumentType = type, Exchange = Exchange.Nfo, AsOfDate = Day };

    static Tick Px(string token, decimal price, DateTimeOffset at, bool depth = false) => new()
    { Token = token, Exchange = Exchange.Nfo, ExchangeTimestamp = at.ToUniversalTime(), ReceivedAt = at.ToUniversalTime(), LastPrice = price,
        Depth = depth ? new MarketDepth(price - 0.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0, price + 0.5m, 65, 0, 0, 0, 0, 0, 0, 0, 0) : null };

    static AdaptiveSessionStateRow Session(string optionJson = "[]") => new()
    {
        Id = 1, TradeDate = Day, ModelVersion = "adaptive-v1", SourceBranch = "test", SourceCommitSha = "test", BuildUtc = Open930,
        FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = Day.AddDays(20), OpeningWindowStartUtc = Open930.AddMinutes(-15),
        OpeningWindowEndUtc = Open930, EstimatorName = "V1", CreatedAtUtc = Open930, OptionUniverseJson = optionJson,
        ResidualCenterStrike = 25000,
    };

    static string OptionJson() => JsonSerializer.Serialize(new[]
    {
        new ObserverOptionInstrument("CE25000", "CE25000", OptionType.Call, 25000, Day.AddDays(6), 65),
        new ObserverOptionInstrument("PE25000", "PE25000", OptionType.Put, 25000, Day.AddDays(6), 65),
        new ObserverOptionInstrument("CE25050", "CE25050", OptionType.Call, 25050, Day.AddDays(6), 65),
    });

    static async Task<(SourceFactory source, ObserverFactory observer, DateTimeOffset boundary)> SeedPinnedAsync()
    {
        var source = new SourceFactory(); var observer = new ObserverFactory();
        var boundary = Open930.AddMinutes(10);                          // end of the pinned bar
        await using (var o = observer.CreateDbContext())
        {
            o.Sessions.Add(Session(OptionJson()));
            for (var seq = 1; seq <= 3; seq++)
            {
                var end = boundary.AddMinutes(seq - 3);
                o.FutureBars.Add(new() { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = end.AddMinutes(-1), EndAvailableAtUtc = end });
                foreach (var side in new[] { OptionType.Call, OptionType.Put })
                    o.OptionBandBars.Add(new() { SessionId = 1, BarSeq = seq, Side = side, BandStrikes = "25000", StartAvailableAtUtc = end.AddMinutes(-1),
                        EndAvailableAtUtc = end, CenterStrike = 25000, BandAvailable = seq != 2 });
            }
            await o.SaveChangesAsync();
        }
        await using (var s = source.CreateDbContext())
        {
            s.Instruments.AddRange(Inst("SPOT", InstrumentType.Index), Inst("VIX", InstrumentType.Vix), Inst("FUT", InstrumentType.Future));
            var dayOpen = Open930.AddMinutes(-20);
            s.Ticks.AddRange(
                Px("SPOT", 24900, dayOpen), Px("SPOT", 24987.5m, boundary.AddSeconds(-10)), Px("SPOT", 29999, boundary.AddSeconds(5)),
                Px("FUT", 25000, dayOpen), Px("FUT", 25044, boundary.AddSeconds(-15)),
                Px("VIX", 14, dayOpen), Px("VIX", 14.5m, boundary.AddSeconds(-30)),
                Px("CE25000", 100, dayOpen), Px("CE25000", 150.25m, boundary.AddSeconds(-20), depth: true), Px("CE25000", 999, boundary.AddSeconds(3), depth: true),
                Px("PE25000", 120, dayOpen), Px("PE25000", 140.75m, boundary.AddSeconds(-25), depth: true),
                Px("CE25050", 5, boundary.AddSeconds(-1)));
            await s.SaveChangesAsync();
        }
        return (source, observer, boundary);
    }

    [Fact]
    public async Task PinnedQuote_UsesOnlyTicksAtOrBeforeTheTargetBarEnd_AndCenterStrikeFromThatBar()
    {
        var (source, observer, boundary) = await SeedPinnedAsync();
        var service = new AdaptiveQuoteAsOfService(source, observer);

        var snap = await service.LoadAsync(1, 3);
        Assert.Null(snap.UnavailableReason);
        Assert.Equal(boundary, snap.BoundaryUtc);
        Assert.Equal(24987.5m, snap.Spot!.Quote.Ltp);          // the 29999 tick after the boundary is never used
        Assert.Equal(87.5m, snap.Spot.Quote.Change);           // vs the first tick of the day
        Assert.Equal(boundary.AddSeconds(-10), snap.Spot.AvailableAtUtc);
        Assert.Equal(25044m, snap.Future!.Quote.Ltp);
        Assert.Equal(14.5m, snap.Vix!.Quote.Ltp);
        Assert.Equal(25000m, snap.CallStrike); Assert.Equal(25000m, snap.PutStrike);
        Assert.Equal(150.25m, snap.Call!.Quote.Ltp);           // 999 after the boundary ignored
        Assert.Equal(149.75m, snap.Call.Quote.Bid); Assert.Equal(150.75m, snap.Call.Quote.Ask);
        Assert.Equal(50.25m, snap.Call.Quote.Change);
        Assert.Equal(140.75m, snap.Put!.Quote.Ltp);
    }

    [Fact]
    public async Task PinnedQuote_EarlierBarIsNotRecenteredOrBackfilledFromLaterData()
    {
        var (source, observer, boundary) = await SeedPinnedAsync();
        var service = new AdaptiveQuoteAsOfService(source, observer);

        var bar1 = await service.LoadAsync(1, 1);                     // boundary 2 minutes before the seeded quotes
        Assert.Equal(boundary.AddMinutes(-2), bar1.BoundaryUtc);
        Assert.Equal(24900m, bar1.Spot!.Quote.Ltp);                   // only the day-open tick precedes it
        Assert.Equal(0m, bar1.Spot.Quote.Change);
        Assert.Equal(100m, bar1.Call!.Quote.Ltp);                     // the later 150.25 tick is not used for an earlier bar
    }

    [Fact]
    public async Task PinnedQuote_BandUnavailableBar_HasNoCenterStrikeAndNoOptionQuotes()
    {
        var (source, observer, _) = await SeedPinnedAsync();
        var snap = await new AdaptiveQuoteAsOfService(source, observer).LoadAsync(1, 2);
        Assert.Null(snap.CallStrike); Assert.Null(snap.PutStrike);
        Assert.Null(snap.Call); Assert.Null(snap.Put);
        Assert.NotNull(snap.Spot);
    }

    [Fact]
    public async Task PinnedQuote_InitializationCapture_UsesFrozen0930BoundaryAndAnchorCenter()
    {
        var (source, observer, _) = await SeedPinnedAsync();
        var snap = await new AdaptiveQuoteAsOfService(source, observer).LoadAsync(1, 0);
        Assert.Equal(Open930, snap.BoundaryUtc);
        Assert.Equal(25000m, snap.CallStrike);
        Assert.Equal(24900m, snap.Spot!.Quote.Ltp);                   // only the 09:10 day-open tick precedes 09:30
        Assert.Equal(100m, snap.Call!.Quote.Ltp);
    }

    [Fact]
    public async Task PinnedQuote_MissingSessionOrBar_IsUnavailableWithReason()
    {
        var (source, observer, _) = await SeedPinnedAsync();
        var service = new AdaptiveQuoteAsOfService(source, observer);
        Assert.NotNull((await service.LoadAsync(null, 3)).UnavailableReason);
        Assert.NotNull((await service.LoadAsync(99, 3)).UnavailableReason);
        var noBar = await service.LoadAsync(1, 77);
        Assert.NotNull(noBar.UnavailableReason); Assert.Null(noBar.Spot);
    }

    // ---- Rendering ----

    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new();
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }

    static async Task<string> RenderAsync<T>(ServiceCollection services, Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
        return System.Net.WebUtility.HtmlDecode(html); // Razor encodes "+", "·" and "—"; assert on the text a browser shows
    }

    [Fact]
    public async Task LiveQuotePanel_CaptureMode_RendersPinnedValuesAsLabelsWithoutLiveOnlyControls()
    {
        var (source, observer, boundary) = await SeedPinnedAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<NiftySignalDbContext>>(source);
        services.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(observer);
        services.AddSingleton<AdaptiveQuoteAsOfService>();
        using var live = new LiveDataService(source, new FlatTradeAuthClient(new HttpClient(),
            Options.Create(new FlatTradeOptions { UserId = "t", ApiKey = "t", ApiSecret = "t" })), new Monitor(), NullLogger<LiveDataService>.Instance, startPolling: false);
        services.AddSingleton(live);

        var html = await RenderAsync<LiveQuotePanel>(services, new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 3 });

        Assert.Contains("NIFTY Live Quote", html);
        Assert.Contains("data-capture-ready=\"true\"", html);
        Assert.Contains("pinned as of 09:40:00 IST", html);
        Assert.Contains("24987.50 (+87.50)", html);
        Assert.Contains("25044.00", html);
        Assert.Contains("150.25 (+50.25) · 149.75 / 150.75", html);
        Assert.Contains("@ 09:39:50", html);                         // each quote shows its source tick's availability time
        Assert.Matches("Nifty call strike[^>]*>25000<", html);       // exact strike label, not a selectable (re-centerable) control
        Assert.DoesNotContain("<select", html);
        Assert.DoesNotContain("Gamma Flip", html);                   // live-chain derived; cannot be reproduced as of a past bar
        Assert.DoesNotContain("Session metadata stale", html);
        Assert.DoesNotContain("29999", html); Assert.DoesNotContain("999.00", html);
    }

    [Fact]
    public async Task LiveQuotePanel_CaptureMode_MissingQuotesRenderUnavailableNotAnOlderOrLaterQuote()
    {
        var (source, observer, _) = await SeedPinnedAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<NiftySignalDbContext>>(source);
        services.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(observer);
        services.AddSingleton<AdaptiveQuoteAsOfService>();
        using var live = new LiveDataService(source, new FlatTradeAuthClient(new HttpClient(),
            Options.Create(new FlatTradeOptions { UserId = "t", ApiKey = "t", ApiSecret = "t" })), new Monitor(), NullLogger<LiveDataService>.Instance, startPolling: false);
        services.AddSingleton(live);

        var html = await RenderAsync<LiveQuotePanel>(services, new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 2 });   // band unavailable on bar 2

        Assert.Contains("data-capture-ready=\"true\"", html);
        Assert.Matches("Nifty call strike[^>]*>—<", html);
        Assert.Contains("unavailable", html);
        Assert.DoesNotContain("150.25", html);
    }

    // ---- Compact grids ----

    static async Task<ObserverFactory> SeedGridAsync()
    {
        var factory = new ObserverFactory();
        await using var db = factory.CreateDbContext();
        db.Sessions.Add(Session());
        db.Runtime.Add(new() { SessionId = 1, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = Open930, LastCompletedBarSeq = 12 });
        for (var seq = 1; seq <= 12; seq++)
        {
            var end = Open930.AddMinutes(seq); var begin = end.AddMinutes(-1);
            var zeroDuration = seq == 12;
            db.FutureBars.Add(new()
            {
                SessionId = 1, BarSeq = seq, StartAvailableAtUtc = begin, EndAvailableAtUtc = end, Volume = 1000, TradeUpdates = 1,
                DurationSeconds = zeroDuration ? 0 : 50, Open = 25000, High = 25001, Low = 25000, Close = 25001, BarPriceDisplacement = 1.5,
                StrictDelta = 123, EnrichedDelta = -456,
            });
            if (seq >= 3)
                db.RollingStates.Add(new()
                {
                    SessionId = 1, StartBarSeq = seq - 2, EndBarSeq = seq, StartAvailableAtUtc = begin, EndAvailableAtUtc = end,
                    PriceDisplacement = -7.25, StrictDelta = -777, EnrichedDelta = -888, RollingStrictAbsDeltaChange = -99, OiChange = 4242,
                    Efficiency = 0.5, State = AdaptiveStateKind.Weak2, StrictDominanceEvolution = "Weakening",
                });
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
                db.OptionBandBars.Add(new()
                {
                    SessionId = 1, BarSeq = seq, Side = side, BandStrikes = "25000", StartAvailableAtUtc = begin, EndAvailableAtUtc = end,
                    CenterStrike = 25000, BandAvailable = seq != 11, UnavailableReason = "fixture option quotes missing",
                    BarBandPriceChange = side == OptionType.Call ? 2.5 : -3.5, RollingBandPriceChange = side == OptionType.Call ? 10.5 : -20.5,
                    ContractStrictDelta = side == OptionType.Call ? 321 : -654, ContractEnrichedDelta = side == OptionType.Call ? 111 : -222,
                    ContractRollingStrictDelta = side == OptionType.Call ? 5555 : -6666,
                });
            foreach (var variant in new[] { ResidualVariant.Atm, ResidualVariant.AtmPlusMinus2 })
                db.OptionResidualBars.Add(new()
                {
                    SessionId = 1, BarSeq = seq, Variant = variant, EndAvailableAtUtc = end, Relationship = "ALIGN", IsAvailable = seq != 11,
                    UnavailableReason = "fixture diagnostic quote missing", CenterStrike = 25000, FutureChangeFrom0930 = 12.5,
                    CEResidualPct = 1.25, PEResidualPct = -2.5, DirectionalResidualPct = 3.75, ResidualDelta = 0.5, ResidualDirection = 1, MaxQuoteAgeSeconds = 0.75,
                    CEResidual = 2, PEResidual = 3,
                });
        }
        // Frozen 09:30 anchor: 5 CE + 5 PE components at 20 each (center flagged), so the ATM±2 baselines are 100 + 100 and ATM 20 + 20.
        foreach (var side in new[] { OptionType.Call, OptionType.Put })
            for (var k = 0; k < 5; k++)
                db.ResidualAnchorComponents.Add(new() { SessionId = 1, Side = side, Strike = 24900 + 50 * k, Token = $"{side}{k}", TradingSymbol = $"{side}{k}",
                    QuoteTimestampUtc = Open930, Price0930 = 20, IsCenterStrike = k == 2 });
        db.OptionsSupplemental.Add(new() { SessionId = 1, BarSeq = 12, MetricsVersion = OptionsSupplementalBar.MetricsVersion,
            CeOiDelta = 100, PeOiDelta = -100, CePosition = "CallLongBuild", PePosition = "PutShortCover", CeDeltaIv = 3.0, PeDeltaIv = 2.75,
            IvSkewEnd = 1.25, VolPcr = 0.8, RollVolPcr = 1.1, DayCeVolume = 1000, DayPeVolume = 900, UniverseTokenCount = 40, TokensObserved = 38 });
        db.FuturesSupplemental.Add(new() { SessionId = 1, BarSeq = 12, MetricsVersion = FuturesMicrostructureBar.MetricsVersion, MicroDevTimeWeighted = 0.1234, Ofi = -400 });
        db.FuturesSupplemental.Add(new() { SessionId = 1, BarSeq = 11, MetricsVersion = "some-other-version", MicroDevTimeWeighted = 9.9, Ofi = 999 }); // another contract: never shown
        await db.SaveChangesAsync();
        return factory;
    }

    static ServiceCollection GridServices(ObserverFactory factory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AdaptiveObserverDbContext>>(factory);
        services.AddSingleton<AdaptiveObserverDataService>();
        return services;
    }

    static string[] Headers(string html, int tableIndex)
    {
        var table = Regex.Matches(html, "<table.*?</table>", RegexOptions.Singleline)[tableIndex].Value;
        var head = Regex.Match(table, "<thead.*?</thead>", RegexOptions.Singleline).Value;
        return Regex.Matches(head, "<th(?: [^>]*)?>(.*?)</th>", RegexOptions.Singleline).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim())).ToArray();
    }

    static string[][] FirstRows(string html, int tableIndex, int rows)
    {
        var table = Regex.Matches(html, "<table.*?</table>", RegexOptions.Singleline)[tableIndex].Value;
        var body = Regex.Match(table, "<tbody.*?</tbody>", RegexOptions.Singleline).Value;
        return Regex.Matches(body, "<tr[ >].*?</tr>", RegexOptions.Singleline).Take(rows)
            .Select(r => Regex.Matches(r.Value, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline)
                .Select(c => System.Net.WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]+>", "").Trim())).ToArray()).ToArray();
    }

    [Fact]
    public async Task CompactGrids_Capture_ShowOnlyTheSliceOneColumnsWithoutPlaceholders()
    {
        var factory = await SeedGridAsync();
        var html = await RenderAsync<AdaptiveObserverPanel>(GridServices(factory), new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 12 });

        Assert.Equal(3, Regex.Matches(html, "<table[ >]").Count);
        Assert.Equal(["Seq", "End IST", "Dur s", "Urgency", "Bar ΔPx", "Roll ΔPx", "Strict Δ", "Enriched Δ", "Roll Strict", "Roll Enriched",
            "|Strict| Δ", "MicroDev", "OFI", "Roll OI Δ", "Roll Efficiency", "Evolution", "State"], Headers(html, 0));
        Assert.Equal(["Seq", "End IST", "Center", "Roll?", "CE ΔPx", "PE ΔPx", "CE Roll ΔPx", "PE Roll ΔPx", "CE Strict Δ", "PE Strict Δ",
            "CE Enriched Δ", "PE Enriched Δ", "CE Roll Strict", "PE Roll Strict", "CE OI Δ", "PE OI Δ", "CE Position", "PE Position",
            "CE ΔIV", "PE ΔIV", "IV Skew", "Vol PCR", "Roll Vol PCR"], Headers(html, 1));
        Assert.Equal(["Seq", "End IST", "Future Δ09:30", "CE Res %", "CE Res Δ%", "PE Res %", "PE Res Δ%", "Directional Res %",
            "Adjacent Directional Res Δ", "Straddle Res %", "Direction", "Relation", "Quote Age"], Headers(html, 2));

        // No columns from later slices, no legacy bridged delta, no diagnostic wide-table headers, no diagnostic toggle in a capture.
        foreach (var absent in new[] { "ΔBasis", "Day Vol PCR", "Legacy Bridged", "Strict ratio", "Diagnostic view" })
            Assert.DoesNotContain(absent, html);
    }

    [Fact]
    public async Task CompactFuturesGrid_ShowsDerivedUrgencyAndRollingFields_ZeroDurationBarIsADash()
    {
        var factory = await SeedGridAsync();
        var html = await RenderAsync<AdaptiveObserverPanel>(GridServices(factory), new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 12 });

        var rows = FirstRows(html, 0, 2);
        // Bar 12: zero duration => Urgency unavailable; rolling values from the persisted rolling row.
        // MicroDev / OFI come from the versioned sidecar row for that bar.
        Assert.Equal(["12", "09:42:00", "0.0", "—", "+1.50", "-7.25", "+123", "-456", "-777", "-888", "-99", "+0.123", "-400", "+4242", "0.500", "Weakening", "Weak2"], rows[0]);
        // Bar 11: 1000 / 50s = 20.0 volume per second; it has no sidecar row, so MicroDev / OFI are unavailable (not zero).
        Assert.Equal("20.0", rows[1][3]);
        Assert.Equal(["—", "—"], rows[1][11..13]);
    }

    [Fact]
    public async Task CompactOptionsGrid_UsesContractQuantitiesAndExplicitUnavailableRows()
    {
        var factory = await SeedGridAsync();
        var html = await RenderAsync<AdaptiveObserverPanel>(GridServices(factory), new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 12 });

        var rows = FirstRows(html, 1, 2);
        // Bar 12 has a sidecar row: center OI Δ, positions, ΔIV, skew (end of bar) and PCRs; bar 10 has none, so those cells are unavailable (never zero).
        Assert.Equal(["12", "09:42:00", "25000", "—", "+2.50", "-3.50", "+10.50", "-20.50", "+321", "-654", "+111", "-222", "+5555", "-6666",
            "+100", "-100", "CallLongBuild", "PutShortCover", "+3.00", "+2.75", "+1.25", "0.80", "1.10"], rows[0]);
        Assert.Equal(["11", "09:41:00", "Unavailable: fixture option quotes missing"], rows[1]);
        Assert.Equal(["—", "—", "—", "—", "—", "—", "—", "—", "—"], FirstRows(html, 1, 3)[2][14..23]);
    }

    [Fact]
    public async Task CompactResidualGrid_ShowsExistingFieldsOnly_AndExplicitUnavailableRows()
    {
        var factory = await SeedGridAsync();
        var html = await RenderAsync<AdaptiveObserverPanel>(GridServices(factory), new()
        { ["CaptureMode"] = true, ["CaptureSessionId"] = 1L, ["CaptureBarSeq"] = 12 });

        var rows = FirstRows(html, 2, 4);
        // Bar 12: the previous bar (11) is unavailable, so every adjacent delta is unavailable even though bar 10 was valid (no bridging).
        // The straddle value uses the frozen 09:30 baselines (5 CE + 5 PE anchors at 20 each = 200): 100 * (2 + 3) / 200.
        Assert.Equal(["12", "09:42:00", "+12.50", "+1.25%", "—", "-2.50%", "—", "+3.75%", "—", "+2.50%", "UP", "ALIGN", "0.75"], rows[0]);
        Assert.Equal(["11", "09:41:00", "Unavailable: fixture diagnostic quote missing"], rows[1]);
        // Bar 10 follows valid bar 9 with identical readings: adjacent deltas are zero and the identity holds.
        Assert.Equal(["10", "09:40:00", "+12.50", "+1.25%", "0.00%", "-2.50%", "0.00%", "+3.75%", "0.00%", "+2.50%", "UP", "ALIGN", "0.75"], rows[2]);
    }

    [Fact]
    public async Task LiveDashboard_DefaultsToCompactGrids_WithADiagnosticViewControl()
    {
        var factory = await SeedGridAsync();
        // The live panel reads "today"; give the session today's IST trade date and re-render.
        await using (var db = factory.CreateDbContext())
        {
            var session = await db.Sessions.SingleAsync();
            session.TradeDate = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
            await db.SaveChangesAsync();
        }
        var html = await RenderAsync<AdaptiveObserverPanel>(GridServices(factory), new());

        Assert.Contains("Diagnostic view", html);
        Assert.Equal(3, Regex.Matches(html, "<table[ >]").Count);
        Assert.Equal(17, Headers(html, 0).Length);
        Assert.DoesNotContain("Strict ratio", html);                   // diagnostic wide tables are not rendered by default
        Assert.DoesNotContain("Legacy Bridged", html);
    }
}
