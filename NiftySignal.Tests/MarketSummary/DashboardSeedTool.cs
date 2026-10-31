using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Domain.ValueObjects;
using NiftySignal.Persistence;

namespace NiftySignal.Tests.MarketSummary;

/// <summary>
/// Developer tool, not an assertion: when <c>NSE_DASHBOARD_SEED_CONN</c> names a THROWAWAY local PostgreSQL cluster (loopback, port other than 5432, database "niftysignal") it fills it with one synthetic
/// fixed-clock session (60 completed bars, a 21-strike weekly universe, v3 per-strike rows, an Auto-Follow centre move at bar 41, India VIX and spot ticks, a live partial bar) so a locally running Dashboard can be
/// driven in a real browser. Skipped (returns) when the variable is not set. Never used by the suite.
/// </summary>
public class DashboardSeedTool
{
    [Fact]
    public async Task SeedThrowawayDashboardDatabases_WhenRequested()
    {
        var text = Environment.GetEnvironmentVariable("NSE_DASHBOARD_SEED_CONN");
        if (string.IsNullOrWhiteSpace(text)) return;
        var b = new NpgsqlConnectionStringBuilder(text);
        if (b.Host is not ("localhost" or "127.0.0.1") || b.Port == 5432 || b.Database != "niftysignal") throw new InvalidOperationException("Refusing: the seed target must be a throwaway loopback cluster (port != 5432, database niftysignal).");

        var ist = TimeSpan.FromHours(5.5);
        var now = DateTimeOffset.UtcNow;
        var day = DateOnly.FromDateTime(now.ToOffset(ist).DateTime);
        var t0 = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), ist).ToUniversalTime();
        var expiry = day.AddDays(3);
        const int bars = 60;
        var strikes = Enumerable.Range(0, 21).Select(i => 25_500d + i * 50).ToList();

        await using (var source = new NiftySignalDbContext(new DbContextOptionsBuilder<NiftySignalDbContext>().UseNpgsql(text).Options))
        {
            await source.Database.EnsureCreatedAsync();
            source.Instruments.AddRange(
                new Instrument { Token = "SPOT", TradingSymbol = "Nifty 50", Underlying = "NIFTY", InstrumentType = InstrumentType.Index, Exchange = Exchange.Nse, AsOfDate = day },
                new Instrument { Token = "26017", TradingSymbol = "India VIX", Underlying = "NIFTY", InstrumentType = InstrumentType.Vix, Exchange = Exchange.Nse, AsOfDate = day },
                new Instrument { Token = "FUT", TradingSymbol = "NIFTYFUT", Underlying = "NIFTY", InstrumentType = InstrumentType.Future, Exchange = Exchange.Nfo, AsOfDate = day, ExpiryDate = day.AddDays(20), LotSize = 65 });
            for (var s = -120; s <= bars * 60 + 120; s += 3)
            {
                var at = t0.AddSeconds(s);
                source.Ticks.Add(new Tick { Token = "26017", Exchange = Exchange.Nse, ExchangeTimestamp = at, ReceivedAt = at.AddMilliseconds(300), LastPrice = 13.00m + (s / 60) * 0.01m });
                if (s % 15 == 0) source.Ticks.Add(new Tick { Token = "SPOT", Exchange = Exchange.Nse, ExchangeTimestamp = at, ReceivedAt = at.AddMilliseconds(300), LastPrice = 26_000m + (s / 60) });
            }

            await source.SaveChangesAsync();
        }

        var obsConn = new NpgsqlConnectionStringBuilder(text) { Database = AdaptiveObserverDbContext.DatabaseName }.ConnectionString;
        await using var db = new AdaptiveObserverDbContext(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseNpgsql(obsConn).Options);
        await db.Database.MigrateAsync();
        var universe = strikes.SelectMany(s => new[]
        {
            new ObserverOptionInstrument($"CE{s:0}", $"NIFTY CE{s:0}", OptionType.Call, s, expiry, 65), new ObserverOptionInstrument($"PE{s:0}", $"NIFTY PE{s:0}", OptionType.Put, s, expiry, 65),
        }).ToList();
        var session = new AdaptiveSessionStateRow
        {
            TradeDate = day, ModelVersion = AdaptiveFixedClockV1.ModelVersion, SourceBranch = "seed", SourceCommitSha = "seed", BuildUtc = now, FutureToken = "FUT", FutureSymbol = "NIFTYFUT", FutureExpiry = day.AddDays(20),
            OpeningWindowStartUtc = t0, OpeningWindowEndUtc = t0.AddMinutes(15), EstimatorName = "x", CreatedAtUtc = now, BaseBarVolume = 13_650, WeeklyOptionExpiry = expiry, OptionUniverseJson = JsonSerializer.Serialize(universe),
            AtmRuleVersion = "spot-atm-v1", RollingWindowBars = 10,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.Runtime.Add(new AdaptiveObserverRuntimeRow { SessionId = session.Id, RuntimeStatus = AdaptiveRuntimeStatus.Live, LastHeartbeatUtc = now, LastCompletedBarSeq = bars, CurrentPartialBarVolume = 10_420, CurrentPartialBarStartedAtUtc = t0.AddMinutes(bars) });
        var r = new Random(7);
        for (var seq = 1; seq <= bars; seq++)
        {
            var end = t0.AddMinutes(seq); var begin = end.AddMinutes(-1);
            var centre = seq <= 40 ? 26_000d : 26_050d;                                      // Auto Follow: the centre moves at bar 41
            db.FutureBars.Add(new AdaptiveFutureBarRow
            {
                SessionId = session.Id, BarSeq = seq, StartAvailableAtUtc = begin, EndAvailableAtUtc = end, Volume = 13_650, TradeUpdates = 40, DurationSeconds = 60, Open = 26_000 + seq, High = 26_010 + seq, Low = 25_995 + seq, Close = 26_002 + seq,
                StrictBuyVolume = 7_000, StrictSellVolume = 6_000, StrictUnknownVolume = 650, StrictDelta = 1_000, EnrichedBuyVolume = 7_100, EnrichedSellVolume = 6_000, EnrichedUnknownVolume = 550, EnrichedDelta = 1_100, OiOpen = 1_000_000 + seq * 10, OiClose = 1_000_010 + seq * 10, OiChange = 10,
            });
            foreach (var side in new[] { OptionType.Call, OptionType.Put })
                db.OptionBandBars.Add(new AdaptiveOptionBandBarRow
                {
                    SessionId = session.Id, BarSeq = seq, Side = side, BandStrikes = string.Join(',', Enumerable.Range(-2, 5).Select(i => centre + 50 * i)), StartAvailableAtUtc = begin, EndAvailableAtUtc = end, CenterStrike = centre, BandAvailable = true,
                    BarBandPriceChange = 1.5, RollingBandPriceChange = 2.5, ContractStrictDelta = 120, ContractEnrichedDelta = 130, ContractRollingStrictDelta = 900,
                });
            foreach (var s in strikes)
                foreach (var call in new[] { true, false })
                    db.OptionStrikeBars.Add(new AdaptiveOptionStrikeBarRow
                    {
                        SessionId = session.Id, BarSeq = seq, MetricsVersion = OptionStrikeBarMetrics.MetricsVersion, EndAvailableAtUtc = end, Expiry = expiry, Strike = s, Side = call ? OptionType.Call : OptionType.Put,
                        Token = $"{(call ? "CE" : "PE")}{s:0}", TradingSymbol = $"NIFTY {(call ? "CE" : "PE")}{s:0}", PriceStart = 100 + r.Next(30), PriceEnd = 100 + r.Next(30), TotalQuantity = r.Next(0, 900), StrictDelta = r.Next(-80, 80),
                        EnrichedBuy = 40, EnrichedSell = 35, EnrichedUnknown = 5, EnrichedDelta = r.Next(-40, 40), OiStart = 10_000, OiEnd = 10_000 + r.Next(-50, 50), IvStart = 12, IvEnd = 12 + r.NextDouble(),
                        DeltaStart = call ? 0.45 : -0.45, DeltaEnd = (call ? 0.45 : -0.45) + 0.01, VegaStart = 6, VegaEnd = 6.1, NetOfi = r.Next(-20, 20), GrossOfi = 60, TobTimeWeighted = 0.1, MicroDevTimeWeighted = 0.05, TobUsableSeconds = 30,
                    });
        }

        await db.SaveChangesAsync();
    }
}
