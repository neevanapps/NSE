using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Components.Dashboard;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.Tests.AdaptiveObserver;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>Top-5 Market Summary / Dashboard integration: pure aggregation (pooling, time weighting, missing values), attestation enforcement in the loaders, per-bar projections and rendering.</summary>
public class Top5SummaryTests
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateOnly Expiry = new(2026, 10, 13);
    static readonly DateTimeOffset T0 = new(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));

    // ---------------------------------------------------------------------------------------------------------------- pure aggregation

    static DepthBar Bar(int seq, double bid, double ask, double usable = 30) => new(seq, usable, bid, ask);

    [Fact]
    public void Futures_Imbalance_IsPooledAndTimeWeighted_NotAMeanOfBarRatios()
    {
        // bar 1: bid 300 ask 100 for 10 s (ratio .5, weight big); bar 2: bid 100 ask 300 for 30 s (ratio -.5)
        var bars = new Dictionary<int, DepthBar> { [1] = Bar(1, 300, 100, 10), [2] = Bar(2, 100, 300, 30) };
        var w = DepthSummaryCalculator.Futures(bars, 2).First(x => x.Window.Name == "5 Bars");
        Assert.Equal(CoverageStatus.Insufficient, w.Status);                                  // only 2 completed bars: a 5-bar window does not exist yet
        var day = DepthSummaryCalculator.Futures(bars, 2).Single(x => x.Window.Bars is null);
        // num = (200*10) + (-200*30) = -4000; den = (400*10) + (400*30) = 16000 -> -0.25 (a plain mean of the ratios would be 0)
        Assert.Equal(-0.25, day.Imbalance.Raw!.Value, 12);
        Assert.Equal(-4000, day.Imbalance.Num!.Value, 9);
        Assert.Equal(16000, day.Imbalance.Den!.Value, 9);
        Assert.Equal(-25d, day.Imbalance.Ratio!.Value, 9);
        Assert.Equal(CoverageStatus.Full, day.Status);
    }

    [Fact]
    public void Futures_Imbalance_SkipsBarsWithoutUsableDepth_ReportsPartial_AndNeverFillsZero()
    {
        var bars = Enumerable.Range(1, 10).ToDictionary(i => i, i => Bar(i, 300, 100));
        bars.Remove(4);                                                                         // no row at all
        bars[7] = new DepthBar(7, 0, null, null);                                               // row without usable seconds (all stale / invalid)
        var w = DepthSummaryCalculator.Futures(bars, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((8, 10, CoverageStatus.Partial), (w.UsableBars, w.Bars, w.Status));
        Assert.Equal(0.5, w.Imbalance.Raw!.Value, 12);                                          // the usable seconds that exist, time-weighted
        Assert.Contains("partial", w.Text);

        var none = DepthSummaryCalculator.Futures(new Dictionary<int, DepthBar>(), 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Null(none.Imbalance.Raw);
        Assert.Equal(CoverageStatus.Insufficient, none.Status);
        Assert.Equal("—", none.Text);
    }

    static (Basket Basket, DepthHistory History) OptionFixture(int bars, Func<int, string, DepthBar?> bar)
    {
        var listed = Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).Select(s => new ListedStrike(s, $"CE{s:0}", $"PE{s:0}")).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var list = new List<(string, DepthBar)>();
        for (var seq = 1; seq <= bars; seq++)
            foreach (var c in basket.Contracts)
                if (bar(seq, c.Token!) is { } b) list.Add((c.Token!, b));
        return (basket, new DepthHistory(list));
    }

    [Fact]
    public void TotalDirection_NormalizesCeAndPeSeparately_ThenSubtracts()
    {
        var (basket, history) = OptionFixture(10, (seq, t) => t.StartsWith("CE") ? Bar(seq, 300, 100) : Bar(seq, 100, 300));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((5, 5, 10), (w.CeContracts, w.PeContracts, w.Included));
        Assert.Equal(CoverageStatus.Full, w.Status);
        Assert.Equal(0.5, w.Ce.Raw!.Value, 12);
        Assert.Equal(-0.5, w.Pe.Raw!.Value, 12);
        Assert.Equal(1.0, w.Direction.Raw!.Value, 12);
        Assert.Equal(100d, w.Direction.Ratio!.Value, 9);                                        // (CE - PE) x 100
    }

    [Fact]
    public void TotalDirection_PoolsPremiumNotional_BigContractsDominate_NotAMeanOfContractRatios()
    {
        // CE: one large strike (bid 900 / ask 100) and four small ones (bid 10 / ask 30). PE is flat.
        var (basket, history) = OptionFixture(5, (seq, t) => t is "CE26000" ? Bar(seq, 900, 100) : t.StartsWith("CE") ? Bar(seq, 10, 30) : Bar(seq, 100, 100));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 5).Single(x => x.Window.Name == "5 Bars");
        // pooled CE = (800 + 4*-20) / (1000 + 4*40) = 720/1160; the mean of contract ratios would be (0.8 + 4*-0.5)/5 = -0.24
        Assert.Equal(720d / 1160d, w.Ce.Raw!.Value, 12);
        Assert.Equal(0d, w.Pe.Raw!.Value, 12);
        Assert.Equal(720d / 1160d, w.Direction.Raw!.Value, 12);
    }

    [Fact]
    public void TotalDirection_ExcludesAContractWithAHoleInTheWindow_AndNeedsBothSides()
    {
        var (basket, history) = OptionFixture(10, (seq, t) => (t == "CE26000" && seq == 6) ? null : (t == "PE26050" && seq == 3) ? new DepthBar(3, 0, null, null) : t.StartsWith("CE") ? Bar(seq, 300, 100) : Bar(seq, 100, 300));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((4, 4, CoverageStatus.Partial), (w.CeContracts, w.PeContracts, w.Status));   // the two contracts with a hole are excluded, never bridged or zero-filled
        Assert.Equal(1.0, w.Direction.Raw!.Value, 12);                                            // the remaining contracts still carry the same values
        Assert.Equal("8/10 partial", w.Text);

        // a side with no complete contract: Direction is unavailable (never CE minus nothing)
        var (b2, h2) = OptionFixture(10, (seq, t) => t.StartsWith("PE") ? null : Bar(seq, 300, 100));
        var none = DepthSummaryCalculator.Direction(b2, GridKind.Total, h2, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Null(none.Direction.Raw);
        Assert.Equal(CoverageStatus.Insufficient, none.Status);
        Assert.Equal(0, none.PeContracts);
    }

    [Fact]
    public void TotalDirection_ShortSessions_AreInsufficientForLongWindows_AndTimeWeightingUsesUsableSeconds()
    {
        var (basket, history) = OptionFixture(7, (seq, t) => t.StartsWith("CE") ? Bar(seq, seq <= 3 ? 300 : 100, 100, seq <= 3 ? 30 : 10) : Bar(seq, 100, 100));
        var rows = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 7);
        Assert.Null(rows.Single(x => x.Window.Name == "50 Bars").Direction.Raw);
        var day = rows.Single(x => x.Window.Bars is null);
        // num = 3*(200*30) + 4*(0*10) = 18000; den = 3*(400*30) + 4*(200*10) = 36000 + 8000 = 44000
        Assert.Equal(18000d / 44000d, day.Ce.Raw!.Value, 12);
    }

    // ---------------------------------------------------------------------------------------------------------------- loaders (attestation)

    sealed class Factory : IDbContextFactory<AdaptiveObserverDbContext>
    {
        readonly DbContextOptions<AdaptiveObserverDbContext> _o = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public AdaptiveObserverDbContext CreateDbContext() => new(_o);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    static IReadOnlyList<double> Strikes => Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).ToList();

    static List<ObserverOptionInstrument> Universe() => Strikes.SelectMany(s => new[]
    {
        new ObserverOptionInstrument($"CE{s:0}", $"NIFTY CE{s:0}", OptionType.Call, s, Expiry, 65),
        new ObserverOptionInstrument($"PE{s:0}", $"NIFTY PE{s:0}", OptionType.Put, s, Expiry, 65),
    }).ToList();

    static AdaptiveSessionStateRow Session() => new()
    {
        Id = 1, TradeDate = Day, ModelVersion = "adaptive-fixed13650-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = Day.AddDays(20),
        OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0.AddMinutes(15), EstimatorName = "x", CreatedAtUtc = T0, WeeklyOptionExpiry = Expiry, OptionUniverseJson = JsonSerializer.Serialize(Universe()),
    };

    static AdaptiveOptionsDepth5Row DepthRow(int seq, string token, bool call, double strike, double bid, double ask) => new()
    {
        SessionId = 1, BarSeq = seq, Token = token, MetricsVersion = OptionDepth5Bar.MetricsVersion, TradingSymbol = "x", Expiry = Expiry, Strike = strike, Side = call ? OptionType.Call : OptionType.Put,
        BarSeconds = 60, Ofi5 = call ? 30 : -10, GrossOfi5 = 100, Ofi5DirectTransitions = 3, Ofi5BridgedTransitions = 1, Tob5TimeWeighted = call ? 0.2 : -0.1, MicroDev5TimeWeighted = call ? 0.05 : -0.02,
        BidNotionalTimeWeighted = bid, AskNotionalTimeWeighted = ask, Depth5UsableSeconds = 30,
    };

    static async Task<Factory> SeedAsync(int bars, bool futuresDepth = true, bool optionDepth = true, bool attest = true)
    {
        var f = new Factory();
        await using var db = f.CreateDbContext();
        db.Sessions.Add(Session());
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = 1, LastHeartbeatUtc = T0, LastCompletedBarSeq = bars });
        for (var seq = 1; seq <= bars; seq++)
        {
            db.FutureBars.Add(new() { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T0.AddMinutes(seq - 1), EndAvailableAtUtc = T0.AddMinutes(seq), DurationSeconds = 60, Open = 26_000, Close = 26_001, Volume = 13_650 });
            if (futuresDepth)
                db.FuturesDepth5.Add(new() { SessionId = 1, BarSeq = seq, MetricsVersion = FuturesDepth5Bar.MetricsVersion, BarSeconds = 60, Ofi5 = 40, GrossOfi5 = 100, Ofi5DirectTransitions = 2, Ofi5BridgedTransitions = 1, Tob5TimeWeighted = 0.3, MicroDev5TimeWeighted = 0.1,
                    BidNotionalTimeWeighted = 200, AskNotionalTimeWeighted = 100, Depth5UsableSeconds = 40 });
            foreach (var s in Strikes)
                foreach (var call in new[] { true, false })
                    db.OptionStrikeBars.Add(new()
                    {
                        SessionId = 1, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T0.AddMinutes(seq), Expiry = Expiry, Strike = s, Side = call ? OptionType.Call : OptionType.Put,
                        Token = $"{(call ? "CE" : "PE")}{s:0}", TradingSymbol = "x", PriceStart = 100, PriceEnd = 101, TotalQuantity = 100, StrictDelta = 10, EnrichedBuy = 5, EnrichedSell = 5, EnrichedDelta = 1, OiStart = 1000, OiEnd = 1010, IvStart = 12, IvEnd = 12.5,
                    });
            if (optionDepth)
            {
                var rows = Strikes.SelectMany(s => new[] { DepthRow(seq, $"CE{s:0}", true, s, 300, 100), DepthRow(seq, $"PE{s:0}", false, s, 100, 300) }).ToList();
                db.OptionsDepth5.AddRange(rows);
                if (attest)
                    db.OptionsDepth5Attestations.Add(new() { SessionId = 1, BarSeq = seq, MetricsVersion = OptionDepth5Bar.MetricsVersion, TokenCount = rows.Count, Digests = OptionsDepth5Attestation.Format(rows), VerifiedAtUtc = DateTimeOffset.UtcNow });
            }
        }

        await db.SaveChangesAsync();
        await db.AttestStrikesAsync();
        return f;
    }

    static AdaptiveObserverDataService Service(Factory f) => new(f, NullLogger<AdaptiveObserverDataService>.Instance);

    static FixedStrikeCatalog Catalog() => FixedStrikeCatalog.FromSession(Session())!;

    [Fact]
    public async Task Summary_WithAttestedDepth_FillsFuturesImbalance_AndAllThreeTotalGridsDirections()
    {
        var f = await SeedAsync(120);
        var v = await Service(f).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        Assert.Null(v.DepthNote);
        Assert.All(v.FuturesDepth!, w => { Assert.Equal(1d / 3d, w.Imbalance.Raw!.Value, 12); Assert.Equal(CoverageStatus.Full, w.Status); });
        foreach (var grid in new[] { v.TotalDepth!, v.LowerDepth!, v.UpperDepth! })
            Assert.All(grid, w => { Assert.Equal((5, 5), (w.CeContracts, w.PeContracts)); Assert.Equal(1.0, w.Direction.Raw!.Value, 12); });
    }

    [Fact]
    public async Task Summary_WithoutAttestation_OrWithAChangedRow_OrAMarker_WithholdsDepth_ButNeverTheCoreSummary()
    {
        var f = await SeedAsync(120, attest: false);
        var cat = Catalog();
        var none = await Service(f).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        Assert.Contains("no attested", none.DepthNote);
        Assert.All(none.TotalDepth!, w => Assert.Null(w.Direction.Raw));                                       // unavailable, never zero
        Assert.All(none.Grids.Single(g => g.Kind == GridKind.Total).Rows, r => Assert.Equal("10/10 – FULL", r.Coverage.Text));    // strike-v3 core is untouched

        var g = await SeedAsync(120);
        await using (var db = g.CreateDbContext())                                                              // one contract edited after it was attested
        {
            var row = await db.OptionsDepth5.FirstAsync(x => x.BarSeq == 120 && x.Token == "CE26000");
            row.BidNotionalTimeWeighted += 1;
            await db.SaveChangesAsync();
        }

        var changed = await Service(g).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        Assert.All(changed.TotalDepth!, w => Assert.Equal((4, 5), (w.CeContracts, w.PeContracts)));             // that contract is withheld from every window containing bar 120

        await using (var db = g.CreateDbContext())                                                              // futures bar 120 carries a failed-verification marker
        {
            db.ProjectionHealth.Add(new() { SessionId = 1, Component = AdaptiveProjectionComponents.FuturesDepth5, Version = FuturesDepth5Bar.MetricsVersion, BarSeq = 120, Detail = "x", DetectedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var marked = await Service(g).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        var five = marked.FuturesDepth!.Single(w => w.Window.Name == "5 Bars");
        Assert.Equal((4, 5, CoverageStatus.Partial), (five.UsableBars, five.Bars, five.Status));
    }

    [Fact]
    public async Task Summary_WithFlagsOff_NoDepthRowsAtAll_IsUnavailableAndTheCoreSummaryIsIdentical()
    {
        var off = await SeedAsync(120, futuresDepth: false, optionDepth: false);
        var on = await SeedAsync(120);
        var cat = Catalog();
        var a = await Service(off).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        var b = await Service(on).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        Assert.All(a.FuturesDepth!, w => Assert.Null(w.Imbalance.Raw));
        Assert.All(a.TotalDepth!, w => Assert.Null(w.Direction.Raw));
        Assert.Equal(JsonSerializer.Serialize(a.Grids), JsonSerializer.Serialize(b.Grids));                    // every existing grid is byte-identical with or without depth
        Assert.Equal(JsonSerializer.Serialize(a.Futures), JsonSerializer.Serialize(b.Futures));
        Assert.Equal(JsonSerializer.Serialize(a.Lower), JsonSerializer.Serialize(b.Lower));
        Assert.Equal(JsonSerializer.Serialize(a.Upper), JsonSerializer.Serialize(b.Upper));
    }

    [Fact]
    public async Task FixedStrike_ServesOnlyTheExactAttestedContracts_AndSnapshotCarriesFuturesDepth()
    {
        var f = await SeedAsync(20);
        var bars = Enumerable.Range(1, 20).Select(i => (i, T0.AddMinutes(i))).ToList();
        var h = (await Service(f).LoadFixedStrikeHistoryAsync(1, Catalog(), 26_000, bars))!;
        Assert.All(h.Rows, r => { Assert.Equal("CE26000", r.CeDepth!.Token); Assert.Equal("PE26000", r.PeDepth!.Token); });
        var dir = Depth5Ratios.OptionOfi5(h.Rows[0].CeDepth, h.Rows[0].PeDepth);
        Assert.Equal((30, -10, 40), (dir.Ce.Raw, dir.Pe.Raw, dir.Direction.Raw) is ({ } a, { } b, { } c) ? ((int)a, (int)b, (int)c) : (0, 0, 0));
        Assert.Equal(40d / 200d * 100d, dir.Direction.Ratio!.Value, 9);                                        // CE - PE over the COMBINED gross
        var micro = Depth5Ratios.OptionMicroDev5(h.Rows[0].CeDepth, h.Rows[0].PeDepth);
        Assert.Equal(0.07, micro.Direction.Raw!.Value, 12);
        Assert.Equal((0.2 - -0.1) / 2d * 100d, micro.Direction.Ratio!.Value, 9);

        await using (var db = f.CreateDbContext()) { db.OptionsDepth5Attestations.RemoveRange(await db.OptionsDepth5Attestations.ToListAsync()); await db.SaveChangesAsync(); }
        var gone = (await Service(f).LoadFixedStrikeHistoryAsync(1, Catalog(), 26_000, bars))!;
        Assert.All(gone.Rows, r => { Assert.Null(r.CeDepth); Assert.Null(r.PeDepth); Assert.NotNull(r.Ce); });  // the strike-v3 cells are unaffected
        Assert.Equal(RatioPart.None, Depth5Ratios.OptionOfi5(gone.Rows[0].CeDepth, gone.Rows[0].PeDepth).Direction);

        var snap = await Service(f).LoadSnapshotAsync(10, default, 1, 20);
        Assert.Equal(10, snap!.FuturesDepth5!.Count);
    }

    [Fact]
    public void Futures_AverageRow_UsesSumOverSumForOfi_UsableSecondsForMicroDev_AndSharesForAgreement()
    {
        var rows = Enumerable.Range(1, 4).Select(i => new AdaptiveFuturesGridRow(
            new AdaptiveFutureBarRow { SessionId = 1, BarSeq = i, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, DurationSeconds = 60, Open = 1, Close = 1, Volume = 1 },
            new AdaptiveRollingStateRow { SessionId = 1, EndBarSeq = i, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, FallbackShare = i / 10d, PriceStrictDeltaAgree = i <= 3 ? i != 2 : null, PriceEnrichedDeltaAgree = true })).ToList();
        var depth = new Dictionary<int, AdaptiveFuturesDepth5Row>
        {
            [1] = new() { SessionId = 1, BarSeq = 1, MetricsVersion = "v", Ofi5 = 10, GrossOfi5 = 100, Tob5TimeWeighted = 0.1, MicroDev5TimeWeighted = 0.1, Depth5UsableSeconds = 10 },
            [2] = new() { SessionId = 1, BarSeq = 2, MetricsVersion = "v", Ofi5 = 30, GrossOfi5 = 100, Tob5TimeWeighted = 0.3, MicroDev5TimeWeighted = 0.3, Depth5UsableSeconds = 30 },
            [3] = new() { SessionId = 1, BarSeq = 3, MetricsVersion = "v", Ofi5 = null, GrossOfi5 = null, Depth5UsableSeconds = 0 },
        };
        var a = Depth5Averages.Compute(rows, s => depth.GetValueOrDefault(s));
        Assert.Equal((40d, 200d), (a.Ofi5.Num!.Value, a.Ofi5.Den!.Value));                                       // bar 3 (no valid transition) and bar 4 (no row) are excluded, not zero
        Assert.Equal((0.1 * 10 + 0.3 * 30) / 40d, a.MicroDev5.Raw!.Value, 12);
        Assert.Equal(100d * 2 / 3, a.AgreeStrictPct!.Value, 9);                                                  // 3 defined bars, 2 agree; bar 4 undefined is excluded
        Assert.Equal(3, a.AgreeStrictBars);
        Assert.Equal(100d, a.AgreeEnrichedPct!.Value, 9);
        Assert.Equal(25d, a.FallbackSharePct!.Value, 9);                                                         // mean of 10/20/30/40 %
        Assert.Equal(3, a.DepthBars);
    }

    // ---------------------------------------------------------------------------------------------------------------- rendering

    static async Task<string> RenderAsync(MarketSummaryView view)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            System.Net.WebUtility.HtmlDecode((await renderer.RenderComponentAsync<MarketSummarySection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString()));
    }

    [Fact]
    public async Task Render_ShowsDepthColumns_OnlyWhereApproved_AndUnavailableAsDash()
    {
        var f = await SeedAsync(120);
        var view = await Service(f).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        var html = await RenderAsync(view);
        Assert.Contains("Depth Imb L5", html);                                                                  // futures summary
        Assert.Contains("Depth L5 Dir", html);                                                                  // total grids
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "Depth L5 Dir</th>").Count);         // main, ITM-shifted, OTM-shifted
        Assert.Equal(15, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"depth-l5-dir\"").Count);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"depth-imb-l5\"").Count);
        Assert.DoesNotContain("OFI5", html);                                                                    // not approved for the Market Summary grids
        Assert.DoesNotContain("MicroDev5", html);
        Assert.Contains("1.000 (100.00) · 10/10", html);                                  // CE ratio - PE ratio, with the included-contract count

        var off = await Service(await SeedAsync(120, futuresDepth: false, optionDepth: false)).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        var offHtml = await RenderAsync(off);
        Assert.Contains("market-summary-depth-note", offHtml);
        Assert.DoesNotContain("1.000 (100.00)", offHtml);
    }
}
