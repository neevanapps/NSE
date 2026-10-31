using System.Diagnostics;
using System.Globalization;
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
using Xunit.Abstractions;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>
/// Measurements (not benchmarks of the VM): in-memory aggregation, database loading against a disposable local PostgreSQL 18 database holding a full-session-sized sidecar (80 strikes x CE/PE x 220 bars =
/// 35,200 rows), and Razor rendering of the five-row grids. The numbers are written to the test output and to <c>market-summary-perf.txt</c> in the temp directory; the assertions are deliberately loose guards.
/// </summary>
public class MarketSummaryPerformanceTests(ITestOutputHelper output)
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateOnly Expiry = new(2026, 10, 13);
    static readonly DateTimeOffset T0 = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
    const int Bars = 220;
    const int StrikeCount = 80;

    static string Pct(List<double> ms)
    {
        ms.Sort();
        double P(double q) => ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(q * ms.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"n={ms.Count} p50={P(0.50):0.000} ms p95={P(0.95):0.000} ms p99={P(0.99):0.000} ms max={ms[^1]:0.000} ms");
    }

    void Report(string line)
    {
        output.WriteLine(line);
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "market-summary-perf.txt"), line + Environment.NewLine);
    }

    static List<ObserverOptionInstrument> Universe() => Enumerable.Range(0, StrikeCount).Select(i => 24_050d + i * 50).SelectMany(s => new[]
    {
        new ObserverOptionInstrument($"CE{s:0}", $"NIFTY CE{s:0}", OptionType.Call, s, Expiry, 65), new ObserverOptionInstrument($"PE{s:0}", $"NIFTY PE{s:0}", OptionType.Put, s, Expiry, 65),
    }).ToList();

    static ContractBar Bar(int seq, string token, bool call, Random r) => new(seq, token, call, 100 + r.Next(50), 100 + r.Next(50), r.Next(100, 900), r.Next(-80, 80), r.Next(10, 100), r.Next(10, 100), r.Next(0, 20), r.Next(-50, 50),
        10_000 + r.Next(500), 10_000 + r.Next(500), 12 + r.NextDouble(), 12 + r.NextDouble(), 0.4 + r.NextDouble() * 0.1, 0.4 + r.NextDouble() * 0.1, 5 + r.NextDouble(), 5 + r.NextDouble(),
        r.NextDouble() - 0.5, r.NextDouble() - 0.5, 5 + r.NextDouble() * 20, r.Next(-30, 30), r.Next(40, 90));

    [Fact]
    public void Aggregation_InMemory_FullSession_Timings()
    {
        var listed = Universe().GroupBy(o => o.Strike).Select(g => new ListedStrike(g.Key, g.Single(o => o.OptionType == OptionType.Call).Token, g.Single(o => o.OptionType == OptionType.Put).Token)).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var r = new Random(11);
        var rows = basket.Contracts.SelectMany(c => Enumerable.Range(1, Bars).Select(s => Bar(s, c.Token!, c.IsCall, r))).ToList();
        var history = new ContractHistory(rows, Enumerable.Range(1, Bars).ToDictionary(i => i, _ => 60d));
        var fut = Enumerable.Range(1, Bars).ToDictionary(i => i, i => new FuturesBar(i, 60, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        for (var i = 0; i < 30; i++) OptionSummaryCalculator.ComputeAll(basket, history, Bars);          // warm-up (JIT)
        var all = new List<double>(); var futs = new List<double>();
        for (var i = 0; i < 400; i++)
        {
            var sw = Stopwatch.StartNew();
            OptionSummaryCalculator.ComputeAll(basket, history, Bars);
            all.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            FuturesSummaryCalculator.Compute(fut, Bars);
            futs.Add(sw.Elapsed.TotalMilliseconds);
        }

        Report("aggregation ComputeAll (4 grids x 5 windows, 10 contracts x 220 bars, in memory): " + Pct(all));
        Report("aggregation futures summary (5 windows x 220 bars, in memory): " + Pct(futs));
        Assert.True(all.OrderBy(x => x).ElementAt(380) < 50, "aggregation p95 guard (target 5 ms)");
    }

    [Fact]
    public async Task Rendering_FiveRowGrids_Timings()
    {
        var listed = Universe().GroupBy(o => o.Strike).Select(g => new ListedStrike(g.Key, g.Single(o => o.OptionType == OptionType.Call).Token, g.Single(o => o.OptionType == OptionType.Put).Token)).ToList();
        var basket = BasketResolver.Resolve(Expiry, 26_000, listed);
        var r = new Random(11);
        var rows = basket.Contracts.SelectMany(c => Enumerable.Range(1, Bars).Select(s => Bar(s, c.Token!, c.IsCall, r))).ToList();
        var fut = Enumerable.Range(1, Bars).ToDictionary(i => i, i => new FuturesBar(i, 60, 26_000, 26_001, 13_650, 100, 100, 100, 10, 5, 1000, 1010, 0.1, 0.1, 20, 20, 5, 50));
        var view = new MarketSummaryView(Bars, basket, FuturesSummaryCalculator.Compute(fut, Bars),
            OptionSummaryCalculator.ComputeAll(basket, new ContractHistory(rows, Enumerable.Range(1, Bars).ToDictionary(i => i, _ => 60d)), Bars), null);
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        async Task<int> Once() => await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<MarketSummarySection>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["View"] = view }))).ToHtmlString().Length);
        for (var i = 0; i < 20; i++) await Once();
        var ms = new List<double>(); var len = 0;
        for (var i = 0; i < 200; i++) { var sw = Stopwatch.StartNew(); len = await Once(); ms.Add(sw.Elapsed.TotalMilliseconds); }
        Report($"render MarketSummarySection to HTML ({len:N0} chars, 9 tables x 5 rows): " + Pct(ms));
        Assert.True(len > 10_000);
    }

    [PgMigrationFact]
    public async Task DatabaseLoad_PostgreSql_FullSessionSidecar_Timings()
    {
        // Own database prefix: the existing migration tests assert that no "niftysignal_adaptive_mig_test_*" database exists while they run, and test classes run in parallel.
        await using var temp = await PerfDatabase.CreateAsync();
        await temp.MigrateAsync();
        var universe = Universe();
        await using (var db = temp.Context())
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Sessions.Add(new AdaptiveSessionStateRow
            {
                Id = 1, TradeDate = Day, ModelVersion = "adaptive-fixed13650-v1", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "FUT", FutureSymbol = "F", FutureExpiry = Day.AddDays(20),
                OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0.AddMinutes(15), EstimatorName = "x", CreatedAtUtc = T0, WeeklyOptionExpiry = Expiry, OptionUniverseJson = JsonSerializer.Serialize(universe),
            });
            await db.SaveChangesAsync();
            var r = new Random(3);
            for (var seq = 1; seq <= Bars; seq++)
            {
                db.FutureBars.Add(new() { SessionId = 1, BarSeq = seq, StartAvailableAtUtc = T0.AddMinutes(seq - 1), EndAvailableAtUtc = T0.AddMinutes(seq), DurationSeconds = 60, Open = 26_000, Close = 26_001, Volume = 13_650 });
                foreach (var o in universe)
                    db.OptionStrikeBars.Add(new()
                    {
                        SessionId = 1, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = T0.AddMinutes(seq), Expiry = Expiry, Strike = o.Strike, Side = o.OptionType, Token = o.Token, TradingSymbol = o.TradingSymbol,
                        PriceStart = 100, PriceEnd = 101, TotalQuantity = r.Next(0, 900), StrictDelta = 3, IvStart = 12, IvEnd = 12.2, DeltaStart = .4, DeltaEnd = .41, VegaStart = 5, VegaEnd = 5.1, OiStart = 1000, OiEnd = 1010, NetOfi = 3, GrossOfi = 9, TobTimeWeighted = .1, MicroDevTimeWeighted = .1, TobUsableSeconds = 30,
                    });
                if (seq % 20 == 0) { await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
            }

            await db.SaveChangesAsync();
        }

        var factory = new PgFactory(temp.ConnectionString);       // pooled, like the Dashboard
        var service = new AdaptiveObserverDataService(factory, NullLogger<AdaptiveObserverDataService>.Instance);
        var catalog = FixedStrikeCatalog.FromSession(new AdaptiveSessionStateRow
        {
            Id = 1, TradeDate = Day, ModelVersion = "m", SourceBranch = "t", SourceCommitSha = "t", BuildUtc = T0, FutureToken = "FUT", FutureSymbol = "F", FutureExpiry = Day, OpeningWindowStartUtc = T0, OpeningWindowEndUtc = T0, EstimatorName = "x", CreatedAtUtc = T0,
            WeeklyOptionExpiry = Expiry, OptionUniverseJson = JsonSerializer.Serialize(universe),
        })!;
        for (var i = 0; i < 5; i++) await service.LoadMarketSummaryAsync(1, Bars, catalog, 26_000);        // warm-up
        var full = new List<double>(); var early = new List<double>();
        for (var i = 0; i < 60; i++)
        {
            var sw = Stopwatch.StartNew();
            var v = await service.LoadMarketSummaryAsync(1, Bars, catalog, 26_000);
            full.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Null(v.UnavailableReason);
            Assert.All(v.Grids.Single(g => g.Kind == GridKind.Total).Rows, row => Assert.Equal("10/10 – FULL", row.Coverage.Text));
        }

        for (var i = 0; i < 40; i++) { var sw = Stopwatch.StartNew(); await service.LoadMarketSummaryAsync(1, 30, catalog, 26_000); early.Add(sw.Elapsed.TotalMilliseconds); }
        Report($"database load + aggregate (PostgreSQL 18 local, {StrikeCount * 2} contracts x {Bars} bars = {StrikeCount * 2 * Bars:N0} sidecar rows, 10 tokens read, bar {Bars}): " + Pct(full));
        Report("database load + aggregate (same database, bar 30): " + Pct(early));
        Assert.True(full.OrderBy(x => x).ElementAt(56) < 500, "warm load guard (target 50 ms)");
    }

    sealed class PerfDatabase : IAsyncDisposable
    {
        readonly string _name;
        public string ConnectionString { get; }

        PerfDatabase(string name, string cs) { _name = name; ConnectionString = cs; }

        public static async Task<PerfDatabase> CreateAsync()
        {
            var name = "niftysignal_ms_perf_test_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = new Npgsql.NpgsqlConnection(AdaptivePg.Maintenance))
            {
                await admin.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await cmd.ExecuteNonQueryAsync();
            }

            return new PerfDatabase(name, new Npgsql.NpgsqlConnectionStringBuilder(AdaptivePg.Maintenance!) { Database = name, Pooling = true }.ConnectionString);
        }

        public AdaptiveObserverDbContext Context() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(ConnectionString).Options);

        public async Task MigrateAsync()
        {
            await using var db = Context();
            await db.Database.MigrateAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var admin = new Npgsql.NpgsqlConnection(AdaptivePg.Maintenance);
            await admin.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    sealed class PgFactory(string connectionString) : IDbContextFactory<AdaptiveObserverDbContext>
    {
        public AdaptiveObserverDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(connectionString).Options);
        public Task<AdaptiveObserverDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
