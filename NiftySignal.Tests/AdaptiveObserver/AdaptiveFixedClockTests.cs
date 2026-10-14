using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NiftySignal.AdaptiveObserver;
using NiftySignal.AdaptiveObserverData;
using NiftySignal.Domain.Configuration;
using NiftySignal.Domain.Entities;
using NiftySignal.Domain.Enums;
using NiftySignal.Host;
using NiftySignal.Persistence;
using NiftySignal.Tests.Trading.Adapters;
using NiftySignal.Trading.Adapters;

namespace NiftySignal.Tests.AdaptiveObserver;

/// <summary>
/// The fixed 13,650-contract futures-volume clock (adaptive-fixed13650-v1): model identity, 09:15 start, exact bars, deterministic restart, the separate 09:30 residual anchor and the
/// fixed-clock-only Strong threshold policy. adaptive-v1 behaviour is covered by the existing suites and is asserted unchanged here where the two meet.
/// </summary>
public sealed class AdaptiveFixedClockTests
{
    static readonly DateOnly Day = new(2026, 10, 14);
    static readonly DateTimeOffset Open = new(2026, 10, 14, 3, 45, 0, TimeSpan.Zero);          // 09:15 IST
    static readonly DateTimeOffset Cutoff = Open.AddMinutes(15);                                  // 09:30 IST

    // ---- identity ----

    [Fact]
    public void ModelIdentity_IsADistinctVersion_AndAdaptiveV1IsNeverRedefined()
    {
        Assert.Equal("adaptive-fixed13650-v1", AdaptiveFixedClockV1.ModelVersion);
        Assert.Equal(13_650, AdaptiveFixedClockV1.BarVolume);
        Assert.Equal("adaptive-v1", OpeningVolumeProjectionV1.ModelVersion);
        Assert.NotEqual(OpeningVolumeProjectionV1.ModelVersion, AdaptiveFixedClockV1.ModelVersion);
        Assert.True(AdaptiveFixedClockV1.IsSupported("adaptive-v1"));
        Assert.True(AdaptiveFixedClockV1.IsSupported("adaptive-fixed13650-v1"));
        Assert.False(AdaptiveFixedClockV1.IsSupported("something-else"));
        Assert.Equal("Fixed Volume 13,650 | start 09:15", AdaptiveFixedClockV1.Describe(AdaptiveFixedClockV1.ModelVersion));
    }

    // ---- session freeze ----

    [Fact]
    public async Task FixedSession_IsFrozenAt0915_With13650_AndNoOpeningEstimator()
    {
        await using var source = Source(); await using var db = Observer();
        AddInstruments(source); await source.SaveChangesAsync();
        var coordinator = Coordinator(AdaptiveClockMode.Fixed13650);

        Assert.Null(await coordinator.TryGetOrCreateAsync(source, db, Day, Open.AddSeconds(-1), default));      // before the open: nothing
        var context = await coordinator.TryGetOrCreateAsync(source, db, Day, Open.AddMinutes(1), default);        // one minute in, NOT 09:30
        Assert.NotNull(context);
        var s = context!.Session;
        Assert.Equal(AdaptiveFixedClockV1.ModelVersion, s.ModelVersion);
        Assert.Equal(13_650, s.BaseBarVolume);
        Assert.Equal(13_650 * 10, s.RollingWindowVolume);
        Assert.Equal(0, s.OpeningVolume);                                      // no opening-volume estimate selects the size
        Assert.False(s.UsesMedianOpeningFallback);
        Assert.Equal(Open, s.ObservationStartUtc);
        Assert.Equal(Open, s.OpeningWindowStartUtc);
        Assert.Equal(Cutoff, context.SignalStartUtc);                          // residual / signal boundary stays 09:30
        Assert.Null(context.ResidualAnchor);                                   // the anchor does not exist yet
        Assert.Null(s.Future0930);
        Assert.Null(s.StrongThreshold);                                        // no fixed-clock history yet: uncalibrated, never borrowed
        Assert.Equal(0, s.StrongThresholdPriorStateCount);
        Assert.Equal(AdaptiveRuntimeStatus.Calibrating, (await db.Runtime.SingleAsync()).RuntimeStatus);

        var restarted = await coordinator.TryGetOrCreateAsync(source, db, Day, Open.AddHours(2), default);          // a late restart reloads the same frozen session
        Assert.Equal(s.Id, restarted!.Session.Id);
        Assert.Equal(13_650, restarted.Definition.BaseBarVolume);
        Assert.Equal(Cutoff, restarted.SignalStartUtc);
        Assert.Single(await db.Sessions.ToListAsync());
    }

