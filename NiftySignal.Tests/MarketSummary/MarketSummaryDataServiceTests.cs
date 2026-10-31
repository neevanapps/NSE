using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>Loader level: Auto Follow / manual pin (= the strike the Dashboard hands in), pinned historical boundary, adaptive-v1 sessions and mixed row versions, against the real EF model.</summary>
public class MarketSummaryDataServiceTests
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateOnly Expiry = new(2026, 10, 13);
    static readonly DateTimeOffset T0 = new(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5));

    sealed class Factory : IDbContextFactory<AdaptiveObserverDbContext>
    {
        readonly DbContextOptions<AdaptiveObserverDbContext> _o = new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public AdaptiveObserverDbContext CreateDbContext() => new(_o);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    static IReadOnlyList<double> Strikes => Enumerable.Range(0, 11).Select(i => 25_750d + i * 50).ToList();

    static AdaptiveObserverDataService Service(Factory f) => new(f, NullLogger<AdaptiveObserverDataService>.Instance);

    static FixedStrikeCatalog Catalog(string model = "adaptive-fixed13650-v1")
    {
        var universe = Strikes.SelectMany(s => new[]
        {
            new ObserverOptionInstrument($"CE{s:0}", $"NIFTY CE{s:0}", OptionType.Call, s, Expiry, 65),
            new ObserverOptionInstrument($"PE{s:0}", $"NIFTY PE{s:0}", OptionType.Put, s, Expiry, 65),
        }).ToList();
        return FixedStrikeCatalog.FromSession(Session(model, universe))!;
    }

    static AdaptiveSessionStateRow Session(string model, IReadOnlyList<ObserverOptionInstrument> universe) => new()
    {
        Id = 1, TradeDate = Day, ModelVersion = model, SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = Day.AddDays(20),
        OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0.AddMinutes(15), EstimatorName = "x", CreatedAtUtc = T0, WeeklyOptionExpiry = Expiry,
        OptionUniverseJson = JsonSerializer.Serialize(universe),
    };

    static AdaptiveOptionStrikeBarRow Row(int seq, double strike, bool call, string version, long qty = 100) => new()
    {
        SessionId = 1, BarSeq = seq, MetricsVersion = version, EndAvailableAtUtc = T0.AddMinutes(seq), Expiry = Expiry, Strike = strike, Side = call ? OptionType.Call : OptionType.Put,
        Token = $"{(call ? "CE" : "PE")}{strike:0}", TradingSymbol = "x", PriceStart = 100, PriceEnd = 101, TotalQuantity = qty, StrictDelta = 10, EnrichedBuy = 5, EnrichedSell = 5, EnrichedUnknown = 0, EnrichedDelta = 1,
        OiStart = 1000, OiEnd = 1010, IvStart = 12, IvEnd = 12.5,
    };

    static async Task<Factory> SeedAsync(int bars, string model = "adaptive-fixed13650-v1", Func<int, string>? version = null, int extraFutureBars = 0)
    {
        var f = new Factory();
        await using var db = f.CreateDbContext();
        var cat = Catalog(model);
        db.Sessions.Add(Session(model, JsonSerializer.Deserialize<List<ObserverOptionInstrument>>(JsonSerializer.Serialize(Strikes.SelectMany(s => new[]
        {
            new ObserverOptionInstrument($"CE{s:0}", $"NIFTY CE{s:0}", OptionType.Call, s, Expiry, 65), new ObserverOptionInstrument($"PE{s:0}", $"NIFTY PE{s:0}", OptionType.Put, s, Expiry, 65),
        })))!));
        for (var seq = 1; seq <= bars + extraFutureBars; seq++)
        {
            db.FutureBars.Add(new() { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T0.AddMinutes(seq - 1), EndAvailableAtUtc = T0.AddMinutes(seq), DurationSeconds = 60, Open = 26_000, Close = 26_001, Volume = 13_650 });
            foreach (var s in Strikes)
                foreach (var call in new[] { true, false })
                    db.OptionStrikeBars.Add(Row(seq, s, call, version?.Invoke(seq) ?? OptionStrikeBarMetrics.MetricsVersion, seq > bars ? 9_999_999 : 100));
        }

        await db.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task AutoFollowCentreChange_ReResolvesTheWholeBasketOnce_ForEveryWindow()
    {
        var f = await SeedAsync(120);
        var cat = Catalog();
        var a = await Service(f).LoadMarketSummaryAsync(1, 120, cat, 26_000);
        var b = await Service(f).LoadMarketSummaryAsync(1, 120, cat, 26_050);      // Auto Follow moved the centre by one strike
        Assert.Equal(26_000, a.Basket!.Center); Assert.Equal(26_050, b.Basket!.Center);
        Assert.Equal(new[] { "CE25900", "CE25950", "CE26000", "CE26050", "CE26100", "PE25900", "PE25950", "PE26000", "PE26050", "PE26100" }, a.Basket.Contracts.Select(c => c.Token!).OrderBy(x => x));
        Assert.Equal(new[] { "CE25950", "CE26000", "CE26050", "CE26100", "CE26150", "PE25950", "PE26000", "PE26050", "PE26100", "PE26150" }, b.Basket.Contracts.Select(c => c.Token!).OrderBy(x => x));
        foreach (var v in new[] { a, b })
            Assert.All(v.Grids.Single(g => g.Kind == GridKind.Total).Rows, r => Assert.Equal("10/10 – FULL", r.Coverage.Text));    // all five windows computed under the one basket
    }

    [Fact]
    public async Task ManualPin_KeepsTheChosenCentre_RegardlessOfLaterBars()
    {
        var f = await SeedAsync(60);
        var v = await Service(f).LoadMarketSummaryAsync(1, 60, Catalog(), 25_950);
        Assert.Equal(25_950, v.Basket!.Center);
        Assert.Equal("CE25850", v.Basket.Contracts.Single(c => c.Offset == -2 && c.IsCall).Token);
        Assert.Equal(5, v.Grids.Count(g => g.Rows.Count == 5) + 1);          // four option grids with five rows each (+1 = the futures list below)
        Assert.Equal(5, v.Futures.Count);
    }

    [Fact]
    public async Task PinnedHistoricalCapture_ReadsNothingAfterTheTargetBar()
    {
        var f = await SeedAsync(60, extraFutureBars: 20);                    // bars 61..80 carry absurd values
        var cat = Catalog();
        var atTarget = await Service(f).LoadMarketSummaryAsync(1, 60, cat, 26_000);
        var clean = await SeedAsync(60);
        var reference = await Service(clean).LoadMarketSummaryAsync(1, 60, cat, 26_000);
        Assert.Equivalent(reference.Grids, atTarget.Grids, strict: true);
        Assert.Equivalent(reference.Futures, atTarget.Futures, strict: true);
        Assert.Equal(60, atTarget.EndBarSeq);
        var total = atTarget.Grids.Single(g => g.Kind == GridKind.Total);
        Assert.All(total.Rows, r => Assert.True(r.ToBar == 60));
        Assert.True(total.Rows[4].Ce!.Volume < 100_000);                     // would be ~10^8 if bars 61..80 leaked
    }

    [Fact]
    public async Task AdaptiveV1Session_AndMixedRowVersions_LoadWithoutErrorAndPreferTheNewestVersion()
    {
        var f = await SeedAsync(30, model: "adaptive-v1", version: seq => seq <= 10 ? OptionStrikeBarMetrics.LegacyMetricsVersion : seq <= 20 ? OptionStrikeBarMetrics.PreviousMetricsVersion : OptionStrikeBarMetrics.MetricsVersion);
        var v = await Service(f).LoadMarketSummaryAsync(1, 30, Catalog("adaptive-v1"), 26_000);
        Assert.Null(v.UnavailableReason);
        Assert.All(v.Grids.Single(g => g.Kind == GridKind.Atm).Rows.Take(2), r => Assert.Equal(CoverageStatus.Full, r.Coverage.Status));
        // a bar that carries both v2 and v3 rows (replay backfill) uses exactly one row per contract and bar
        await using (var db = f.CreateDbContext())
        {
            db.OptionStrikeBars.Add(Row(30, 26_000, true, OptionStrikeBarMetrics.PreviousMetricsVersion, qty: 5_000_000));
            await db.SaveChangesAsync();
        }

        var again = await Service(f).LoadMarketSummaryAsync(1, 30, Catalog("adaptive-v1"), 26_000);
        Assert.Equal(v.Grids.Single(g => g.Kind == GridKind.Atm).Rows[0].Ce!.Volume, again.Grids.Single(g => g.Kind == GridKind.Atm).Rows[0].Ce!.Volume);   // v3 wins over v2
    }

    [Fact]
    public async Task NoCatalogOrNoCentre_IsUnavailableWithAReason_NeverZeros()
    {
        var f = await SeedAsync(5);
        Assert.Equal("no frozen option universe for this session", (await Service(f).LoadMarketSummaryAsync(1, 5, null, 26_000)).UnavailableReason);
        Assert.Equal("no centre strike selected", (await Service(f).LoadMarketSummaryAsync(1, 5, Catalog(), null)).UnavailableReason);
        Assert.Equal("no completed bar", (await Service(f).LoadMarketSummaryAsync(1, 0, Catalog(), 26_000)).UnavailableReason);
    }
}
