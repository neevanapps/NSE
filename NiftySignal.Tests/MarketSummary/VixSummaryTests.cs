using Microsoft.EntityFrameworkCore;
using NiftySignal.Dashboard.Services;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.MarketSummary;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.MarketSummary;

public class VixSummaryTests
{
    static readonly DateOnly Day = new(2026, 10, 7);
    static readonly DateTimeOffset T0 = new DateTimeOffset(Day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
    static readonly TimeSpan Limit = VixSummaryCalculator.FreshnessLimit;

    static VixObservation O(double sec, double v) => new(T0.AddSeconds(sec), v);

    [Fact]
    public void AsOf_NeverUsesALaterObservation_AndPicksTheLatestAtOrBefore()
    {
        var obs = new[] { O(-30, 13.0), O(10, 13.2), O(20, 13.4), O(21, 99.0) };
        Assert.Equal(13.4, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(20), Limit).Value);          // exactly at the instant counts
        Assert.Equal(13.4, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(20.9), Limit).Value);        // the 21 s observation is later: never used
        Assert.Equal(13.0, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(5), Limit).Value);
        Assert.Equal(VixStatus.Missing, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(-31), Limit).Status);
    }

    [Fact]
    public void AsOf_FreshnessLimit_IsInclusive_AndStaleWithholdsTheValue()
    {
        var obs = new[] { O(0, 13.0) };
        Assert.Equal(VixStatus.Fresh, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(60), Limit).Status);
        var stale = VixSummaryCalculator.AsOf(obs, T0.AddSeconds(60.001), Limit);
        Assert.Equal(VixStatus.Stale, stale.Status);
        Assert.Null(stale.Value);                                  // an old value is never presented as current
        Assert.Equal(T0, stale.ObservedAt);
        Assert.Equal(60d, VixSummaryCalculator.FreshnessLimit.TotalSeconds);
    }

    [Fact]
    public void AsOf_SkipsInvalidValues_ButNeverBridgesAStaleGap()
    {
        var obs = new[] { O(0, 13.0), O(10, 0), O(11, double.NaN), O(12, -1) };
        var p = VixSummaryCalculator.AsOf(obs, T0.AddSeconds(15), Limit);
        Assert.Equal(13.0, p.Value);                               // latest VALID observation
        Assert.Equal(15d, p.AgeSeconds);
        Assert.Equal(VixStatus.Stale, VixSummaryCalculator.AsOf(obs, T0.AddSeconds(100), Limit).Status);     // stale, not backfilled
        Assert.Equal(VixStatus.Missing, VixSummaryCalculator.AsOf([], T0, Limit).Status);
        Assert.Equal(VixStatus.Missing, VixSummaryCalculator.AsOf(obs, null, Limit).Status);
    }

    [Fact]
    public void Compute_LevelChangeAndPercent_PerWindow_DayUsesTheSessionStartObservation()
    {
        var obs = new[] { O(-5, 14.0), O(100, 14.5), O(590, 15.0), O(598, 15.4) };
        var instants = new[]
        {
            new WindowInstant(SummaryWindow.All[0], T0.AddSeconds(100), T0.AddSeconds(600)),     // 5 Bars
            new WindowInstant(SummaryWindow.All[4], T0, T0.AddSeconds(600)),                       // Day: session start .. boundary
        };
        var r = VixSummaryCalculator.Compute(obs, instants, Limit);
        Assert.Equal(14.5, r[0].Start.Value); Assert.Equal(15.4, r[0].End.Value);
        Assert.Equal(0.9, r[0].Change!.Value, 9);
        Assert.Equal(0.9 / 14.5 * 100d, r[0].ChangePct!.Value, 9);
        Assert.Equal(14.0, r[1].Start.Value);                                                      // observation at/before session start
        Assert.Equal(1.4, r[1].Change!.Value, 9);
        Assert.Equal(1.4 / 14.0 * 100d, r[1].ChangePct!.Value, 9);
        Assert.Equal(2d, r[0].End.AgeSeconds);
    }

    [Fact]
    public void Compute_StaleOrMissingStart_GivesNoChange_NotAZeroOrAGuess()
    {
        var obs = new[] { O(-500, 14.0), O(595, 15.0) };
        var r = VixSummaryCalculator.Compute(obs, [new WindowInstant(SummaryWindow.All[0], T0.AddSeconds(100), T0.AddSeconds(600))], Limit)[0];
        Assert.Equal(15.0, r.End.Value);
        Assert.Equal(VixStatus.Stale, r.Start.Status);
        Assert.Null(r.Change); Assert.Null(r.ChangePct);
        var none = VixSummaryCalculator.Compute([], [new WindowInstant(SummaryWindow.All[0], null, null)], Limit)[0];
        Assert.Equal(VixStatus.Missing, none.End.Status); Assert.Null(none.Change);
    }

    // ---- service against the raw tick table ----

    sealed class SourceFactory : IDbContextFactory<NiftySignalDbContext>
    {
        readonly DbContextOptions<NiftySignalDbContext> _o = new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public NiftySignalDbContext CreateDbContext() => new(_o);
        public Task<NiftySignalDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    static Tick Px(string token, decimal price, DateTimeOffset exchange, DateTimeOffset? received = null) => new()
    { Token = token, Exchange = Exchange.Nse, ExchangeTimestamp = exchange.ToUniversalTime(), ReceivedAt = (received ?? exchange).ToUniversalTime(), LastPrice = price };

    static async Task<SourceFactory> SeedAsync(params Instrument[] instruments)
    {
        var f = new SourceFactory();
        await using var db = f.CreateDbContext();
        db.Instruments.AddRange(instruments);
        await db.SaveChangesAsync();
        return f;
    }

    static Instrument Vix(string token) => new() { Token = token, TradingSymbol = "India VIX", Underlying = "NIFTY", InstrumentType = InstrumentType.Vix, Exchange = Exchange.Nse, AsOfDate = Day };

    [Fact]
    public async Task Service_UsesTheInstrumentMasterVixToken_AndNothingAfterTheBoundary()
    {
        var f = await SeedAsync(Vix("26017"));
        await using (var db = f.CreateDbContext())
        {
            db.Ticks.AddRange(
                Px("26017", 14.0m, T0.AddSeconds(-5)),
                Px("26017", 15.0m, T0.AddSeconds(595)),
                Px("26017", 16.0m, T0.AddSeconds(601)),                                           // exchanged after the boundary
                Px("26017", 17.0m, T0.AddSeconds(590), received: T0.AddSeconds(605)),             // exchanged before, but not AVAILABLE until after the boundary
                Px("NIFTY50", 99m, T0.AddSeconds(599)));                                          // another instrument
            await db.SaveChangesAsync();
        }

        var r = await new VixSummaryService(f).LoadAsync(Day, [new WindowInstant(SummaryWindow.All[4], T0, T0.AddSeconds(600))]);
        Assert.Null(r.UnavailableReason);
        var w = r.Windows!.Single();
        Assert.Equal(14.0, w.Start.Value);
        Assert.Equal(15.0, w.End.Value);                       // not 16, not 17
        Assert.Equal(1.0, w.Change!.Value, 9);
    }

    [Fact]
    public async Task Service_NoInstrumentOrAmbiguousInstrument_IsUnavailableWithReason()
    {
        var instants = new[] { new WindowInstant(SummaryWindow.All[4], T0, T0.AddSeconds(600)) };
        Assert.Equal("no India VIX instrument in the instrument master", (await new VixSummaryService(await SeedAsync()).LoadAsync(Day, instants)).UnavailableReason);
        Assert.Equal("India VIX instrument is not unique", (await new VixSummaryService(await SeedAsync(Vix("1"), Vix("2"))).LoadAsync(Day, instants)).UnavailableReason);
        Assert.Equal("bar boundaries unavailable", (await new VixSummaryService(await SeedAsync(Vix("1"))).LoadAsync(Day, [new WindowInstant(SummaryWindow.All[0], null, null)])).UnavailableReason);
    }

    [Fact]
    public async Task Service_NoTicksInFreshnessRange_IsMissingNotZero_AndNoHistoryIsInvented()
    {
        var f = await SeedAsync(Vix("26017"));
        await using (var db = f.CreateDbContext()) { db.Ticks.Add(Px("26017", 14.0m, T0.AddHours(-2))); await db.SaveChangesAsync(); }       // far older than the limit: not even read
        var w = (await new VixSummaryService(f).LoadAsync(Day, [new WindowInstant(SummaryWindow.All[4], T0, T0.AddSeconds(600))])).Windows!.Single();
        Assert.Equal(VixStatus.Missing, w.End.Status);
        Assert.Null(w.Change);
    }
}