    [Fact]
    public async Task ExistingAdaptiveV1Session_KeepsItsModel_EvenWhenNewSessionsAreFixed()
    {
        await using var source = Source(); await using var db = Observer();
        AddInstruments(source); await source.SaveChangesAsync();
        var v1 = AdaptiveWeak2ObservationServiceTests.Context().Session;
        v1.Id = 0; v1.TradeDate = Day; v1.IsHistoricalSeed = false; v1.OpeningVolume = 150_000; v1.BaseBarVolume = 16_250; v1.WeeklyOptionExpiry = Day;
        v1.OptionUniverseJson = System.Text.Json.JsonSerializer.Serialize(new[] { new ObserverOptionInstrument("CE", "NIFTYCE", OptionType.Call, 23000, Day, 65) });
        db.Sessions.Add(v1); await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var context = await Coordinator(AdaptiveClockMode.Fixed13650).TryGetOrCreateAsync(source, db, Day, Open.AddHours(1), default);
        Assert.Equal("adaptive-v1", context!.Session.ModelVersion);
        Assert.Equal(16_250, context.Definition.BaseBarVolume);
        Assert.Single(await db.Sessions.ToListAsync());                          // no fixed session was added next to it
    }

    // ---- strong calibration ----

    [Fact]
    public async Task StrongThreshold_UsesOnlyPriorFixedClockSessions_NeverAdaptiveV1_NeverTheFuture()
    {
        await using var db = Observer();
        var fixedRatios = new List<double>();
        for (var d = 1; d <= 12; d++)
        {
            var ratios = Enumerable.Range(1, 5).Select(i => d * 0.01 + i * 0.001).ToArray();
            fixedRatios.AddRange(ratios);
            AddSession(db, AdaptiveFixedClockV1.ModelVersion, Day.AddDays(-d), seed: true, coverage: 15, ratios);
        }

        AddSession(db, "adaptive-v1", Day.AddDays(-3), seed: true, coverage: 15, [0.9, 0.95, 0.99]);                         // other clock: ignored
        AddSession(db, AdaptiveFixedClockV1.ModelVersion, Day.AddDays(1), seed: false, coverage: null, [0.9, 0.95, 0.99]);   // the future: ignored
        AddSession(db, AdaptiveFixedClockV1.ModelVersion, Day, seed: false, coverage: null, [0.9, 0.95, 0.99]);              // today itself: ignored
        AddSession(db, AdaptiveFixedClockV1.ModelVersion, Day.AddDays(-20), seed: true, coverage: 14, [0.9, 0.95, 0.99]);    // incomplete opening coverage: ignored
        await db.SaveChangesAsync();

        var (threshold, count) = await AdaptiveSessionCoordinator.FixedClockStrongThresholdAsync(db, Day, default);
        Assert.Equal(60, count);
        Assert.Equal(AdaptiveWeak2Classifier.ComputeStrongThreshold(fixedRatios), threshold!.Value, 12);
        Assert.True(threshold < 0.5);                                                                                          // none of the large decoy ratios leaked in
    }

    [Fact]
    public async Task StrongThreshold_IsExplicitlyUncalibrated_WhenTooFewFixedSessionsExist()
    {
        await using var db = Observer();
        for (var d = 1; d < AdaptiveFixedClockV1.MinimumPriorSessionsForStrongThreshold; d++)
            AddSession(db, AdaptiveFixedClockV1.ModelVersion, Day.AddDays(-d), seed: true, coverage: 15, [0.1, 0.2]);
        for (var d = 20; d < 40; d++)
            AddSession(db, "adaptive-v1", Day.AddDays(-d), seed: true, coverage: 15, [0.3, 0.4]);                              // plenty of v1 history is NOT a substitute
        await db.SaveChangesAsync();

        var (threshold, count) = await AdaptiveSessionCoordinator.FixedClockStrongThresholdAsync(db, Day, default);
        Assert.Null(threshold);
        Assert.Equal(0, count);
    }

