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

/// <summary>Top-5 Market Summary / Dashboard integration: pure aggregation (event pooling, no time weights, missing values), attestation enforcement in the loaders, per-bar projections and rendering.</summary>
public class Top5SummaryTests
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateOnly Expiry = new(2026, 10, 13);
    static readonly DateTimeOffset T0 = new(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));

    // ---------------------------------------------------------------------------------------------------------------- pure aggregation

    /// <summary>One bar of EVENT accumulators: <paramref name="bid"/> / <paramref name="ask"/> are the bar's summed notionals over <paramref name="events"/> coherent snapshots; MicroDev5 / TOB5 sums and OFI5 are optional.</summary>
    static DepthBar Bar(int seq, double bid, double ask, int events = 1, double? micro = null, double? tob = null, int? microEvents = null, long? net = null, long? gross = null)
        => new(seq, bid, ask, events, micro, tob, microEvents ?? (micro is null ? 0 : events), net, gross);

    [Fact]
    public void Futures_Imbalance_IsPooledOverEvents_NotAMeanOfBarRatios()
    {
        // bar 1: ONE snapshot (bid 300 ask 100, ratio +.5); bar 2: THREE snapshots (bids sum 300, asks sum 900, ratio -.5): events, not seconds, set the weight
        var bars = new Dictionary<int, DepthBar> { [1] = Bar(1, 300, 100, 1), [2] = Bar(2, 300, 900, 3) };
        var w = DepthSummaryCalculator.Futures(bars, 2).First(x => x.Window.Name == "5 Bars");
        Assert.Equal(CoverageStatus.Insufficient, w.Status);                                  // only 2 completed bars: a 5-bar window does not exist yet
        var day = DepthSummaryCalculator.Futures(bars, 2).Single(x => x.Window.Bars is null);
        // num = (300-100) + (300-900) = -400; den = 400 + 1200 = 1600 -> -0.25 (a plain mean of the per-bar ratios would be 0)
        Assert.Equal(-0.25, day.Imbalance.Raw!.Value, 12);
        Assert.Equal(-400, day.Imbalance.Num!.Value, 9);
        Assert.Equal(1600, day.Imbalance.Den!.Value, 9);
        Assert.Equal(-25d, day.Imbalance.Ratio!.Value, 9);
        Assert.Equal(4, day.DepthEvents);
        Assert.Equal(CoverageStatus.Full, day.Status);
    }

    [Fact]
    public void Futures_Imbalance_SkipsBarsWithoutUsableDepth_ReportsPartial_AndNeverFillsZero()
    {
        var bars = Enumerable.Range(1, 10).ToDictionary(i => i, i => Bar(i, 300, 100));
        bars.Remove(4);                                                                         // no row at all
        bars[7] = new DepthBar(7, null, null, 0);                                                // row without any eligible event (all invalid / superseded)
        var w = DepthSummaryCalculator.Futures(bars, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((8, 10, CoverageStatus.Partial), (w.UsableBars, w.Bars, w.Status));
        Assert.Equal(0.5, w.Imbalance.Raw!.Value, 12);                                          // the events that exist
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
        var (basket, history) = OptionFixture(10, (seq, t) => (t == "CE26000" && seq == 6) ? null : (t == "PE26050" && seq == 3) ? new DepthBar(3, null, null, 0) : t.StartsWith("CE") ? Bar(seq, 300, 100) : Bar(seq, 100, 300));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((4, 4, CoverageStatus.Partial), (w.CeContracts, w.PeContracts, w.Status));   // the two contracts with a hole are excluded, never bridged or zero-filled
        Assert.Equal(1.0, w.Direction.Raw!.Value, 12);                                            // the remaining contracts still carry the same values
        Assert.StartsWith("8/10 partial", w.Text);

        // a side with no complete contract: Direction is unavailable (never CE minus nothing)
        var (b2, h2) = OptionFixture(10, (seq, t) => t.StartsWith("PE") ? null : Bar(seq, 300, 100));
        var none = DepthSummaryCalculator.Direction(b2, GridKind.Total, h2, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Null(none.Direction.Raw);
        Assert.Equal(CoverageStatus.Insufficient, none.Status);
        Assert.Equal(0, none.PeContracts);
    }

    [Fact]
    public void TotalDirection_ShortSessions_AreInsufficientForLongWindows_AndEveryEventCountsOnce()
    {
        // CE bars 1-3: one snapshot (bid 300 / ask 100); bars 4-7: TWO snapshots (sums bid 200 / ask 200). PE flat.
        var (basket, history) = OptionFixture(7, (seq, t) => t.StartsWith("CE") ? (seq <= 3 ? Bar(seq, 300, 100, 1) : Bar(seq, 200, 200, 2)) : Bar(seq, 100, 100));
        var rows = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 7);
        Assert.Null(rows.Single(x => x.Window.Name == "50 Bars").Direction.Raw);
        var day = rows.Single(x => x.Window.Bars is null);
        // num = 3*200 + 4*0 = 600; den = 3*400 + 4*400 = 2800 (no elapsed time anywhere)
        Assert.Equal(600d / 2800d, day.Ce.Raw!.Value, 12);
        Assert.Equal(90, day.DepthEvents);                                                          // 5 CE x (3x1 + 4x2) + 5 PE x 7 events
    }

    [Fact]
    public void Futures_Ofi5_AndMicroDev5_ArePooledSums_AndUnavailableBarsAreSkippedNotZero()
    {
        var bars = new Dictionary<int, DepthBar>
        {
            // MicroDev5 raw = micro/events; ratio = tob/events x 100
            [1] = Bar(1, 300, 100, 2, micro: 0.4, tob: 0.6, net: 40, gross: 100),
            [2] = Bar(2, 100, 100, 6, micro: 0.6, tob: -0.6, net: -10, gross: 50),
            [3] = new DepthBar(3, null, null, 0),                                              // no eligible event and no valid OFI5 transition
        };
        var w = DepthSummaryCalculator.Futures(bars, 3).Single(x => x.Window.Bars is null);
        Assert.Equal((30d, 30d, 150d), (w.Ofi5.Raw!.Value, w.Ofi5.Num!.Value, w.Ofi5.Den!.Value));       // ΣNet / ΣGross
        Assert.Equal(1.0 / 8d, w.MicroDev5.Raw!.Value, 12);                                              // (0.4 + 0.6) / (2 + 6) events
        Assert.Equal(0d, w.MicroDev5.Ratio!.Value, 9);                                                   // mean TOB5 = (0.6 - 0.6) / 8
        Assert.Equal((8, 8), (w.DepthEvents, w.MicroDev5Events));
        Assert.Equal((2, 3, CoverageStatus.Partial), (w.UsableBars, w.Bars, w.Status));
    }

    [Fact]
    public void TotalDirection_Ofi5_AndMicroDev5_UseCombinedDenominator_RawDirection_AndOwnCoverageCounts()
    {
        // CE: net +30 gross 100, MicroDev5 raw 0.05 (1 event, TOB5 0.2). PE: net -10 gross 100, raw -0.02 (TOB5 -0.1).
        var (basket, history) = OptionFixture(5, (seq, t) => t.StartsWith("CE") ? Bar(seq, 300, 100, 1, 0.05, 0.2, net: 30, gross: 100) : Bar(seq, 100, 300, 1, -0.02, -0.1, net: -10, gross: 100));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 5).Single(x => x.Window.Name == "5 Bars");
        Assert.Equal((10, 10), (w.Ofi5Contracts, w.MicroDev5Contracts));
        Assert.Equal((5 * 5 * 30d - 5 * 5 * -10d, 5 * 5 * 200d), (w.Ofi5Dir.Num!.Value, w.Ofi5Dir.Den!.Value));      // (ΣCE − ΣPE) / (ΣCE gross + ΣPE gross)
        Assert.Equal(0.05 - -0.02, w.MicroDev5Dir.Raw!.Value, 12);                                                    // CE raw − PE raw, NOT negated
        Assert.Equal((0.2 - -0.1) / 2d * 100d, w.MicroDev5Dir.Ratio!.Value, 9);
        Assert.True(w.MicroDev5Dir.Raw > 0);
    }

    [Fact]
    public void TotalDirection_ExplainsWhyContractsAreExcluded_NoRow_VersusNoEligibleEvent()
    {
        var (basket, history) = OptionFixture(10, (seq, t) => t == "CE26000" && seq == 6 ? null : t == "PE26050" && seq == 3 ? new DepthBar(3, null, null, 0) : Bar(seq, 100, 100));
        var w = DepthSummaryCalculator.Direction(basket, GridKind.Total, history, 10).Single(x => x.Window.Name == "10 Bars");
        Assert.Equal((1, 1, 0), (w.DepthWhy!.NoAttestedRow, w.DepthWhy.NoEligibleEvents, w.DepthWhy.Unresolved));
        Assert.Equal((4, 4), (w.CeContracts, w.PeContracts));
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

    static AdaptiveOptionsDepth5Row DepthRow(int seq, string token, bool call, double strike, double bid, double ask, string? version = null) => new()
    {
        SessionId = 1, BarSeq = seq, Token = token, MetricsVersion = version ?? OptionDepth5Bar.MetricsVersion, TradingSymbol = "x", Expiry = Expiry, Strike = strike, Side = call ? OptionType.Call : OptionType.Put,
        BarSeconds = 60, Ofi5 = call ? 30 : -10, GrossOfi5 = 100, Ofi5DirectTransitions = 3, Ofi5BridgedTransitions = 1,
        Tob5EventSum = call ? 0.2 : -0.1, MicroDev5EventSum = call ? 0.05 : -0.02, ValidMicroDev5Events = 1,
        BidNotionalEventSum = bid, AskNotionalEventSum = ask, ValidDepthEvents = 1,
    };

    static async Task<Factory> SeedAsync(int bars, bool futuresDepth = true, bool optionDepth = true, bool attest = true, bool legacyV1 = false)
    {
        var f = new Factory();
        await using var db = f.CreateDbContext();
        db.Sessions.Add(Session());
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = 1, LastHeartbeatUtc = T0, LastCompletedBarSeq = bars });
        for (var seq = 1; seq <= bars; seq++)
        {
            db.FutureBars.Add(new() { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T0.AddMinutes(seq - 1), EndAvailableAtUtc = T0.AddMinutes(seq), DurationSeconds = 60, Open = 26_000, Close = 26_001, Volume = 13_650 });
            if (futuresDepth)
                db.FuturesDepth5.Add(new() { SessionId = 1, BarSeq = seq, MetricsVersion = legacyV1 ? FuturesDepth5Bar.LegacyTimeWeightedVersion : FuturesDepth5Bar.MetricsVersion, BarSeconds = 60, Ofi5 = 40, GrossOfi5 = 100, Ofi5DirectTransitions = 2, Ofi5BridgedTransitions = 1, Tob5EventSum = 0.6, MicroDev5EventSum = 0.2, ValidMicroDev5Events = 2,
                    BidNotionalEventSum = 400, AskNotionalEventSum = 200, ValidDepthEvents = 2 });
            foreach (var s in Strikes)
                foreach (var call in new[] { true, false })
                    db.OptionStrikeBars.Add(new()
                    {
                        SessionId = 1, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T0.AddMinutes(seq), Expiry = Expiry, Strike = s, Side = call ? OptionType.Call : OptionType.Put,
                        Token = $"{(call ? "CE" : "PE")}{s:0}", TradingSymbol = "x", PriceStart = 100, PriceEnd = 101, TotalQuantity = 100, StrictDelta = 10, EnrichedBuy = 5, EnrichedSell = 5, EnrichedDelta = 1, OiStart = 1000, OiEnd = 1010, IvStart = 12, IvEnd = 12.5,
                    });
            if (optionDepth)
            {
                var rows = Strikes.SelectMany(s => new[] { DepthRow(seq, $"CE{s:0}", true, s, 300, 100, legacyV1 ? OptionDepth5Bar.LegacyTimeWeightedVersion : null), DepthRow(seq, $"PE{s:0}", false, s, 100, 300, legacyV1 ? OptionDepth5Bar.LegacyTimeWeightedVersion : null) }).ToList();
                db.OptionsDepth5.AddRange(rows);
                if (attest)
                    db.OptionsDepth5Attestations.Add(new() { SessionId = 1, BarSeq = seq, MetricsVersion = legacyV1 ? OptionDepth5Bar.LegacyTimeWeightedVersion : OptionDepth5Bar.MetricsVersion, TokenCount = rows.Count, Digests = OptionsDepth5Attestation.Format(rows), VerifiedAtUtc = DateTimeOffset.UtcNow });
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
            row.BidNotionalEventSum += 1;
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
    public async Task Summary_NeverServesV1TimeWeightedRowsAsV2_EvenWhenAttested()
    {
        var f = await SeedAsync(120, legacyV1: true);                                                          // futures + attested options rows exist, but only under the v1 version
        var v = await Service(f).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        Assert.All(v.FuturesDepth!, w => Assert.Null(w.Imbalance.Raw));
        Assert.All(v.FuturesDepth!, w => Assert.Null(w.Ofi5.Raw));
        foreach (var grid in new[] { v.TotalDepth!, v.LowerDepth!, v.UpperDepth! })
            Assert.All(grid, w => { Assert.Null(w.Direction.Raw); Assert.Null(w.Ofi5Dir.Raw); Assert.Null(w.MicroDev5Dir.Raw); });
        var h = (await Service(f).LoadFixedStrikeHistoryAsync(1, Catalog(), 26_000, Enumerable.Range(1, 20).Select(i => (i, T0.AddMinutes(i))).ToList()))!;
        Assert.All(h.Rows, r => { Assert.Null(r.CeDepth); Assert.Null(r.PeDepth); });
    }

    [Fact]
    public async Task Summary_WithAttestedDepth_FillsFuturesOfi5MicroDev5_AndTheTotalGridOfi5AndMicroDev5Dirs()
    {
        var f = await SeedAsync(120);
        var v = await Service(f).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        Assert.All(v.FuturesDepth!, w =>
        {
            Assert.Equal((40d, 100d), (w.Ofi5.Num!.Value / (w.Window.Bars ?? 120), w.Ofi5.Den!.Value / (w.Window.Bars ?? 120)));   // ΣNet / ΣGross of the window
            Assert.Equal(0.2 / 2, w.MicroDev5.Raw!.Value, 12);                                                                  // ΣMicroDev5 / Σevents per bar (0.2 / 2)
            Assert.Equal(0.6 / 2 * 100d, w.MicroDev5.Ratio!.Value, 9);                                                         // mean TOB5 x 100
        });
        foreach (var grid in new[] { v.TotalDepth!, v.LowerDepth!, v.UpperDepth! })
            Assert.All(grid, w =>
            {
                Assert.Equal(0.05 - -0.02, w.MicroDev5Dir.Raw!.Value, 12);                                                      // RAW CE - PE, not inverted
                Assert.Equal(30d * 5 - -10d * 5, w.Ofi5Dir.Num!.Value / (w.Window.Bars ?? 120));
                Assert.Equal((10, 10), (w.Ofi5Contracts, w.MicroDev5Contracts));
            });
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
    public void Futures_AverageRow_UsesSumOverSumForOfi_EventCountsForMicroDev_AndSharesForAgreement()
    {
        var rows = Enumerable.Range(1, 4).Select(i => new AdaptiveFuturesGridRow(
            new AdaptiveFutureBarRow { SessionId = 1, BarSeq = i, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, DurationSeconds = 60, Open = 1, Close = 1, Volume = 1 },
            new AdaptiveRollingStateRow { SessionId = 1, EndBarSeq = i, StartAvailableAtUtc = T0, EndAvailableAtUtc = T0, FallbackShare = i / 10d, PriceStrictDeltaAgree = i <= 3 ? i != 2 : null, PriceEnrichedDeltaAgree = true })).ToList();
        var depth = new Dictionary<int, AdaptiveFuturesDepth5Row>
        {
            [1] = new() { SessionId = 1, BarSeq = 1, MetricsVersion = "v", Ofi5 = 10, GrossOfi5 = 100, Tob5EventSum = 1.0, MicroDev5EventSum = 1.0, ValidMicroDev5Events = 10 },
            [2] = new() { SessionId = 1, BarSeq = 2, MetricsVersion = "v", Ofi5 = 30, GrossOfi5 = 100, Tob5EventSum = 9.0, MicroDev5EventSum = 9.0, ValidMicroDev5Events = 30 },
            [3] = new() { SessionId = 1, BarSeq = 3, MetricsVersion = "v", Ofi5 = null, GrossOfi5 = null, ValidMicroDev5Events = 0 },
        };
        var a = Depth5Averages.Compute(rows, s => depth.GetValueOrDefault(s));
        Assert.Equal((40d, 200d), (a.Ofi5.Num!.Value, a.Ofi5.Den!.Value));                                       // bar 3 (no valid transition) and bar 4 (no row) are excluded, not zero
        Assert.Equal((1.0 + 9.0) / 40d, a.MicroDev5.Raw!.Value, 12);                                      // ΣMicroDev5 / Σevents (event-weighted, no seconds)
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

    static string FirstCell(string html, string col) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "<td data-col=\"" + col + "\"[^>]*>([^<]*)</td>").Groups[1].Value);

    [Fact]
    public async Task Render_Contracts_PercentScalingOnce_Denominators_RawVsRatio_AndMicroDevDirectionRaw()
    {
        var f = await SeedAsync(120);
        var html = await RenderAsync(await Service(f).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000));
        // futures OFI5, 5 bars: net 5 x 40 = 200, gross 5 x 100 = 500 -> ratio 40.00 (x100 exactly once; 0.40 x 100, never 4000)
        Assert.Equal("200 (40.00)", FirstCell(html, "ofi5"));
        // futures MicroDev5 (5 bars, per bar sum 0.2 / 2 events, TOB5 sum 0.6): RAW price units 0.10 and the normalized ratio mean TOB5 x 100 = 30.00 are distinct; displayed inverted like MicroDev
        Assert.Equal("-0.10 (-30.00)", FirstCell(html, "microdev5"));
        // options OFI5 Dir: CE net 25 contract-bars x 30 = 750, PE 25 x -10 = -250 -> (750 - -250) = 1000 over the COMBINED gross 25 x 100 + 25 x 100 = 5000 -> 20.00
        Assert.StartsWith("1000 (20.00)", FirstCell(html, "ofi5-dir"));
        // MicroDev5 Dir: RAW CE - PE = 0.05 - -0.02 = 0.07 (positive, NOT negated); ratio (0.2 - -0.1) / 2 x 100 = 15.00
        Assert.StartsWith("0.07 (15.00)", FirstCell(html, "microdev5-dir"));
        // Depth L5 Dir: CE (300-100)/400 = 0.5, PE -0.5 -> direction 1.000, ratio 100.00
        Assert.StartsWith("1.000 (100.00)", FirstCell(html, "depth-l5-dir"));
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
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "OFI5 Dir</th>").Count);            // OFI5 Dir / MicroDev5 Dir in all three Total grids
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "MicroDev5 Dir</th>").Count);
        Assert.Equal(15, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"ofi5-dir\"").Count);
        Assert.Equal(15, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"microdev5-dir\"").Count);
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"ofi5\"").Count);       // Futures Summary: OFI5 and MicroDev5 per window
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(html, "data-col=\"microdev5\"").Count);
        Assert.DoesNotContain("MicroDev Dir (Inv)", html);                                                     // the direction inversion was reverted
        Assert.DoesNotContain("MicroDev5 Dir (Inv)", html);
        Assert.Contains("1.000 (100.00) · 10/10", html);                                  // CE ratio - PE ratio, with the included-contract count

        var off = await Service(await SeedAsync(120, futuresDepth: false, optionDepth: false)).LoadMarketSummaryAsync(1, 120, Catalog(), 26_000);
        var offHtml = await RenderAsync(off);
        Assert.Contains("market-summary-depth-note", offHtml);
        Assert.DoesNotContain("1.000 (100.00)", offHtml);
    }
}