    // ---- exact bars / replay ----

    static (SessionFreeze Freeze, IReadOnlyList<(string Token, CleanObserverTick Tick)> Clean, IReadOnlyList<(string Token, CleanObserverTick Tick)[]> Groups) Synthetic()
    {
        var rows = SyntheticSession.Rows(new SyntheticSession.Options { Windows = new[] { (SyntheticSession.Ist(9, 15), SyntheticSession.Ist(12, 0)) } });
        var freeze = SyntheticSession.Freeze(rows);
        var normalizer = new AdaptiveIncrementalTickNormalizer();
        var clean = new List<(string Token, CleanObserverTick Tick)>();
        foreach (var r in rows)
        {
            if (normalizer.Process(r.Token, r.ToRaw()) is { } t && t.AvailableAt >= freeze.ObservationStartUtc) clean.Add((r.Token, t));
        }

        var ordered = clean.OrderBy(c => c.Tick.AvailableAt).ThenBy(c => c.Tick.Id).ToList();
        var groups = ordered.GroupBy(c => c.Tick.AvailableAt).OrderBy(g => g.Key).Select(g => g.ToArray()).ToList();
        return (freeze, ordered, groups);
    }


    /// <summary>The anchor exactly as AdaptiveSessionCoordinator builds it for adaptive-v1: the latest CLEAN quote of every option and the last CLEAN future tick at or before 09:30.</summary>
    static OptionResidualAnchor ExpectedAnchor(SessionFreeze f, IReadOnlyList<(string Token, CleanObserverTick Tick)> clean)
    {
        var cutoff = f.SignalStartUtc;
        var quotes = new Dictionary<string, OptionQuoteSnapshot>(StringComparer.Ordinal);
        CleanObserverTick? future = null;
        foreach (var (token, t) in clean.Where(c => c.Tick.AvailableAt <= cutoff))
        {
            if (token == f.Definition.FutureToken) future = t;
            else quotes[token] = new OptionQuoteSnapshot(token, t.AvailableAt, t.Last, t.Bid, t.Ask, t.OpenInterest);
        }

        return OptionResidualModel.BuildAnchor(cutoff, f.WeeklyOptionExpiry, future!.Value.Last, f.Options, quotes, f.RiskFreeRate, 5d)!;
    }

    static AdaptiveObserverEngine Engine(SessionFreeze f, bool selfAnchor, OptionResidualAnchor? anchor = null) => new(
        f.Definition with { BaseBarVolume = 13_650 }, f.WeeklyOptionExpiry, f.RiskFreeRate, null, f.SignalStartUtc, f.Options, anchor,
        selfAnchorCutoffUtc: selfAnchor ? f.SignalStartUtc : null);

    static List<AdaptiveCompletedBarPackage> Run(AdaptiveObserverEngine engine, IEnumerable<(string Token, CleanObserverTick Tick)[]> groups) =>
        groups.SelectMany(g => engine.ProcessAvailabilityGroup(g)).ToList();

    [Fact]
    public void Bars_Start0915_AreExactly13650_AndTheOpeningFifteenMinutesAreKept()
    {
        var (freeze, clean, groups) = Synthetic();
        var engine = Engine(freeze, selfAnchor: true);
        var packages = Run(engine, groups);

        Assert.True(packages.Count >= 8);
        Assert.All(packages, p => Assert.Equal(13_650, p.FutureBar.Volume));                           // every completed bar holds exactly 13,650 contracts
        Assert.All(engine.FutureBars.Where(b => b.IsComplete), b => Assert.Equal(13_650, b.Volume));
        Assert.True(packages[0].FutureBar.StartAvailableAtUtc >= freeze.ObservationStartUtc);
        Assert.True(packages[0].FutureBar.StartAvailableAtUtc < freeze.ObservationStartUtc.AddMinutes(1));   // the first bar starts at the first tick at/after 09:15
        Assert.Contains(packages, p => p.FutureBar.EndAvailableAtUtc < freeze.SignalStartUtc);               // 09:15-09:30 volume forms bars: it is not thrown away

        // volume conservation: complete bars + the partial bar account for every contract traded after the baseline tick
        var enricher = new AdaptiveTradeFlowEnricher();
        var traded = clean.Where(c => c.Token == freeze.Definition.FutureToken).Sum(c => enricher.Process(c.Tick).TradeVolume);
        Assert.Equal(traded, packages.Count * 13_650L + engine.PartialBar.AccumulatedVolume);
    }

    [Fact]
    public void Restart_AndLateRestart_ReconstructIdenticalBoundaries_RegardlessOfHowTheTicksArriveInBatches()
    {
        var (freeze, _, groups) = Synthetic();
        var live = Run(Engine(freeze, true), groups);                                                    // "live": group by group
        var replay = Run(Engine(freeze, true), groups.Chunk(37).SelectMany(c => c));                    // restart replay: the same ordered groups handed over in other batch shapes
        Assert.Equal(live.Count, replay.Count);
        for (var i = 0; i < live.Count; i++)
        {
            Assert.Equal(live[i].FutureBar.BarSeq, replay[i].FutureBar.BarSeq);
            Assert.Equal(live[i].FutureBar.StartAvailableAtUtc, replay[i].FutureBar.StartAvailableAtUtc);
            Assert.Equal(live[i].FutureBar.EndAvailableAtUtc, replay[i].FutureBar.EndAvailableAtUtc);
            Assert.Equal(live[i].FutureBar.Close, replay[i].FutureBar.Close);
            Assert.Equal(live[i].FutureBar.StrictDelta, replay[i].FutureBar.StrictDelta);
        }
    }

    // ---- residual anchor stays 09:30 ----

    [Fact]
    public void ResidualAnchor_IsFrozenCausallyAt0930_AndNoResidualExistsBeforeIt()
    {
        var (freeze, clean, groups) = Synthetic();
        var cutoff = freeze.SignalStartUtc;
        var engine = Engine(freeze, selfAnchor: true);
        var packages = new List<AdaptiveCompletedBarPackage>();
        foreach (var g in groups)
        {
            var before = g[0].Tick.AvailableAt <= cutoff;
            if (before) Assert.False(engine.SelfAnchorAttempted);                                      // not frozen until the first input AFTER 09:30
            packages.AddRange(engine.ProcessAvailabilityGroup(g));
        }

        Assert.True(engine.SelfAnchorAttempted);
        Assert.NotNull(engine.ResidualAnchor);
        var expected = ExpectedAnchor(freeze, clean);                                                  // the coordinator's own construction (latest clean quote at or before 09:30)
        Assert.Equal(expected.Future0930, engine.ResidualAnchor!.Future0930);
        Assert.Equal(expected.CenterStrike, engine.ResidualAnchor.CenterStrike);
        Assert.Equal(expected.SyntheticUnderlying0930, engine.ResidualAnchor.SyntheticUnderlying0930, 9);
        Assert.Equal(expected.Calls.Select(c => (c.Instrument.Token, c.Price0930)), engine.ResidualAnchor.Calls.Select(c => (c.Instrument.Token, c.Price0930)));

        var early = packages.Where(p => p.FutureBar.EndAvailableAtUtc < cutoff).ToList();
        var late = packages.Where(p => p.FutureBar.EndAvailableAtUtc >= cutoff).ToList();
        Assert.NotEmpty(early); Assert.NotEmpty(late);
        Assert.All(early, p => Assert.Empty(p.Residuals));                                              // no residual is fabricated before the anchor exists
        Assert.All(late, p => Assert.NotEmpty(p.Residuals));
        Assert.Contains(late, p => p.Residuals.Any(r => r.Reading is not null));
    }

    [Fact]
    public void SelfAnchoring_ReproducesExactlyTheResultsOfAnEngineGivenTheSameAnchorUpFront()
    {
        var (freeze, clean, groups) = Synthetic();
        var self = Run(Engine(freeze, true), groups);
        var given = Run(Engine(freeze, false, ExpectedAnchor(freeze, clean)), groups);
        Assert.Equal(given.Count, self.Count);
        for (var i = 0; i < self.Count; i++)
        {
            Assert.Equal(given[i].Residuals.Count, self[i].Residuals.Count);
            for (var k = 0; k < self[i].Residuals.Count; k++)
            {
                Assert.Equal(given[i].Residuals[k].Reading?.DirectionalResidualPct, self[i].Residuals[k].Reading?.DirectionalResidualPct);
                Assert.Equal(given[i].Residuals[k].Relationship, self[i].Residuals[k].Relationship);
            }
        }
    }

    [Fact]
    public void WithoutAnyAnchorSource_ResidualsAreUnavailableNotFabricated()
    {
        var (freeze, _, groups) = Synthetic();
        var packages = Run(Engine(freeze, selfAnchor: false, anchor: null), groups);
        Assert.All(packages.Where(p => p.FutureBar.EndAvailableAtUtc >= Cutoff), p => Assert.All(p.Residuals, r => Assert.Null(r.Reading)));
    }

    // ---- anchor persistence ----

    [Fact]
    public async Task AnchorPersistence_IsFillOnce_AndAStoredAnchorIsNeverOverwritten()
    {
        var (freeze, _, groups) = Synthetic();
        var engine = Engine(freeze, true);
        Run(engine, groups);
        await using var db = Observer();
        var session = AdaptiveWeak2ObservationServiceTests.Context().Session;
        session.Id = 0; session.TradeDate = Day; session.ModelVersion = AdaptiveFixedClockV1.ModelVersion; session.Future0930 = null; session.SyntheticWeeklyUnderlying0930 = null; session.ResidualCenterStrike = null;
        db.Sessions.Add(session); await db.SaveChangesAsync();
        var persistence = new AdaptiveObserverPersistence(NullLogger<AdaptiveObserverPersistence>.Instance);

        Assert.True(await persistence.PersistResidualAnchorOnceAsync(db, session.Id, engine.ResidualAnchor, DateTimeOffset.UtcNow, default));
        var components = await db.ResidualAnchorComponents.CountAsync();
        Assert.Equal(engine.ResidualAnchor!.Calls.Count + engine.ResidualAnchor.Puts.Count, components);
        Assert.Equal(engine.ResidualAnchor.CenterStrike, (await db.Sessions.SingleAsync()).ResidualCenterStrike);

        Assert.False(await persistence.PersistResidualAnchorOnceAsync(db, session.Id, engine.ResidualAnchor with { CenterStrike = engine.ResidualAnchor.CenterStrike + 50 }, DateTimeOffset.UtcNow, default));
        Assert.Equal(engine.ResidualAnchor.CenterStrike, (await db.Sessions.AsNoTracking().SingleAsync()).ResidualCenterStrike);       // left as stored
        Assert.Equal(components, await db.ResidualAnchorComponents.CountAsync());
        Assert.False(await persistence.PersistResidualAnchorOnceAsync(db, session.Id, null, DateTimeOffset.UtcNow, default));
    }

    // ---- history rebuild on the fixed clock ----

    [Fact]
    public async Task HistoryBootstrap_RebuildsPriorDaysOnTheFixedClock_ForCalibrationOnly_AndIsIdempotent()
    {
        await using var source = Source(); await using var db = Observer();
        var today = Day;
        var good = new[] { today.AddDays(-1), today.AddDays(-2) };
        var partialOpening = today.AddDays(-3);
        foreach (var day in good.Append(partialOpening))
        {
            source.Instruments.Add(new Instrument { Token = "FUT", TradingSymbol = "NIFTYFUT", Exchange = Exchange.Nfo, Underlying = "NIFTY", InstrumentType = InstrumentType.Future, AsOfDate = day, ExpiryDate = day.AddDays(20), LotSize = 65 });
            var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), TimeSpan.FromHours(5.5)).ToUniversalTime();
            var start = day == partialOpening ? open.AddMinutes(10) : open;              // 09:15-09:30 not covered: must not calibrate
            for (var minute = 0; minute <= 360; minute++)
            {
                var at = start.AddMinutes(minute);
                if (day == partialOpening && minute == 0) { }
                source.Ticks.Add(new Tick { Token = "FUT", Exchange = Exchange.Nfo, ExchangeTimestamp = at, ReceivedAt = at, LastPrice = 23000 + minute % 7, Volume = 1000 + minute * 6000L });
            }
        }

        await source.SaveChangesAsync();
        var service = new AdaptiveHistoricalBootstrapService(new(), NullLogger<AdaptiveHistoricalBootstrapService>.Instance);

        Assert.Equal(2, await service.EnsurePriorFixedSessionsAsync(source, db, today, default));
        var sessions = await db.Sessions.OrderBy(x => x.TradeDate).ToListAsync();
        Assert.Equal(good.OrderBy(x => x), sessions.Select(x => x.TradeDate));
        Assert.All(sessions, s =>
        {
            Assert.Equal(AdaptiveFixedClockV1.ModelVersion, s.ModelVersion);
            Assert.True(s.IsHistoricalSeed);
            Assert.Equal(13_650, s.BaseBarVolume);
            Assert.Equal(15, s.OpeningCoverageMinutes);
            Assert.Null(s.StrongThreshold);                                                // too few prior fixed sessions to calibrate even these
        });
        Assert.True(await db.RollingStates.AnyAsync());
        Assert.All(await db.FutureBars.ToListAsync(), b => Assert.Equal(13_650, b.Volume));
        Assert.Equal(0, await service.EnsurePriorFixedSessionsAsync(source, db, today, default));    // idempotent
        Assert.Equal(2, await db.Sessions.CountAsync());
    }

    // ---- helpers ----

    static void AddSession(AdaptiveObserverDbContext db, string model, DateOnly day, bool seed, int? coverage, double[] ratios)
    {
        var row = AdaptiveWeak2ObservationServiceTests.Context().Session;
        row.Id = 0; row.TradeDate = day; row.ModelVersion = model; row.IsHistoricalSeed = seed; row.OpeningCoverageMinutes = coverage;
        db.Sessions.Add(row);
        db.SaveChanges();
        foreach (var (ratio, i) in ratios.Select((r, i) => (r, i)))
        {
            db.RollingStates.Add(new AdaptiveRollingStateRow
            {
                SessionId = row.Id, StartBarSeq = i + 1, EndBarSeq = i + 10, StartAvailableAtUtc = Open, EndAvailableAtUtc = Open.AddMinutes(i + 1),
                StrictDeltaRatioTotal = i % 2 == 0 ? ratio : -ratio, StrictDominanceEvolution = "Stable",
            });
        }

        db.SaveChanges();
    }

    static void AddInstruments(NiftySignalDbContext source)
    {
        source.Instruments.Add(new Instrument { Token = "FUT", TradingSymbol = "NIFTYFUT", Exchange = Exchange.Nfo, Underlying = "NIFTY", InstrumentType = InstrumentType.Future, AsOfDate = Day, ExpiryDate = Day.AddDays(20), LotSize = 65 });
        source.Instruments.Add(new Instrument { Token = "CE", TradingSymbol = "NIFTYCE", Exchange = Exchange.Nfo, Underlying = "NIFTY", InstrumentType = InstrumentType.Option, OptionType = OptionType.Call, AsOfDate = Day, ExpiryDate = Day.AddDays(6), StrikePrice = 23000, LotSize = 65 });
    }

    static NiftySignalDbContext Source() => new(new DbContextOptionsBuilder<NiftySignalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    static AdaptiveObserverDbContext Observer() => new(new DbContextOptionsBuilder<AdaptiveObserverDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);

    static AdaptiveSessionCoordinator Coordinator(AdaptiveClockMode mode) => new(new(), new Monitor(), NullLogger<AdaptiveSessionCoordinator>.Instance, mode);

    sealed class Monitor : IOptionsMonitor<PricingOptions>
    {
        public PricingOptions CurrentValue { get; } = new() { RiskFreeRate = .065 };
        public PricingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PricingOptions, string?> listener) => null;
    }
}
